using System.Text.Json;
using PomoDock.Core;

namespace PomoDock.Web;

/// <summary>
/// The agent's hands on the workspace: the desktop's <c>…FromAgent</c> doors, with the same names,
/// the same answers and the same rules. Where the desktop hands a WidgetCard around, the web hands
/// the card's config; a widget's own view (a calendar month, a To Do filter) is reached through the
/// live component that registered itself under the config's id.
/// </summary>
public partial class MainWindow
{
    internal bool AgentPlanApproved { get; set; }
    internal Guid? LastCreatedWidgetId { get; private set; }
    internal bool AgentCreatedWidgetThisTurn { get; set; }

    /// <summary>The chat floats above every page, as on the desktop, and is not part of any one.</summary>
    internal WidgetConfig? AgentConfig { get; private set; }
    private bool AgentVisible => AgentConfig is not null && Settings.AgentWidgetVisible;

    private readonly List<(Guid Id, object Part)> live = [];
    private PomoAgent? agent;
    private AgentConversations? agentChats;

    /// <summary>The agent outlives its card, as the desktop's AgentWindow does: hiding the chat keeps the session.</summary>
    internal PomoAgent Agent => agent ??= new PomoAgent(this);
    internal AgentConversations AgentChats => agentChats ??= new AgentConversations(Store);

    /// <summary>A widget body announces itself so the agent can steer the view it is showing.</summary>
    internal void Register(Guid id, object part) => live.Add((id, part));
    internal void Unregister(object part) => live.RemoveAll(item => ReferenceEquals(item.Part, part));
    internal T? Live<T>(Guid id) where T : class =>
        live.Where(item => item.Id == id).Select(item => item.Part).OfType<T>().LastOrDefault();

    private List<WidgetConfig> Cards => CurrentPage.Widgets;

    // ------------------------------------------------------------------ the chat card

    private void AgentClick()
    {
        Sounds.Button("click");
        if (AgentVisible) HideAgentWidget();
        else ShowAgentWidget();
    }

    internal void ShowAgentWidget()
    {
        if (AgentConfig is null)
        {
            var config = Settings.AgentWidget ?? new WidgetConfig { Kind = "agent", Title = L.T("agent.name"), Width = 360, Height = 520 };
            config.Kind = "agent";
            if (config.Width < 280) config.Width = 360;
            if (config.Height < 280) config.Height = 520;
            if (config.X == 0 && config.Y == 0 && canvasHeight > 0)
            {
                config.X = 16;
                config.Y = Math.Max(16, canvasHeight - config.Height - 24);
            }
            AgentConfig = config;
            Settings.AgentWidget = config;
            Arrive(config.Id);
        }
        Settings.AgentWidgetVisible = true;
        front = AgentConfig.Id;
        SaveState();
        StateHasChanged();
    }

    internal void HideAgentWidget()
    {
        if (AgentConfig is null) return;
        Settings.AgentWidget = AgentConfig;
        Settings.AgentWidgetVisible = false;
        SaveState();
        StateHasChanged();
    }

    /// <summary>The chat keeps its own rectangle across pages; it stays inside whatever canvas there is.</summary>
    private CardBox AgentRect(WidgetConfig config)
    {
        double width = Math.Min(Math.Max(260, config.Width), Math.Max(260, canvasWidth));
        double height = Math.Min(Math.Max(280, config.Height), Math.Max(280, canvasHeight));
        double x = Math.Clamp(config.X, 0, Math.Max(0, canvasWidth - width));
        double y = Math.Clamp(config.Y, 0, Math.Max(0, canvasHeight - height));
        return config.CanvasFill
            ? new CardBox(0, 0, Math.Max(260, canvasWidth), Math.Max(280, canvasHeight))
            : new CardBox(x, y, width, height);
    }

    internal void ShowCreatedWork(Guid? widgetId)
    {
        Status(L.T("agent.showingWork"));
        if (widgetId is { } id && Cards.FirstOrDefault(item => item.Id == id) is { } card)
        {
            BringCardToFront(card);
            Arrive(card.Id);
        }
        else PulsePageChrome();
        StateHasChanged();
    }

    internal void AskBirthFor(Guid id) => askBirthFor = id;

    /// <summary>Fills the canvas with one card or gives it back its own rectangle, as its header button does.</summary>
    internal void FillCardFromAgent(WidgetConfig config, bool fill)
    {
        if (fill)
        {
            config.Collapsed = false;
            ClearCanvasFillsExcept(config);
            config.FillCanvas(canvasWidth, canvasHeight);
            front = config.Id;
        }
        else if (config.CanvasFill) config.RestoreFromCanvasFill();
        SaveState();
        StateHasChanged();
    }

    internal void SetTimerCanvasFill(bool fill)
    {
        if (CurrentTimerWidget is not { } timer) return;
        if (fill)
        {
            ClearCanvasFillsExcept(timer);
            timer.FillCanvas(canvasWidth, canvasHeight);
            CurrentPage.TimerPositionCustomized = true;
        }
        else if (timer.CanvasFill) timer.RestoreFromCanvasFill();
        ArrangeCards();
        front = timer.Id;
        SaveState();
        StateHasChanged();
    }

    // ------------------------------------------------------------------ pages

    internal int CurrentWorkspacePageIndex => currentPageIndex;
    internal bool CurrentWorkspacePageIsBlank => !CurrentPage.HasContent;

    internal AgentToolResult DeleteEmptyPagesFromAgent()
    {
        if (Settings.WorkspacePages.Count <= 1)
            return AgentResult(false, L.T("pages.cannotDeleteOnly"));
        var empty = Settings.WorkspacePages
            .Select((page, index) => (page, index))
            .Where(item => !item.page.HasContent)
            .Select(item => item.index)
            .ToList();
        if (empty.Count == 0)
            return AgentResult(false, L.T("agent.noEmptyPages"));
        int keep = Settings.WorkspacePages.FindIndex(page => page.HasContent);
        if (keep < 0) keep = 0;
        empty.RemoveAll(index => index == keep);
        if (empty.Count == 0)
            return AgentResult(false, L.T("pages.cannotDeleteOnly"));
        if (currentPageIndex != keep) JumpToWorkspacePage(keep);
        foreach (int index in empty.OrderByDescending(index => index))
        {
            if (index < 0 || index >= Settings.WorkspacePages.Count) continue;
            if (index == currentPageIndex) continue;
            if (Settings.WorkspacePages[index].HasContent) continue;
            DeleteWorkspacePage(index);
        }
        PulsePageChrome();
        Status(L.T("agent.deletedEmptyPages", empty.Count));
        return AgentResult(true, L.T("agent.deletedEmptyPages", empty.Count));
    }

    /// <summary>Deletes one page (empty or not), same as right-click on a page in the map.</summary>
    internal AgentToolResult DeletePageFromAgent(JsonElement args)
    {
        if (!TryAgentPage(args, out int index, out var error)) return error!;
        if (Settings.WorkspacePages.Count <= 1)
            return AgentResult(false, L.T("pages.cannotDeleteOnly"));
        // The toolbox asked first when no plan was approved: the web's dialogs cannot block here.
        string name = Settings.WorkspacePages[index].Name;
        DeleteWorkspacePage(index);
        return AgentResult(true, EsPages() ? $"Eliminé la página «{name}»." : $"Deleted page “{name}”.");
    }

