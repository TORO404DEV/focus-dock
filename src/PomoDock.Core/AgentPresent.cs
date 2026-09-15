using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PomoDock.Core;

/// <summary>
/// Turns tool JSON into something you can read in chat. The model is not allowed to dump this
/// into a sticky note, and the voice only speaks the first line.
/// </summary>
public static class AgentPresent
{
    public static bool WantsTable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return Regex.IsMatch(text, @"\b(tabla|table|desglose|detalle|detallad\w*|breakdown|grid)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(text, @"lista (de )?(las )?sesiones|session list", RegexOptions.IgnoreCase);
    }

    public static bool WantsANote(string? text) =>
        !string.IsNullOrWhiteSpace(text) && Regex.IsMatch(text, @"\b(nota|note|sticky)\b", RegexOptions.IgnoreCase);

    public static bool WantsFocusTable(string request, IReadOnlyList<AgentChatMessage>? history)
    {
        if (!WantsTable(request) || WantsANote(request)) return false;
        if (MentionsFocus(request)) return true;
        foreach (var message in (history ?? []).Reverse().Take(12))
        {
            if (message.Receipts.Any(receipt => receipt.Tool.StartsWith("focus.", StringComparison.OrdinalIgnoreCase)))
                return true;
            if (MentionsFocus(message.Text)) return true;
        }
        return false;
    }

    public static bool LooksLikeGrid(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int rows = lines.Count(line => line.Contains("  ") && line.Trim().Length > 12);
        return rows >= 3;
    }

    public static string SpokenLead(string display, bool spanish)
    {
        if (string.IsNullOrWhiteSpace(display)) return "";
        string first = display.Replace("\r\n", "\n").Split('\n')[0].Trim();
        if (!LooksLikeGrid(display)) return display.Trim();
        if (first.Length == 0)
            return spanish ? "La tabla está en el chat." : "The table is in the chat.";
        return first;
    }

    public static string GuessFocusPeriod(string request, IReadOnlyList<AgentChatMessage>? history)
    {
        if (FocusHistory.TryGuessPeriod(request, out var fromRequest) && MentionsFocus(request))
            return fromRequest;
        foreach (var message in (history ?? []).Reverse().Take(12))
        {
            if (FocusHistory.TryGuessPeriod(message.Text, out var fromHistory))
                return fromHistory;
        }
        return "this_month";
    }

    public static string FormatFocusQuery(string json, bool spanish)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = document.RootElement;
        var data = root.TryGetProperty("data", out var nested) ? nested : root;
        int total = ReadInt(data, "totalMatching", ReadInt(data, "returned", 0));
        int shown = ReadInt(data, "returned", 0);
        double minutes = ReadDouble(data, "totalMinutes", 0);
        if (minutes <= 0 && data.TryGetProperty("sessions", out var rowsForSum) && rowsForSum.ValueKind == JsonValueKind.Array)
            minutes = rowsForSum.EnumerateArray().Sum(row => ReadDouble(row, "minutes", 0));
        string from = ReadString(data, "from");
        string to = ReadString(data, "to");
        var lines = new StringBuilder();
        string hours = minutes.ToString("0.##", CultureInfo.CurrentCulture);
        if (spanish)
            lines.Append(shown < total
                ? $"{total} sesiones · {hours} h (muestro las {shown} más recientes)."
                : $"{total} sesiones · {hours} h.");
        else
            lines.Append(shown < total
                ? $"{total} sessions · {hours} h (showing the latest {shown})."
                : $"{total} sessions · {hours} h.");
        if (from.Length > 0 && to.Length > 0)
            lines.Append(spanish ? $" {from} → {to}." : $" {from} → {to}.");
        lines.AppendLine();
        lines.AppendLine(spanish
            ? "FECHA       INICIO FIN   PROYECTO          TAREA             MIN  ESTADO"
            : "DATE        START  END   PROJECT           TASK              MIN  STATE");
        if (!data.TryGetProperty("sessions", out var sessions) || sessions.ValueKind != JsonValueKind.Array)
            return lines.ToString().TrimEnd();
        foreach (var row in sessions.EnumerateArray())
        {
            DateTimeOffset.TryParse(ReadString(row, "started"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var start);
            DateTimeOffset.TryParse(ReadString(row, "ended"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var end);
            string date = start == default ? "----------" : start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string startHm = start == default ? "--:--" : start.ToString("HH:mm", CultureInfo.InvariantCulture);
            string endHm = end == default ? "--:--" : end.ToString("HH:mm", CultureInfo.InvariantCulture);
            string project = Clip(Blank(ReadString(row, "Project"), spanish ? "Sin proyecto" : "No project"), 16);
            string task = Clip(Blank(ReadString(row, "Task"), spanish ? "Enfoque libre" : "Free focus"), 16);
            int min = (int)Math.Round(ReadDouble(row, "minutes", 0));
            string outcome = Outcome(ReadString(row, "outcome"), spanish);
            lines.AppendLine($"{date}  {startHm} {endHm}  {project,-16}  {task,-16}  {min,3}  {outcome}");
        }
        return lines.ToString().TrimEnd();
    }

    private static bool MentionsFocus(string? text) =>
        !string.IsNullOrWhiteSpace(text) && Regex.IsMatch(text,
            @"enfoq|pomodoro|\bfocus\b|\bhoras?\b|\bhours?\b|\bsesi[oó]n|\bsessions?\b|\btrabaj|minutos de (enfoque|foco)",
            RegexOptions.IgnoreCase);

    private static string Outcome(string raw, bool spanish) => raw.ToLowerInvariant() switch
    {
        "completed" => spanish ? "Completada" : "Completed",
        "partial" => spanish ? "Parcial" : "Partial",
        _ => raw.Length == 0 ? (spanish ? "—" : "—") : raw
    };

    private static string Blank(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) || value is "(none)" or "(ninguno)" or "Sin proyecto" ? fallback : value.Trim();

    private static string Clip(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    private static string ReadString(JsonElement data, string key)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(key, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? "",
            JsonValueKind.Number => value.ToString(),
            _ => ""
        };
    }

    private static int ReadInt(JsonElement data, string key, int fallback) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var value) && value.TryGetInt32(out int number)
            ? number : fallback;

    private static double ReadDouble(JsonElement data, string key, double fallback)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(key, out var value)) return fallback;
        if (value.TryGetDouble(out double number)) return number;
        return fallback;
    }
}
