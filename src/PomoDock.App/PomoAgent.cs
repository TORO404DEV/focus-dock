using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Documents;
using PomoDock.Core;

namespace PomoDock.App;

internal sealed record AgentReceipt(string Tool, string Summary);
internal sealed record AgentRunResult(string Message, IReadOnlyList<AgentReceipt> Receipts);

/// <summary>
/// A bounded observe/act loop. The model may decide what to do, but every effect passes through
/// <see cref="AgentToolbox"/>; generated prose can never touch the database or the desktop.
/// </summary>
internal sealed class PomoAgent
{
    private readonly MainWindow owner;
    private readonly Func<string, IAgentConversation> beginConversation;
    private readonly AgentToolbox tools;

    public PomoAgent(MainWindow owner, LocalAgentModel model) : this(owner, model.Begin) { }

    internal PomoAgent(MainWindow owner, Func<string, IAgentConversation> beginConversation)
    {
        this.owner = owner;
        this.beginConversation = beginConversation;
        tools = new AgentToolbox(owner);
    }

    public async Task<AgentRunResult> RunAsync(string request, Action<string>? activity, CancellationToken cancellationToken)
    {
        var receipts = new List<AgentReceipt>();
        var seenAtRevision = new Dictionary<string, AgentToolResult>(StringComparer.Ordinal);
        var completedMutations = new HashSet<string>(StringComparer.Ordinal);
        int stateRevision = 0, duplicateWarnings = 0, malformedReplies = 0;
        string lastSummary = "";
        using var conversation = beginConversation(SystemPrompt());
        string input = $"""
            FECHA LOCAL: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({TimeZoneInfo.Local.DisplayName})
            IDIOMA DE LA INTERFAZ: {Strings.Culture.TwoLetterISOLanguageName}
            PETICIÓN: {request.Trim()}
            Empieza. Si para decidir necesitas ver datos reales, usa get_state.
            /no_think
            """;

        for (int step = 0; step < 12; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            activity?.Invoke(step == 0 ? L.T("agent.working") : $"{L.T("agent.working")}  {step + 1:00}/12");
            var raw = await conversation.AskAsync(input, cancellationToken);
            if (!TryReadTurn(raw, out var turn, out var error))
            {
                malformedReplies++;
                if (malformedReplies >= 3)
                    return StopResult(receipts, "El modelo local no produjo una acción válida después de tres intentos.", "The local model did not produce a valid action after three attempts.");
                input = $"""
                    Tu respuesta no cumplió el protocolo ({error}). Devuelve únicamente un objeto JSON válido,
                    sin Markdown ni explicación, con action y arguments. Respuesta anterior: {raw}
                    /no_think
                    """;
                continue;
            }
            malformedReplies = 0;
            if (string.Equals(turn.Action, "finish", StringComparison.OrdinalIgnoreCase))
            {
                var message = string.IsNullOrWhiteSpace(turn.Message)
                    ? (Strings.Culture.TwoLetterISOLanguageName == "es" ? "Listo." : "Done.")
                    : turn.Message.Trim();
                return new AgentRunResult(message, receipts);
            }

            string action = turn.Action.Trim().ToLowerInvariant();
            var kind = AgentToolbox.KindOf(action);
            string fingerprint = Fingerprint(action, turn.Arguments);
            string revisionKey = $"{stateRevision}:{fingerprint}";
            bool repeatedCompletedMutation = (kind is AgentToolKind.Mutation or AgentToolKind.Destructive or AgentToolKind.Control)
                && completedMutations.Contains(fingerprint);
            seenAtRevision.TryGetValue(revisionKey, out var previous);
            if (repeatedCompletedMutation || previous is not null)
            {
                duplicateWarnings++;
                var prior = previous?.Json ?? JsonSerializer.Serialize(new { success = true, summary = "La acción ya se ejecutó correctamente." });
                if (duplicateWarnings >= 2)
                    return StopResult(receipts,
                        $"Me detuve porque el modelo intentó repetir '{action}' después de recibir su resultado. No ejecuté acciones duplicadas.",
                        $"I stopped because the model tried to repeat '{action}' after receiving its result. I did not execute duplicate actions.");
                input = $"""
                    ACCIÓN BLOQUEADA: {action} ya se llamó con los mismos argumentos y el estado no cambió.
                    RESULTADO YA DISPONIBLE: {prior}
                    No la repitas. Termina con action=finish si ya puedes responder, o elige una herramienta diferente que aporte información nueva.
                    /no_think
                    """;
                continue;
            }

            activity?.Invoke(kind == AgentToolKind.Query ? $"CONSULTANDO · {action.ToUpperInvariant()}" : $"EJECUTANDO · {action.ToUpperInvariant()}");
            var result = await tools.ExecuteAsync(action, turn.Arguments, cancellationToken);
            seenAtRevision[revisionKey] = result;
            lastSummary = result.Summary;
            if (result.Success && (kind is AgentToolKind.Mutation or AgentToolKind.Destructive or AgentToolKind.Control))
                completedMutations.Add(fingerprint);
            if (result.Changed) stateRevision++;
            duplicateWarnings = 0;
            if (result.Changed) receipts.Add(new AgentReceipt(turn.Action, result.Summary));
            input = $"""
                RESULTADO DE {turn.Action}: {result.Json}
                Continúa con el siguiente paso necesario. No repitas una acción que ya tuvo success=true.
                Cuando la petición esté totalmente cumplida responde con action=finish y resume brevemente lo hecho.
                /no_think
                """;
        }
        string finalSummary = string.IsNullOrWhiteSpace(lastSummary) ? (Strings.Culture.TwoLetterISOLanguageName == "es" ? "ninguno" : "none") : lastSummary;
        return StopResult(receipts,
            $"No pude cerrar la orden dentro del presupuesto seguro de acciones. Último resultado: {finalSummary}.",
            $"I could not finish within the safe action budget. Last result: {finalSummary}.");
    }