    internal AgentToolResult SetWebKeepAliveFromAgent(JsonElement args)
    {
        var card = FindAgentCard(args) ?? Cards.FirstOrDefault(item => item.Kind == "web");
        if (card is null || card.Kind != "web")
            return AgentResult(false, EsPages() ? "No hay widget web." : "No web widget.");
        bool keep = true;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("keep_alive", out var flag)
            && flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
            keep = flag.GetBoolean();
        else if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("keepalive", out flag)
            && flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
            keep = flag.GetBoolean();
        card.KeepAlive = keep;
        SaveState();
        return AgentResult(true, keep
            ? (EsPages() ? $"Keep-alive activado en «{card.Title}»." : $"Keep-alive on for “{card.Title}”.")
            : (EsPages() ? $"Keep-alive desactivado en «{card.Title}»." : $"Keep-alive off for “{card.Title}”."));
    }

    internal AgentToolResult AddPageFromAgent()
    {
        if (pageTransitioning) return AgentResult(false, L.T("agent.pageBusy"));
        if (!CurrentPage.HasContent)
            return AgentResult(false, L.T("agent.pageAlreadyEmpty"));
        int dx = 0, dy = 0;
        if (CanCreateWorkspacePage(1, 0)) dx = 1;
        else if (CanCreateWorkspacePage(0, 1)) dy = 1;
        else if (CanCreateWorkspacePage(-1, 0)) dx = -1;
        else if (CanCreateWorkspacePage(0, -1)) dy = -1;
        else return AgentResult(false, L.T("pages.lastAlreadyEmpty"));
        CommitWorkspacePageChange(dx, dy, null, true);
        ShowCreatedWork(null);
        return AgentResult(true, L.T("agent.pageCreated", (currentPageIndex + 1).ToString()));
    }

    internal AgentToolResult GotoPageFromAgent(JsonElement args)
    {
        int? index = ResolvePageIndexFromAgent(args);
        if (index is null)
            return AgentResult(false, EsPages() ? "No encontré esa página." : "Page not found.");
        if (index.Value != currentPageIndex) JumpToWorkspacePage(index.Value);
        PulsePageChrome();
        var page = CurrentPage;
        bool home = Settings.HomePageId == page.Id;
        return AgentResult(true, EsPages()
            ? $"Abrí «{page.Name}» (página {currentPageIndex + 1}){(home ? ", tu casa" : "")}."
            : $"Opened “{page.Name}” (page {currentPageIndex + 1}){(home ? ", your home" : "")}.");
    }

    internal AgentToolResult RenamePageFromAgent(JsonElement args)
    {
        int? index = ResolvePageIndexFromAgent(args) ?? currentPageIndex;
        if (index is null || index < 0 || index >= Settings.WorkspacePages.Count)
            return AgentResult(false, EsPages() ? "No encontré esa página." : "Page not found.");
        string name = TextArg(args, "name");
        if (name.Length == 0) name = TextArg(args, "new_name");
        if (name.Length == 0) name = TextArg(args, "title");
        name = name.Trim();
        if (name.Length == 0)
            return AgentResult(false, EsPages() ? "Falta el nuevo nombre de la página." : "Missing the new page name.");
        var page = Settings.WorkspacePages[index.Value];
        string previous = page.Name;
        page.Name = name;
        bool makeHome = BoolArg(args, "home", IsHomeAlias(name));
        if (makeHome) Settings.HomePageId = page.Id;
        SaveState();
        return AgentResult(true, EsPages()
            ? $"Renombré la página {index.Value + 1}: «{previous}» → «{page.Name}»{(makeHome ? " (casa)" : "")}."
            : $"Renamed page {index.Value + 1}: “{previous}” → “{page.Name}”{(makeHome ? " (home)" : "")}.");
    }

    internal AgentToolResult SetHomePageFromAgent(JsonElement args)
    {
        int? index = ResolvePageIndexFromAgent(args) ?? currentPageIndex;
        if (index is null || index < 0 || index >= Settings.WorkspacePages.Count)
            return AgentResult(false, EsPages() ? "No encontré esa página." : "Page not found.");
        var page = Settings.WorkspacePages[index.Value];
        Settings.HomePageId = page.Id;
        SaveState();
        return AgentResult(true, EsPages()
            ? $"«{page.Name}» es ahora tu página principal (casa)."
            : $"“{page.Name}” is now your home page.");
    }

    /// <summary>Resolves page by number, name, or home aliases (casa / principal / home).</summary>
    private int? ResolvePageIndexFromAgent(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;

        if (args.TryGetProperty("page", out var pageValue))
        {
            if (pageValue.ValueKind == JsonValueKind.Null) { /* fall through */ }
            else if (pageValue.TryGetInt32(out int number))
                return number >= 1 && number <= Settings.WorkspacePages.Count ? number - 1 : null;
            else if (pageValue.ValueKind == JsonValueKind.String)
            {
                string raw = pageValue.GetString()?.Trim() ?? "";
                if (int.TryParse(raw, out number))
                    return number >= 1 && number <= Settings.WorkspacePages.Count ? number - 1 : null;
                int? byName = FindPageIndexByNameOrAlias(raw);
                if (byName is not null) return byName;
            }
        }

        string name = TextArg(args, "name");
        if (name.Length == 0) name = TextArg(args, "title");
        if (name.Length > 0)
        {
            int? byName = FindPageIndexByNameOrAlias(name);
            if (byName is not null) return byName;
        }

        if (BoolArg(args, "home", false) || IsHomeAlias(TextArg(args, "to")))
            return FindHomePageIndex();

        return null;
    }

