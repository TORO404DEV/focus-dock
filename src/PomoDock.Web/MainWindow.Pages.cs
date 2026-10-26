using System.Globalization;
using System.Text.Json;
using Microsoft.JSInterop;
using PomoDock.Core;

namespace PomoDock.Web;

/// <summary>Where a widget sits on the canvas, already clamped the way the desktop arranges it.</summary>
public readonly record struct CardBox(double X, double Y, double Width, double Height)
{
    public string Style(int z) => string.Create(CultureInfo.InvariantCulture,
        $"left:{X:0.##}px;top:{Y:0.##}px;width:{Width:0.##}px;height:{Height:0.##}px;z-index:{z}");
}

public partial class MainWindow
{
    /// <summary>Pages drawn this session. Their widgets stay alive while parked, as the desktop keeps their cards.</summary>
    private readonly HashSet<Guid> livePages = [];
    private int currentPageIndex;
    private bool pageTransitioning;
    private int swipeDx;
    private int swipeDy;
    private int? swipeTargetIndex;
    private bool swipeCreatesPage;
    private bool swipeDestinationAvailable;
    private bool animateCarouselAfterRender;
    private bool resetCarouselAfterRender;
    private bool pulsePageMap;

    private WorkspacePage CurrentPage => Settings.WorkspacePages[currentPageIndex];
    private WidgetConfig? CurrentTimerWidget => CurrentPage.TimerWidget;
    internal bool CanAddTimerWidget => CurrentTimerWidget is null;
    internal double CanvasWidth => canvasWidth;
    internal double CanvasHeight => canvasHeight;

    /// <summary>The incoming page waits one viewport away, on the side it will slide in from.</summary>
    private string IncomingStyle => swipeDx != 0
        ? $"transform:translate3d({swipeDx * 100}%,0,0)"
        : $"transform:translate3d(0,{swipeDy * 100}%,0)";

    private IEnumerable<WorkspacePage> LivePages() => Settings.WorkspacePages.Where(page => livePages.Contains(page.Id));

    /// <summary>
    /// A blank neighbor can open in any empty cardinal cell next to the current page, but only
    /// when this page already holds something. That keeps one blank canvas per edge of the live
    /// page and never stacks two empty pages in a row from the same spot.
    /// </summary>
    private bool CanCreateWorkspacePage(int dx, int dy)
    {
        if ((dx == 0 && dy == 0) || (dx != 0 && dy != 0)) return false;
        if (!CurrentPage.HasContent) return false;
        return FindPageIndex(CurrentPage.Col + dx, CurrentPage.Row + dy) is null;
    }

    private int? FindPageIndex(int col, int row)
    {
        for (int i = 0; i < Settings.WorkspacePages.Count; i++)
        {
            var page = Settings.WorkspacePages[i];
            if (page.Col == col && page.Row == row) return i;
        }
        return null;
    }

    private static (int Dx, int Dy) StepToward(WorkspacePage from, WorkspacePage to)
    {
        int dx = Math.Sign(to.Col - from.Col);
        int dy = Math.Sign(to.Row - from.Row);
        if (dx != 0 && dy != 0)
        {
            // Jumping to a diagonal neighbour via the map: prefer the larger axis so the slide
            // still reads as a single-direction carousel move.
            return Math.Abs(to.Col - from.Col) >= Math.Abs(to.Row - from.Row) ? (dx, 0) : (0, dy);
        }
        if (dx == 0 && dy == 0) return (1, 0);
        return (dx, dy);
    }

    /// <summary>
    /// A dialog owns the workspace while it is visible. Keep every navigation entry point
    /// behind the same gate so swipe, keyboard and the page map behave alike.
    /// </summary>
    private bool IsWorkspaceNavigationBlocked => dialogs.Count > 0 || settingsOpen || tasksOpen || notificationsOpen;

    private void InitializeWorkspacePages()
    {
        currentPageIndex = Math.Clamp(Settings.ActiveWorkspacePage, 0, Settings.WorkspacePages.Count - 1);
        livePages.Add(CurrentPage.Id);
    }

