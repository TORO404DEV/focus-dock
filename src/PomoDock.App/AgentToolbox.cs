using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Documents;
using PomoDock.Core;

namespace PomoDock.App;

internal sealed record AgentToolResult(
    bool Changed,
    string Summary,
    string Json,
    bool Success = true,
    string? UndoTool = null,
    string? UndoArgumentsJson = null)
{
    public AgentActionReceipt Receipt(string tool) => new()
    {
        Tool = tool,
        Summary = Summary,
        UndoTool = UndoTool,
        UndoArgumentsJson = UndoArgumentsJson
    };
}

internal enum AgentToolKind { Query, Mutation, Destructive, Control, Unknown }

/// <summary>Small, typed door between an untrusted model response and PomoDock's live state.</summary>
internal sealed class AgentToolbox
{
    private readonly MainWindow owner;
    private readonly AgentMemoryStore memory;
    /// <summary>When true, destructive tools skip their second dialog — the plan was already approved.</summary>
    internal bool PlanApproved { get; set; }

    public AgentToolbox(MainWindow owner)
    {
        this.owner = owner;
        memory = new AgentMemoryStore(owner.Store);
    }

    public async Task<AgentToolResult> ExecuteAsync(string name, JsonElement args, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await owner.Dispatcher.InvokeAsync(() => Execute(name.Trim().ToLowerInvariant(), args));
    }

    private AgentToolResult Execute(string name, JsonElement args)
    {
        try
        {
            return name switch
            {
                "get_state" => State(Text(args, "scope", "all")),
                "focus.summarize" => SummarizeFocus(args),
                "focus.query_sessions" => QueryFocus(args),
                "focus.compare_periods" => CompareFocus(args),
                "focus.list_projects" => ListProjects(),
                "todo.search" => SearchTodos(args),
                "add_todo" or "todo.create" => AddTodo(Required(args, "text")),
                "todo.update" => UpdateTodo(args),
                "complete_todo" or "todo.complete" => CompleteTodo(Required(args, "title"), Bool(args, "done", true)),
                "delete_todo" or "todo.delete" => DeleteTodo(Required(args, "title")),
                "calendar.search" => SearchEvents(args),
                "calendar.upcoming" => UpcomingEvents(args),
                "add_event" or "calendar.create" => AddEvent(args),
                "calendar.update" or "calendar.reschedule" => UpdateEvent(args),
                "complete_event" or "calendar.complete" => CompleteEvent(Required(args, "title"), Date(args, "date", DateOnly.FromDateTime(DateTime.Today)), Bool(args, "done", true)),
                "delete_event" or "calendar.delete" => DeleteEvent(args),
                "calendar.navigate" => owner.NavigateCalendarFromAgent(args),
                "calendar.set_show_done" => owner.SetCalendarShowDoneFromAgent(args),
                "habits.search" => SearchHabits(args),
                "add_habit" or "habits.create" => AddHabit(args),
                "mark_habit" or "habits.mark" => MarkHabit(Required(args, "name"), Date(args, "date", DateOnly.FromDateTime(DateTime.Today)), Bool(args, "done", true)),
                "habits.update" => UpdateHabit(args),
                "archive_habit" or "habits.archive" => ArchiveHabit(Required(args, "name")),
                "notes.search" => SearchNotes(args),
                "add_note" or "notes.create" => AddNote(args),
                "notes.append" => AppendNote(args),
                "notes.reopen" => ReopenNote(args),
                "notes.forget" or "notes.delete" => ForgetNote(args),
                "add_widget" or "workspace.add_widget" => AddWidget(args),
                "workspace.list" => State("widgets"),
                "workspace.move_widget" => MoveWidget(args),
                "workspace.rename_widget" => owner.RenameWidgetFromAgent(args),
                "workspace.collapse_widget" => owner.CollapseWidgetFromAgent(args),
                "workspace.resize_widget" => owner.ResizeWidgetFromAgent(args),
                "workspace.set_web_url" => owner.SetWebUrlFromAgent(args),
                "workspace.reload_web" => owner.ReloadWebFromAgent(args),
                "workspace.open" => AddWidget(args),
                "workspace.add_page" => owner.AddPageFromAgent(),
                "workspace.goto" => owner.GotoPageFromAgent(args),
                "workspace.rename_page" => owner.RenamePageFromAgent(args),
                "workspace.set_home" => owner.SetHomePageFromAgent(args),
                "workspace.focus" => owner.FocusWidgetFromAgent(args),
                "workspace.delete_empty_pages" => owner.DeleteEmptyPagesFromAgent(),
                "workspace.delete_page" => owner.DeletePageFromAgent(args),
                "workspace.remove_timer" => owner.RemoveTimerFromAgent(args),
                "workspace.remove_widget" => owner.RemoveWidgetFromAgent(args),
                "workspace.clear_widgets" => owner.ClearWidgetsFromAgent(args),
                "workspace.set_web_keepalive" => owner.SetWebKeepAliveFromAgent(args),
                "layouts.list" => owner.ListLayoutsFromAgent(),
                "layouts.save" => owner.SaveLayoutFromAgent(args),
                "layouts.load" => owner.LoadLayoutFromAgent(args),
                "layouts.delete" => owner.DeleteLayoutFromAgent(args),
                "app.fullscreen" => owner.FullscreenFromAgent(args),
                "app.open_panel" => owner.OpenPanelFromAgent(args),
                "app.open_data_folder" => owner.OpenDataFolderFromAgent(),
                "notifications.list" => owner.NotificationsFromAgent(args),
                "notifications.dismiss" => owner.DismissNotificationsFromAgent(args),
                "calendar.set_view" => owner.SetCalendarViewFromAgent(args),
                "todo.clear_done" => ClearDoneTodos(),
                "todo.sort" => SortTodos(),
                "todo.move" => MoveTodo(args),
                "habits.restore" => RestoreHabit(Required(args, "name")),
                "habits.delete" => DeleteHabitForever(Required(args, "name")),
                "habits.set_target" => SetHabitTarget(args),
                "habits.move" => MoveHabit(args),
                "habits.set_count" => SetHabitCount(args),
                "finance.set_currency" => SetFinanceCurrency(args),
                "notes.set_color" => SetNoteColor(args),
                "settings.apply_rhythm" => owner.ApplyRhythmFromAgent(args),
                "focus_tasks.list" => ListFocusTasks(),
                "focus_tasks.update" => UpdateFocusTask(args),
                "focus_tasks.set_done" => SetFocusTaskDone(args),
                "focus_tasks.delete" => DeleteFocusTask(args),
                "focus_tasks.save_template" => SaveFocusTemplate(args),
                "focus_tasks.add_from_template" => AddFocusFromTemplate(args),
                "focus_projects.list" => ListFocusProjects(),
                "focus_projects.add" => AddFocusProject(args),
                "focus_projects.rename" => RenameFocusProject(args),
                "focus_projects.delete" => DeleteFocusProject(args),
                "sounds.list" => ListSounds(args),
                "sounds.set" => SetSound(args),
                "finance.summary" => FinanceSummary(args),
                "finance.search" => FinanceSearch(args),
                "finance.add" => FinanceAdd(args),
                "finance.add_recurring" => FinanceRecurring(args),
                "finance.mark_paid" => FinancePaid(args),
                "finance.delete" => FinanceDelete(args),
                "finance.clear_expenses" => FinanceClearExpenses(args),
                "finance.archive" => FinanceArchive(args),
                "add_focus_task" => AddFocusTask(args),
                "timer" or "timer.start" or "timer.pause" or "timer.reset" or "timer.configure" => Timer(args, name),
                "timer.inspect" => State("timer"),
                "set_focus_duration" => SetFocusDuration(Int(args, "minutes", 25)),
                "settings.read" => ReadSettings(),
                "settings.update" => UpdateSettings(args),
                "memory.search" => MemorySearch(args),
                "memory.propose" => MemoryWrite(args, false),
                "memory.confirm" => MemoryWrite(args, true),
                "memory.forget" => MemoryForget(args),
                "memory.export" => MemoryExport(),
                _ => Result(false, false, $"Herramienta desconocida: {name}")
            };
        }
        catch (Exception ex) { return Result(false, false, ex.Message); }
    }

    internal static AgentToolKind KindOf(string name) => AgentCatalog.RiskOf(name) switch
    {
        AgentRisk.Read => AgentToolKind.Query,
        AgentRisk.Destructive => AgentToolKind.Destructive,
        AgentRisk.Batch => AgentToolKind.Control,
        _ => AgentToolKind.Mutation
    };