    private int? FindPageIndexByNameOrAlias(string raw)
    {
        string needle = raw.Trim();
        if (needle.Length == 0) return null;
        if (IsHomeAlias(needle)) return FindHomePageIndex();

        // "página 3" / "page 3"
        var match = System.Text.RegularExpressions.Regex.Match(needle, @"^(?:p[aá]gina|page)\s*0*(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int number))
            return number >= 1 && number <= Settings.WorkspacePages.Count ? number - 1 : null;

        for (int i = 0; i < Settings.WorkspacePages.Count; i++)
        {
            if (string.Equals(Settings.WorkspacePages[i].Name, needle, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        for (int i = 0; i < Settings.WorkspacePages.Count; i++)
        {
            if (Settings.WorkspacePages[i].Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return null;
    }

    private int? FindHomePageIndex()
    {
        if (Settings.HomePageId is { } id)
        {
            int found = Settings.WorkspacePages.FindIndex(page => page.Id == id);
            if (found >= 0) return found;
        }
        // Fallback: page named like home, else first page.
        for (int i = 0; i < Settings.WorkspacePages.Count; i++)
        {
            if (IsHomeAlias(Settings.WorkspacePages[i].Name)) return i;
        }
        return Settings.WorkspacePages.Count > 0 ? 0 : null;
    }

    private static bool IsHomeAlias(string? value)
    {
        string key = (value ?? "").Trim().ToLowerInvariant();
        return key is "casa" or "home" or "principal" or "inicio" or "main" or "página principal" or "pagina principal" or "página casa" or "pagina casa";
    }

    private static bool BoolArg(JsonElement args, string key, bool fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var value)) return fallback;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString()?.Trim().ToLowerInvariant() ?? "";
            if (text is "true" or "1" or "yes" or "si" or "sí") return true;
            if (text is "false" or "0" or "no") return false;
        }
        return fallback;
    }

    // ------------------------------------------------------------------ widgets

    internal AgentToolResult FocusWidgetFromAgent(JsonElement args)
    {
        string title = TextArg(args, "title");
        if (title.Length == 0) return AgentResult(false, L.T("agent.widgetMissing"));
        var match = Settings.WorkspacePages
            .Select((page, index) => (page, index, widget: page.Widgets.FirstOrDefault(item => item.Title.Contains(title, StringComparison.OrdinalIgnoreCase))))
            .FirstOrDefault(item => item.widget is not null);
        if (match.widget is null) return AgentResult(false, L.T("agent.widgetMissing"));
        if (match.index != currentPageIndex) JumpToWorkspacePage(match.index);
        ShowCreatedWork(match.widget.Id);
        return AgentResult(true, L.T("agent.showingWork") + " · " + match.widget.Title);
    }

    internal AgentToolResult AddTimerFromAgent()
    {
        if (CurrentTimerWidget is not null)
            return new AgentToolResult(false, "El temporizador ya está en esta página", JsonSerializer.Serialize(new { success = true, changed = false, summary = "El temporizador ya está en esta página" }));
        AddTimerWidget();
        return new AgentToolResult(true, "Widget de temporizador añadido", JsonSerializer.Serialize(new { success = true, changed = true, summary = "Widget de temporizador añadido" }));
    }

    internal AgentToolResult RemoveTimerFromAgent(JsonElement args)
    {
        if (!TryAgentPage(args, out int index, out var error)) return error!;
        if (index != currentPageIndex) JumpToWorkspacePage(index);
        if (CurrentTimerWidget is null)
            return AgentResult(false, EsPages() ? "Esa página no tiene temporizador." : "That page has no timer.");
        RemoveTimerWidget();
        PulsePageChrome();
        return AgentResult(true, EsPages() ? $"Quité el temporizador de la página {currentPageIndex + 1}." : $"Removed the timer from page {currentPageIndex + 1}.");
    }

    internal AgentToolResult RemoveWidgetFromAgent(JsonElement args)
    {
        if (!TryAgentPage(args, out int pageIndex, out var error)) return error!;
        if (pageIndex != currentPageIndex) JumpToWorkspacePage(pageIndex);
        SyncWorkspaceForAgent();
        string title = TextArg(args, "title");
        string kind = KindAlias(TextArg(args, "kind").ToLowerInvariant());
        string titleKind = KindAlias(title.ToLowerInvariant());
        var card = Cards.FirstOrDefault(item =>
            (title.Length > 0 && item.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
            || (kind.Length > 0 && item.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
            || (title.Length > 0 && item.Kind.Equals(titleKind, StringComparison.OrdinalIgnoreCase)));
        if (card is null) return AgentResult(false, L.T("agent.widgetMissing"));
        string name = card.Title;
        RemoveCard(card);
        return AgentResult(true, EsPages() ? $"Quité {name}." : $"Removed {name}.");
    }

    /// <summary>Removes every content widget on the current (or named) page. Does not remove the agent chat.</summary>
    internal AgentToolResult ClearWidgetsFromAgent(JsonElement args)
    {
        if (!TryAgentPage(args, out int pageIndex, out var error)) return error!;
        if (pageIndex != currentPageIndex) JumpToWorkspacePage(pageIndex);
        SyncWorkspaceForAgent();
        bool includeTimer = false;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("include_timer", out var flag)
            && flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
            includeTimer = flag.GetBoolean();

        var victims = Cards.Where(card => card.Kind != "agent").ToList();
        if (victims.Count == 0 && !(includeTimer && CurrentTimerWidget is not null))
            return AgentResult(true, EsPages()
                ? $"La página {pageIndex + 1} ya no tiene widgets de contenido."
                : $"Page {pageIndex + 1} already has no content widgets.");

        var names = victims.Select(card => $"{card.Kind}:{card.Title}").ToList();
        foreach (var card in victims) RemoveCard(card);
        if (includeTimer && CurrentTimerWidget is not null)
        {
            names.Add("timer:POMODORO");
            using var timerArgs = JsonDocument.Parse($"{{\"page\":{pageIndex + 1}}}");
            RemoveTimerFromAgent(timerArgs.RootElement);
        }
        return AgentResult(true, EsPages()
            ? $"Quité {names.Count} widget(s) de la página {pageIndex + 1}: {string.Join(", ", names)}."
            : $"Removed {names.Count} widget(s) from page {pageIndex + 1}: {string.Join(", ", names)}.");
    }

    internal void SyncWorkspaceForAgent() => SaveCurrentWorkspacePage();

    private static bool LooksLikeYouTube(WidgetConfig card) =>
        card.Kind == "web" && YouTubeWatch.IsYouTube(card.Value);

    internal IReadOnlyList<object> DescribeCurrentScreenWidgets()
    {
        SyncWorkspaceForAgent();
        return Cards.Where(card => card.Kind != "agent")
            .Select(card => (object)new
            {
                card.Id,
                card.Kind,
                card.Title,
                app = card.Kind == "window" ? card.Value : "",
                url = card.Kind == "web" ? card.Value : "",
                window_title = "",
                youtube = LooksLikeYouTube(card),
                browser = false,
                href = card.Href
            })
            .ToList();
    }

    /// <summary>The desktop's screen line, told truthfully for the browser: YouTube plays in a web widget, from its link.</summary>
    internal string DescribeCurrentScreenForAgent()
    {
        SyncWorkspaceForAgent();
        var widgets = Cards.Where(card => card.Kind != "agent").Select(DescribeWidgetLine).ToList();
        string list = widgets.Count == 0
            ? (EsPages() ? "(ninguno)" : "(none)")
            : string.Join("; ", widgets);
        string timer = CurrentTimerWidget is null ? "no" : (EsPages() ? "sí" : "yes");
        bool youtube = Cards.Any(LooksLikeYouTube);
        string head = EsPages()
            ? $"Página actual {currentPageIndex + 1}/{Settings.WorkspacePages.Count} «{CurrentPage.Name}»{(Settings.HomePageId == CurrentPage.Id ? " [casa]" : "")}. Widgets visibles ahora: {list}. Timer: {timer}."
            : $"Current page {currentPageIndex + 1}/{Settings.WorkspacePages.Count} “{CurrentPage.Name}”{(Settings.HomePageId == CurrentPage.Id ? " [home]" : "")}. Widgets visible now: {list}. Timer: {timer}.";
        string web = EsPages()
            ? " Esto es PomoDock Web, en el navegador: no hay Brave ni ventanas de programas que incrustar. workspace.play_youtube necesita la url del vídeo (un enlace de YouTube) y lo abre en un widget web, cerrando el que ya suena; con solo una búsqueda, pide el enlace."
            : " This is PomoDock Web, in the browser: there is no Brave and no program window to embed. workspace.play_youtube needs the video url (a YouTube link) and opens it in a web widget, closing the one already playing; with only a search, ask for the link.";
        if (youtube)
            return head + (EsPages() ? " Hay YouTube en esta pantalla." : " YouTube is on this screen.") + web;
        return head + web;
    }

    private string DescribeWidgetLine(WidgetConfig card)
    {
        string url = card.Kind == "web" ? card.Value : card.Href;
        string youtube = LooksLikeYouTube(card) ? (EsPages() ? " youtube=sí" : " youtube=yes") : " youtube=no";
        string extra = url.Length > 0 ? $" url={url}" : "";
        string app = card.Kind == "window" && card.Value.Length > 0 ? $" [{card.Value}]" : "";
        return $"{card.Kind}:{card.Title}{app}{extra}{youtube}";
    }

    internal readonly record struct RetiredPlayback(bool Any, bool Fill);

    /// <summary>The web widgets that are playing YouTube on this page leave before the next song arrives.</summary>
    internal RetiredPlayback RetirePlaybackWidgets()
    {
        SyncWorkspaceForAgent();
        var victims = Cards.Where(LooksLikeYouTube).ToList();
        if (victims.Count == 0) return new RetiredPlayback(false, false);
        bool fill = victims.Any(card => card.CanvasFill);
        foreach (var card in victims) RemoveCard(card);
        return new RetiredPlayback(true, fill);
    }

    internal WidgetConfig? FindUrlWidgetOnCurrentScreen()
    {
        SyncWorkspaceForAgent();
        return Cards.FirstOrDefault(LooksLikeYouTube) ?? Cards.FirstOrDefault(item => item.Kind == "web");
    }

    internal AgentToolResult NavigateWidgetUrl(WidgetConfig card, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            return AgentResult(false, EsPages() ? "La URL debe ser http(s)." : "URL must be http(s).");
        if (card.Kind != "web")
            return AgentResult(false, EsPages() ? "Ese widget no es web." : "That widget is not a web widget.");
        if (uri.Scheme != "https")
            return AgentResult(false, EsPages() ? "La URL debe ser https." : "URL must be https.");
        card.Value = uri.AbsoluteUri;
        SaveState();
        return AgentResult(true, EsPages() ? $"URL: {card.Value}" : $"URL set: {card.Value}");
    }

    internal AgentToolResult RenameWidgetFromAgent(JsonElement args)
    {
        var card = FindAgentCard(args);
        if (card is null) return AgentResult(false, L.T("agent.widgetMissing"));
        string previous = card.Title;
        string title = TextArg(args, "new_title");
        if (title.Length == 0) title = TextArg(args, "name");
        if (title.Length == 0) return AgentResult(false, EsPages() ? "Falta el nuevo título." : "Missing the new title.");
        card.Title = title;
        SaveState();
        return AgentResult(true, EsPages() ? $"Renombré «{previous}» a «{title}»." : $"Renamed “{previous}” to “{title}”.");
    }

    internal AgentToolResult CollapseWidgetFromAgent(JsonElement args)
    {
        var card = FindAgentCard(args);
        if (card is null) return AgentResult(false, L.T("agent.widgetMissing"));
        bool collapsed = true;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("collapsed", out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            collapsed = value.GetBoolean();
        if (collapsed && card.CanvasFill) card.RestoreFromCanvasFill();
        card.Collapsed = collapsed;
        SaveState();
        return AgentResult(true, collapsed
            ? (EsPages() ? $"Contraté «{card.Title}»." : $"Collapsed “{card.Title}”.")
            : (EsPages() ? $"Expandí «{card.Title}»." : $"Expanded “{card.Title}”."));
    }

    internal AgentToolResult FillWidgetFromAgent(JsonElement args)
    {
        bool fill = true;
        if (args.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "fill", "on", "fullscreen", "canvas_fill" })
            {
                if (args.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    fill = value.GetBoolean();
                    break;
                }
            }
        }
        string kind = TextArg(args, "kind").ToLowerInvariant();
        string title = TextArg(args, "title");
        if (kind is "timer" or "pomodoro" or "temporizador"
            || title.Contains("pomodoro", StringComparison.OrdinalIgnoreCase)
            || title.Contains("temporizador", StringComparison.OrdinalIgnoreCase))
        {
            if (CurrentTimerWidget is null) return AgentResult(false, EsPages() ? "No hay pomodoro en esta página." : "No pomodoro on this page.");
            SetTimerCanvasFill(fill);
            AgentCreatedWidgetThisTurn = false;
            return AgentResult(true, fill
                ? (EsPages() ? "Pomodoro a pantalla de lienzo." : "Pomodoro filled the canvas.")
                : (EsPages() ? "Restauré el pomodoro." : "Restored the pomodoro."));
        }
        var card = FindAgentCard(args)
            ?? (LastCreatedWidgetId is { } id ? Cards.FirstOrDefault(item => item.Id == id) : null)
            ?? Cards.LastOrDefault(item => item.Kind is "window" or "web")
            ?? Cards.LastOrDefault();
        if (card is null) return AgentResult(false, L.T("agent.widgetMissing"));
        if (card == AgentConfig)
        {
            if (fill) card.FillCanvas(canvasWidth, canvasHeight);
            else if (card.CanvasFill) card.RestoreFromCanvasFill();
            SaveState();
        }
        else FillCardFromAgent(card, fill);
        AgentCreatedWidgetThisTurn = false;
        return AgentResult(true, fill
            ? (EsPages() ? $"«{card.Title}» a pantalla de lienzo." : $"“{card.Title}” filled the canvas.")
            : (EsPages() ? $"Restauré «{card.Title}»." : $"Restored “{card.Title}”."));
    }

    internal AgentToolResult ResizeWidgetFromAgent(JsonElement args)
    {
        if (IsTimerAgentTarget(args))
            return ResizeTimerFromAgent(args);
        var card = FindAgentCard(args);
        if (card is null) return AgentResult(false, L.T("agent.widgetMissing"));
        double width = card.Width, height = card.Height;
        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("width", out var w) && w.TryGetDouble(out double left)) width = left;
            if (args.TryGetProperty("height", out var h) && h.TryGetDouble(out double top)) height = top;
        }
        double minWidth = card.Kind == "agent" ? 260 : 220, minHeight = card.Kind == "agent" ? 280 : 90;
        card.Width = Math.Clamp(width, minWidth, Math.Max(minWidth, canvasWidth));
        card.Height = Math.Clamp(height, minHeight, Math.Max(minHeight, canvasHeight));
        if (card.CanvasFill) card.DropCanvasFillIfBoundsMoved(canvasWidth, canvasHeight);
        ArrangeCards();
        SaveState();
        return AgentResult(true, EsPages()
            ? $"Redimensioné «{card.Title}» a {card.Width:0}×{card.Height:0}."
            : $"Resized “{card.Title}” to {card.Width:0}×{card.Height:0}.");
    }

    internal bool IsTimerAgentTarget(JsonElement args)
    {
        string kind = TextArg(args, "kind").ToLowerInvariant();
        string title = TextArg(args, "title");
        if (kind is "timer" or "pomodoro" or "temporizador") return true;
        if (title.Length == 0) return false;
        bool named = title.Contains("pomodoro", StringComparison.OrdinalIgnoreCase)
            || title.Contains("temporizador", StringComparison.OrdinalIgnoreCase)
            || title.Equals("timer", StringComparison.OrdinalIgnoreCase);
        if (!named) return false;
        return !Settings.WorkspacePages.SelectMany(page => page.Widgets)
            .Any(widget => widget.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
    }

    private bool EnsureTimerOnCurrentPage()
    {
        if (CurrentTimerWidget is not null) return true;
        int page = Settings.WorkspacePages.FindIndex(item => item.TimerWidget is not null);
        if (page < 0) return false;
        if (page != currentPageIndex) JumpToWorkspacePage(page);
        return CurrentTimerWidget is not null;
    }

    internal AgentToolResult MoveTimerFromAgent(JsonElement args)
    {
        if (!EnsureTimerOnCurrentPage())
            return AgentResult(false, EsPages() ? "No hay pomodoro en el tablero." : "No pomodoro on the board.");
        var config = CurrentTimerWidget!;
        double previousX = config.X, previousY = config.Y;
        if (args.TryGetProperty("x", out var x) && x.TryGetDouble(out double left)) config.X = left;
        if (args.TryGetProperty("y", out var y) && y.TryGetDouble(out double top)) config.Y = top;
        CurrentPage.TimerPositionCustomized = true;
        ArrangeCards();
        SaveState();
        return new(true,
            EsPages() ? $"Pomodoro movido a {config.X:0},{config.Y:0}." : $"Pomodoro moved to {config.X:0},{config.Y:0}.",
            JsonSerializer.Serialize(new { success = true, changed = true, summary = $"Pomodoro movido: {config.X:0},{config.Y:0}", data = new { config.Id, config.X, config.Y } }),
            true, "workspace.move_widget",
            JsonSerializer.Serialize(new { kind = "timer", x = previousX, y = previousY }));
    }

    internal AgentToolResult ResizeTimerFromAgent(JsonElement args)
    {
        if (!EnsureTimerOnCurrentPage())
            return AgentResult(false, EsPages() ? "No hay pomodoro en el tablero." : "No pomodoro on the board.");
        var config = CurrentTimerWidget!;
        double previousW = config.Width, previousH = config.Height;
        if (args.TryGetProperty("width", out var w) && w.TryGetDouble(out double width)) config.Width = width;
        if (args.TryGetProperty("height", out var h) && h.TryGetDouble(out double height)) config.Height = height;
        CurrentPage.TimerPositionCustomized = true;
        ArrangeCards();
        SaveState();
        return new AgentToolResult(true,
            EsPages() ? $"Pomodoro a {config.Width:0}×{config.Height:0}." : $"Pomodoro sized {config.Width:0}×{config.Height:0}.",
            JsonSerializer.Serialize(new { success = true, changed = true, summary = $"Pomodoro {config.Width:0}×{config.Height:0}", data = new { config.Id, config.Width, config.Height } }),
            true, "workspace.resize_widget",
            JsonSerializer.Serialize(new { kind = "timer", width = previousW, height = previousH }));
    }

    internal AgentToolResult SetTodoFilterFromAgent(JsonElement args)
    {
        string filter = TextArg(args, "filter");
        if (filter.Length == 0) filter = TextArg(args, "view");
        var card = FindAgentCard(args) ?? Cards.FirstOrDefault(item => item.Kind == "todo");
        if (card is null)
            return AgentResult(false, EsPages() ? "No hay widget de tareas en esta página." : "No to-do widget on this page.");
        if (Live<TodoBoard>(card.Id)?.ApplyFilter(filter) != true)
            return AgentResult(false, EsPages() ? "Filtro inválido. Usa all, open, today o done." : "Invalid filter. Use all, open, today, or done.");
        return AgentResult(true, EsPages() ? $"Filtro de tareas: {filter}." : $"To-do filter: {filter}.");
    }

    internal AgentToolResult NavigateHabitsFromAgent(JsonElement args)
    {
        string to = TextArg(args, "to");
        if (to.Length == 0) to = TextArg(args, "when");
        if (to.Length == 0) to = TextArg(args, "direction");
        var card = FindAgentCard(args) ?? Cards.FirstOrDefault(item => item.Kind == "habits");
        if (card is null)
            return AgentResult(false, EsPages() ? "No hay widget de hábitos en esta página." : "No habits widget on this page.");
        if (Live<HabitsBoard>(card.Id)?.ApplyNavigate(to) != true)
            return AgentResult(false, EsPages() ? "Destino inválido. Usa prev, next o today." : "Invalid target. Use prev, next, or today.");
        return AgentResult(true, EsPages() ? $"Hábitos en: {to}." : $"Habits at: {to}.");
    }

    internal AgentToolResult SetFinanceViewFromAgent(JsonElement args)
    {
        string span = TextArg(args, "span");
        if (span.Length == 0) span = TextArg(args, "period");
        string tab = TextArg(args, "tab");
        if (tab.Length == 0) tab = TextArg(args, "view");
        string to = TextArg(args, "to");
        if (to.Length == 0) to = TextArg(args, "when");
        if (to.Length == 0) to = TextArg(args, "direction");
        var card = FindAgentCard(args) ?? Cards.FirstOrDefault(item => item.Kind == "finance");
        if (card is null)
            return AgentResult(false, EsPages() ? "No hay widget de finanzas en esta página." : "No finance widget on this page.");
        if (Live<FinanceBoard>(card.Id)?.ApplyView(span, tab, to) != true)
            return AgentResult(false, EsPages()
                ? "Vista inválida. span: week|month|quarter|year. tab: moves|fixed|subs. to: prev|next|today|yyyy-MM."
                : "Invalid view. span: week|month|quarter|year. tab: moves|fixed|subs. to: prev|next|today|yyyy-MM.");
        return AgentResult(true, EsPages() ? "Actualicé la vista de finanzas." : "Updated the finance view.");
    }

    internal AgentToolResult ReloadWebFromAgent(JsonElement args)
    {
        var card = FindAgentCard(args) ?? Cards.FirstOrDefault(item => item.Kind == "web");
        if (card is null || card.Kind != "web") return AgentResult(false, EsPages() ? "No hay widget web." : "No web widget.");
        Live<WidgetCard>(card.Id)?.ReloadWeb();
        return AgentResult(true, EsPages() ? $"Recargué «{card.Title}»." : $"Reloaded “{card.Title}”.");
    }

    internal AgentToolResult FullscreenFromAgent(JsonElement args)
    {
        string target = TextArg(args, "target").ToLowerInvariant();
        bool widgetTarget = target is "widget" or "canvas" or "lienzo" or "card";
        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("title", out var titled) && titled.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(titled.GetString()))
                widgetTarget = true;
            if (args.TryGetProperty("kind", out var kinded) && kinded.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(kinded.GetString()))
                widgetTarget = true;
        }
        if (widgetTarget || AgentCreatedWidgetThisTurn)
            return FillWidgetFromAgent(args);

        bool? want = null;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("on", out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            want = value.GetBoolean();
        // The browser reports the change later; the answer names the state that was asked for.
        bool next = want ?? !fullscreen;
        if (next != fullscreen) Browser.ToggleFullscreen();
        return AgentResult(true, next
            ? (EsPages() ? "Pantalla completa de PomoDock (F11)." : "PomoDock fullscreen (F11).")
            : (EsPages() ? "Pantalla completa desactivada." : "Fullscreen off."));
    }

    // ------------------------------------------------------------------ notifications and reminders

    internal AgentToolResult NotificationsFromAgent(JsonElement args)
    {
        string command = TextArg(args, "command").ToLowerInvariant();
        if (command is "dismiss" or "read" or "mark_read")
        {
            notifications.MarkAllRead();
            alerts.Clear();
            return AgentResult(true, EsPages() ? "Notificaciones marcadas como leídas." : "Notifications marked read.");
        }
        var items = notifications.History.Items.Take(20).Select(item => new
        {
            item.Id, item.Title, item.Read, item.ScheduledLocal, item.DeliveredLocal
        }).ToList();
        string summary = EsPages()
            ? $"{items.Count} notificaciones · {notifications.History.Unread} sin leer"
            : $"{items.Count} notifications · {notifications.History.Unread} unread";
        return new AgentToolResult(false, summary, JsonSerializer.Serialize(new { success = true, changed = false, summary, data = items }), true);
    }

    internal AgentToolResult DismissNotificationsFromAgent(JsonElement args)
    {
        string idText = TextArg(args, "id");
        if (idText.Length > 0 && Guid.TryParse(idText, out var id))
        {
            if (!notifications.MarkRead(id))
                return AgentResult(false, EsPages() ? "No encontré esa notificación." : "Notification not found.");
            alerts.RemoveAll(card => card.Id == id);
            return AgentResult(true, EsPages() ? "Notificación marcada como leída." : "Notification marked read.");
        }
        notifications.MarkAllRead();
        alerts.Clear();
        return AgentResult(true, EsPages() ? "Notificaciones marcadas como leídas." : "Notifications marked read.");
    }

    internal AgentToolResult SnoozeTimerAlertFromAgent(int minutes)
    {
        minutes = Math.Clamp(minutes <= 0 ? 5 : minutes, 1, 120);
        if (!timerAlertArmed && timerAlertSnooze is null)
            return AgentResult(false, EsPages() ? "No hay una alerta de pomodoro que posponer." : "There is no pomodoro alert to snooze.");
        SnoozeTimerAlert(minutes, lastTimerAlertCompleted, lastTimerAlertNext);
        alerts.RemoveAll(card => card.CueKey.Length == 0);
        return AgentResult(true, EsPages()
            ? $"Pospué la alerta de pomodoro {minutes} minutos."
            : $"Snoozed the pomodoro alert for {minutes} minutes.");
    }

    /// <summary>AgendaReminders.TrySnooze: the reminder on screen first, then the nearest one that matches.</summary>
    internal AgentToolResult SnoozeCalendarFromAgent(JsonElement args)
    {
        string title = TextArg(args, "title");
        if (title.Length == 0) title = TextArg(args, "name");
        int minutes = 5;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("minutes", out var value) && value.TryGetInt32(out int parsed))
            minutes = parsed;
        minutes = Math.Clamp(minutes <= 0 ? 5 : minutes, 1, 120);
        var book = Agenda.Book;
        ReminderCue? cue = alerts.LastOrDefault(card => card.CueKey.Length > 0 && (title.Length == 0 || card.Title.Contains(title, StringComparison.OrdinalIgnoreCase))) is { } shown
            ? book.CueFor(shown.CueKey)
            : null;
        if (cue is null)
        {
            var now = DateTime.Now;
            cue = book.Cues(now.AddHours(-12), now.AddHours(12))
                .Where(item => title.Length == 0 || item.Event.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => book.Delivered.Contains(item.Key, StringComparer.Ordinal) || book.Snoozed.ContainsKey(item.Key))
                .ThenBy(item => item.Start)
                .FirstOrDefault();
        }
        if (cue is null && title.Length > 0)
        {
            var match = book.Events.FirstOrDefault(item => item.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                var day = DateOnly.FromDateTime(DateTime.Now);
                int offset = match.Reminders.Count > 0 ? match.Reminders[0] : 0;
                cue = new ReminderCue(match, day, match.AnchorFor(day), offset, match.AnchorFor(day).AddMinutes(-offset));
            }
        }
        if (cue is null)
            return AgentResult(false, EsPages() ? "No encontré un recordatorio que posponer." : "No reminder to snooze.");
        SnoozeReminder(cue, minutes);
        alerts.RemoveAll(card => card.CueKey == cue.Key);
        return AgentResult(true, EsPages()
            ? $"Pospué «{cue.Event.Title}» {minutes} minutos."
            : $"Snoozed “{cue.Event.Title}” for {minutes} minutes.");
    }

    // ------------------------------------------------------------------ calendar, panels, rhythm, layouts

    internal AgentToolResult SetCalendarViewFromAgent(JsonElement args)
    {
        string view = TextArg(args, "view");
        if (view.Length == 0) view = TextArg(args, "mode");
        var card = FindAgentCard(args) ?? Cards.FirstOrDefault(item => item.Kind == "calendar");
        if (card is null)
            return AgentResult(false, EsPages() ? "No hay widget de calendario en esta página." : "No calendar widget on this page.");
        string key = view.Trim().ToLowerInvariant() switch
        {
            "mes" or "month" or "m" => "month",
            "sem" or "semana" or "week" or "w" => "week",
            "lista" or "agenda" or "list" or "a" => "agenda",
            _ => view.Trim().ToLowerInvariant()
        };
        if (key is not ("month" or "week" or "agenda") || Live<CalendarWidget>(card.Id) is not { } calendar)
            return AgentResult(false, EsPages() ? "Vista inválida. Usa month, week o agenda." : "Invalid view. Use month, week, or agenda.");
        calendar.ApplyView(key);
        return AgentResult(true, EsPages() ? $"Vista del calendario: {view}." : $"Calendar view: {view}.");
    }

    internal AgentToolResult NavigateCalendarFromAgent(JsonElement args)
    {
        string to = TextArg(args, "to");
        if (to.Length == 0) to = TextArg(args, "when");
        if (to.Length == 0) to = TextArg(args, "direction");
        var card = FindAgentCard(args) ?? Cards.FirstOrDefault(item => item.Kind == "calendar");
        if (card is null)
            return AgentResult(false, EsPages() ? "No hay widget de calendario." : "No calendar widget.");
        if (Live<CalendarWidget>(card.Id)?.ApplyNavigate(to) != true)
            return AgentResult(false, EsPages() ? "Destino inválido. Usa prev, next, today o yyyy-MM." : "Invalid target. Use prev, next, today, or yyyy-MM.");
        return AgentResult(true, EsPages() ? $"Calendario en: {to}." : $"Calendar at: {to}.");
    }

    internal AgentToolResult SetCalendarShowDoneFromAgent(JsonElement args)
    {
        bool show = true;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("show", out var flag)
            && flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
            show = flag.GetBoolean();
        else if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("show_done", out var alt)
            && alt.ValueKind is JsonValueKind.True or JsonValueKind.False)
            show = alt.GetBoolean();
        var card = FindAgentCard(args) ?? Cards.FirstOrDefault(item => item.Kind == "calendar");
        if (card is null || Live<CalendarWidget>(card.Id) is not { } calendar)
            return AgentResult(false, EsPages() ? "No hay widget de calendario." : "No calendar widget.");
        calendar.ApplyShowDone(show);
        return AgentResult(true, show
            ? (EsPages() ? "Muestro hechos en el calendario." : "Showing completed calendar items.")
            : (EsPages() ? "Oculto hechos en el calendario." : "Hiding completed calendar items."));
    }

