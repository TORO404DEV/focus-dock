using PomoDock.Core;
using PomoDock.Core.Agent;

internal static class AgentLoopGuardTests
{
    public static void Run(Action<string, Action> test, Action<double, double> equal, Action<bool, string> assert)
    {
        void Test(string name, Action body) => test(name, body);

        Test("tool catalog classifies focus.summarize as Read", () =>
        {
            assert(AgentToolCatalog.KindOf("focus.summarize") == AgentToolKind.Read, "read");
            assert(AgentToolCatalog.KindOf("memory.remember") == AgentToolKind.Write, "write");
            assert(AgentToolCatalog.KindOf("memory.forget") == AgentToolKind.Destructive, "destructive");
            assert(AgentToolCatalog.KindOf("timer.start") == AgentToolKind.Control, "control");
        });

        Test("every catalog tool has a known kind", () =>
        {
            foreach (var tool in AgentToolCatalog.All)
            {
                assert(tool.Name.Length > 0, "name");
                assert(Enum.IsDefined(tool.Kind), "kind defined");
            }
            assert(AgentToolCatalog.All.Count >= 20, "surface covers PomoDock ops");
        });

        Test("identical tool fingerprints match regardless of argument order", () =>
        {
            var a = new AgentToolCall
            {
                Name = "focus.summarize",
                Arguments = new Dictionary<string, string> { ["period"] = "last_month", ["project"] = "X" }
            };
            var b = new AgentToolCall
            {
                Name = "focus.summarize",
                Arguments = new Dictionary<string, string> { ["project"] = "X", ["period"] = "last_month" }
            };
            assert(AgentLoopGuard.Fingerprint(a) == AgentLoopGuard.Fingerprint(b), "order-independent");
        });

        Test("loop guard blocks the third identical call by default", () =>
        {
            var guard = new AgentLoopGuard(maxIdentical: 2);
            var call = new AgentToolCall
            {
                Name = "focus.summarize",
                Arguments = new Dictionary<string, string> { ["period"] = "last_month" }
            };
            assert(guard.TryEnter(call, out _), "1");
            assert(guard.TryEnter(call, out _), "2");
            assert(!guard.TryEnter(call, out var reason), "3 blocked");
            assert(reason.Contains("Bucle", StringComparison.OrdinalIgnoreCase), "explains loop");
            assert(guard.WouldBlock(call), "would block");
        });

        Test("different arguments are not a loop", () =>
        {
            var guard = new AgentLoopGuard(2);
            assert(guard.TryEnter(new AgentToolCall { Name = "focus.summarize", Arguments = new() { ["period"] = "today" } }, out _), "a");
            assert(guard.TryEnter(new AgentToolCall { Name = "focus.summarize", Arguments = new() { ["period"] = "yesterday" } }, out _), "b");
            assert(guard.TryEnter(new AgentToolCall { Name = "focus.summarize", Arguments = new() { ["period"] = "today" } }, out _), "a2");
        });

        Test("executor surfaces loop block without running host twice beyond limit", () =>
        {
            var host = new FakeAgentHost();
            host.SessionsList.Add(SessionAt(new DateTimeOffset(2026, 8, 10, 10, 0, 0, TimeSpan.Zero), 60));
            var guard = new AgentLoopGuard(1);
            var exec = new AgentToolExecutor(host, guard);
            var call = new AgentToolCall { Name = "focus.summarize", Arguments = new() { ["period"] = "last_month" } };
            var first = exec.Execute(call);
            assert(first.Ok && !first.BlockedByLoop, "first ok");
            equal(1, host.FocusSummarizeHits);
            var second = exec.Execute(call);
            assert(!second.Ok && second.BlockedByLoop, "second blocked");
            equal(1, host.FocusSummarizeHits);
        });

        Test("runtime handle answers last-month hours via focus.summarize", () =>
        {
            var host = new FakeAgentHost
            {
                NowValue = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero)
            };
            host.SessionsList.Add(SessionAt(new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero), 90));
            host.SessionsList.Add(SessionAt(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero), 30));
            var runtime = new AgentRuntime(host);
            var turn = runtime.Handle("¿Cuántas horas enfoqué el último mes?");
            assert(turn.Plan.Steps.Single().Call.Name == "focus.summarize", "tool");
            assert(turn.Plan.Steps.Single().Call.Arguments["period"] == "last_month", "period");
            assert(turn.Results.Single().Ok, "ok");
            assert(turn.Reply.Contains("1,5") || turn.Reply.Contains("1.5") || turn.Reply.Contains("90"), "mentions hours or minutes path");
            equal(1.5, FocusSummary.Summarize(host.SessionsList, new FocusSummary.Request
            {
                Period = FocusPeriodKind.LastMonth, Zone = TimeZoneInfo.Utc, Now = host.NowValue
            }).FocusHours);
        });

        Test("writes require approval before execute", () =>
        {
            var host = new FakeAgentHost();
            var runtime = new AgentRuntime(host);
            var plan = runtime.Plan("recuerda que mi proyecto principal es Alpha");
            assert(plan.NeedsApproval, "needs approval");
            assert(plan.Status == AgentPlanStatus.AwaitingApproval, "awaiting");
            var blocked = runtime.Execute(plan);
            assert(blocked.Count == 0, "blocked without approve");
            runtime.Approve(plan);
            var results = runtime.Execute(plan);
            assert(results.Count == 1 && results[0].Ok, "runs after approve");
            assert(host.Memory.Facts.Any(f => f.Text.Contains("Alpha")), "remembered");
        });

        Test("cancel prevents execution", () =>
        {
            var host = new FakeAgentHost();
            var runtime = new AgentRuntime(host);
            var plan = runtime.Plan("recuerda comprar café");
            runtime.Cancel(plan);
            assert(runtime.Execute(plan).Count == 0, "cancelled");
            assert(host.Memory.Facts.Count == 0, "no side effect");
        });

        Test("edit plan can disable steps", () =>
        {
            var host = new FakeAgentHost();
            var runtime = new AgentRuntime(host);
            var plan = AgentPlanner.FromToolCalls("multi",
            [
                new AgentToolCall { Name = "memory.remember", Arguments = new() { ["text"] = "A" } },
                new AgentToolCall { Name = "memory.remember", Arguments = new() { ["text"] = "B" } }
            ]);
            AgentPlanner.MarkAwaitingApproval(plan);
            runtime.Approve(plan);
            runtime.EditPlan(plan, [plan.Steps[1].Id]);
            // Edit resets to awaiting when writes remain
            if (plan.Status == AgentPlanStatus.AwaitingApproval) runtime.Approve(plan);
            var results = runtime.Execute(plan);
            assert(results.Count == 1, "one step");
            assert(host.Memory.Facts.Single().Text == "B", "only B");
        });

        Test("memory remember forget list export", () =>
        {
            var book = new AgentMemoryBook();
            var fact = AgentMemory.Remember(book, "Prefiero descansos de 5 minutos");
            assert(book.Facts.Count == 1, "one");
            var json = AgentMemory.ExportJson(book);
            var copy = AgentMemory.ImportJson(json);
            assert(copy.Facts.Single().Text == fact.Text, "roundtrip");
            assert(AgentMemory.Forget(book, id: fact.Id), "forget");
            assert(book.Facts.Count == 0, "empty");
        });

        Test("conversations persist messages and title", () =>
        {
            var book = new AgentConversationBook();
            var conversation = AgentConversations.EnsureActive(book);
            AgentConversations.Append(conversation, new AgentMessage { Role = "user", Text = "Horas del mes pasado" });
            assert(conversation.Title.Contains("Horas"), "title from first user msg");
            assert(book.ActiveId == conversation.Id, "active");
            AgentConversations.StartNew(book, "Otro");
            assert(book.Conversations.Count == 2, "two threads");
            assert(AgentConversations.Delete(book, book.ActiveId!), "delete");
        });

        Test("activity events fire for understand plan execute receipt", () =>
        {
            var host = new FakeAgentHost();
            var runtime = new AgentRuntime(host);
            var kinds = new List<AgentActivityKind>();
            runtime.Activity += a => kinds.Add(a.Kind);
            var turn = runtime.Handle("recuerda el deadline del viernes");
            assert(turn.NeedsApproval, "approval");
            runtime.Approve(turn.Plan);
            runtime.Execute(turn.Plan);
            assert(kinds.Contains(AgentActivityKind.Understood), "understood");
            assert(kinds.Contains(AgentActivityKind.AwaitingApproval), "await");
            assert(kinds.Contains(AgentActivityKind.Receipt), "receipt");
        });

        Test("repeated mutation is cut by loop guard inside runtime", () =>
        {
            var host = new FakeAgentHost();
            var guard = new AgentLoopGuard(1);
            var runtime = new AgentRuntime(host, guard);
            var plan = AgentPlanner.FromToolCalls("dup",
            [
                new AgentToolCall { Name = "memory.remember", Arguments = new() { ["text"] = "mismo" } },
                new AgentToolCall { Name = "memory.remember", Arguments = new() { ["text"] = "mismo" } }
            ]);
            runtime.Approve(plan);
            var results = runtime.Execute(plan);
            assert(results.Count == 2, "two attempts");
            assert(results[0].Ok && results[1].BlockedByLoop, "second blocked");
            equal(1, host.Memory.Facts.Count);
        });
    }

    private static Session SessionAt(DateTimeOffset start, double minutes) => new()
    {
        Phase = Phase.Focus,
        Outcome = Outcome.Completed,
        Started = start,
        Ended = start.AddMinutes(minutes),
        PlannedSeconds = minutes * 60,
        Segments = [new FocusSegment(start, start.AddMinutes(minutes))]
    };
}