    private void SaveCurrentWorkspacePage()
    {
        Settings.ActiveWorkspacePage = currentPageIndex;
        Settings.Widgets = CurrentPage.Widgets;
        Settings.TimerWidget = CurrentTimerWidget ?? new WidgetConfig { Kind = "timer", Title = "POMODORO" };
        Settings.TimerPositionCustomized = CurrentTimerWidget is not null && CurrentPage.TimerPositionCustomized;
    }

    // ------------------------------------------------------------------ placing widgets

    /// <summary>Same clamps as ArrangeCards: never narrower than the minimum, never off the canvas.</summary>
    internal CardBox CardRect(WidgetConfig config)
    {
        config.ApplyCanvasFillIfNeeded(canvasWidth, canvasHeight);
        double minW = Math.Min(config.Kind == "agent" ? 260 : 220, canvasWidth);
        double minH = config.Collapsed ? 50 : config.Kind == "agent" ? 280 : 50;
        double width = Math.Clamp(config.Width, minW, Math.Max(minW, canvasWidth));
        double height = config.Collapsed ? 50 : Math.Clamp(config.Height, minH, Math.Max(minH, canvasHeight));
        double x = Math.Clamp(config.X, 0, Math.Max(0, canvasWidth - width));
        double y = Math.Clamp(config.Y, 0, Math.Max(0, canvasHeight - height));
        return new(x, y, width, height);
    }

    /// <summary>Same rules as ArrangeTimerWidget: a default corner until the user moves it, then theirs.</summary>
    private CardBox TimerRect(WorkspacePage page, WidgetConfig config)
    {
        config.Kind = "timer"; if (string.IsNullOrWhiteSpace(config.Title)) config.Title = "POMODORO";
        double minWidth = Math.Min(360, canvasWidth), minHeight = Math.Min(300, canvasHeight);
        double maxWidth = Math.Max(minWidth, canvasWidth), maxHeight = Math.Max(minHeight, canvasHeight);
        if (config.CanvasFill)
        {
            config.ApplyCanvasFillIfNeeded(canvasWidth, canvasHeight);
            return new(0, 0, canvasWidth, canvasHeight);
        }
        if (config.Width <= 0) config.Width = Math.Min(560, maxWidth);
        if (config.Height <= 0) config.Height = Math.Min(360, maxHeight);
        if (!page.TimerPositionCustomized)
        {
            config.X = 12;
            config.Y = Settings.TimerAtBottom ? Math.Max(12, canvasHeight - config.Height - 12) : 12;
        }
        double width = Math.Clamp(config.Width, minWidth, maxWidth);
        double height = Math.Clamp(config.Height, minHeight, maxHeight);
        double x = Math.Clamp(config.X, 0, Math.Max(0, canvasWidth - width));
        double y = Math.Clamp(config.Y, 0, Math.Max(0, canvasHeight - height));
        return new(x, y, width, height);
    }

    /// <summary>Keeps every stored rectangle inside the canvas after the window changed size.</summary>
    public void ArrangeCards()
    {
        foreach (var page in LivePages())
        {
            foreach (var config in page.Widgets)
            {
                var box = CardRect(config);
                config.X = box.X; config.Y = box.Y;
            }
            if (page.TimerWidget is { } timer)
            {
                var box = TimerRect(page, timer);
                if (!timer.CanvasFill) { timer.X = box.X; timer.Y = box.Y; timer.Width = box.Width; timer.Height = box.Height; }
            }
        }
    }

    public void AddCard(WidgetConfig config, bool save)
    {
        var newPlacement = config.Width <= 0 && config.Height <= 0 && config.X == 0 && config.Y == 0;
        if (config.Width <= 0)
        {
            var twoColumnWidth = (canvasWidth - 40) / 2;
            config.Width = config.Kind == "memento"
                ? Math.Max(320, Math.Min(420, canvasWidth - 24))
                : config.Kind == "workout"
                    ? Math.Max(340, Math.Min(420, canvasWidth - 24))
                : canvasWidth >= 560 ? Math.Max(240, twoColumnWidth) : Math.Max(280, canvasWidth - 24);
        }
        if (config.Height <= 0) config.Height = config.Kind switch
        {
            "stats" => 260,
            "memento" => 380,
            "notes" => 360,
            "todo" => 400,
            "habits" => 390,
            "workout" => 680,
            "calendar" => 520,
            "finance" => 580,
            "agent" => 520,
            "window" => 420,
            "web" => 360,
            _ => 300
        };
        if (newPlacement)
        {
            // A new widget lands where the eye already is, not tucked into a corner behind
            // whatever is already there — dead centre of the visible canvas, every time.
            config.X = Math.Max(0, (canvasWidth - config.Width) / 2);
            config.Y = Math.Max(0, (canvasHeight - config.Height) / 2);
        }
        CurrentPage.Widgets.Add(config);
        // On top of everything else — the timer included — so it is never born hidden under one.
        front = config.Id;
        if (save)
        {
            Arrive(config.Id);
            SaveState();
        }
    }