    internal AgentToolResult HideAgentFromAgent()
    {
        if (!AgentVisible)
            return AgentResult(true, EsPages() ? "El chat del agente ya estaba oculto." : "The agent chat was already hidden.");
        HideAgentWidget();
        return AgentResult(true, EsPages() ? "Oculté el chat del agente." : "Hid the agent chat.");
    }

    internal AgentToolResult OpenPanelFromAgent(JsonElement args)
    {
        string panel = TextArg(args, "panel");
        if (panel.Length == 0) panel = TextArg(args, "name");
        panel = panel.Trim().ToLowerInvariant();
        switch (panel)
        {
            case "settings" or "ajustes" or "config":
                settingsOpen = true;
                return AgentResult(true, EsPages() ? "Abrí ajustes." : "Opened settings.");
            case "tasks" or "tareas" or "focus_tasks" or "enfoque":
                tasksOpen = true;
                return AgentResult(true, EsPages() ? "Abrí tareas de enfoque." : "Opened focus tasks.");
            case "layouts" or "layout":
                return ListLayoutsFromAgent();
            case "report" or "informe" or "historial":
                if (!BoolArg(args, "open", true))
                {
                    reportOpen = false;
                    return AgentResult(true, EsPages() ? "Cerré el informe." : "Closed the report.");
                }
                reportOpen = true;
                return AgentResult(true, EsPages() ? "Abrí el informe." : "Opened the report.");
            case "agent" or "agente":
                if (!BoolArg(args, "open", true)) return HideAgentFromAgent();
                ShowAgentWidget();
                return AgentResult(true, EsPages() ? "Abrí el agente." : "Opened the agent.");
            case "notifications" or "notificaciones" or "inbox":
                if (!BoolArg(args, "open", true))
                {
                    CloseNotificationCenter();
                    return AgentResult(true, EsPages() ? "Cerré las notificaciones." : "Closed notifications.");
                }
                notificationsOpen = true;
                return AgentResult(true, EsPages() ? "Abrí las notificaciones." : "Opened notifications.");
            case "page_map" or "mapa" or "pages" or "map" or "paginas" or "páginas":
                if (BoolArg(args, "open", true)) ShowPageMap();
                else HidePageMap();
                return AgentResult(true, pageMapOpen
                    ? (EsPages() ? "Abrí el mapa de páginas." : "Opened the page map.")
                    : (EsPages() ? "Cerré el mapa de páginas." : "Closed the page map."));
            default:
                return AgentResult(false, EsPages()
                    ? "Panel desconocido. Usa: settings, tasks, layouts, report, agent, notifications, page_map. open:false cierra informe, notificaciones, agente o mapa."
                    : "Unknown panel. Use: settings, tasks, layouts, report, agent, notifications, page_map. open:false closes report, notifications, agent, page_map.");
        }
    }

