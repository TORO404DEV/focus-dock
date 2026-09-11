namespace PomoDock.Core.Agent;

/// <summary>
/// Orchestrates understand → plan → (approve) → execute → receipts.
/// Separates planning from execution (Fases 1–2) and emits activity for the chat UI (Fase 3).
/// </summary>
public sealed class AgentRuntime
{
    private readonly IAgentHost host;
    private readonly AgentToolExecutor executor;
    private readonly List<AgentReceipt> receipts = [];

    public AgentRuntime(IAgentHost host, AgentLoopGuard? guard = null)
    {
        this.host = host;
        executor = new AgentToolExecutor(host, guard);
    }

    public AgentToolExecutor Executor => executor;
    public IReadOnlyList<AgentReceipt> Receipts => receipts;

    public event Action<AgentActivity>? Activity;

    public AgentPlan Plan(string userText)
    {
        var plan = AgentPlanner.FromUserText(userText);
        Emit(AgentActivityKind.Understood, plan.Understood);
        if (plan.Steps.Count > 0)
        {
            Emit(AgentActivityKind.Planning, $"Plan con {plan.Steps.Count} paso(s).");
            AgentPlanner.MarkAwaitingApproval(plan);
            if (plan.Status == AgentPlanStatus.AwaitingApproval)
                Emit(AgentActivityKind.AwaitingApproval, "Esperando aprobación del plan.");
        }
        return plan;
    }

    public AgentPlan EditPlan(AgentPlan plan, IEnumerable<string> enabledStepIds)
    {
        var allow = enabledStepIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var step in plan.Steps) step.Enabled = allow.Contains(step.Id);
        plan.Status = AgentPlanStatus.Draft;
        AgentPlanner.MarkAwaitingApproval(plan);
        Emit(AgentActivityKind.Planning, "Plan editado.");
        if (plan.Status == AgentPlanStatus.AwaitingApproval)
            Emit(AgentActivityKind.AwaitingApproval, "Esperando aprobación del plan editado.");
        return plan;
    }

    public void Approve(AgentPlan plan)
    {
        plan.Status = AgentPlanStatus.Approved;
        Emit(AgentActivityKind.Planning, "Plan aprobado.");
    }

    public void Cancel(AgentPlan plan)
    {
        plan.Status = AgentPlanStatus.Cancelled;
        Emit(AgentActivityKind.Blocked, "Plan cancelado.");
    }

    public void Reject(AgentPlan plan)
    {
        plan.Status = AgentPlanStatus.Rejected;
        Emit(AgentActivityKind.Blocked, "Plan rechazado.");
    }

    /// <summary>
    /// Runs an approved plan (or a read-only draft). Write/destructive/control steps require approval
    /// when settings say so.
    /// </summary>
    public List<AgentToolResult> Execute(AgentPlan plan, bool force = false)
    {
        var require = host.AgentSettings.RequireApprovalForWrites && !force;
        if (require && plan.NeedsApproval && plan.Status is not AgentPlanStatus.Approved and not AgentPlanStatus.Running)
        {
            Emit(AgentActivityKind.Blocked, "El plan necesita aprobación antes de ejecutarse.");
            return [];
        }
        if (plan.Status is AgentPlanStatus.Cancelled or AgentPlanStatus.Rejected)
            return [];

        plan.Status = AgentPlanStatus.Running;
        var results = new List<AgentToolResult>();
        foreach (var step in plan.Steps.Where(s => s.Enabled))
        {
            Emit(AgentActivityKind.Consulting, $"Consultando {step.Call.Name}…");
            Emit(AgentActivityKind.Executing, $"Ejecutando: {step.Label}");
            var result = executor.Execute(step.Call);
            results.Add(result);
            if (result.BlockedByLoop)
            {
                Emit(AgentActivityKind.Blocked, result.Summary);
                plan.Status = AgentPlanStatus.Failed;
                return results;
            }
            if (result.Receipt is not null)
            {
                receipts.Add(result.Receipt);
                Emit(AgentActivityKind.Receipt, result.Receipt.Summary, receiptId: result.Receipt.Id);
            }
            else
            {
                Emit(result.Ok ? AgentActivityKind.Reply : AgentActivityKind.Error, result.Summary);
            }
            if (!result.Ok && !result.BlockedByLoop)
            {
                plan.Status = AgentPlanStatus.Failed;
                return results;
            }
        }
        plan.Status = AgentPlanStatus.Completed;
        return results;
    }

    /// <summary>One-shot path for read-only focus questions (Fase 0 happy path).</summary>
    public AgentTurnResult Handle(string userText, bool autoApproveReads = true)
    {
        var plan = Plan(userText);
        if (plan.Steps.Count == 0)
        {
            return new AgentTurnResult
            {
                Plan = plan,
                Reply = "No pude armar un plan determinista. Configura el modelo local o reformula."
            };
        }

        if (!plan.NeedsApproval && autoApproveReads)
            Approve(plan);
        else if (plan.NeedsApproval)
        {
            return new AgentTurnResult
            {
                Plan = plan,
                Reply = "Hay un plan pendiente de aprobación.",
                NeedsApproval = true
            };
        }

        var results = Execute(plan);
        var reply = string.Join("\n", results.Select(r => r.Summary));
        return new AgentTurnResult
        {
            Plan = plan,
            Results = results,
            Reply = reply,
            NeedsApproval = false
        };
    }

    public bool Undo(string receiptId, out string message)
    {
        var receipt = receipts.FirstOrDefault(r => r.Id == receiptId);
        if (receipt is null)
        {
            message = "No hay recibo con ese id.";
            return false;
        }
        if (!receipt.CanUndo || receipt.Undone)
        {
            message = "Ese recibo no se puede deshacer.";
            return false;
        }
        if (!host.Undo(receipt.Id, out message) && !host.Undo(receipt.UndoToken, out message))
            return false;
        receipt.Undone = true;
        Emit(AgentActivityKind.Undo, message, receiptId: receipt.Id);
        return true;
    }

    private void Emit(AgentActivityKind kind, string text, string? planId = null, string? receiptId = null) =>
        Activity?.Invoke(new AgentActivity { Kind = kind, Text = text, PlanId = planId, ReceiptId = receiptId });
}

public sealed class AgentTurnResult
{
    public required AgentPlan Plan { get; init; }
    public List<AgentToolResult> Results { get; init; } = [];
    public string Reply { get; init; } = "";
    public bool NeedsApproval { get; init; }
}