    private static AgentRunResult StopResult(IReadOnlyList<AgentReceipt> receipts, string spanish, string english) =>
        new(Strings.Culture.TwoLetterISOLanguageName == "es" ? spanish : english, receipts);

    private static string Fingerprint(string action, JsonElement arguments)
    {
        var result = new StringBuilder(action).Append('|');
        AppendCanonical(result, arguments);
        return result.ToString();
    }

    private static void AppendCanonical(StringBuilder output, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                output.Append('{');
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    output.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                    AppendCanonical(output, property.Value);
                }
                output.Append('}');
                break;
            case JsonValueKind.Array:
                output.Append('[');
                foreach (var item in value.EnumerateArray()) AppendCanonical(output, item);
                output.Append(']');
                break;
            default:
                output.Append(value.GetRawText());
                break;
        }
    }

    internal static string SystemPrompt(DateTime? clock = null) => """
        Eres el agente de acciones de PomoDock. No eres RAG ni un asistente de conversación general.
        Tu misión es cumplir literalmente la petición dentro de las herramientas disponibles.
        Trabajas en español o inglés según el usuario. Hoy es __TODAY__.

        REGLAS:
        - Responde SIEMPRE con un solo JSON, sin Markdown, comentarios ni texto exterior.
        - Forma: {"action":"nombre","arguments":{...},"message":""}
        - Haz una llamada por turno. Después recibirás su resultado real y podrás continuar.
        - Nunca afirmes que hiciste algo hasta recibir success=true.
        - Usa fechas ISO locales yyyy-MM-dd y horas HH:mm. Resuelve hoy/mañana/días de semana con la fecha dada.
        - "antes del lunes" termina el domingo; "a más tardar el lunes" termina el lunes.
        - Para horas trabajadas, enfoque, pomodoros o proyectos históricos usa SIEMPRE focus.summarize o focus.query_sessions. Nunca calcules esos datos por tu cuenta.
        - Usa focus.summarize para totales, resúmenes y comparaciones. Usa focus.query_sessions solamente si piden listar o inspeccionar sesiones individuales.
        - period acepta today, yesterday, this_week, last_week, this_month, last_month, last_7_days, last_30_days, all o custom. "El último mes" significa last_month, no this_month.
        - Después de una consulta exitosa, responde con finish; no repitas la misma herramienta.
        - Para otros datos usa get_state. Para terminar usa action=finish, arguments={} y message con el resumen factual.
        - Las operaciones destructivas muestran una confirmación al usuario. Si cancela, respétalo.
        - No inventes herramientas ni campos.

        HERRAMIENTAS:
        get_state {"scope":"all|todos|calendar|habits|timer|widgets|focus_tasks|focus_history"}
        focus.summarize {"period":"today|yesterday|this_week|last_week|this_month|last_month|last_7_days|last_30_days|all|custom","from":"yyyy-MM-dd solo custom","to":"yyyy-MM-dd solo custom","project":"opcional"}
        focus.query_sessions {"period":"...","from":"opcional","to":"opcional","project":"opcional","limit":30}
        add_todo {"text":"texto natural completo de la tarea, incluyendo fecha/hora/prioridad"}
        complete_todo {"title":"parte inequívoca del título","done":true}
        delete_todo {"title":"parte inequívoca del título"}
        add_event {"title":"...","start":"yyyy-MM-dd HH:mm","minutes":60,"all_day":false,"reminder_minutes":10,"notes":""}
        complete_event {"title":"...","date":"yyyy-MM-dd","done":true}
        delete_event {"title":"parte inequívoca del título"}
        add_habit {"name":"...","cadence":"daily|weekdays|weekly","times_per_week":3}
        mark_habit {"name":"...","date":"yyyy-MM-dd","done":true}
        archive_habit {"name":"..."}
        add_note {"title":"...","text":"...","color":"paper|yellow|mint|blue|rose|lavender"}
        add_widget {"kind":"notes|todo|habits|calendar|stats|timer","title":"opcional"}
        add_focus_task {"name":"...","project":"Sin proyecto","estimate":1}
        timer {"command":"start|pause|reset|skip|select","phase":"focus|short|long","task":"título opcional"}
        set_focus_duration {"minutes":25}
        finish {}
        """.Replace("__TODAY__", (clock ?? DateTime.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private static bool TryReadTurn(string text, out AgentTurn turn, out string error)
    {
        turn = new AgentTurn(); error = "JSON ausente";
        int start = text.IndexOf('{');
        if (start < 0) return false;
        bool quoted = false, escaped = false; int depth = 0;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (escaped) { escaped = false; continue; }
            if (quoted && c == '\\') { escaped = true; continue; }
            if (c == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0)
            {
                try
                {
                    turn = JsonSerializer.Deserialize<AgentTurn>(text[start..(i + 1)], new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }) ?? new AgentTurn();
                    if (string.IsNullOrWhiteSpace(turn.Action)) { error = "action vacío"; return false; }
                    return true;
                }
                catch (JsonException ex) { error = ex.Message; return false; }
            }
        }
        error = "JSON incompleto";
        return false;
    }

    private sealed class AgentTurn
    {
        public string Action { get; set; } = "";
        public JsonElement Arguments { get; set; }
        public string Message { get; set; } = "";
    }
}

