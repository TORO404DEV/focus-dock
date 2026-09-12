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
    public bool CanAutoRun => Steps.Count > 0 && Steps.All(step => step.Risk != AgentRisk.Destructive);

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
                    if (string.IsNullOrWhiteSpace(turn.Action))
                    {
                        using var document = JsonDocument.Parse(text[start..(i + 1)]);
                        var root = document.RootElement;
                        turn.Action = Read(root, "action") is { Length: > 0 } action ? action
                            : Read(root, "tool") is { Length: > 0 } tool ? tool
                            : Read(root, "name");
                    }
                    turn.Action = AgentCatalog.Canonical(turn.Action);
                    if (string.IsNullOrWhiteSpace(turn.Action)) { error = "action vacío"; return false; }
                    return true;
                }
                catch (JsonException ex) { error = ex.Message; return false; }
            }
        }
        error = "JSON incompleto";
        return false;
    }

    private static string Read(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? "" : "";
}

public static class AgentCatalog
{
    /// <summary>
    /// The small, canonical surface shown to the model. <see cref="Actions"/> also contains
    /// backwards-compatible aliases understood by the executor, but offering duplicates to a
    /// 4B model makes tool selection slower and less reliable.
    /// </summary>
    public static readonly string[] ModelActions =
    [
        "get_state", "focus.summarize", "focus.query_sessions", "focus.compare_periods", "focus.list_projects",
        "todo.search", "add_todo", "todo.update", "complete_todo", "delete_todo", "todo.clear_done", "todo.sort", "todo.move",
        "calendar.search", "calendar.upcoming", "add_event", "calendar.update", "complete_event", "delete_event", "calendar.set_view", "calendar.navigate", "calendar.set_show_done",
        "habits.search", "add_habit", "mark_habit", "habits.update", "archive_habit", "habits.restore", "habits.delete", "habits.set_target", "habits.move", "habits.set_count",
        "notes.search", "add_note", "notes.append", "notes.set_color", "notes.reopen", "notes.forget",
        "workspace.list", "workspace.move_widget", "workspace.rename_widget", "workspace.collapse_widget", "workspace.resize_widget",
        "workspace.set_web_url", "workspace.reload_web", "workspace.add_page", "workspace.rename_page", "workspace.set_home", "workspace.goto", "workspace.focus",
        "workspace.delete_empty_pages", "workspace.delete_page", "workspace.remove_widget", "workspace.clear_widgets", "workspace.remove_timer", "workspace.set_web_keepalive", "add_widget",
        "layouts.list", "layouts.save", "layouts.load", "layouts.delete", "app.fullscreen", "app.open_panel", "app.open_data_folder", "notifications.list", "notifications.dismiss",
        "sounds.list", "sounds.set", "add_focus_task", "focus_tasks.list", "focus_tasks.update", "focus_tasks.set_done", "focus_tasks.delete", "focus_tasks.save_template", "focus_tasks.add_from_template",
        "focus_projects.list", "focus_projects.add", "focus_projects.rename", "focus_projects.delete",
        "finance.summary", "finance.search", "finance.add", "finance.add_recurring", "finance.mark_paid", "finance.delete", "finance.clear_expenses", "finance.archive", "finance.set_currency",
        "timer", "timer.inspect", "set_focus_duration", "settings.read", "settings.update", "settings.apply_rhythm",
        "memory.search", "memory.propose", "memory.confirm", "memory.forget", "memory.export", "finish"
    ];

    public static readonly string[] Actions =
    [
        "get_state", "focus.summarize", "focus.query_sessions", "focus.compare_periods", "focus.list_projects",
        "todo.search", "todo.create", "todo.update", "todo.complete", "todo.delete", "todo.clear_done", "todo.sort", "todo.move",
        "add_todo", "complete_todo", "delete_todo",
        "calendar.search", "calendar.upcoming", "calendar.create", "calendar.update", "calendar.reschedule", "calendar.complete", "calendar.delete", "calendar.set_view", "calendar.navigate", "calendar.set_show_done",
        "add_event", "complete_event", "delete_event",
        "habits.search", "habits.create", "habits.mark", "habits.update", "habits.archive", "habits.restore", "habits.delete", "habits.set_target", "habits.move", "habits.set_count",
        "add_habit", "mark_habit", "archive_habit",
        "notes.search", "notes.create", "notes.append", "notes.set_color", "notes.reopen", "notes.forget", "notes.delete", "add_note",
        "workspace.list", "workspace.add_widget", "workspace.move_widget", "workspace.rename_widget", "workspace.collapse_widget", "workspace.resize_widget",
        "workspace.set_web_url", "workspace.reload_web", "workspace.set_web_keepalive", "workspace.open",
        "workspace.add_page", "workspace.rename_page", "workspace.set_home", "workspace.goto", "workspace.focus", "workspace.delete_empty_pages", "workspace.delete_page",
        "workspace.remove_widget", "workspace.clear_widgets", "workspace.remove_timer",
        "layouts.list", "layouts.save", "layouts.load", "layouts.delete", "app.fullscreen", "app.open_panel", "app.open_data_folder", "notifications.list", "notifications.dismiss",
        "add_widget", "add_focus_task", "focus_tasks.list", "focus_tasks.update", "focus_tasks.set_done", "focus_tasks.delete", "focus_tasks.save_template", "focus_tasks.add_from_template",
        "focus_projects.list", "focus_projects.add", "focus_projects.rename", "focus_projects.delete",
        "sounds.list", "sounds.set",
        "finance.summary", "finance.search", "finance.add", "finance.add_recurring", "finance.mark_paid", "finance.delete", "finance.clear_expenses", "finance.archive", "finance.set_currency",
        "timer", "timer.start", "timer.pause", "timer.reset", "timer.configure", "timer.inspect", "set_focus_duration",
        "settings.read", "settings.update", "settings.apply_rhythm",
        "memory.search", "memory.propose", "memory.confirm", "memory.forget", "memory.export",
        "finish", "plan"
    ];

