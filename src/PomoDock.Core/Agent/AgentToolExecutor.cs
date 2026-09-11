namespace PomoDock.Core.Agent;

/// <summary>
/// App-layer operations the Core executor cannot perform alone (live timer, widgets, to-do board).
/// The WPF host implements this; tests use a fake.
/// </summary>
public interface IAgentHost
{
    IReadOnlyList<Session> Sessions();
    TimeZoneInfo TimeZone { get; }
    DateTimeOffset Now { get; }
    AgentMemoryBook Memory { get; }
    void SaveMemory(AgentMemoryBook book);
    AgentSettings AgentSettings { get; }

    string TimerStatus();
    AgentReceipt? TimerStart(string? phase);
    AgentReceipt? TimerPause();
    AgentReceipt? TimerSkip();
    AgentReceipt? TimerSetPhase(string phase);

    string TasksList();
    AgentReceipt? TasksAdd(string name, string? project, int? estimate);
    AgentReceipt? TasksComplete(string id);

    string TodoList();
    AgentReceipt? TodoAdd(string title, string? due, string? priority);
    AgentReceipt? TodoComplete(string id);

    string HabitsList();
    AgentReceipt? HabitsTick(string id);
    AgentReceipt? HabitsAdd(string name, string? cadence);

    string AgendaList(int days);
    AgentReceipt? AgendaAdd(string text);

    AgentReceipt? NotesAdd(string text, string? color);
    string SettingsSnapshot();
    string WorkspaceList();
    bool Undo(string receiptId, out string message);
}

/// <summary>Executes catalog tools against a host, with loop protection.</summary>
public sealed class AgentToolExecutor
{
    private readonly IAgentHost host;
    private readonly AgentLoopGuard guard;

    public AgentToolExecutor(IAgentHost host, AgentLoopGuard? guard = null)
    {
        this.host = host;
        this.guard = guard ?? new AgentLoopGuard();
    }

    public AgentLoopGuard Guard => guard;

    public AgentToolResult Execute(AgentToolCall call)
    {
        if (!guard.TryEnter(call, out var blocked))
        {
            return new AgentToolResult
            {
                CallId = call.Id,
                Name = call.Name,
                Ok = false,
                Summary = blocked,
                BlockedByLoop = true
            };
        }

        try
        {
            return Dispatch(call);
        }
        catch (Exception ex)
        {
            return new AgentToolResult
            {
                CallId = call.Id,
                Name = call.Name,
                Ok = false,
                Summary = ex.Message
            };
        }
    }

    private AgentToolResult Dispatch(AgentToolCall call)
    {
        var name = call.Name.Trim().ToLowerInvariant();
        return name switch
        {
            "focus.summarize" => FocusSummarize(call),
            "memory.list" => Ok(call, AgentMemory.FormatList(host.Memory)),
            "memory.remember" => MemoryRemember(call),
            "memory.forget" => MemoryForget(call),
            "timer.status" => Ok(call, host.TimerStatus()),
            "timer.start" => Receipt(call, host.TimerStart(Arg(call, "phase"))),
            "timer.pause" => Receipt(call, host.TimerPause()),
            "timer.skip" => Receipt(call, host.TimerSkip()),
            "timer.set_phase" => Receipt(call, host.TimerSetPhase(Arg(call, "phase") ?? "Focus")),
            "tasks.list" => Ok(call, host.TasksList()),
            "tasks.add" => Receipt(call, host.TasksAdd(Arg(call, "name") ?? "", Arg(call, "project"), IntArg(call, "estimate"))),
            "tasks.complete" => Receipt(call, host.TasksComplete(Arg(call, "id") ?? "")),
            "todo.list" => Ok(call, host.TodoList()),
            "todo.add" => Receipt(call, host.TodoAdd(Arg(call, "title") ?? "", Arg(call, "due"), Arg(call, "priority"))),
            "todo.complete" => Receipt(call, host.TodoComplete(Arg(call, "id") ?? "")),
            "habits.list" => Ok(call, host.HabitsList()),
            "habits.tick" => Receipt(call, host.HabitsTick(Arg(call, "id") ?? "")),
            "habits.add" => Receipt(call, host.HabitsAdd(Arg(call, "name") ?? "", Arg(call, "cadence"))),
            "agenda.list" => Ok(call, host.AgendaList(IntArg(call, "days") ?? 7)),
            "agenda.add" => Receipt(call, host.AgendaAdd(Arg(call, "text") ?? "")),
            "notes.add" => Receipt(call, host.NotesAdd(Arg(call, "text") ?? "", Arg(call, "color"))),
            "settings.get" => Ok(call, host.SettingsSnapshot()),
            "workspace.list" => Ok(call, host.WorkspaceList()),
            "agent.undo" => Undo(call),
            _ => new AgentToolResult { CallId = call.Id, Name = call.Name, Ok = false, Summary = $"Herramienta desconocida: {call.Name}" }
        };
    }

