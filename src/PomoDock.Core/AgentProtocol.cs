using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PomoDock.Core;

public enum AgentRisk { Read = 0, Write = 1, Batch = 2, Destructive = 3 }

public enum AgentEventKind
{
    Interpreting, Reading, PlanReady, Executing, ToolResult, AnswerDelta, Speaking, Done, Failed, NeedDecision, Cancelled
}

public sealed record AgentNotice(AgentEventKind Kind, string Text);

public sealed class AgentPlanStep
{
    public string Tool { get; set; } = "";
    public string ArgumentsJson { get; set; } = "{}";
    public AgentRisk Risk { get; set; }
    public string Label { get; set; } = "";
}

public sealed class AgentPlan
{
    public string Understood { get; set; } = "";
    public string Changes { get; set; } = "";
    public string Message { get; set; } = "";
    public AgentRisk Risk { get; set; }
    public List<AgentPlanStep> Steps { get; set; } = [];
    public bool HasMutations => Steps.Any(step => step.Risk != AgentRisk.Read);

    public static AgentPlan Reply(string understood, string message) => new()
    {
        Understood = understood,
        Message = message,
        Changes = "",
        Risk = AgentRisk.Read
    };
}

public sealed class AgentActionReceipt
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Tool { get; set; } = "";
    public string Summary { get; set; } = "";
    public string? UndoTool { get; set; }
    public string? UndoArgumentsJson { get; set; }
    public bool CanUndo => !string.IsNullOrWhiteSpace(UndoTool);
}

public sealed class AgentModelTurn
{
    public string Action { get; set; } = "";
    public JsonElement Arguments { get; set; }
    public string Message { get; set; } = "";
    public string Understood { get; set; } = "";
}

public static class AgentFingerprints
{
    public static string Of(string action, JsonElement arguments)
    {
        var output = new StringBuilder(action).Append('|');
        Append(output, arguments);
        return output.ToString();
    }

    private static void Append(StringBuilder output, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                output.Append('{');
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    output.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                    Append(output, property.Value);
                }
                output.Append('}');
                break;
            case JsonValueKind.Array:
                output.Append('[');
                foreach (var item in value.EnumerateArray()) Append(output, item);
                output.Append(']');
                break;
            default:
                output.Append(value.ValueKind == JsonValueKind.Undefined ? "null" : value.GetRawText());
                break;
        }
    }
}

public static class AgentJson
{
    public static bool TryReadTurn(string text, out AgentModelTurn turn, out string error)
    {
        turn = new AgentModelTurn();
        error = "JSON ausente";
        text = AgentThink.Strip(text);
        int start = text.IndexOf('{');
        if (start < 0) return false;
        bool quoted = false, escaped = false;
        int depth = 0;
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
                    turn = JsonSerializer.Deserialize<AgentModelTurn>(text[start..(i + 1)], new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }) ?? new AgentModelTurn();
                    if (string.IsNullOrWhiteSpace(turn.Action)) { error = "action vacío"; return false; }
                    return true;
                }
                catch (JsonException ex) { error = ex.Message; return false; }
            }
        }
        error = "JSON incompleto";
        return false;
    }
}

public static class AgentCatalog
{
    public static AgentRisk RiskOf(string name) => name.Trim().ToLowerInvariant() switch
    {
        "get_state" or "focus.summarize" or "focus.query_sessions" or "focus.compare_periods" or "focus.list_projects"
            or "todo.search" or "calendar.search" or "habits.search" or "notes.search"
            or         "workspace.list" or "timer.inspect" or "settings.read" or "memory.search"
            or "finance.summary" or "finance.search" => AgentRisk.Read,
        "todo.delete" or "delete_todo" or "calendar.delete" or "delete_event" or "habits.archive" or "memory.forget" or "finance.delete" or "finance.archive" => AgentRisk.Destructive,
        "todo.create" or "todo.update" or "todo.complete"
            or "calendar.create" or "calendar.update" or "calendar.reschedule" or "calendar.complete"
            or "habits.create" or "habits.mark" or "habits.update"
            or "notes.create" or "notes.append" or "notes.format" or "notes.check"
            or "workspace.add_widget" or "workspace.move_widget" or "workspace.open"
            or "add_todo" or "complete_todo" or "add_event" or "complete_event" or "add_habit" or "mark_habit"
            or "add_note" or "add_widget" or "add_focus_task" or "memory.propose" or "memory.confirm"
            or "finance.add" or "finance.add_recurring" or "finance.mark_paid" => AgentRisk.Write,
        "timer" or "timer.start" or "timer.pause" or "timer.reset" or "timer.configure" or "set_focus_duration"
            or "settings.update" => AgentRisk.Batch,
        _ => AgentRisk.Write
    };

    public static bool IsQuery(string name) => RiskOf(name) == AgentRisk.Read;

    public static string Label(string name, string fallback = "") => name.Trim().ToLowerInvariant() switch
    {
        "focus.summarize" => "historial de enfoque",
        "focus.query_sessions" => "sesiones de enfoque",
        "focus.compare_periods" => "comparar periodos",
        "get_state" => "estado de PomoDock",
        "todo.create" or "add_todo" => "crear tarea",
        "todo.update" => "editar tarea",
        "todo.complete" or "complete_todo" => "completar tarea",
        "todo.delete" or "delete_todo" => "eliminar tarea",
        "calendar.create" or "add_event" => "crear evento",
        "calendar.update" or "calendar.reschedule" => "reprogramar evento",
        "habits.create" or "add_habit" => "crear hábito",
        "habits.mark" or "mark_habit" => "marcar hábito",
        "notes.create" or "add_note" => "crear nota",
        "timer" or "timer.start" => "temporizador",
        "memory.propose" or "memory.confirm" => "recordar",
        "memory.forget" => "olvidar",
        _ => string.IsNullOrWhiteSpace(fallback) ? name : fallback
    };
}

public sealed class AgentLoopGuard
{
    private readonly Dictionary<string, string> seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> lastJson = new(StringComparer.Ordinal);
    private readonly HashSet<string> completedMutations = new(StringComparer.Ordinal);
    public int Duplicates { get; private set; }
    public int Malformed { get; private set; }
    public int Revision { get; private set; }

    public bool NoteMalformed() => ++Malformed >= 3;
    public void NoteValid() => Malformed = 0;

    public bool IsRepeat(string action, JsonElement arguments, AgentRisk risk, out string priorJson)
    {
        string fingerprint = AgentFingerprints.Of(action, arguments);
        string key = $"{Revision}:{fingerprint}";
        bool repeatedMutation = risk != AgentRisk.Read && completedMutations.Contains(fingerprint);
        if (repeatedMutation || seen.ContainsKey(key))
        {
            Duplicates++;
            priorJson = lastJson.GetValueOrDefault(fingerprint) ?? seen.GetValueOrDefault(key) ?? "";
            return true;
        }
        priorJson = "";
        return false;
    }

    public bool TooManyRepeats() => Duplicates >= 2;

    public void Remember(string action, JsonElement arguments, AgentRisk risk, string json, bool changed, bool success)
    {
        string fingerprint = AgentFingerprints.Of(action, arguments);
        seen[$"{Revision}:{fingerprint}"] = json;
        lastJson[fingerprint] = json;
        if (success && risk != AgentRisk.Read) completedMutations.Add(fingerprint);
        if (changed) Revision++;
        Duplicates = 0;
    }
}