    public void RemoveCard(WidgetConfig config)
    {
        if (config.Kind == "notes") ArchiveNote(config);
        CurrentPage.Widgets.Remove(config);
        SaveState();
        StateHasChanged();
    }

    internal void ClearCanvasFillsExcept(WidgetConfig? keep)
    {
        if (CurrentTimerWidget is { } timer && timer != keep && timer.CanvasFill)
            timer.RestoreFromCanvasFill();
        foreach (var config in CurrentPage.Widgets)
            if (config != keep && config.CanvasFill) config.RestoreFromCanvasFill();
    }

    internal void BringCardToFront(WidgetConfig config)
    {
        if (front == config.Id) return;
        front = config.Id;
        StateHasChanged();
    }

    private void BringTimerToFront(WidgetConfig config)
    {
        if (front == config.Id) return;
        front = config.Id;
        StateHasChanged();
    }

    /// <summary>The widget that just landed plays its arrival once, for as long as the animation lasts.</summary>
    private void Arrive(Guid id)
    {
        if (Settings.ReduceMotion) return;
        arriving = id;
        After(260, () => { if (arriving == id) arriving = null; });
    }

    [JSInvokable]
    public void BeginGesture(string id)
    {
        if (!Guid.TryParse(id, out var key)) return;
        front = key;
        StateHasChanged();
    }

    /// <summary>A move or resize the page performed; the widget keeps the rectangle it ended on.</summary>
    [JSInvokable]
    public void GestureEnded(string id, double x, double y, double width, double height)
    {
        if (!Guid.TryParse(id, out var key)) return;
        if (CurrentTimerWidget is { } timer && timer.Id == key)
        {
            timer.X = x; timer.Y = y; timer.Width = width; timer.Height = height;
            timer.DropCanvasFillIfBoundsMoved(canvasWidth, canvasHeight);
            CurrentPage.TimerPositionCustomized = true;
        }
        else if (CurrentPage.Widgets.FirstOrDefault(widget => widget.Id == key) is { } config)
        {
            config.X = x; config.Y = y; config.Width = width;
            if (!config.Collapsed) config.Height = height;
            config.DropCanvasFillIfBoundsMoved(canvasWidth, canvasHeight);
        }
        else return;
        SaveState();
        StateHasChanged();
    }

    // ------------------------------------------------------------------ the timer on the page

    private void TimerFillClick()
    {
        if (CurrentTimerWidget is not { } timer) return;
        if (!timer.CanvasFill)
        {
            ClearCanvasFillsExcept(timer);
            timer.FillCanvas(canvasWidth, canvasHeight);
            CurrentPage.TimerPositionCustomized = true;
        }
        else timer.RestoreFromCanvasFill();
        front = timer.Id;
        SaveState();
    }

    private void AddTimerWidget()
    {
        if (CurrentTimerWidget is not null)
        {
            Status(L.T("pages.timerAlreadyHere"));
            return;
        }
        var timer = new WidgetConfig { Kind = "timer", Title = "POMODORO" };
        CurrentPage.TimerWidget = timer;
        CurrentPage.TimerPositionCustomized = false;
        front = timer.Id;
        Arrive(timer.Id);
        SaveState();
    }

    private void RemoveTimerClick() => RemoveTimerWidget();

    private void RemoveTimerWidget()
    {
        if (CurrentTimerWidget is null) return;
        CurrentPage.TimerWidget = null;
        CurrentPage.TimerPositionCustomized = false;
        SaveState();
    }

