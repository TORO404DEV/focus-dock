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
                "add_event" or "calendar.create" => AddEvent(args),
                "calendar.update" or "calendar.reschedule" => UpdateEvent(args),
                "complete_event" or "calendar.complete" => CompleteEvent(Required(args, "title"), Date(args, "date", DateOnly.FromDateTime(DateTime.Today)), Bool(args, "done", true)),
                "delete_event" or "calendar.delete" => DeleteEvent(Required(args, "title")),
                "habits.search" => SearchHabits(args),
                "add_habit" or "habits.create" => AddHabit(args),
                "mark_habit" or "habits.mark" => MarkHabit(Required(args, "name"), Date(args, "date", DateOnly.FromDateTime(DateTime.Today)), Bool(args, "done", true)),
                "habits.update" => UpdateHabit(args),
                "archive_habit" or "habits.archive" => ArchiveHabit(Required(args, "name")),
                "notes.search" => SearchNotes(args),
                "add_note" or "notes.create" => AddNote(args),
                "notes.append" => AppendNote(args),
                "notes.format" or "notes.check" => AppendNote(args),
                "add_widget" or "workspace.add_widget" => AddWidget(args),
                "workspace.list" => State("widgets"),
                "finance.summary" => FinanceSummary(args),
                "finance.search" => FinanceSearch(args),
                "finance.add" => FinanceAdd(args),
                "finance.add_recurring" => FinanceRecurring(args),
                "finance.mark_paid" => FinancePaid(args),
                "finance.delete" => FinanceDelete(args),
                "finance.archive" => FinanceArchive(args),
                "workspace.move_widget" => MoveWidget(args),
                "workspace.open" => AddWidget(args),
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
            "widgets" => owner.Settings.WorkspacePages.Select((p, i) => new { Page = i + 1, p.Name, Widgets = p.Widgets.Select(w => new { w.Id, w.Kind, w.Title, w.X, w.Y }) }),
            "focus_tasks" => owner.Settings.Tasks.Select(x => new { x.Id, x.Name, x.Project, x.Estimate, x.Done }),
            "focus_history" => FocusSnapshot("this_month"),
            "finance" => FinanceSnapshot(),
            _ => new { Todos = todos, Calendar = events, Habits = habits, Timer = new { Phase = owner.Timer.Phase.ToString(), owner.Timer.Running, RemainingMinutes = Math.Ceiling(owner.Timer.Remaining / 60) }, FocusTasks = owner.Settings.Tasks.Where(x => !x.Done).Select(x => new { x.Name, x.Project }), FocusHistory = FocusSnapshot("this_month"), Finance = FinanceSnapshot() }
        };
        return new AgentToolResult(false, "", JsonSerializer.Serialize(new { success = true, state = value }));
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
        string subject = summary.Project is null ? "el enfoque" : $"el proyecto {summary.Project}";
        return Result(true, false, $"Resumen de {subject}: {summary.Minutes:0.#} min ({Math.Round(summary.Minutes / 60, 2)} h) entre {summary.Range.From:dd/MM/yyyy} y {summary.Range.To:dd/MM/yyyy}", data);
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
        string previous = JsonSerializer.Serialize(new { title = item.Title, due = item.Due?.ToString("yyyy-MM-dd"), priority = item.Priority.ToString() });
        string newTitle = Text(args, "new_title", "");
        if (newTitle.Length > 0) item.Title = newTitle;
        if (OptionalDate(args, "due") is { } due) item.Due = due;
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
        var items = AgendaStore.For(owner.Store).Book.Events
            .Where(item => query.Length == 0 || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Start)
            .Select(item => new { item.Id, item.Title, Start = item.Start.ToString("yyyy-MM-dd HH:mm"), item.Minutes, item.AllDay })
            .Take(40).ToList();
        return Result(true, false, $"{items.Count} eventos", items);
    }

    private AgentToolResult AddEvent(JsonElement args)
    {
        string title = Required(args, "title"); bool allDay = Bool(args, "all_day", false);
        DateTime start = DateTime.TryParseExact(Required(args, "start"), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed : throw new ArgumentException("start debe usar yyyy-MM-dd HH:mm");
        var item = new AgendaEvent
        {
            Title = title, Start = start, AllDay = allDay,
            Minutes = allDay ? 1440 : Int(args, "minutes", 60),
            Notes = Text(args, "notes", ""), Reminders = [Math.Clamp(Int(args, "reminder_minutes", 10), 0, 20160)]
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
        string newTitle = Text(args, "new_title", "");
        if (newTitle.Length > 0) item.Title = newTitle;
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

    private AgentToolResult DeleteEvent(string query)
    {
        var store = AgendaStore.For(owner.Store); var item = FindOne(store.Book.Events, x => x.Title, query, "evento", x => x.Id);
        if (!Confirm($"Eliminar evento: {item.Title}")) return Result(false, false, "El usuario canceló la eliminación");
        store.Remove(item); return Result(true, true, $"Evento eliminado: {item.Title}");
    }

    private AgentToolResult SearchHabits(JsonElement args)
    {
        string query = Text(args, "query", "");
        var items = HabitStore.For(owner.Store).Book.Habits
            .Where(item => query.Length == 0 || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(item => new { item.Id, item.Name, item.Archived, Cadence = item.Cadence.ToString() })
            .ToList();
        return Result(true, false, $"{items.Count} hábitos", items);
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
        else if (cadence == "weekly") habit.Cadence = HabitCadence.Weekly;
        store.Save();
        return Result(true, true, $"Hábito actualizado: {habit.Name}");
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
        string title = Text(args, "title", "NOTAS"); string noteText = Required(args, "text");
        string color = Text(args, "color", "paper").ToLowerInvariant();
        if (color is not ("paper" or "yellow" or "mint" or "blue" or "rose" or "lavender")) color = "paper";
        var document = new FlowDocument();
        document.Blocks.Add(new Paragraph(new Run(noteText)));
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
        return Result(true, true, $"Nota creada: {title} · {color}", new { config.Id, color });
    }

    private AgentToolResult AppendNote(JsonElement args)
    {
        string title = Required(args, "title"); string extra = Required(args, "text");
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

    private AgentToolResult AddWidget(JsonElement args)
    {
        string kind = Required(args, "kind").ToLowerInvariant();
        if (kind == "timer") return owner.AddTimerFromAgent();
        if (kind is not ("notes" or "todo" or "habits" or "calendar" or "stats" or "finance")) throw new ArgumentException("Tipo de widget no permitido");
        string title = Text(args, "title", kind.ToUpperInvariant());
        var config = new WidgetConfig { Kind = kind, Title = title }; owner.AddCard(config, true);
        return Result(true, true, $"Widget añadido: {title}", new { config.Id });
    }

    private AgentToolResult MoveWidget(JsonElement args)
    {
        string title = Required(args, "title");
        var config = owner.Settings.WorkspacePages.SelectMany(page => page.Widgets)
            .FirstOrDefault(widget => widget.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No encontré widget que coincida con '{title}'");
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
        var data = new { s.FocusMinutes, s.ShortMinutes, s.LongMinutes, s.LongInterval, s.DailyGoalMinutes, s.AutoBreak, s.AutoFocus, s.Language, s.AgentVoiceEnabled, s.AgentMemoryEnabled };
        return Result(true, false, "Ajustes actuales", data);
    }

    private AgentToolResult UpdateSettings(JsonElement args)
    {
        var s = owner.Settings;
        var previous = new { s.FocusMinutes, s.ShortMinutes, s.LongMinutes, s.DailyGoalMinutes };
        if (args.TryGetProperty("focus_minutes", out _)) s.FocusMinutes = Int(args, "focus_minutes", s.FocusMinutes);
        if (args.TryGetProperty("short_minutes", out _)) s.ShortMinutes = Int(args, "short_minutes", s.ShortMinutes);
        if (args.TryGetProperty("long_minutes", out _)) s.LongMinutes = Int(args, "long_minutes", s.LongMinutes);
        if (args.TryGetProperty("daily_goal_minutes", out _)) s.DailyGoalMinutes = Int(args, "daily_goal_minutes", s.DailyGoalMinutes);
        owner.ApplyLiveSettings(); owner.SaveState();
        return Result(true, true, "Ajustes actualizados", null, "settings.update", JsonSerializer.Serialize(new
        {
            focus_minutes = previous.FocusMinutes,
            short_minutes = previous.ShortMinutes,
            long_minutes = previous.LongMinutes,
            daily_goal_minutes = previous.DailyGoalMinutes
        }));
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
        var entry = store.MarkPaid(item, DateOnly.FromDateTime(DateTime.Today));
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

    private AgentToolResult FinanceArchive(JsonElement args)
    {
        var store = FinanceStore.For(owner.Store);
        var item = store.Book.RecurringBy(Required(args, "title")) ?? throw new InvalidOperationException("No encontré esa suscripción o gasto fijo");
        if (!Confirm($"Archivar: {item.Title}")) return Result(false, false, "El usuario canceló el archivo");
        store.Archive(item);
        return Result(true, true, $"Archivado: {item.Title}");
    }

    private bool Confirm(string text) => Dialogs.Choose(owner, "CONFIRMAR ACCIÓN", [text, L.T("common.cancel")]) == 0;

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
    private static DateOnly Date(JsonElement args, string key, DateOnly fallback) => DateOnly.TryParseExact(Text(args, "date", Text(args, key, "")), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : fallback;
    private static DateOnly? OptionalDate(JsonElement args, string key) => DateOnly.TryParseExact(Text(args, key, ""), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    private static AgentToolResult Result(bool success, bool changed, string summary, object? extra = null, string? undoTool = null, string? undoArgs = null) =>
        new(changed, summary, JsonSerializer.Serialize(new { success, changed, summary, data = extra }), success, undoTool, undoArgs);
}