    private AgentToolResult State(string scope)
    {
        var todos = TodoStore.For(owner.Store).Book.Ordered().Select(x => new { x.Id, x.Title, x.Done, Due = x.Due?.ToString("yyyy-MM-dd"), At = x.At?.ToString("HH:mm"), Priority = x.Priority.ToString() });
        var events = AgendaStore.For(owner.Store).Book.Events.OrderBy(x => x.Start).Select(x => new { x.Id, x.Title, Start = x.Start.ToString("yyyy-MM-dd HH:mm"), x.Minutes, x.AllDay, Repeat = x.Repeat.ToString() });
        var habits = HabitStore.For(owner.Store).Book.Habits.Select(x => new { x.Id, x.Name, x.Archived, Cadence = x.Cadence.ToString(), DoneToday = x.IsComplete(DateOnly.FromDateTime(DateTime.Today)) });
        object value = scope.ToLowerInvariant() switch
        {
            "todos" => todos,
            "calendar" => events,
            "habits" => habits,
            "timer" => new { Phase = owner.Timer.Phase.ToString(), owner.Timer.Running, RemainingMinutes = Math.Ceiling(owner.Timer.Remaining / 60) },
            "widgets" => WorkspaceWidgetState(),
            "focus_tasks" => owner.Settings.Tasks.Select(x => new { x.Id, x.Name, x.Project, x.Estimate, x.Done }),
            "focus_history" => FocusSnapshot("this_month"),
            "finance" => FinanceSnapshot(),
            _ => new { Todos = todos, Calendar = events, Habits = habits, Timer = new { Phase = owner.Timer.Phase.ToString(), owner.Timer.Running, RemainingMinutes = Math.Ceiling(owner.Timer.Remaining / 60) }, FocusTasks = owner.Settings.Tasks.Where(x => !x.Done).Select(x => new { x.Name, x.Project }), FocusHistory = FocusSnapshot("this_month"), Finance = FinanceSnapshot(), Workspace = WorkspaceWidgetState() }
        };
        return new AgentToolResult(false, "", JsonSerializer.Serialize(new { success = true, state = value }));
    }

    private object WorkspaceWidgetState()
    {
        owner.SyncWorkspaceForAgent();
        int current = owner.CurrentWorkspacePageIndex + 1;
        var pages = owner.Settings.WorkspacePages.Select((p, i) => new
        {
            Page = i + 1,
            Current = i == owner.CurrentWorkspacePageIndex,
            Home = owner.Settings.HomePageId == p.Id,
            p.Name,
            p.Col,
            p.Row,
            Empty = !p.HasContent,
            Timer = p.TimerWidget is not null,
            Widgets = p.Widgets.Select(w => new { w.Id, w.Kind, w.Title, w.X, w.Y })
        });
        return new
        {
            CurrentPage = current,
            CurrentName = owner.Settings.WorkspacePages.Count > 0
                ? owner.Settings.WorkspacePages[owner.CurrentWorkspacePageIndex].Name
                : "",
            HomePage = owner.Settings.HomePageId is { } home
                ? owner.Settings.WorkspacePages.FindIndex(p => p.Id == home) + 1
                : (int?)null,
            VisibleNow = owner.DescribeCurrentScreenWidgets(),
            Pages = pages
        };
    }

