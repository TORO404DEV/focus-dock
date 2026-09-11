using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PomoDock.Core.Agent;

/// <summary>
/// Stops the agent from repeating the same tool call (or thrashing the same failing shape).
/// Identical fingerprints inside one turn budget are blocked with a clear explanation.
/// </summary>
public sealed class AgentLoopGuard
{
    private readonly int maxIdentical;
    private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);
    private readonly List<string> history = [];

    public AgentLoopGuard(int maxIdentical = 2)
    {
        if (maxIdentical < 1) throw new ArgumentOutOfRangeException(nameof(maxIdentical));
        this.maxIdentical = maxIdentical;
    }

    public IReadOnlyList<string> History => history;

    public static string Fingerprint(AgentToolCall call)
    {
        var args = call.Arguments
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key.ToLowerInvariant()}={pair.Value?.Trim() ?? ""}");
        var raw = $"{call.Name.Trim().ToLowerInvariant()}|{string.Join("&", args)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16];
    }

    public bool WouldBlock(AgentToolCall call) => counts.GetValueOrDefault(Fingerprint(call)) >= maxIdentical;

    /// <summary>Records a call. Returns false when the call must not run again.</summary>
    public bool TryEnter(AgentToolCall call, out string reason)
    {
        var fp = Fingerprint(call);
        int next = counts.GetValueOrDefault(fp) + 1;
        if (next > maxIdentical)
        {
            reason = $"Bucle detectado: «{call.Name}» con los mismos argumentos ya se ejecutó {maxIdentical} veces. No se repetirá. Cambia el pedido o cancela.";
            history.Add($"blocked:{fp}:{call.Name}");
            return false;
        }
        counts[fp] = next;
        history.Add($"ok:{fp}:{call.Name}");
        reason = "";
        return true;
    }

    public void Reset()
    {
        counts.Clear();
        history.Clear();
    }
}

/// <summary>Catalog of tools the runtime may expose to the model and to the host.</summary>
public static class AgentToolCatalog
{
    public static IReadOnlyList<AgentToolDefinition> All { get; } =
    [
        new() { Name = "focus.summarize", Kind = AgentToolKind.Read, Description = "Totales deterministas de enfoque desde Store.Sessions (periodo, proyecto).", SchemaHint = "{period, project?, from?, to?}" },
        new() { Name = "memory.list", Kind = AgentToolKind.Read, Description = "Lista hechos de memoria personal local.", SchemaHint = "{}" },
        new() { Name = "memory.remember", Kind = AgentToolKind.Write, Description = "Guarda un hecho en memoria personal.", SchemaHint = "{text, tags?}" },
        new() { Name = "memory.forget", Kind = AgentToolKind.Destructive, Description = "Olvida un hecho por id o texto.", SchemaHint = "{id?, text?}" },
        new() { Name = "timer.status", Kind = AgentToolKind.Read, Description = "Estado actual del temporizador.", SchemaHint = "{}" },
        new() { Name = "timer.start", Kind = AgentToolKind.Control, Description = "Inicia o reanuda el temporizador.", SchemaHint = "{phase?}" },
        new() { Name = "timer.pause", Kind = AgentToolKind.Control, Description = "Pausa el temporizador.", SchemaHint = "{}" },
        new() { Name = "timer.skip", Kind = AgentToolKind.Control, Description = "Salta la fase actual (parcial).", SchemaHint = "{}" },
        new() { Name = "timer.set_phase", Kind = AgentToolKind.Control, Description = "Cambia a Focus / ShortBreak / LongBreak.", SchemaHint = "{phase}" },
        new() { Name = "tasks.list", Kind = AgentToolKind.Read, Description = "Lista tareas y proyectos de enfoque.", SchemaHint = "{}" },
        new() { Name = "tasks.add", Kind = AgentToolKind.Write, Description = "Añade una tarea de pomodoro.", SchemaHint = "{name, project?, estimate?}" },
        new() { Name = "tasks.complete", Kind = AgentToolKind.Write, Description = "Marca una tarea de pomodoro hecha.", SchemaHint = "{id}" },
        new() { Name = "todo.list", Kind = AgentToolKind.Read, Description = "Lista ítems del To Do.", SchemaHint = "{}" },
        new() { Name = "todo.add", Kind = AgentToolKind.Write, Description = "Añade un ítem al To Do.", SchemaHint = "{title, due?, priority?}" },
        new() { Name = "todo.complete", Kind = AgentToolKind.Write, Description = "Completa un ítem del To Do.", SchemaHint = "{id}" },
        new() { Name = "habits.list", Kind = AgentToolKind.Read, Description = "Lista hábitos activos.", SchemaHint = "{}" },
        new() { Name = "habits.tick", Kind = AgentToolKind.Write, Description = "Marca un hábito hoy.", SchemaHint = "{id}" },
        new() { Name = "habits.add", Kind = AgentToolKind.Write, Description = "Crea un hábito.", SchemaHint = "{name, cadence?}" },
        new() { Name = "agenda.list", Kind = AgentToolKind.Read, Description = "Eventos próximos de la agenda.", SchemaHint = "{days?}" },
        new() { Name = "agenda.add", Kind = AgentToolKind.Write, Description = "Añade un evento (texto natural o campos).", SchemaHint = "{text}" },
        new() { Name = "notes.add", Kind = AgentToolKind.Write, Description = "Crea una nota rápida.", SchemaHint = "{text, color?}" },
        new() { Name = "settings.get", Kind = AgentToolKind.Read, Description = "Lee ajustes no sensibles.", SchemaHint = "{}" },
        new() { Name = "workspace.list", Kind = AgentToolKind.Read, Description = "Páginas y widgets del canvas.", SchemaHint = "{}" },
        new() { Name = "agent.undo", Kind = AgentToolKind.Control, Description = "Deshace un recibo previo.", SchemaHint = "{receiptId}" }
    ];

    public static AgentToolDefinition? Find(string name) =>
        All.FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase));

    public static AgentToolKind KindOf(string name) => Find(name)?.Kind ?? AgentToolKind.Control;

    public static string FingerprintCatalog() =>
        string.Join("\n", All.Select(t => $"{t.Name}|{t.Kind}|{t.Description}"));
}