/// <summary>In-memory host for Core agent tests.</summary>
internal sealed class FakeAgentHost : IAgentHost
{
    public List<Session> SessionsList { get; } = [];
    public AgentMemoryBook Memory { get; private set; } = new();
    public AgentSettings AgentSettings { get; } = new() { RequireApprovalForWrites = true };
    public TimeZoneInfo TimeZone => TimeZoneInfo.Utc;
    public DateTimeOffset NowValue { get; set; } = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    public DateTimeOffset Now => NowValue;
    public int FocusSummarizeHits { get; private set; }
    private readonly Dictionary<string, Action> undos = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<Session> Sessions()
    {
        FocusSummarizeHits++;
        return SessionsList;
    }

    public void SaveMemory(AgentMemoryBook book) => Memory = book;

    public string TimerStatus() => "idle";
    public AgentReceipt? TimerStart(string? phase) => Rec("timer.start", $"start {phase}");
    public AgentReceipt? TimerPause() => Rec("timer.pause", "paused");
    public AgentReceipt? TimerSkip() => Rec("timer.skip", "skipped");
    public AgentReceipt? TimerSetPhase(string phase) => Rec("timer.set_phase", phase);
    public string TasksList() => "(sin tareas)";
    public AgentReceipt? TasksAdd(string name, string? project, int? estimate) => Rec("tasks.add", name);
    public AgentReceipt? TasksComplete(string id) => Rec("tasks.complete", id);
    public string TodoList() => "(vacío)";
    public AgentReceipt? TodoAdd(string title, string? due, string? priority) => Rec("todo.add", title);
    public AgentReceipt? TodoComplete(string id) => Rec("todo.complete", id);
    public string HabitsList() => "(vacío)";
    public AgentReceipt? HabitsTick(string id) => Rec("habits.tick", id);
    public AgentReceipt? HabitsAdd(string name, string? cadence) => Rec("habits.add", name);
    public string AgendaList(int days) => "(vacío)";
    public AgentReceipt? AgendaAdd(string text) => Rec("agenda.add", text);
    public AgentReceipt? NotesAdd(string text, string? color) => Rec("notes.add", text);
    public string SettingsSnapshot() => "{}";
    public string WorkspaceList() => "[]";

    public bool Undo(string receiptId, out string message)
    {
        if (undos.TryGetValue(receiptId, out var action))
        {
            action();
            message = "Deshecho.";
            return true;
        }
        if (receiptId.StartsWith("memory:", StringComparison.OrdinalIgnoreCase))
        {
            var id = receiptId["memory:".Length..];
            if (AgentMemory.Forget(Memory, id: id))
            {
                message = "Memoria revertida.";
                return true;
            }
        }
        message = "No se pudo deshacer.";
        return false;
    }

    private AgentReceipt Rec(string tool, string summary)
    {
        var receipt = new AgentReceipt { Tool = tool, Summary = summary, CanUndo = true, UndoToken = Guid.NewGuid().ToString("N") };
        undos[receipt.Id] = () => { };
        undos[receipt.UndoToken] = () => { };
        return receipt;
    }
}