    public static string Canonical(string name) => name.Trim().ToLowerInvariant() switch
    {
        "create_note" or "new_note" or "nota" or "notes.create" => "add_note",
        "create_todo" or "new_todo" or "tarea" => "add_todo",
        "create_habit" or "new_habit" => "add_habit",
        "create_event" or "new_event" => "add_event",
        "create_widget" or "new_widget" => "add_widget",
        "delete_widget" or "remove_widget" or "workspace.delete_widget" or "quitar_widget" => "workspace.remove_widget",
        "clear_widgets" or "clear_page" or "workspace.clear" or "workspace.clear_page" or "vaciar_pantalla" or "limpiar_pantalla" => "workspace.clear_widgets",
        "delete_timer" or "remove_timer" or "quitar_timer" or "quitar_temporizador" => "workspace.remove_timer",
        "list_sounds" or "sounds" or "get_sounds" or "sonidos" => "sounds.list",
        "set_sound" or "change_sound" or "use_sound" or "sounds.update" => "sounds.set",
        "delete_empty_pages" or "delete_empty_page" or "clear_empty_pages" => "workspace.delete_empty_pages",
        "rename_page" or "name_page" or "workspace.name_page" or "llamar_pagina" or "renombrar_pagina" => "workspace.rename_page",
        "set_home" or "workspace.home" or "set_home_page" => "workspace.set_home",
        "notes.delete" => "notes.forget",
        _ => name.Trim().ToLowerInvariant()
    };

    public static AgentRisk RiskOf(string name) => Canonical(name) switch
    {
        "get_state" or "focus.summarize" or "focus.query_sessions" or "focus.compare_periods" or "focus.list_projects"
            or "todo.search" or "calendar.search" or "calendar.upcoming" or "habits.search" or "notes.search"
            or "workspace.list" or "timer.inspect" or "settings.read" or "memory.search"
            or "finance.summary" or "finance.search" or "workspace.goto" or "workspace.focus"
            or "sounds.list" or "layouts.list" or "notifications.list" or "focus_tasks.list" or "focus_projects.list" or "memory.export" => AgentRisk.Read,
        "todo.delete" or "delete_todo" or "calendar.delete" or "delete_event" or "habits.archive" or "habits.delete" or "memory.forget"
            or "finance.delete" or "finance.clear_expenses" or "finance.archive" or "workspace.delete_empty_pages" or "workspace.delete_page"
            or "workspace.remove_widget" or "workspace.clear_widgets" or "workspace.remove_timer" or "focus_tasks.delete" or "focus_projects.delete" or "todo.clear_done"
            or "layouts.delete" or "notes.forget" => AgentRisk.Destructive,
        "todo.create" or "todo.update" or "todo.complete" or "todo.sort" or "todo.move"
            or "calendar.create" or "calendar.update" or "calendar.reschedule" or "calendar.complete" or "calendar.set_view" or "calendar.navigate" or "calendar.set_show_done"
            or "habits.create" or "habits.mark" or "habits.update" or "habits.restore" or "habits.set_target" or "habits.move" or "habits.set_count"
            or "notes.create" or "notes.append" or "notes.set_color" or "notes.reopen"
            or "workspace.add_widget" or "workspace.move_widget" or "workspace.rename_widget" or "workspace.collapse_widget"
            or "workspace.resize_widget" or "workspace.set_web_url" or "workspace.reload_web" or "workspace.set_web_keepalive" or "workspace.open" or "workspace.add_page"
            or "workspace.rename_page" or "workspace.set_home"
            or "layouts.save" or "layouts.load" or "notifications.dismiss"
            or "add_todo" or "complete_todo" or "add_event" or "complete_event" or "add_habit" or "mark_habit"
            or "add_note" or "add_widget" or "add_focus_task" or "focus_tasks.update" or "focus_tasks.set_done" or "focus_tasks.save_template" or "focus_tasks.add_from_template"
            or "focus_projects.add" or "focus_projects.rename"
            or "memory.propose" or "memory.confirm"
            or "finance.add" or "finance.add_recurring" or "finance.mark_paid" or "finance.set_currency" => AgentRisk.Write,
        "timer" or "timer.start" or "timer.pause" or "timer.reset" or "timer.configure" or "set_focus_duration"
            or "settings.update" or "settings.apply_rhythm" or "sounds.set" or "app.fullscreen" or "app.open_panel" or "app.open_data_folder" => AgentRisk.Batch,
        _ => AgentRisk.Write
    };