    /// <summary>The web has no folder to open: the data lives in this browser and leaves it as a backup file.</summary>
    internal AgentToolResult OpenDataFolderFromAgent() =>
        AgentResult(false, EsPages()
            ? "En la web tus datos viven en este navegador (localStorage), no en una carpeta. Para sacarlos usa Ajustes › Datos › Exportar."
            : "On the web your data lives in this browser (localStorage), not in a folder. To take it out use Settings › Data › Export.");

    internal AgentToolResult ApplyRhythmFromAgent(JsonElement args)
    {
        string name = TextArg(args, "name");
        if (name.Length == 0) name = TextArg(args, "rhythm");
        name = name.Trim().ToLowerInvariant();
        var preset = name switch
        {
            "classic" or "clasico" or "clásico" or "25" => (25, 5, 15, 4, "classic"),
            "deep" or "profundo" or "50" => (50, 10, 20, 3, "deep"),
            "sprint" or "15" => (15, 3, 10, 4, "sprint"),
            "marathon" or "maraton" or "maratón" or "90" => (90, 20, 30, 2, "marathon"),
            _ => (0, 0, 0, 0, "")
        };
        if (preset.Item5.Length == 0)
            return AgentResult(false, EsPages()
                ? "Ritmo desconocido. Usa classic, deep, sprint o marathon."
                : "Unknown rhythm. Use classic, deep, sprint, or marathon.");
        Settings.FocusMinutes = preset.Item1;
        Settings.ShortMinutes = preset.Item2;
        Settings.LongMinutes = preset.Item3;
        Settings.LongInterval = preset.Item4;
        ApplyLiveSettings();
        SaveState();
        return AgentResult(true, EsPages()
            ? $"Ritmo {preset.Item5}: {preset.Item1} · {preset.Item2} · {preset.Item3}."
            : $"Rhythm {preset.Item5}: {preset.Item1} · {preset.Item2} · {preset.Item3}.");
    }