internal sealed record AgentToolResult(bool Changed, string Summary, string Json, bool Success = true);

internal enum AgentToolKind { Query, Mutation, Destructive, Control, Unknown }

/// <summary>Small, typed door between an untrusted model response and PomoDock's live state.</summary>
internal sealed class AgentToolbox
{
    private readonly MainWindow owner;
    public AgentToolbox(MainWindow owner) => this.owner = owner;

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
                "add_todo" => AddTodo(Required(args, "text")),
                "complete_todo" => CompleteTodo(Required(args, "title"), Bool(args, "done", true)),
                "delete_todo" => DeleteTodo(Required(args, "title")),
                "add_event" => AddEvent(args),
                "complete_event" => CompleteEvent(Required(args, "title"), Date(args, "date", DateOnly.FromDateTime(DateTime.Today)), Bool(args, "done", true)),
                "delete_event" => DeleteEvent(Required(args, "title")),
                "add_habit" => AddHabit(args),
                "mark_habit" => MarkHabit(Required(args, "name"), Date(args, "date", DateOnly.FromDateTime(DateTime.Today)), Bool(args, "done", true)),
                "archive_habit" => ArchiveHabit(Required(args, "name")),
                "add_note" => AddNote(args),
                "add_widget" => AddWidget(args),
                "add_focus_task" => AddFocusTask(args),
                "timer" => Timer(args),
                "set_focus_duration" => SetFocusDuration(Int(args, "minutes", 25)),
                _ => Result(false, false, $"Herramienta desconocida: {name}")
            };
        }
        catch (Exception ex) { return Result(false, false, ex.Message); }
    }

    internal static AgentToolKind KindOf(string name) => name.Trim().ToLowerInvariant() switch
    {
        "get_state" or "focus.summarize" or "focus.query_sessions" => AgentToolKind.Query,
        "delete_todo" or "delete_event" or "archive_habit" => AgentToolKind.Destructive,
        "timer" or "set_focus_duration" => AgentToolKind.Control,
        "add_todo" or "complete_todo" or "add_event" or "complete_event" or "add_habit" or "mark_habit" or "add_note" or "add_widget" or "add_focus_task" => AgentToolKind.Mutation,
        _ => AgentToolKind.Unknown
    };

    private AgentToolResult State(string scope)
    {
        var todos = TodoStore.For(owner.Store).Book.Ordered().Select(x => new { x.Title, x.Done, Due = x.Due?.ToString("yyyy-MM-dd"), At = x.At?.ToString("HH:mm"), Priority = x.Priority.ToString() });
        var events = AgendaStore.For(owner.Store).Book.Events.OrderBy(x => x.Start).Select(x => new { x.Title, Start = x.Start.ToString("yyyy-MM-dd HH:mm"), x.Minutes, x.AllDay, Repeat = x.Repeat.ToString() });
        var habits = HabitStore.For(owner.Store).Book.Habits.Select(x => new { x.Name, x.Archived, Cadence = x.Cadence.ToString(), DoneToday = x.IsComplete(DateOnly.FromDateTime(DateTime.Today)) });
        object value = scope.ToLowerInvariant() switch
        {
            "todos" => todos,
            "calendar" => events,
            "habits" => habits,
            "timer" => new { Phase = owner.Timer.Phase.ToString(), owner.Timer.Running, RemainingMinutes = Math.Ceiling(owner.Timer.Remaining / 60) },
            "widgets" => owner.Settings.WorkspacePages.Select((p, i) => new { Page = i + 1, p.Name, Widgets = p.Widgets.Select(w => new { w.Kind, w.Title }) }),
            "focus_tasks" => owner.Settings.Tasks.Select(x => new { x.Name, x.Project, x.Estimate, x.Done }),
            "focus_history" => FocusSnapshot("this_month"),
            _ => new { Todos = todos, Calendar = events, Habits = habits, Timer = new { Phase = owner.Timer.Phase.ToString(), owner.Timer.Running, RemainingMinutes = Math.Ceiling(owner.Timer.Remaining / 60) }, FocusTasks = owner.Settings.Tasks.Where(x => !x.Done).Select(x => new { x.Name, x.Project }), FocusHistory = FocusSnapshot("this_month") }
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
        return Result(true, false, $"Resumen de {subject}: {summary.Minutes:0.#} min entre {summary.Range.From:dd/MM/yyyy} y {summary.Range.To:dd/MM/yyyy}", data);
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

    private AgentToolResult AddTodo(string text)
    {
        var item = TodoStore.For(owner.Store).Add(text, DateTime.Now);
        return item is null ? Result(false, false, "No pude interpretar una tarea") : Result(true, true, $"Tarea añadida: {item.Title}", new { item.Id, item.Title, Due = item.Due?.ToString("yyyy-MM-dd"), At = item.At?.ToString("HH:mm") });
    }

    private AgentToolResult CompleteTodo(string query, bool done)
    {
        var store = TodoStore.For(owner.Store); var item = FindOne(store.Book.Items, x => x.Title, query, "tarea");
        store.SetDone(item, done);
        return Result(true, true, $"Tarea {(done ? "completada" : "reabierta")}: {item.Title}");
    }

    private AgentToolResult DeleteTodo(string query)
    {
        var store = TodoStore.For(owner.Store); var item = FindOne(store.Book.Items, x => x.Title, query, "tarea");
        if (!Confirm($"Eliminar tarea: {item.Title}")) return Result(false, false, "El usuario canceló la eliminación");
        store.Book.Items.Remove(item); store.Save();
        return Result(true, true, $"Tarea eliminada: {item.Title}");
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
        return Result(true, true, $"Evento creado: {item.Title} · {item.Start:dd/MM HH:mm}", new { item.Id });
    }

    private AgentToolResult CompleteEvent(string query, DateOnly date, bool done)
    {
        var store = AgendaStore.For(owner.Store); var item = FindOne(store.Book.Events, x => x.Title, query, "evento");
        item.SetDone(date, done); store.Save();
        return Result(true, true, $"Evento {(done ? "completado" : "reabierto")}: {item.Title}");
    }

    private AgentToolResult DeleteEvent(string query)
    {
        var store = AgendaStore.For(owner.Store); var item = FindOne(store.Book.Events, x => x.Title, query, "evento");
        if (!Confirm($"Eliminar evento: {item.Title}")) return Result(false, false, "El usuario canceló la eliminación");
        store.Remove(item); return Result(true, true, $"Evento eliminado: {item.Title}");
    }

    private AgentToolResult AddHabit(JsonElement args)
    {
        var store = HabitStore.For(owner.Store); string name = Required(args, "name");
        var habit = store.Add(name); string cadence = Text(args, "cadence", "daily").ToLowerInvariant();
        if (cadence == "weekdays") { habit.Cadence = HabitCadence.Selected; habit.Days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]; }
        else if (cadence == "weekly") { habit.Cadence = HabitCadence.Weekly; habit.TimesPerWeek = Math.Clamp(Int(args, "times_per_week", 3), 1, 7); }
        store.Save(); return Result(true, true, $"Hábito creado: {habit.Name}", new { habit.Id });
    }

    private AgentToolResult MarkHabit(string query, DateOnly date, bool done)
    {
        var store = HabitStore.For(owner.Store); var habit = FindOne(store.Book.Habits, x => x.Name, query, "hábito");
        habit.SetCount(date, done ? habit.Target : 0); store.Save();
        return Result(true, true, $"Hábito {(done ? "marcado" : "desmarcado")}: {habit.Name} · {date:dd/MM}");
    }

    private AgentToolResult ArchiveHabit(string query)
    {
        var store = HabitStore.For(owner.Store); var habit = FindOne(store.Book.Habits.Where(x => !x.Archived), x => x.Name, query, "hábito");
        if (!Confirm($"Archivar hábito: {habit.Name}")) return Result(false, false, "El usuario canceló el archivo");
        habit.Archived = true; store.Save(); return Result(true, true, $"Hábito archivado: {habit.Name}");
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

    private AgentToolResult AddWidget(JsonElement args)
    {
        string kind = Required(args, "kind").ToLowerInvariant();
        if (kind == "timer") return owner.AddTimerFromAgent();
        if (kind is not ("notes" or "todo" or "habits" or "calendar" or "stats")) throw new ArgumentException("Tipo de widget no permitido");
        string title = Text(args, "title", kind.ToUpperInvariant());
        var config = new WidgetConfig { Kind = kind, Title = title }; owner.AddCard(config, true);
        return Result(true, true, $"Widget añadido: {title}", new { config.Id });
    }

    private AgentToolResult AddFocusTask(JsonElement args)
    {
        var item = new WorkTask { Name = Required(args, "name"), Project = Text(args, "project", "Sin proyecto"), Estimate = Math.Clamp(Int(args, "estimate", 1), 1, 99) };
        owner.Settings.Tasks.Add(item);
        if (!owner.Settings.Projects.Contains(item.Project, StringComparer.OrdinalIgnoreCase)) owner.Settings.Projects.Add(item.Project);
        owner.SaveState(); return Result(true, true, $"Tarea de enfoque creada: {item.Name}", new { item.Id });
    }

    private AgentToolResult Timer(JsonElement args) => owner.RunTimerFromAgent(Text(args, "command", "start"), Text(args, "phase", "focus"), Text(args, "task", ""));

    private AgentToolResult SetFocusDuration(int minutes)
    {
        owner.Settings.FocusMinutes = Math.Clamp(minutes, 1, 180); owner.ApplyLiveSettings(); owner.SaveState();
        return Result(true, true, $"Duración de enfoque: {owner.Settings.FocusMinutes} min");
    }

    private bool Confirm(string text) => Dialogs.Choose(owner, "CONFIRMAR ACCIÓN", [text, L.T("common.cancel")]) == 0;

    private static T FindOne<T>(IEnumerable<T> source, Func<T, string> label, string query, string kind)
    {
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
    private static DateOnly Date(JsonElement args, string key, DateOnly fallback) => DateOnly.TryParseExact(Text(args, key, ""), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : fallback;
    private static DateOnly? OptionalDate(JsonElement args, string key) => DateOnly.TryParseExact(Text(args, key, ""), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    private static AgentToolResult Result(bool success, bool changed, string summary, object? extra = null) => new(changed, summary,
        JsonSerializer.Serialize(new { success, changed, summary, data = extra }), success);
}