    private async Task AddWidgetClick()
    {
        var choice = await ChooseWidget();
        if (choice == 1)
        {
            var url = await Prompt(L.T("widgets.webUrlTitle"), L.T("widgets.webUrlLabel"), "https://www.youtube.com");
            if (url is null) return;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") { Status(L.T("status.needHttps")); return; }
            AddCard(new() { Kind = "web", Title = uri.Host, Value = uri.AbsoluteUri }, true);
        }
        else if (choice == 2) AddCard(new() { Kind = "notes", Title = "NOTAS" }, true);
        else if (choice == 3) AddCard(new() { Kind = "stats", Title = "MI ENFOQUE" }, true);
        else if (choice == 4) AddCard(new() { Kind = "todo", Title = "TO DO" }, true);
        else if (choice == 5) AddCard(new() { Kind = "habits", Title = L.T("widgets.kindHabits") }, true);
        else if (choice == 6) AddCard(new() { Kind = "calendar", Title = "AGENDA" }, true);
        else if (choice == 7) AddCard(new() { Kind = "finance", Title = L.T("widgets.kindFinance") }, true);
        else if (choice == 8) AddCard(new() { Kind = "memento", Title = L.T("widgets.kindMemento") }, true);
        else if (choice == 9) AddCard(new() { Kind = "workout", Title = L.T("widgets.kindWorkout") }, true);
        else if (choice == 10) AddTimerWidget();
        StateHasChanged();
    }

    // ------------------------------------------------------------------ moving between pages

    private void NavigateWorkspace(int dx, int dy)
    {
        if (dx == 0 && dy == 0 || IsWorkspaceNavigationBlocked) return;
        int? target = FindPageIndex(CurrentPage.Col + dx, CurrentPage.Row + dy);
        if (target is { } index) SwitchWorkspacePage(index);
        else if (CanCreateWorkspacePage(dx, dy)) BeginCarouselTransition(dx, dy, null, true);
        else PulsePageChrome();
    }

    /// <summary>
    /// Right-click on a page in the map. Confirms first — deleting a page throws its widgets away for
    /// good, unlike closing one — then deletes it whether it holds anything or not.
    /// </summary>
    private async Task DeleteWorkspacePageClick(int index)
    {
        if (pageTransitioning || index < 0 || index >= Settings.WorkspacePages.Count) return;
        if (Settings.WorkspacePages.Count <= 1)
        {
            Status(L.T("pages.cannotDeleteOnly"));
            PulsePageChrome();
            return;
        }
        var page = Settings.WorkspacePages[index];
        int count = page.WidgetCount;
        string suffix = count == 0 ? "" : count == 1 ? L.T("pages.deleteSuffixOne") : L.T("pages.deleteSuffixMany", count);
        if (await Choose(L.T("pages.deleteTitle", page.Name.ToUpperInvariant(), suffix), [L.T("pages.deleteConfirm"), L.T("common.cancel")]) != 0) return;
        DeleteWorkspacePage(index);
        StateHasChanged();
    }

    /// <summary>Removes a page outright, widgets and all.</summary>
    private void DeleteWorkspacePage(int index)
    {
        var page = Settings.WorkspacePages[index];
        bool removingCurrent = index == currentPageIndex;
        SaveState();
        foreach (var config in page.Widgets.Where(widget => widget.Kind == "notes")) ArchiveNote(config);
        livePages.Remove(page.Id);
        Settings.WorkspacePages.RemoveAt(index);
        if (removingCurrent)
        {
            currentPageIndex = Math.Min(index, Settings.WorkspacePages.Count - 1);
            livePages.Add(CurrentPage.Id);
            ArrangeCards();
        }
        else if (index < currentPageIndex) currentPageIndex--;
        if (Settings.HomePageId == page.Id) Settings.HomePageId = Settings.WorkspacePages[0].Id;
        Settings.ActiveWorkspacePage = currentPageIndex;
        SaveState();
    }

    /// <summary>Closing a note keeps its words in the note history instead of throwing them away.</summary>
    internal void ArchiveNote(WidgetConfig config) => NoteArchiveStore.For(Store).Close(config);

    private void SwitchWorkspacePage(int target)
    {
        if (IsWorkspaceNavigationBlocked || pageTransitioning || target < 0 || target >= Settings.WorkspacePages.Count || target == currentPageIndex) return;
        var (dx, dy) = StepToward(CurrentPage, Settings.WorkspacePages[target]);
        BeginCarouselTransition(dx, dy, target, false);
    }

