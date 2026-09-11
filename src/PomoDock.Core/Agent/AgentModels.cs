using System.Text.Json.Serialization;

namespace PomoDock.Core.Agent;

/// <summary>How dangerous a tool is. Reads may run free; writes and worse need a plan.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentToolKind
{
    Read,
    Write,
    Destructive,
    Control
}

/// <summary>Where a turn is in the plan → approve → run loop.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentPlanStatus
{
    Draft,
    AwaitingApproval,
    Approved,
    Rejected,
    Cancelled,
    Running,
    Completed,
    Failed
}

/// <summary>Visible activity the UI may show. Never raw model reasoning.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentActivityKind
{
    Understood,
    Consulting,
    Planning,
    AwaitingApproval,
    Executing,
    Receipt,
    Undo,
    Blocked,
    Error,
    Reply
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FocusPeriodKind
{
    Today,
    Yesterday,
    ThisWeek,
    LastWeek,
    ThisMonth,
    LastMonth,
    AllTime,
    Custom
}

public sealed class AgentToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required AgentToolKind Kind { get; init; }
    public string SchemaHint { get; init; } = "{}";
}

public sealed class AgentToolCall
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required string Name { get; set; }
    public Dictionary<string, string> Arguments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AgentToolResult
{
    public required string CallId { get; init; }
    public required string Name { get; init; }
    public required bool Ok { get; init; }
    public string Summary { get; init; } = "";
    public string DetailJson { get; init; } = "{}";
    public AgentReceipt? Receipt { get; init; }
    public bool BlockedByLoop { get; init; }
}

public sealed class AgentPlanStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required AgentToolCall Call { get; set; }
    public AgentToolKind Kind { get; set; }
    public string Label { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class AgentPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Understood { get; set; } = "";
    public List<AgentPlanStep> Steps { get; set; } = [];
    public AgentPlanStatus Status { get; set; } = AgentPlanStatus.Draft;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool NeedsApproval => Steps.Any(step => step.Enabled && step.Kind is AgentToolKind.Write or AgentToolKind.Destructive or AgentToolKind.Control);
}

public sealed class AgentActivity
{
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public required AgentActivityKind Kind { get; set; }
    public string Text { get; set; } = "";
    public string? PlanId { get; set; }
    public string? ReceiptId { get; set; }
}

public sealed class AgentReceipt
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required string Tool { get; set; }
    public string Summary { get; set; } = "";
    public string UndoToken { get; set; } = "";
    public bool CanUndo { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public bool Undone { get; set; }
}

public sealed class AgentMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required string Role { get; set; } // user | assistant | system | tool | activity
    public string Text { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public List<AgentActivity> Activities { get; set; } = [];
    public AgentPlan? Plan { get; set; }
    public List<AgentReceipt> Receipts { get; set; } = [];
}

public sealed class AgentConversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<AgentMessage> Messages { get; set; } = [];
}

public sealed class AgentConversationBook
{
    public int Version { get; set; } = 1;
    public List<AgentConversation> Conversations { get; set; } = [];
    public string? ActiveId { get; set; }
}

public sealed class AgentMemoryFact
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required string Text { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<string> Tags { get; set; } = [];
}

public sealed class AgentMemoryBook
{
    public int Version { get; set; } = 1;
    public List<AgentMemoryFact> Facts { get; set; } = [];
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>User-facing agent settings. Endpoint stays local by default (Ollama).</summary>
public sealed class AgentSettings
{
    public bool Enabled { get; set; } = true;
    public string Endpoint { get; set; } = "http://127.0.0.1:11434/v1";
    public string Model { get; set; } = "llama3.2";
    public string ApiKey { get; set; } = "";
    public bool RequireApprovalForWrites { get; set; } = true;
    public int MaxContextMessages { get; set; } = 40;
    public void Validate()
    {
        Endpoint = string.IsNullOrWhiteSpace(Endpoint) ? "http://127.0.0.1:11434/v1" : Endpoint.Trim().TrimEnd('/');
        Model = string.IsNullOrWhiteSpace(Model) ? "llama3.2" : Model.Trim();
        ApiKey ??= "";
        MaxContextMessages = Math.Clamp(MaxContextMessages, 8, 200);
    }
}