    public static bool Exists(string name)
    {
        string tool = Canonical(name);
        return Actions.Contains(tool);
    }

    public static bool IsQuery(string name) => RiskOf(name) == AgentRisk.Read;

    public static string Label(string name, string fallback = "") => Canonical(name) switch
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
        "calendar.upcoming" => "próximos pendientes del calendario",
        "calendar.update" or "calendar.reschedule" => "reprogramar evento",
        "habits.create" or "add_habit" => "crear hábito",
        "habits.mark" or "mark_habit" => "marcar hábito",
        "notes.create" or "add_note" => "crear nota",
        "workspace.add_page" => "crear página nueva",
        "workspace.rename_page" => "renombrar página",
        "workspace.set_home" => "marcar página principal",
        "workspace.delete_empty_pages" => "borrar páginas vacías",
        "workspace.remove_widget" => "quitar widget",
        "workspace.clear_widgets" => "vaciar widgets de la página",
        "workspace.remove_timer" => "quitar temporizador",
        "sounds.list" => "sonidos disponibles",
        "sounds.set" => "cambiar sonido",
        "workspace.goto" => "ir a la página",
        "workspace.focus" => "mostrar el widget",
        "add_widget" or "workspace.add_widget" => "añadir widget",
        "timer" or "timer.start" => "temporizador",
        "memory.propose" or "memory.confirm" => "recordar",
        "memory.forget" => "olvidar",
        _ => string.IsNullOrWhiteSpace(fallback) ? name : fallback
    };

    public static string Describe(string name, string argumentsJson, string fallback = "")
    {
        string tool = Canonical(name);
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var args = document.RootElement;
            string title = Read(args, "title");
            string text = Read(args, "text");
            string kind = Read(args, "kind");
            string color = Read(args, "color");
            string page = Read(args, "page");
            return tool switch
            {
                "add_note" or "notes.create" => $"Crear nota{(title.Length > 0 ? " «" + title + "»" : "")}{(color.Length > 0 ? " " + color : "")}{(text.Length > 0 ? ": " + Clip(text, 72) : "")}",
                "add_widget" or "workspace.add_widget" or "workspace.open" => $"Añadir widget{(kind.Length > 0 ? " " + kind : "")}{(title.Length > 0 ? " «" + title + "»" : "")}",
                "workspace.add_page" => "Crear una página nueva y abrirla para que la veas",
                "workspace.rename_page" => DescribeRenamePage(args, page),
                "workspace.set_home" => page.Length > 0 ? $"Marcar la página {page} como casa" : "Marcar esta página como casa",
                "workspace.delete_empty_pages" => "Borrar solo las páginas sin widgets ni temporizador",
                "workspace.remove_timer" => page.Length > 0 ? $"Quitar el temporizador de la página {page}" : "Quitar el temporizador de esta página",
                "workspace.remove_widget" => title.Length > 0 ? $"Quitar el widget «{title}»" : "Quitar ese widget",
                "workspace.clear_widgets" => page.Length > 0 ? $"Vaciar todos los widgets de la página {page}" : "Vaciar todos los widgets de la página actual",
                "sounds.set" => title.Length > 0 ? $"Usar el sonido {title}" : "Cambiar el sonido",
                "workspace.goto" => DescribeGotoPage(args, page),
                "workspace.focus" => title.Length > 0 ? $"Mostrar el widget «{title}»" : "Mostrar el widget",
                "add_todo" or "todo.create" => text.Length > 0 ? $"Crear tarea: {Clip(text, 72)}" : "Crear tarea",
                _ => Label(tool, fallback)
            };
        }
        catch (JsonException)
        {
            return Label(tool, fallback);
        }
    }

    private static string DescribeRenamePage(JsonElement args, string page)
    {
        string renameTo = Read(args, "name");
        if (renameTo.Length == 0) renameTo = Read(args, "new_name");
        if (page.Length > 0 && renameTo.Length > 0) return $"Renombrar la página {page} a «{renameTo}»";
        if (renameTo.Length > 0) return $"Renombrar la página a «{renameTo}»";
        return "Renombrar esa página";
    }

    private static string DescribeGotoPage(JsonElement args, string page)
    {
        string dest = Read(args, "name");
        if (dest.Length == 0) dest = page;
        bool home = args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("home", out var homeVal)
            && homeVal.ValueKind == JsonValueKind.True;
        if (home || dest.Equals("casa", StringComparison.OrdinalIgnoreCase)
            || dest.Equals("home", StringComparison.OrdinalIgnoreCase)
            || dest.Equals("principal", StringComparison.OrdinalIgnoreCase))
            return dest.Length > 0 ? $"Ir a «{dest}»" : "Ir a casa";
        return page.Length > 0 ? $"Ir a la página {page}" : "Ir a esa página";
    }

    private static string Read(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? "" : "";

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";
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
