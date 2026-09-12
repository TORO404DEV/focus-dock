using System.Globalization;
using System.IO;
using System.Reflection;

namespace PomoDock.App;

/// <summary>Loads the agent's operating manual from the embedded Markdown source.</summary>
internal static class AgentInstructions
{
    private static readonly Lazy<string> rules = new(ReadRules);

    internal static string Build(DateTime clock) => rules.Value
        .Replace("{{TODAY}}", clock.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
        // Keep the system prompt stable within a day so the API session is not wiped every second.
        // Exact clock time is supplied on each user turn in Opening (FECHA LOCAL).
        .Replace("{{NOW}}", clock.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static string ReadRules()
    {
        var assembly = typeof(AgentInstructions).Assembly;
        string resource = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("AgentRules.md", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("No se pudieron cargar las reglas del agente.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