    internal WidgetConfig? ResolveNotesCardForAgent(string title)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { title, kind = "notes" }));
        return FindAgentCard(doc.RootElement);
    }

    internal AgentToolResult SaveLayoutFromAgent(JsonElement args)
    {
        string name = TextArg(args, "name");
        if (name.Length == 0) name = TextArg(args, "title");
        if (name.Length == 0) return AgentResult(false, EsPages() ? "Falta el nombre del layout." : "Missing layout name.");
        SaveState();
        var snapshot = JsonSerializer.Deserialize<List<WidgetConfig>>(JsonSerializer.Serialize(CurrentPage.Widgets)) ?? [];
        Settings.Layouts[name] = snapshot;
        SaveState();
        return AgentResult(true, EsPages() ? $"Layout guardado: {name}" : $"Layout saved: {name}");
    }

    internal AgentToolResult LoadLayoutFromAgent(JsonElement args)
    {
        string name = TextArg(args, "name");
        if (name.Length == 0) name = TextArg(args, "title");
        if (name.Length == 0 || !Settings.Layouts.TryGetValue(name, out var layout))
            return AgentResult(false, EsPages() ? "No encontré ese layout." : "Layout not found.");
        foreach (var card in Cards.ToArray()) RemoveCard(card);
        var copy = JsonSerializer.Deserialize<List<WidgetConfig>>(JsonSerializer.Serialize(layout))!;
        foreach (var config in copy) { config.Id = Guid.NewGuid(); AddCard(config, false); }
        SaveState();
        Status(L.T("status.layoutLoaded"));
        return AgentResult(true, EsPages() ? $"Layout cargado: {name}" : $"Layout loaded: {name}");
    }

    internal AgentToolResult ListLayoutsFromAgent()
    {
        var names = Settings.Layouts.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        string summary = names.Count == 0
            ? (EsPages() ? "No hay layouts guardados." : "No saved layouts.")
            : string.Join(", ", names);
        return new AgentToolResult(false, summary, JsonSerializer.Serialize(new { success = true, changed = false, summary, data = names }), true);
    }

    internal AgentToolResult DeleteLayoutFromAgent(JsonElement args)
    {
        string name = TextArg(args, "name");
        if (name.Length == 0) name = TextArg(args, "title");
        if (name.Length == 0 || !Settings.Layouts.ContainsKey(name))
            return AgentResult(false, EsPages() ? "No encontré ese layout." : "Layout not found.");
        Settings.Layouts.Remove(name);
        SaveState();
        return AgentResult(true, EsPages() ? $"Layout borrado: {name}" : $"Layout deleted: {name}");
    }

    // ------------------------------------------------------------------ the timer

    internal AgentToolResult RunTimerFromAgent(string command, string phase, string task, int minutes = 5)
    {
        command = command.Trim().ToLowerInvariant();
        if (command is "select" or "start")
        {
            var selectedPhase = phase.Trim().ToLowerInvariant() switch
            {
                "short" or "shortbreak" => Phase.ShortBreak,
                "long" or "longbreak" => Phase.LongBreak,
                _ => Phase.Focus
            };
            if (selectedPhase != Timer.Phase) Timer.Select(selectedPhase, DateTimeOffset.UtcNow, Monotonic);
        }
        if (!string.IsNullOrWhiteSpace(task))
        {
            if (task.Trim().Equals("free", StringComparison.OrdinalIgnoreCase)
                || task.Trim().Equals("libre", StringComparison.OrdinalIgnoreCase)
                || task.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)
                || task.Trim().Equals("clear", StringComparison.OrdinalIgnoreCase))
                SetSelectedTask(null);
            else
            {
                var matches = Settings.Tasks.Where(item => !item.Done && item.Name.Contains(task, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1)
                    return new AgentToolResult(false, $"Necesito un título de tarea de enfoque más específico para '{task}'", JsonSerializer.Serialize(new { success = false, changed = false, summary = $"Coincidencias de tarea: {matches.Count}" }), false);
                SetSelectedTask(matches[0]);
            }
        }
        string summary;
        switch (command)
        {
            case "pause":
                Timer.Pause(DateTimeOffset.UtcNow, Monotonic); Sounds.StopNoise(); summary = "Temporizador pausado"; break;
            case "reset":
                Sounds.StopNoise(); Timer.Select(Timer.Phase, DateTimeOffset.UtcNow, Monotonic); summary = "Temporizador reiniciado"; break;
            case "skip":
                Sounds.StopNoise(); Timer.Select(Timer.NextPhase(), DateTimeOffset.UtcNow, Monotonic); summary = "Fase omitida"; break;
            case "select":
                summary = "Fase seleccionada"; break;
            case "free" or "clear_task":
                SetSelectedTask(null);
                summary = "Enfoque libre"; break;
            case "snooze" or "posponer":
                return SnoozeTimerAlertFromAgent(minutes);
            case "start":
                if (!Timer.Running)
                {
                    AcknowledgeTimerAlerts();
                    Timer.Start(DateTimeOffset.UtcNow, Monotonic, selectedTask);
                    if (Timer.Phase == Phase.Focus) Sounds.StartNoise();
                }
                summary = "Temporizador iniciado"; break;
            default:
                return new AgentToolResult(false, $"Comando de temporizador desconocido: {command}", JsonSerializer.Serialize(new { success = false, changed = false, summary = "Comando desconocido" }), false);
        }
        UpdateTimer(); SaveState();
        return new AgentToolResult(true, summary, JsonSerializer.Serialize(new { success = true, changed = true, summary, phase = Timer.Phase.ToString(), Timer.Running }));
    }

    // ------------------------------------------------------------------ finding things

    private static string KindAlias(string kind) => kind switch
    {
        "nota" or "note" or "notes" => "notes",
        "tareas" or "todo" => "todo",
        "hábito" or "habitos" or "hábitos" or "habits" => "habits",
        "rutina" or "rutinas" or "workout" or "ejercicio" or "ejercicios" or "gym" or "mancuernas" => "workout",
        "agenda" or "calendar" => "calendar",
        "finanzas" or "finance" => "finance",
        "stats" or "enfoque" => "stats",
        "memento" or "memento_mori" or "mementomori" or "mortalidad" => "memento",
        _ => kind
    };

    internal WidgetConfig? FindAgentCard(JsonElement args)
    {
        string title = TextArg(args, "title");
        string kind = TextArg(args, "kind").ToLowerInvariant() switch
        {
            "web" or "navegador" => "web",
            "ventana" or "window" => "window",
            "agent" or "agente" or "chat" => "agent",
            var other => KindAlias(other)
        };
        if (title.Length == 0 && kind.Length == 0) return null;

        bool Matches(WidgetConfig widget) =>
            (title.Length > 0 && widget.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
            || (kind.Length > 0 && widget.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));

        // Prefer an explicit page when provided; otherwise search every page and jump.
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("page", out _)
            && TryAgentPage(args, out int pageIndex, out _)
            && pageIndex >= 0)
        {
            if (pageIndex != currentPageIndex) JumpToWorkspacePage(pageIndex);
            SyncWorkspaceForAgent();
            return Cards.FirstOrDefault(Matches);
        }

        for (int index = 0; index < Settings.WorkspacePages.Count; index++)
        {
            var match = Settings.WorkspacePages[index].Widgets.FirstOrDefault(Matches);
            if (match is null) continue;
            if (index != currentPageIndex) JumpToWorkspacePage(index);
            SyncWorkspaceForAgent();
            return match;
        }

        if (AgentVisible && Matches(AgentConfig!)) return AgentConfig;
        if (kind == "agent" && AgentVisible) return AgentConfig;
        return Cards.FirstOrDefault(Matches);
    }

    private bool TryAgentPage(JsonElement args, out int index, out AgentToolResult? error)
    {
        index = currentPageIndex;
        error = null;
        int page = currentPageIndex + 1;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("page", out var value))
        {
            if (value.ValueKind == JsonValueKind.Null) return true;
            if (value.TryGetInt32(out int number)) page = number;
            else if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)) page = number;
        }
        index = page - 1;
        if (index < 0 || index >= Settings.WorkspacePages.Count)
        {
            error = AgentResult(false, L.T("agent.pageMissing", page));
            return false;
        }
        return true;
    }

    private static string TextArg(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? "" : "";

    private static bool EsPages() => Strings.Culture.TwoLetterISOLanguageName == "es";

    private static AgentToolResult AgentResult(bool changed, string summary) =>
        new(changed, summary, JsonSerializer.Serialize(new { success = true, changed, summary }), true);
}