    private AgentToolResult FocusSummarize(AgentToolCall call)
    {
        var period = FocusSummary.ParsePeriod(Arg(call, "period"));
        DateOnly? from = DateArg(call, "from");
        DateOnly? to = DateArg(call, "to");
        var request = new FocusSummary.Request
        {
            Period = period,
            From = from,
            To = to,
            Project = Arg(call, "project"),
            Zone = host.TimeZone,
            Now = host.Now
        };
        var result = FocusSummary.Summarize(host.Sessions(), request);
        return new AgentToolResult
        {
            CallId = call.Id,
            Name = call.Name,
            Ok = true,
            Summary = result.SummaryText,
            DetailJson = FocusSummary.ToJson(result)
        };
    }

    private AgentToolResult MemoryRemember(AgentToolCall call)
    {
        var book = host.Memory;
        var tags = (Arg(call, "tags") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var fact = AgentMemory.Remember(book, Arg(call, "text") ?? "", tags);
        host.SaveMemory(book);
        var receipt = new AgentReceipt
        {
            Tool = call.Name,
            Summary = $"Recordado: {fact.Text}",
            CanUndo = true,
            UndoToken = $"memory:{fact.Id}"
        };
        return new AgentToolResult { CallId = call.Id, Name = call.Name, Ok = true, Summary = receipt.Summary, Receipt = receipt };
    }

    private AgentToolResult MemoryForget(AgentToolCall call)
    {
        var book = host.Memory;
        var ok = AgentMemory.Forget(book, Arg(call, "id"), Arg(call, "text"));
        if (ok) host.SaveMemory(book);
        return new AgentToolResult
        {
            CallId = call.Id,
            Name = call.Name,
            Ok = ok,
            Summary = ok ? "Hecho olvidado." : "No encontré ese recuerdo.",
            Receipt = ok ? new AgentReceipt { Tool = call.Name, Summary = "Hecho olvidado.", CanUndo = false } : null
        };
    }

    private AgentToolResult Undo(AgentToolCall call)
    {
        var id = Arg(call, "receiptId") ?? Arg(call, "id") ?? "";
        var ok = host.Undo(id, out var message);
        return new AgentToolResult { CallId = call.Id, Name = call.Name, Ok = ok, Summary = message };
    }

    private static AgentToolResult Ok(AgentToolCall call, string summary) =>
        new() { CallId = call.Id, Name = call.Name, Ok = true, Summary = summary };

    private static AgentToolResult Receipt(AgentToolCall call, AgentReceipt? receipt)
    {
        if (receipt is null)
            return new AgentToolResult { CallId = call.Id, Name = call.Name, Ok = false, Summary = "La operación no pudo completarse." };
        return new AgentToolResult { CallId = call.Id, Name = call.Name, Ok = true, Summary = receipt.Summary, Receipt = receipt };
    }

    private static string? Arg(AgentToolCall call, string key) =>
        call.Arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static int? IntArg(AgentToolCall call, string key) =>
        int.TryParse(Arg(call, key), out var n) ? n : null;

    private static DateOnly? DateArg(AgentToolCall call, string key) =>
        DateOnly.TryParse(Arg(call, key), out var d) ? d : null;
}