    private void JumpToWorkspacePage(int target)
    {
        if (target < 0 || target >= Settings.WorkspacePages.Count || target == currentPageIndex) return;
        if (pageTransitioning) ResetCarousel();
        var (dx, dy) = StepToward(CurrentPage, Settings.WorkspacePages[target]);
        CommitWorkspacePageChange(dx, dy, target, false);
    }

    private void BeginCarouselTransition(int dx, int dy, int? target, bool createPage)
    {
        if (IsWorkspaceNavigationBlocked || pageTransitioning) return;
        PrepareCarousel(dx, dy, target, createPage);
        if (Settings.ReduceMotion)
        {
            CommitWorkspacePageChange(dx, dy, target, createPage);
            return;
        }
        // The page sliding in has to be on screen before the slide can start.
        animateCarouselAfterRender = true;
    }

    private void PrepareCarousel(int dx, int dy, int? target, bool createPage)
    {
        SaveState();
        pageTransitioning = true;
        swipeDx = dx;
        swipeDy = dy;
        swipeTargetIndex = target;
        swipeCreatesPage = createPage;
        swipeDestinationAvailable = target is not null || createPage;
        if (target is { } index) livePages.Add(Settings.WorkspacePages[index].Id);
    }

    private void CommitWorkspacePageChange(int dx, int dy, int? target, bool createPage)
    {
        var oldPage = CurrentPage;
        if (createPage)
        {
            var page = new WorkspacePage
            {
                Name = $"PÁGINA {Settings.WorkspacePages.Count + 1:00}",
                Col = oldPage.Col + dx,
                Row = oldPage.Row + dy
            };
            Settings.WorkspacePages.Add(page);
            target = Settings.WorkspacePages.Count - 1;
        }
        if (target is null) { ResetCarousel(); return; }

        currentPageIndex = target.Value;
        Settings.ActiveWorkspacePage = currentPageIndex;
        livePages.Add(CurrentPage.Id);
        front = CurrentTimerWidget?.Id;
        ResetCarousel();
        ArrangeCards();
        SaveState();
    }

    private void ResetCarousel()
    {
        pageTransitioning = false;
        swipeDestinationAvailable = false;
        swipeTargetIndex = null;
        swipeCreatesPage = false;
        swipeDx = 0;
        swipeDy = 0;
        resetCarouselAfterRender = true;
    }

    private void AfterRenderPages()
    {
        if (animateCarouselAfterRender)
        {
            animateCarouselAfterRender = false;
            Browser.CarouselAnimate(swipeDx, swipeDy, true);
        }
        if (resetCarouselAfterRender)
        {
            resetCarouselAfterRender = false;
            Browser.CarouselReset();
        }
    }

    [JSInvokable]
    public bool CanBeginSwipe() => !IsWorkspaceNavigationBlocked && !pageMapOpen && !pageTransitioning;

    /// <summary>The page drags toward a neighbour: the real neighbour slides in, or a blank one if allowed.</summary>
    [JSInvokable]
    public bool ConfigureSwipe(int dx, int dy)
    {
        int? targetIndex = FindPageIndex(CurrentPage.Col + dx, CurrentPage.Row + dy);
        bool create = targetIndex is null && CanCreateWorkspacePage(dx, dy);
        if (!pageTransitioning) PrepareCarousel(dx, dy, targetIndex, create);
        else
        {
            swipeDx = dx;
            swipeDy = dy;
            swipeTargetIndex = targetIndex;
            swipeCreatesPage = create;
            swipeDestinationAvailable = targetIndex is not null || create;
            if (targetIndex is { } index) livePages.Add(Settings.WorkspacePages[index].Id);
        }
        StateHasChanged();
        return swipeDestinationAvailable;
    }

    [JSInvokable]
    public void FinishCarousel(bool commit)
    {
        if (commit && swipeDestinationAvailable) CommitWorkspacePageChange(swipeDx, swipeDy, swipeTargetIndex, swipeCreatesPage);
        else ResetCarousel();
        StateHasChanged();
    }

    private void PulsePageChrome()
    {
        if (Settings.ReduceMotion) return;
        pulsePageMap = true;
        After(420, () => pulsePageMap = false);
    }
}