    private AgentToolResult SummarizeFocus(JsonElement args)
    {
        var sessions = owner.Store.Sessions();
        var range = FocusRange(args, sessions);
        var summary = FocusHistory.Summarize(sessions, range, TimeZoneInfo.Local, Text(args, "project", ""));
        var data = new
        {
            period = summary.Range.Period,
            from = summary.Range.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            to = summary.Range.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            project = summary.Project,
            totalMinutes = summary.Minutes,
            totalHours = Math.Round(summary.Minutes / 60, 2),
            sessionCount = summary.Sessions,
            activeDays = summary.ActiveDays,
            completed = summary.Completed,
            partial = summary.Partial,
            days = summary.Days.Select(day => new { date = day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), minutes = day.Minutes }),
            projects = summary.Projects.Select(project => new { project = project.Project, minutes = project.Minutes, hours = Math.Round(project.Minutes / 60, 2), sessions = project.Sessions })
        };
        string subject = summary.Project is null ? "enfoque" : $"proyecto {summary.Project}";
        return Result(true, false, $"Resumen del {subject}: {summary.Minutes:0.#} min ({Math.Round(summary.Minutes / 60, 2)} h) entre {summary.Range.From:dd/MM/yyyy} y {summary.Range.To:dd/MM/yyyy}", data);
    }

    private AgentToolResult QueryFocus(JsonElement args)
    {
        var sessions = owner.Store.Sessions();
        var range = FocusRange(args, sessions);
        var query = FocusHistory.Query(sessions, range, TimeZoneInfo.Local, Text(args, "project", ""), Int(args, "limit", 30));
        var data = new
        {
            period = query.Range.Period,
            from = query.Range.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            to = query.Range.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            project = query.Project,
            totalMatching = query.Total,
            returned = query.Sessions.Count,
            sessions = query.Sessions.Select(session => new
            {
                id = session.Id,
                started = session.Started.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture),
                ended = session.Ended.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture),
                session.Project,
                session.Task,
                minutes = session.Minutes,
                outcome = session.Outcome.ToString()
            })
        };
        return Result(true, false, $"{query.Total} sesiones de enfoque encontradas", data);
    }

    private AgentToolResult CompareFocus(JsonElement args)
    {
        var sessions = owner.Store.Sessions();
        var left = FocusHistory.Resolve(Text(args, "left", "last_month"), DateOnly.FromDateTime(DateTime.Today), earliest: FocusHistory.EarliestDay(sessions, TimeZoneInfo.Local));
        var right = FocusHistory.Resolve(Text(args, "right", "this_month"), DateOnly.FromDateTime(DateTime.Today), earliest: FocusHistory.EarliestDay(sessions, TimeZoneInfo.Local));
        string project = Text(args, "project", "");
        var a = FocusHistory.Summarize(sessions, left, TimeZoneInfo.Local, project);
        var b = FocusHistory.Summarize(sessions, right, TimeZoneInfo.Local, project);
        return Result(true, false, $"{a.Range.Period}: {Math.Round(a.Minutes / 60, 2)} h · {b.Range.Period}: {Math.Round(b.Minutes / 60, 2)} h",
            new { left = new { a.Range.Period, a.Minutes, hours = Math.Round(a.Minutes / 60, 2), a.Sessions }, right = new { b.Range.Period, b.Minutes, hours = Math.Round(b.Minutes / 60, 2), b.Sessions } });
    }

    private AgentToolResult ListProjects()
    {
        var sessions = owner.Store.Sessions();
        var range = FocusHistory.Resolve("all", DateOnly.FromDateTime(DateTime.Today), earliest: FocusHistory.EarliestDay(sessions, TimeZoneInfo.Local));
        var summary = FocusHistory.Summarize(sessions, range, TimeZoneInfo.Local);
        return Result(true, false, $"{summary.Projects.Count} proyectos con enfoque", summary.Projects);
    }

    private object FocusSnapshot(string period)
    {
        var sessions = owner.Store.Sessions();
        var range = FocusHistory.Resolve(period, DateOnly.FromDateTime(DateTime.Today), earliest: FocusHistory.EarliestDay(sessions, TimeZoneInfo.Local));
        var summary = FocusHistory.Summarize(sessions, range, TimeZoneInfo.Local);
        return new { period, from = range.From.ToString("yyyy-MM-dd"), to = range.To.ToString("yyyy-MM-dd"), totalMinutes = summary.Minutes, totalHours = Math.Round(summary.Minutes / 60, 2), sessionCount = summary.Sessions };
    }

    private static FocusDateRange FocusRange(JsonElement args, IEnumerable<Session> sessions)
    {
        string period = Text(args, "period", "this_month");
        DateOnly? from = OptionalDate(args, "from"), to = OptionalDate(args, "to");
        return FocusHistory.Resolve(period, DateOnly.FromDateTime(DateTime.Today), from, to, FocusHistory.EarliestDay(sessions, TimeZoneInfo.Local));
    }

    private AgentToolResult SearchTodos(JsonElement args)
    {
        string query = Text(args, "query", "");
        bool open = Bool(args, "open", false);
        var items = TodoStore.For(owner.Store).Book.Ordered()
            .Where(item => !open || !item.Done)
            .Where(item => query.Length == 0 || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(item => new { item.Id, item.Title, item.Done, Due = item.Due?.ToString("yyyy-MM-dd"), Priority = item.Priority.ToString() })
            .Take(40).ToList();
        return Result(true, false, $"{items.Count} tareas", items);
    }

    private AgentToolResult AddTodo(string text)
    {
        var item = TodoStore.For(owner.Store).Add(text, DateTime.Now);
        return item is null
            ? Result(false, false, "No pude interpretar una tarea")
            : Result(true, true, $"Tarea añadida: {item.Title}", new { item.Id, item.Title, Due = item.Due?.ToString("yyyy-MM-dd"), At = item.At?.ToString("HH:mm") },
                "delete_todo", JsonSerializer.Serialize(new { title = item.Title }));
    }

    private AgentToolResult UpdateTodo(JsonElement args)
    {
        var store = TodoStore.For(owner.Store);
        var item = FindOne(store.Book.Items, x => x.Title, Required(args, "title"), "tarea", x => x.Id);
        string previous = JsonSerializer.Serialize(new { title = item.Title, due = item.Due?.ToString("yyyy-MM-dd"), at = item.At?.ToString("HH:mm"), priority = item.Priority.ToString() });
        string newTitle = Text(args, "new_title", "");
        if (newTitle.Length > 0) item.Title = newTitle;
        if (OptionalDate(args, "due") is { } due) item.Due = due;
        if (args.TryGetProperty("at", out var atValue))
        {
            if (atValue.ValueKind == JsonValueKind.Null || (atValue.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(atValue.GetString())))
                item.At = null;
            else if (TimeOnly.TryParseExact(Text(args, "at", ""), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                item.At = at;
        }
        string priority = Text(args, "priority", "");
        if (priority.Equals("high", StringComparison.OrdinalIgnoreCase)) item.Priority = TodoPriority.High;
        else if (priority.Equals("medium", StringComparison.OrdinalIgnoreCase)) item.Priority = TodoPriority.Medium;
        else if (priority.Equals("none", StringComparison.OrdinalIgnoreCase)) item.Priority = TodoPriority.None;
        store.Save();
        return Result(true, true, $"Tarea actualizada: {item.Title}", new { item.Id }, "todo.update", previous);
    }

    private AgentToolResult CompleteTodo(string query, bool done)
    {
        var store = TodoStore.For(owner.Store); var item = FindOne(store.Book.Items, x => x.Title, query, "tarea", x => x.Id);
        store.SetDone(item, done);
        return Result(true, true, $"Tarea {(done ? "completada" : "reabierta")}: {item.Title}", null,
            "complete_todo", JsonSerializer.Serialize(new { title = item.Title, done = !done }));
    }

    private AgentToolResult DeleteTodo(string query)
    {
        var store = TodoStore.For(owner.Store); var item = FindOne(store.Book.Items, x => x.Title, query, "tarea", x => x.Id);
        if (!Confirm($"Eliminar tarea: {item.Title}")) return Result(false, false, "El usuario canceló la eliminación");
        var snapshot = JsonSerializer.Serialize(item);
        store.Book.Items.Remove(item); store.Save();
        return Result(true, true, $"Tarea eliminada: {item.Title}", new { snapshot });
    }

    private AgentToolResult SearchEvents(JsonElement args)
    {
        string query = Text(args, "query", "");
        var today = DateOnly.FromDateTime(DateTime.Today);
        var from = OptionalDate(args, "from") ?? today;
        var to = OptionalDate(args, "to") ?? from.AddYears(1);
        if (to < from) (from, to) = (to, from);
        var items = AgendaStore.For(owner.Store).Book.Between(from, to)
            .Where(item => query.Length == 0 || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(item => new { item.Event.Id, item.Title, Start = item.Start.ToString("yyyy-MM-dd HH:mm"), item.Event.Minutes, item.AllDay, item.Done })
            .Take(40).ToList();
        return Result(true, false, $"{items.Count} eventos", items);
    }

    private AgentToolResult UpcomingEvents(JsonElement args)
    {
        int days = Math.Clamp(Int(args, "days", 365), 1, 3650);
        int limit = Math.Clamp(Int(args, "limit", 10), 1, 50);
        bool includeDone = Bool(args, "include_done", false);
        var items = AgendaStore.For(owner.Store).Book.Upcoming(DateTime.Now, days)
            .Where(item => includeDone || !item.Done)
            .Take(limit)
            .ToList();
        bool spanish = Strings.Culture.TwoLetterISOLanguageName == "es";
        string summary;
        if (items.Count == 0)
            summary = spanish ? "No tienes pendientes próximos en el calendario." : "You have no upcoming calendar items.";
        else
        {
            string lines = string.Join("\n", items.Select(item =>
            {
                string when = item.AllDay
                    ? item.Day.ToString("ddd d MMM", Strings.Culture)
                    : item.Start.ToString("ddd d MMM · HH:mm", Strings.Culture);
                return $"• {when} — {item.Title}";
            }));
            summary = (spanish ? $"Tus próximos {items.Count} pendientes:" : $"Your next {items.Count} calendar items:") + "\n" + lines;
        }
        var data = items.Select(item => new
        {
            item.Event.Id,
            item.Title,
            start = item.Start.ToString("yyyy-MM-dd HH:mm"),
            item.AllDay,
            item.Done
        }).ToList();
        return Result(true, false, summary, data);
    }

    private AgentToolResult AddEvent(JsonElement args)
    {
        string title = Required(args, "title"); bool allDay = Bool(args, "all_day", false);
        DateTime start = DateTime.TryParseExact(Required(args, "start"), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed : throw new ArgumentException("start debe usar yyyy-MM-dd HH:mm");
        bool remindersSpecified = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("reminders", out _);
        var reminders = IntArray(args, "reminders");
        var item = new AgendaEvent
        {
            Title = title, Start = start, AllDay = allDay,
            Minutes = allDay ? Math.Max(1440, Int(args, "minutes", 1440)) : Int(args, "minutes", 60),
            Notes = Text(args, "notes", ""), Location = Text(args, "location", ""), Color = Text(args, "color", "ink"),
            Repeat = Repeat(args), Interval = Math.Clamp(Int(args, "interval", 1), 1, 99),
            Days = Days(args), Until = OptionalDate(args, "until"), Count = Math.Clamp(Int(args, "count", 0), 0, 999),
            Reminders = remindersSpecified ? reminders : [Math.Clamp(Int(args, "reminder_minutes", 10), 0, 20160)]
        };
        item.Normalize(); AgendaStore.For(owner.Store).Add(item);
        return Result(true, true, $"Evento creado: {item.Title} · {item.Start:dd/MM HH:mm}", new { item.Id },
            "delete_event", JsonSerializer.Serialize(new { title = item.Title }));
    }

    private AgentToolResult UpdateEvent(JsonElement args)
    {
        var store = AgendaStore.For(owner.Store);
        var item = FindOne(store.Book.Events, x => x.Title, Required(args, "title"), "evento", x => x.Id);
        if (DateTime.TryParseExact(Text(args, "start", ""), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
            item.Start = start;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("minutes", out _)) item.Minutes = Int(args, "minutes", item.Minutes);
        if (args.TryGetProperty("all_day", out _)) item.AllDay = Bool(args, "all_day", item.AllDay);
        string newTitle = Text(args, "new_title", "");
        if (newTitle.Length > 0) item.Title = newTitle;
        if (args.TryGetProperty("notes", out _)) item.Notes = Text(args, "notes", item.Notes);
        if (args.TryGetProperty("location", out _)) item.Location = Text(args, "location", item.Location);
        if (args.TryGetProperty("color", out _)) item.Color = Text(args, "color", item.Color);
        if (args.TryGetProperty("reminders", out _)) item.Reminders = IntArray(args, "reminders");
        if (args.TryGetProperty("repeat", out _)) item.Repeat = Repeat(args);
        if (args.TryGetProperty("interval", out _)) item.Interval = Math.Clamp(Int(args, "interval", item.Interval), 1, 99);
        if (args.TryGetProperty("days", out _)) item.Days = Days(args);
        if (args.TryGetProperty("until", out _)) item.Until = OptionalDate(args, "until");
        if (args.TryGetProperty("count", out _)) item.Count = Math.Clamp(Int(args, "count", item.Count), 0, 999);
        item.Normalize(); store.Save();
        return Result(true, true, $"Evento actualizado: {item.Title} · {item.Start:dd/MM HH:mm}");
    }

    private AgentToolResult CompleteEvent(string query, DateOnly date, bool done)
    {
        var store = AgendaStore.For(owner.Store); var item = FindOne(store.Book.Events, x => x.Title, query, "evento", x => x.Id);
        item.SetDone(date, done); store.Save();
        return Result(true, true, $"Evento {(done ? "completado" : "reabierto")}: {item.Title}", null,
            "complete_event", JsonSerializer.Serialize(new { title = item.Title, date = date.ToString("yyyy-MM-dd"), done = !done }));
    }

    private AgentToolResult DeleteEvent(JsonElement args)
    {
        string query = Required(args, "title");
        var store = AgendaStore.For(owner.Store);
        var item = FindOne(store.Book.Events, x => x.Title, query, "evento", x => x.Id);
        string scope = Text(args, "scope", "series").Trim().ToLowerInvariant();
        if (scope is "occurrence" or "day" or "this" or "este" or "esta")
        {
            var day = Date(args, "date", DateOnly.FromDateTime(DateTime.Today));
            if (!Confirm($"Cancelar «{item.Title}» solo el {day:dd/MM}"))
                return Result(false, false, "El usuario canceló la eliminación");
            item.Cancel(day);
            store.Save();
            return Result(true, true, $"Cancelé «{item.Title}» el {day:dd/MM}");
        }
        if (!Confirm($"Eliminar evento: {item.Title}")) return Result(false, false, "El usuario canceló la eliminación");
        store.Remove(item);
        return Result(true, true, $"Evento eliminado: {item.Title}");
    }

    private AgentToolResult MoveTodo(JsonElement args)
    {
        var store = TodoStore.For(owner.Store);
        var task = FindOne(store.Book.Items, x => x.Title, Required(args, "title"), "tarea", x => x.Id);
        int direction = Int(args, "direction", -1);
        if (direction == 0) direction = Text(args, "dir", "up").Equals("down", StringComparison.OrdinalIgnoreCase) ? 1 : -1;
        if (!store.Book.Move(task, direction < 0 ? -1 : 1))
            return Result(false, false, $"No pude mover «{task.Title}» más en esa dirección.");
        store.Save();
        return Result(true, true, direction < 0 ? $"Subí «{task.Title}»." : $"Bajé «{task.Title}».");
    }

    private AgentToolResult ForgetNote(JsonElement args)
    {
        string query = Text(args, "query", Text(args, "title", "")).Trim();
        if (query.Length == 0) throw new ArgumentException("Falta query o title");
        var archive = NoteArchiveStore.For(owner.Store);
        archive.Sync(owner.Settings);
        var note = FindOne(archive.Archive.Notes, x => x.Title, query, "nota", x => x.Id);
        if (!Confirm($"Olvidar nota del historial: {note.Title}"))
            return Result(false, false, "El usuario canceló olvidar la nota");
        // Close live widgets that still hold this note.
        foreach (var page in owner.Settings.WorkspacePages.ToList())
        {
            var live = page.Widgets.Where(widget => widget.Kind == "notes" && widget.Id == note.Id).ToList();
            foreach (var widget in live)
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { title = widget.Title, kind = "notes" }));
                owner.RemoveWidgetFromAgent(doc.RootElement);
            }
        }
        archive.Forget(note.Id);
        return Result(true, true, $"Olvidé la nota «{note.Title}» del historial.");
    }

    private AgentToolResult SearchHabits(JsonElement args)
    {
        string query = Text(args, "query", "");
        var items = HabitStore.For(owner.Store).Book.Habits
            .Where(item => query.Length == 0 || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(item => new { item.Id, item.Name, item.Archived, Cadence = item.Cadence.ToString() })
            .ToList();
        string names = string.Join(", ", items.Where(item => !item.Archived).Select(item => item.Name).Take(12));
        string summary = items.Count == 0
            ? "0 hábitos"
            : names.Length == 0
                ? $"{items.Count} hábitos"
                : $"{items.Count} hábitos: {names}";
        return Result(true, false, summary, items);
    }

    private AgentToolResult AddHabit(JsonElement args)
    {
        var store = HabitStore.For(owner.Store); string name = Required(args, "name");
        var habit = store.Add(name); string cadence = Text(args, "cadence", "daily").ToLowerInvariant();
        if (cadence == "weekdays") { habit.Cadence = HabitCadence.Selected; habit.Days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]; }
        else if (cadence == "weekly") { habit.Cadence = HabitCadence.Weekly; habit.TimesPerWeek = Math.Clamp(Int(args, "times_per_week", 3), 1, 7); }
        store.Save(); return Result(true, true, $"Hábito creado: {habit.Name}", new { habit.Id },
            "archive_habit", JsonSerializer.Serialize(new { name = habit.Name }));
    }

    private AgentToolResult MarkHabit(string query, DateOnly date, bool done)
    {
        var store = HabitStore.For(owner.Store); var habit = FindOne(store.Book.Habits, x => x.Name, query, "hábito", x => x.Id);
        habit.SetCount(date, done ? habit.Target : 0); store.Save();
        return Result(true, true, $"Hábito {(done ? "marcado" : "desmarcado")}: {habit.Name} · {date:dd/MM}", null,
            "mark_habit", JsonSerializer.Serialize(new { name = habit.Name, date = date.ToString("yyyy-MM-dd"), done = !done }));
    }

    private AgentToolResult UpdateHabit(JsonElement args)
    {
        var store = HabitStore.For(owner.Store);
        var habit = FindOne(store.Book.Habits.Where(x => !x.Archived), x => x.Name, Required(args, "name"), "hábito", x => x.Id);
        string newName = Text(args, "new_name", "");
        if (newName.Length > 0) habit.Name = newName;
        string cadence = Text(args, "cadence", "").ToLowerInvariant();
        if (cadence == "daily") habit.Cadence = HabitCadence.Daily;
        else if (cadence == "weekdays") { habit.Cadence = HabitCadence.Selected; habit.Days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]; }
        else if (cadence == "weekly")
        {
            habit.Cadence = HabitCadence.Weekly;
            if (args.TryGetProperty("times_per_week", out _))
                habit.TimesPerWeek = Math.Clamp(Int(args, "times_per_week", habit.TimesPerWeek), 1, 7);
        }
        else if (cadence == "selected" || cadence == "custom")
        {
            habit.Cadence = HabitCadence.Selected;
            if (args.TryGetProperty("days", out _)) habit.Days = Days(args);
        }
        store.Save();
        return Result(true, true, $"Hábito actualizado: {habit.Name}");
    }

    private AgentToolResult MoveHabit(JsonElement args)
    {
        var store = HabitStore.For(owner.Store);
        var habit = FindOne(store.Book.Habits.Where(x => !x.Archived), x => x.Name, Required(args, "name"), "hábito", x => x.Id);
        int direction = Int(args, "direction", -1);
        if (direction == 0) direction = Text(args, "dir", "up").Equals("down", StringComparison.OrdinalIgnoreCase) ? 1 : -1;
        if (!store.Book.Move(habit, direction < 0 ? -1 : 1))
            return Result(false, false, $"No pude mover «{habit.Name}» más en esa dirección.");
        store.Save();
        return Result(true, true, direction < 0 ? $"Subí el hábito «{habit.Name}»." : $"Bajé el hábito «{habit.Name}».");
    }

    private AgentToolResult SetHabitCount(JsonElement args)
    {
        var store = HabitStore.For(owner.Store);
        var habit = FindOne(store.Book.Habits.Where(x => !x.Archived), x => x.Name, Required(args, "name"), "hábito", x => x.Id);
        var date = Date(args, "date", DateOnly.FromDateTime(DateTime.Today));
        int count = Math.Clamp(Int(args, "count", habit.Target), 0, Math.Max(habit.Target, 99));
        habit.SetCount(date, count);
        store.Save();
        return Result(true, true, $"Hábito «{habit.Name}»: {count}/{habit.Target} el {date:dd/MM}");
    }

    private AgentToolResult ArchiveHabit(string query)
    {
        var store = HabitStore.For(owner.Store); var habit = FindOne(store.Book.Habits.Where(x => !x.Archived), x => x.Name, query, "hábito", x => x.Id);
        if (!Confirm($"Archivar hábito: {habit.Name}")) return Result(false, false, "El usuario canceló el archivo");
        habit.Archived = true; store.Save(); return Result(true, true, $"Hábito archivado: {habit.Name}");
    }

    private AgentToolResult SearchNotes(JsonElement args)
    {
        string query = Text(args, "query", "");
        var notes = NoteArchiveStore.For(owner.Store).Archive.Notes
            .Where(note => query.Length == 0 || note.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(note => new { note.Id, note.Text, note.Color, note.IsOpen, Updated = note.UpdatedUtc })
            .Take(20).ToList();
        return Result(true, false, $"{notes.Count} notas", notes);
    }

    private AgentToolResult AddNote(JsonElement args)
    {
        string title = Text(args, "title", "NOTAS");
        bool habitsToday = Bool(args, "habits_today", false);
        string noteText = Text(args, "text", "");
        if (habitsToday && noteText.Length == 0)
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            noteText = string.Join("\n", HabitStore.For(owner.Store).Book.Habits.Where(habit => !habit.Archived)
                .Select(habit => (habit.IsComplete(today) ? "✓ " : "☐ ") + habit.Name));
        }
        if (noteText.Trim().Length == 0 && title.Length > 0 && title is not ("NOTAS" or "NOTES")) noteText = title;
        string color = NormalizeNoteColor(Text(args, "color", "paper"));
        var document = new FlowDocument();
        if (noteText.Trim().Length > 0) document.Blocks.Add(new Paragraph(new Run(noteText)));
        using var stream = new MemoryStream();
        new TextRange(document.ContentStart, document.ContentEnd).Save(stream, DataFormats.Xaml);
        var data = new NotesWidgetData
        {
            Color = color,
            DocumentXaml = Encoding.UTF8.GetString(stream.ToArray()),
            UpdatedUtc = DateTime.UtcNow
        };
        var config = new WidgetConfig { Kind = "notes", Title = title, Value = JsonSerializer.Serialize(data) };
        owner.AddCard(config, true); NoteArchiveStore.For(owner.Store).Track(config);
        owner.ShowCreatedWork(config.Id);
        bool spanish = Strings.Culture.TwoLetterISOLanguageName == "es";
        string colorWord = AgentIntent.ColorLabel(color, spanish);
        string clip = noteText.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        if (clip.Length > 80) clip = clip[..80].TrimEnd() + "…";
        string summary = clip.Length == 0
            ? (spanish ? $"Nota {colorWord} lista." : $"{colorWord} note ready.")
            : (spanish ? $"Nota {colorWord}: {clip}" : $"{colorWord} note: {clip}");
        return Result(true, true, summary, new { config.Id, color });
    }

    private AgentToolResult AppendNote(JsonElement args)
    {
        string title = Required(args, "title"); string extra = Required(args, "text");
        var card = owner.ResolveNotesCardForAgent(title);
        if (card is not null && card.AgentAppendNote(extra))
            return Result(true, true, $"Nota actualizada: {card.Config.Title}");

        var config = owner.Settings.WorkspacePages.SelectMany(page => page.Widgets)
            .FirstOrDefault(widget => widget.Kind == "notes" && widget.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No encontré nota que coincida con '{title}'");
        NotesWidgetData data;
        try { data = JsonSerializer.Deserialize<NotesWidgetData>(config.Value) ?? new NotesWidgetData(); }
        catch (JsonException) { data = new NotesWidgetData(); }
        var document = new FlowDocument();
        if (!string.IsNullOrWhiteSpace(data.DocumentXaml))
        {
            using var load = new MemoryStream(Encoding.UTF8.GetBytes(data.DocumentXaml));
            new TextRange(document.ContentStart, document.ContentEnd).Load(load, DataFormats.Xaml);
        }
        document.Blocks.Add(new Paragraph(new Run(extra)));
        using var save = new MemoryStream();
        new TextRange(document.ContentStart, document.ContentEnd).Save(save, DataFormats.Xaml);
        data.DocumentXaml = Encoding.UTF8.GetString(save.ToArray());
        data.UpdatedUtc = DateTime.UtcNow;
        config.Value = JsonSerializer.Serialize(data);
        owner.SaveState();
        NoteArchiveStore.For(owner.Store).Track(config);
        return Result(true, true, $"Nota actualizada: {config.Title}");
    }

    private AgentToolResult ReopenNote(JsonElement args)
    {
        string query = Text(args, "query", Text(args, "title", "")).Trim();
        if (query.Length == 0) throw new ArgumentException("Falta query o title");
        var archive = NoteArchiveStore.For(owner.Store);
        archive.Sync(owner.Settings);
        var closed = archive.Archive.Notes.Where(note => !note.IsOpen).ToList();
        var note = FindOne(closed, x => x.Title, query, "nota cerrada", x => x.Id);
        if (NotesHistory.Reopen(owner, note) is null)
            return Result(false, false, $"No pude reabrir «{note.Title}» (quizá ya está abierta).");
        return Result(true, true, $"Nota reabierta: {note.Title}", new { note.Id });
    }

    private AgentToolResult AddWidget(JsonElement args)
    {
        string kind = Required(args, "kind").ToLowerInvariant();
        kind = kind switch
        {
            "nota" or "note" or "notes" => "notes",
            "tareas" or "todo" => "todo",
            "hábito" or "habitos" or "hábitos" or "habits" => "habits",
            "agenda" or "calendar" => "calendar",
            "finanzas" or "finance" => "finance",
            "stats" or "enfoque" or "focus" => "stats",
            "web" or "navegador" or "browser" => "web",
            "ventana" or "window" => "window",
            "timer" or "pomodoro" or "temporizador" => "timer",
            _ => kind
        };
        if (kind == "timer") return owner.AddTimerFromAgent();
        if (kind == "web")
        {
            string url = Text(args, "url", Text(args, "value", "https://"));
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                throw new ArgumentException("El widget web necesita una URL https");
            string title = Text(args, "title", uri.Host);
            var config = new WidgetConfig { Kind = "web", Title = title, Value = uri.AbsoluteUri };
            owner.AddCard(config, true);
            owner.ShowCreatedWork(config.Id);
            return Result(true, true, $"Widget web añadido: {title}", new { config.Id, url = config.Value });
        }
        if (kind == "window")
            throw new InvalidOperationException("Para incrustar una ventana usa el menú + WIDGET → ventana (requiere elegir HWND en pantalla).");
        if (kind is not ("notes" or "todo" or "habits" or "calendar" or "stats" or "finance"))
            throw new ArgumentException("Tipo de widget no permitido");
        string widgetTitle = Text(args, "title", kind.ToUpperInvariant());
        var card = new WidgetConfig { Kind = kind, Title = widgetTitle }; owner.AddCard(card, true);
        owner.ShowCreatedWork(card.Id);
        return Result(true, true, $"Widget añadido: {widgetTitle}", new { card.Id });
    }

    private AgentToolResult MoveWidget(JsonElement args)
    {
        string title = Required(args, "title");
        var located = owner.Settings.WorkspacePages
            .Select((page, index) => (page, index, widget: page.Widgets.FirstOrDefault(item => item.Title.Contains(title, StringComparison.OrdinalIgnoreCase))))
            .FirstOrDefault(item => item.widget is not null);
        if (located.widget is null)
            throw new InvalidOperationException($"No encontré widget que coincida con '{title}'");
        using var focusArgs = JsonDocument.Parse(JsonSerializer.Serialize(new { title = located.widget.Title }));
        owner.FocusWidgetFromAgent(focusArgs.RootElement);
        var config = located.widget;
        double previousX = config.X, previousY = config.Y;
        if (args.TryGetProperty("x", out var x) && x.TryGetDouble(out double left)) config.X = left;
        if (args.TryGetProperty("y", out var y) && y.TryGetDouble(out double top)) config.Y = top;
        owner.ArrangeCards(); owner.SaveState();
        return Result(true, true, $"Widget movido: {config.Title}", new { config.Id, config.X, config.Y },
            "workspace.move_widget", JsonSerializer.Serialize(new { title = config.Title, x = previousX, y = previousY }));
    }

    private AgentToolResult AddFocusTask(JsonElement args)
    {
        var item = new WorkTask { Name = Required(args, "name"), Project = Text(args, "project", "Sin proyecto"), Estimate = Math.Clamp(Int(args, "estimate", 1), 1, 99) };
        owner.Settings.Tasks.Add(item);
        if (!owner.Settings.Projects.Contains(item.Project, StringComparer.OrdinalIgnoreCase)) owner.Settings.Projects.Add(item.Project);
        owner.SaveState(); return Result(true, true, $"Tarea de enfoque creada: {item.Name}", new { item.Id });
    }

    private AgentToolResult Timer(JsonElement args, string name)
    {
        string command = Text(args, "command", "");
        if (command.Length == 0)
            command = name.EndsWith("pause") ? "pause" : name.EndsWith("reset") ? "reset" : name.Contains("configure") ? "select" : "start";
        return owner.RunTimerFromAgent(command, Text(args, "phase", "focus"), Text(args, "task", ""));
    }

    private AgentToolResult SetFocusDuration(int minutes)
    {
        int previous = owner.Settings.FocusMinutes;
        owner.Settings.FocusMinutes = Math.Clamp(minutes, 1, 180); owner.ApplyLiveSettings(); owner.SaveState();
        return Result(true, true, $"Duración de enfoque: {owner.Settings.FocusMinutes} min", null,
            "set_focus_duration", JsonSerializer.Serialize(new { minutes = previous }));
    }

    private AgentToolResult ReadSettings()
    {
        var s = owner.Settings;
        var data = new
        {
            s.FocusMinutes, s.ShortMinutes, s.LongMinutes, s.LongInterval, s.DailyGoalMinutes,
            s.AutoBreak, s.AutoFocus, s.Sound, s.ButtonSounds, s.WhiteNoise, s.AlarmEnabled,
            s.WhiteNoiseVolume, s.AlarmVolume, s.EffectsVolume, s.AlarmRepeats,
            s.Language, s.Dark, s.ReduceMotion, s.AlwaysOnTop, s.TimerAtBottom, s.Fullscreen,
            s.AgentVoiceEnabled, s.AgentVoiceSpeed, s.AgentMemoryEnabled, s.AgentSendVoiceOnRelease,
            s.AccentColor, s.FocusColor, s.ShortBreakColor, s.LongBreakColor,
            s.FocusEndSound, s.BreakEndSound, s.ReminderSound, s.ClickSound, s.AmbientSound
        };
        return Result(true, false, "Ajustes actuales", data);
    }

    private AgentToolResult UpdateSettings(JsonElement args)
    {
        var s = owner.Settings;
        var previous = new
        {
            s.FocusMinutes, s.ShortMinutes, s.LongMinutes, s.LongInterval, s.DailyGoalMinutes,
            s.AutoBreak, s.AutoFocus, s.Sound, s.ButtonSounds, s.WhiteNoise, s.AlarmEnabled,
            s.WhiteNoiseVolume, s.AlarmVolume, s.EffectsVolume, s.Language, s.Dark,
            s.ReduceMotion, s.AlwaysOnTop, s.TimerAtBottom, s.AgentVoiceEnabled,
            s.AgentVoiceSpeed, s.AgentMemoryEnabled, s.AgentSendVoiceOnRelease, s.AccentColor
        };
        if (args.TryGetProperty("focus_minutes", out _)) s.FocusMinutes = Int(args, "focus_minutes", s.FocusMinutes);
        if (args.TryGetProperty("short_minutes", out _)) s.ShortMinutes = Int(args, "short_minutes", s.ShortMinutes);
        if (args.TryGetProperty("long_minutes", out _)) s.LongMinutes = Int(args, "long_minutes", s.LongMinutes);
        if (args.TryGetProperty("long_interval", out _)) s.LongInterval = Int(args, "long_interval", s.LongInterval);
        if (args.TryGetProperty("daily_goal_minutes", out _)) s.DailyGoalMinutes = Int(args, "daily_goal_minutes", s.DailyGoalMinutes);
        if (args.TryGetProperty("auto_break", out _)) s.AutoBreak = Bool(args, "auto_break", s.AutoBreak);
        if (args.TryGetProperty("auto_focus", out _)) s.AutoFocus = Bool(args, "auto_focus", s.AutoFocus);
        if (args.TryGetProperty("sound", out _)) s.Sound = Bool(args, "sound", s.Sound);
        if (args.TryGetProperty("button_sounds", out _)) s.ButtonSounds = Bool(args, "button_sounds", s.ButtonSounds);
        if (args.TryGetProperty("white_noise", out _)) s.WhiteNoise = Bool(args, "white_noise", s.WhiteNoise);
        if (args.TryGetProperty("alarm_enabled", out _)) s.AlarmEnabled = Bool(args, "alarm_enabled", s.AlarmEnabled);
        if (args.TryGetProperty("white_noise_volume", out _)) s.WhiteNoiseVolume = Int(args, "white_noise_volume", s.WhiteNoiseVolume);
        if (args.TryGetProperty("alarm_volume", out _)) s.AlarmVolume = Int(args, "alarm_volume", s.AlarmVolume);
        if (args.TryGetProperty("effects_volume", out _)) s.EffectsVolume = Int(args, "effects_volume", s.EffectsVolume);
        if (args.TryGetProperty("language", out _)) s.Language = Text(args, "language", s.Language);
        if (args.TryGetProperty("dark", out _)) s.Dark = Bool(args, "dark", s.Dark);
        if (args.TryGetProperty("reduce_motion", out _)) s.ReduceMotion = Bool(args, "reduce_motion", s.ReduceMotion);
        if (args.TryGetProperty("always_on_top", out _)) s.AlwaysOnTop = Bool(args, "always_on_top", s.AlwaysOnTop);
        if (args.TryGetProperty("timer_at_bottom", out _)) s.TimerAtBottom = Bool(args, "timer_at_bottom", s.TimerAtBottom);
        if (args.TryGetProperty("agent_voice_enabled", out _)) s.AgentVoiceEnabled = Bool(args, "agent_voice_enabled", s.AgentVoiceEnabled);
        if (args.TryGetProperty("agent_voice_speed", out var speed) && speed.TryGetDouble(out double pace)) s.AgentVoiceSpeed = pace;
        if (args.TryGetProperty("agent_memory_enabled", out _)) s.AgentMemoryEnabled = Bool(args, "agent_memory_enabled", s.AgentMemoryEnabled);
        if (args.TryGetProperty("agent_send_voice_on_release", out _)) s.AgentSendVoiceOnRelease = Bool(args, "agent_send_voice_on_release", s.AgentSendVoiceOnRelease);
        if (args.TryGetProperty("accent_color", out _)) s.AccentColor = Text(args, "accent_color", s.AccentColor);
        if (args.TryGetProperty("focus_color", out _)) s.FocusColor = Text(args, "focus_color", s.FocusColor);
        if (args.TryGetProperty("short_break_color", out _)) s.ShortBreakColor = Text(args, "short_break_color", s.ShortBreakColor);
        if (args.TryGetProperty("long_break_color", out _)) s.LongBreakColor = Text(args, "long_break_color", s.LongBreakColor);
        if (args.TryGetProperty("alarm_repeats", out _)) s.AlarmRepeats = Int(args, "alarm_repeats", s.AlarmRepeats);
        owner.ApplyLiveSettings(); owner.SaveState();
        return Result(true, true, "Ajustes actualizados", null, "settings.update", JsonSerializer.Serialize(previous));
    }

    private AgentToolResult ClearDoneTodos()
    {
        var store = TodoStore.For(owner.Store);
        int removed = store.Book.ClearDone();
        store.Save();
        return Result(true, true, removed == 0 ? "No había tareas hechas" : $"Eliminé {removed} tareas hechas");
    }

    private AgentToolResult SortTodos()
    {
        var store = TodoStore.For(owner.Store);
        store.Book.SortByUrgency(DateOnly.FromDateTime(DateTime.Today));
        store.Save();
        return Result(true, true, "Tareas ordenadas por urgencia");
    }

    private AgentToolResult RestoreHabit(string query)
    {
        var store = HabitStore.For(owner.Store);
        var habit = FindOne(store.Book.Habits.Where(x => x.Archived), x => x.Name, query, "hábito", x => x.Id);
        habit.Archived = false; store.Save();
        return Result(true, true, $"Hábito restaurado: {habit.Name}");
    }

    private AgentToolResult DeleteHabitForever(string query)
    {
        var store = HabitStore.For(owner.Store);
        var habit = FindOne(store.Book.Habits, x => x.Name, query, "hábito", x => x.Id);
        if (!Confirm($"Borrar para siempre el hábito: {habit.Name}")) return Result(false, false, "El usuario canceló el borrado");
        store.Book.Habits.Remove(habit); store.Save();
        return Result(true, true, $"Hábito eliminado: {habit.Name}");
    }

    private AgentToolResult SetHabitTarget(JsonElement args)
    {
        var store = HabitStore.For(owner.Store);
        var habit = FindOne(store.Book.Habits.Where(x => !x.Archived), x => x.Name, Required(args, "name"), "hábito", x => x.Id);
        int previous = habit.Target;
        habit.Target = Math.Clamp(Int(args, "target", habit.Target), 1, 20);
        store.Save();
        return Result(true, true, $"Meta de {habit.Name}: {habit.Target}/día", null,
            "habits.set_target", JsonSerializer.Serialize(new { name = habit.Name, target = previous }));
    }

    private AgentToolResult SetFinanceCurrency(JsonElement args)
    {
        var ledger = FinanceStore.For(owner.Store);
        string previous = ledger.Book.Currency;
        string code = Required(args, "currency").Trim().ToUpperInvariant();
        if (code.Length is < 1 or > 8) throw new ArgumentException("Código de moneda inválido");
        ledger.SetCurrency(code);
        return Result(true, true, $"Moneda: {code}", null, "finance.set_currency", JsonSerializer.Serialize(new { currency = previous }));
    }

    private AgentToolResult SetNoteColor(JsonElement args)
    {
        string title = Required(args, "title");
        string color = NormalizeNoteColor(Text(args, "color", "yellow"));

        var card = owner.ResolveNotesCardForAgent(title);
        if (card is not null && card.AgentSetNoteColor(color))
            return Result(true, true, $"Color de nota: {color}", null, "notes.set_color",
                JsonSerializer.Serialize(new { title = card.Config.Title, color }));

        var config = owner.Settings.WorkspacePages.SelectMany(page => page.Widgets)
            .FirstOrDefault(widget => widget.Kind == "notes" && widget.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No encontré nota que coincida con '{title}'");
        NotesWidgetData data;
        try { data = JsonSerializer.Deserialize<NotesWidgetData>(config.Value) ?? new NotesWidgetData(); }
        catch (JsonException) { data = new NotesWidgetData(); }
        string previous = data.Color;
        data.Color = color;
        data.UpdatedUtc = DateTime.UtcNow;
        config.Value = JsonSerializer.Serialize(data);
        owner.SaveState();
        NoteArchiveStore.For(owner.Store).Track(config);
        return Result(true, true, $"Color de nota: {color}", null, "notes.set_color",
            JsonSerializer.Serialize(new { title = config.Title, color = previous }));
    }

    private AgentToolResult ListFocusTasks()
    {
        var items = owner.Settings.Tasks.Where(task => !task.Template).Select(task => new { task.Id, task.Name, task.Project, task.Estimate, task.Done }).ToList();
        return Result(true, false, $"{items.Count} tareas de enfoque", items);
    }

    private AgentToolResult UpdateFocusTask(JsonElement args)
    {
        string name = Required(args, "name");
        var task = FindOne(owner.Settings.Tasks.Where(t => !t.Template), x => x.Name, name, "tarea de enfoque", x => x.Id);
        if (args.TryGetProperty("new_name", out _))
        {
            string next = Text(args, "new_name", task.Name).Trim();
            if (next.Length > 0) task.Name = next;
        }
        if (args.TryGetProperty("project", out _))
        {
            task.Project = Text(args, "project", task.Project);
            if (!owner.Settings.Projects.Contains(task.Project, StringComparer.OrdinalIgnoreCase))
                owner.Settings.Projects.Add(task.Project);
        }
        if (args.TryGetProperty("estimate", out _)) task.Estimate = Math.Clamp(Int(args, "estimate", task.Estimate), 1, 99);
        if (args.TryGetProperty("done", out _)) task.Done = Bool(args, "done", task.Done);
        owner.SaveState();
        return Result(true, true, $"Tarea de enfoque actualizada: {task.Name}");
    }

    private AgentToolResult SetFocusTaskDone(JsonElement args)
    {
        string name = Required(args, "name");
        var task = FindOne(owner.Settings.Tasks.Where(t => !t.Template), x => x.Name, name, "tarea de enfoque", x => x.Id);
        bool done = Bool(args, "done", true);
        task.Done = done;
        owner.SaveState();
        return Result(true, true, done
            ? $"Tarea de enfoque marcada hecha: {task.Name}"
            : $"Tarea de enfoque reabierta: {task.Name}", null,
            "focus_tasks.set_done", JsonSerializer.Serialize(new { name = task.Name, done = !done }));
    }

    private AgentToolResult DeleteFocusTask(JsonElement args)
    {
        string name = Required(args, "name");
        var task = FindOne(owner.Settings.Tasks.Where(t => !t.Template), x => x.Name, name, "tarea de enfoque", x => x.Id);
        if (!Confirm($"Eliminar tarea de enfoque: {task.Name}")) return Result(false, false, "El usuario canceló el borrado");
        owner.Settings.Tasks.Remove(task);
        owner.SaveState();
        return Result(true, true, $"Tarea de enfoque eliminada: {task.Name}");
    }

    private AgentToolResult SaveFocusTemplate(JsonElement args)
    {
        string name = Required(args, "name");
        var task = FindOne(owner.Settings.Tasks.Where(t => !t.Template), x => x.Name, name, "tarea de enfoque", x => x.Id);
        var existing = owner.Settings.Tasks.FirstOrDefault(other => other.Template && other.Name == task.Name && other.Project == task.Project);
        if (existing is not null)
            return Result(true, false, $"Ya era plantilla: {task.Name}");
        owner.Settings.Tasks.Add(new WorkTask { Name = task.Name, Project = task.Project, Estimate = task.Estimate, Template = true });
        owner.SaveState();
        return Result(true, true, $"Plantilla guardada: {task.Name}");
    }

    private AgentToolResult AddFocusFromTemplate(JsonElement args)
    {
        string name = Required(args, "name");
        var template = FindOne(owner.Settings.Tasks.Where(t => t.Template), x => x.Name, name, "plantilla de enfoque", x => x.Id);
        owner.Settings.Tasks.Add(new WorkTask { Name = template.Name, Project = template.Project, Estimate = template.Estimate, Template = false });
        owner.SaveState();
        return Result(true, true, $"Tarea creada desde plantilla: {template.Name}", new { template.Name, template.Project });
    }

    private AgentToolResult ListFocusProjects()
    {
        var projects = owner.Settings.Projects.Concat(owner.Settings.Tasks.Select(t => t.Project))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return Result(true, false, $"{projects.Count} proyectos", projects);
    }

    private AgentToolResult AddFocusProject(JsonElement args)
    {
        string name = Required(args, "name").Trim();
        if (owner.Settings.Projects.Contains(name, StringComparer.OrdinalIgnoreCase))
            return Result(false, false, $"El proyecto «{name}» ya existe.");
        owner.Settings.Projects.Add(name);
        owner.SaveState();
        return Result(true, true, $"Proyecto creado: {name}");
    }

    private AgentToolResult RenameFocusProject(JsonElement args)
    {
        string old = Required(args, "name");
        string next = Required(args, "new_name").Trim();
        int index = owner.Settings.Projects.FindIndex(p => string.Equals(p, old, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new InvalidOperationException($"No encontré proyecto '{old}'");
        if (owner.Settings.Projects.Any(p => string.Equals(p, next, StringComparison.OrdinalIgnoreCase) && !string.Equals(p, old, StringComparison.OrdinalIgnoreCase)))
            return Result(false, false, $"Ya existe un proyecto «{next}».");
        string previous = owner.Settings.Projects[index];
        owner.Settings.Projects[index] = next;
        foreach (var task in owner.Settings.Tasks.Where(task => task.Project == previous)) task.Project = next;
        owner.SaveState();
        return Result(true, true, $"Proyecto renombrado: {previous} → {next}");
    }

    private AgentToolResult DeleteFocusProject(JsonElement args)
    {
        string name = Required(args, "name");
        if (!owner.Settings.Projects.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"No encontré proyecto '{name}'");
        int count = owner.Settings.Tasks.Count(task => !task.Template && string.Equals(task.Project, name, StringComparison.OrdinalIgnoreCase));
        if (!Confirm(count == 0 ? $"Borrar proyecto «{name}»" : $"Borrar proyecto «{name}» ({count} tareas quedan sin proyecto)"))
            return Result(false, false, "El usuario canceló el borrado");
        owner.Settings.Projects.RemoveAll(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
        foreach (var task in owner.Settings.Tasks.Where(task => string.Equals(task.Project, name, StringComparison.OrdinalIgnoreCase)))
            task.Project = "Sin proyecto";
        owner.SaveState();
        return Result(true, true, $"Proyecto eliminado: {name}");
    }

    private AgentToolResult MemorySearch(JsonElement args)
    {
        if (!owner.Settings.AgentMemoryEnabled) return Result(true, false, "La memoria personal está desactivada", Array.Empty<object>());
        var items = memory.Book.Search(Text(args, "query", "")).Select(item => new { item.Id, item.Kind, item.Content, Status = item.Status.ToString() }).ToList();
        return Result(true, false, $"{items.Count} recuerdos", items);
    }

    private AgentToolResult MemoryWrite(JsonElement args, bool confirm)
    {
        if (!owner.Settings.AgentMemoryEnabled) return Result(false, false, "La memoria personal está desactivada");
        string text = Required(args, "text");
        string kind = Text(args, "kind", "preference");
        var item = confirm ? memory.Book.Remember(text, kind) : memory.Book.Propose(text, kind);
        if (confirm) item.Status = AgentMemoryStatus.Confirmed;
        memory.Save();
        return Result(true, true, confirm ? $"Recuerdo guardado: {item.Content}" : $"Recuerdo propuesto: {item.Content}", new { item.Id },
            "memory.forget", JsonSerializer.Serialize(new { text = item.Content }));
    }

    private AgentToolResult MemoryForget(JsonElement args)
    {
        if (!memory.Book.Forget(Required(args, "text"))) return Result(false, false, "No encontré un recuerdo inequívoco para olvidar");
        memory.Save();
        return Result(true, true, "Recuerdo olvidado");
    }

    private AgentToolResult MemoryExport()
    {
        var path = System.IO.Path.Combine(owner.Store.DirectoryPath, "agent-memory.json");
        System.IO.File.WriteAllText(path, memory.ExportJson());
        return Result(true, true, $"Memoria exportada: {path}", new { path });
    }

    private object FinanceSnapshot(DateOnly? day = null)
    {
        var month = FinanceStore.For(owner.Store).Book.Month(day ?? DateOnly.FromDateTime(DateTime.Today));
        return new
        {
            from = month.Month.ToString("yyyy-MM"),
            income = month.Recorded.Income,
            expense = month.Recorded.Expense,
            net = month.Recorded.Net,
            projected = month.ProjectedNet,
            obligations = month.RecurringExpense,
            subscriptions = month.Subscriptions.Select(item => new { item.Item.Title, item.Item.Amount, item.Recorded }),
            fixedCosts = month.Fixed.Select(item => new { item.Item.Title, item.Item.Amount, item.Recorded })
        };
    }

    private AgentToolResult FinanceSummary(JsonElement args)
    {
        DateOnly month = OptionalDate(args, "month") ?? DateOnly.FromDateTime(DateTime.Today);
        var snapshot = FinanceStore.For(owner.Store).Book.Month(month);
        return Result(true, false, $"Finanzas {snapshot.Month:MM/yyyy}: ingreso {snapshot.Recorded.Income:0.##}, gasto {snapshot.Recorded.Expense:0.##}, neto {snapshot.Recorded.Net:0.##}", FinanceSnapshot(month));
    }

    private AgentToolResult FinanceSearch(JsonElement args)
    {
        string query = Text(args, "query", "");
        var book = FinanceStore.For(owner.Store).Book;
        var entries = book.Entries.Where(item => query.Length == 0 || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Date).Take(30)
            .Select(item => new { item.Id, item.Title, item.Amount, Flow = item.Flow.ToString(), Date = item.Date.ToString("yyyy-MM-dd"), item.Category }).ToList();
        var recurring = book.Active.Where(item => query.Length == 0 || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(item => new { item.Id, item.Title, item.Amount, item.Subscription, item.Category }).ToList();
        return Result(true, false, $"{entries.Count} movimientos · {recurring.Count} recurrentes", new { entries, recurring });
    }

    private AgentToolResult FinanceAdd(JsonElement args)
    {
        var parsed = FinanceQuickAdd.Parse(Required(args, "text"), DateOnly.FromDateTime(DateTime.Today));
        if (parsed is RecurringMoney recurring)
        {
            var saved = FinanceStore.For(owner.Store).AddRecurring(recurring.Title, recurring.Amount, recurring.Subscription, recurring.Category, recurring.Flow, recurring.DayOfMonth, recurring.Repeat);
            return Result(true, true, $"{(saved.Subscription ? "Suscripción" : "Gasto fijo")} : {saved.Title} · {saved.Amount}", new { saved.Id });
        }
        if (parsed is not MoneyEntry entry) return Result(false, false, "No pude interpretar el movimiento");
        var store = FinanceStore.For(owner.Store);
        var added = store.AddEntry(entry.Flow, entry.Amount, entry.Title, entry.Date, entry.Category);
        return Result(true, true, $"{(added.Flow == MoneyFlow.Income ? "Ingreso" : "Gasto")} : {added.Title} · {added.Amount}", new { added.Id },
            "finance.delete", JsonSerializer.Serialize(new { id = added.Id }));
    }

    private AgentToolResult FinanceRecurring(JsonElement args)
    {
        bool sub = Bool(args, "subscription", true);
        var item = FinanceStore.For(owner.Store).AddRecurring(Required(args, "title"), decimal.Parse(Text(args, "amount", "0"), CultureInfo.InvariantCulture), sub, Text(args, "category", sub ? "ai" : "rent"));
        return Result(true, true, $"{(sub ? "Suscripción" : "Gasto fijo")} creada: {item.Title} · {item.Amount}", new { item.Id });
    }

    private AgentToolResult FinancePaid(JsonElement args)
    {
        var store = FinanceStore.For(owner.Store);
        var item = store.Book.RecurringBy(Required(args, "title")) ?? throw new InvalidOperationException("No encontré esa suscripción o gasto fijo");
        var entry = store.MarkPaid(item, OptionalDate(args, "month") ?? DateOnly.FromDateTime(DateTime.Today));
        return entry is null ? Result(true, false, $"Ya estaba marcado este mes: {item.Title}") : Result(true, true, $"Pagado este mes: {item.Title} · {item.Amount}");
    }

    private AgentToolResult FinanceDelete(JsonElement args)
    {
        var store = FinanceStore.For(owner.Store);
        if (Guid.TryParse(Text(args, "id", ""), out var id))
        {
            var entry = store.Book.Entries.FirstOrDefault(item => item.Id == id) ?? throw new InvalidOperationException("No encontré ese movimiento");
            if (!Confirm($"Eliminar movimiento: {entry.Title}")) return Result(false, false, "El usuario canceló la eliminación");
            store.RemoveEntry(entry);
            return Result(true, true, $"Movimiento eliminado: {entry.Title}");
        }
        string title = Required(args, "title");
        var matches = store.Book.Entries.Where(item => item.Title.Contains(title, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1) throw new InvalidOperationException("Usa un título más específico o el id del movimiento");
        if (!Confirm($"Eliminar movimiento: {matches[0].Title}")) return Result(false, false, "El usuario canceló la eliminación");
        store.RemoveEntry(matches[0]);
        return Result(true, true, $"Movimiento eliminado: {matches[0].Title}");
    }

    private AgentToolResult FinanceClearExpenses(JsonElement args)
    {
        var store = FinanceStore.For(owner.Store);
        DateOnly? month = OptionalDate(args, "month");
        var victims = store.Book.Entries
            .Where(entry => entry.Flow == MoneyFlow.Expense)
            .Where(entry => month is null || (entry.Date.Year == month.Value.Year && entry.Date.Month == month.Value.Month))
            .ToList();
        if (victims.Count == 0)
            return Result(true, false, month is null ? "No había gastos que borrar." : $"No había gastos en {month:MM/yyyy}.");
        string scope = month is null ? "todos los gastos" : $"los {victims.Count} gastos de {month:MM/yyyy}";
        if (!Confirm($"Eliminar {scope} ({victims.Count})"))
            return Result(false, false, "El usuario canceló la eliminación");
        foreach (var entry in victims) store.RemoveEntry(entry);
        return Result(true, true, $"Eliminé {victims.Count} gasto(s).");
    }

    private AgentToolResult FinanceArchive(JsonElement args)
    {
        var store = FinanceStore.For(owner.Store);
        var item = store.Book.RecurringBy(Required(args, "title")) ?? throw new InvalidOperationException("No encontré esa suscripción o gasto fijo");
        if (!Confirm($"Archivar: {item.Title}")) return Result(false, false, "El usuario canceló el archivo");
        store.Archive(item);
        return Result(true, true, $"Archivado: {item.Title}");
    }

    private AgentToolResult ListSounds(JsonElement args)
    {
        string kindName = Text(args, "kind", "ambient").ToLowerInvariant();
        var kind = kindName switch
        {
            "alarm" or "alarma" => SoundKind.Alarm,
            "reminder" or "recordatorio" => SoundKind.Reminder,
            "click" or "clic" => SoundKind.Click,
            _ => SoundKind.Ambient
        };
        var items = SoundLibrary.OfKind(kind).Select(sound => new { sound.Id, sound.Name }).ToList();
        string names = string.Join(", ", items.Select(item => item.Name));
        return Result(true, false, names, items);
    }

    private AgentToolResult SetSound(JsonElement args)
    {
        string slot = Text(args, "slot", "ambient").ToLowerInvariant();
        string id = Text(args, "id", Text(args, "name", ""));
        var kind = slot switch
        {
            "focus_end" or "break_end" => SoundKind.Alarm,
            "reminder" => SoundKind.Reminder,
            "click" => SoundKind.Click,
            _ => SoundKind.Ambient
        };
        var sound = SoundLibrary.Resolve(id, kind) ?? SoundLibrary.Resolve(id)
            ?? throw new InvalidOperationException($"No encontré el sonido '{id}'");
        var s = owner.Settings;
        if (slot is "focus_end") s.FocusEndSound = SoundLibrary.Valid(sound.Id, SoundKind.Alarm);
        else if (slot is "break_end") s.BreakEndSound = SoundLibrary.Valid(sound.Id, SoundKind.Alarm);
        else if (slot is "reminder") s.ReminderSound = SoundLibrary.Valid(sound.Id, SoundKind.Reminder);
        else if (slot is "click") s.ClickSound = SoundLibrary.Valid(sound.Id, SoundKind.Click);
        else
        {
            s.AmbientSound = SoundLibrary.Valid(sound.Id, SoundKind.Ambient);
            s.WhiteNoise = true;
            s.Sound = true;
        }
        owner.ApplyLiveSettings();
        owner.SaveState();
        return Result(true, true, $"Sonido: {sound.Name}");
    }

    private bool Confirm(string text) =>
        PlanApproved || Dialogs.Choose(owner, "CONFIRMAR ACCIÓN", [text, L.T("common.cancel")]) == 0;

    /// <summary>Maps Spanish/English colour words and hex onto NoteSkin keys.</summary>
    private static string NormalizeNoteColor(string? raw)
    {
        string value = (raw ?? "").Trim();
        if (value.Length == 0) return "paper";
        string key = value.ToLowerInvariant();
        key = key switch
        {
            "blanco" or "white" or "papel" or "paper" => "paper",
            "amarillo" or "yellow" => "yellow",
            "menta" or "verde" or "mint" or "green" => "mint",
            "azul" or "blue" => "blue",
            "rosa" or "rose" or "pink" => "rose",
            "lila" or "lavender" or "lilac" or "morado" or "purple" => "lilac",
            _ => key
        };
        if (key is "paper" or "yellow" or "mint" or "blue" or "rose" or "lilac") return key;
        if (NoteSkin.TryParse(value, out _)) return value.StartsWith('#') ? value : "#" + value.TrimStart('#');
        return "paper";
    }

    private static T FindOne<T>(IEnumerable<T> source, Func<T, string> label, string query, string kind, Func<T, Guid>? id = null)
    {
        if (id is not null && Guid.TryParse(query, out var guid))
        {
            var byId = source.Where(item => id(item) == guid).ToList();
            if (byId.Count == 1) return byId[0];
        }
        var exact = source.Where(x => string.Equals(label(x), query, StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = exact.Count > 0 ? exact : source.Where(x => label(x).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) throw new InvalidOperationException($"No encontré {kind} que coincida con '{query}'");
        if (matches.Count > 1) throw new InvalidOperationException($"Hay varios resultados para '{query}'; usa un título más específico");
        return matches[0];
    }

    private static string Required(JsonElement args, string key)
    {
        var value = Text(args, key, "").Trim();
        return value.Length == 0 ? throw new ArgumentException($"Falta {key}") : value;
    }
    private static string Text(JsonElement args, string key, string fallback) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    private static bool Bool(JsonElement args, string key, bool fallback) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
    private static int Int(JsonElement args, string key, int fallback) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var value) && value.TryGetInt32(out int number) ? number : fallback;
    private static List<int> IntArray(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.TryGetInt32(out _)).Select(item => Math.Clamp(item.GetInt32(), 0, 20160)).Distinct().ToList()
            : [];
    private static RepeatKind Repeat(JsonElement args) => Enum.TryParse<RepeatKind>(Text(args, "repeat", "none"), true, out var value) ? value : RepeatKind.None;
    private static List<DayOfWeek> Days(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("days", out var value) || value.ValueKind != JsonValueKind.Array) return [];
        var days = new List<DayOfWeek>();
        foreach (var item in value.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && Enum.TryParse<DayOfWeek>(item.GetString(), true, out var day)) days.Add(day);
        return days.Distinct().ToList();
    }
    private static DateOnly Date(JsonElement args, string key, DateOnly fallback) => DateOnly.TryParseExact(Text(args, "date", Text(args, key, "")), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : fallback;
    private static DateOnly? OptionalDate(JsonElement args, string key) => DateOnly.TryParseExact(Text(args, key, ""), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    private static AgentToolResult Result(bool success, bool changed, string summary, object? extra = null, string? undoTool = null, string? undoArgs = null) =>
        new(changed, summary, JsonSerializer.Serialize(new { success, changed, summary, data = extra }), success, undoTool, undoArgs);
}
