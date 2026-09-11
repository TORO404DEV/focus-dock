using System.Text.RegularExpressions;

namespace PomoDock.Core;

/// <summary>Deterministic intent for memory vs reminders vs focus questions, before the model speaks.</summary>
public static class AgentIntent
{
    public static bool IsWhatDoYouKnow(string text) =>
        Regex.IsMatch(text ?? "", @"qu[eé]\s+sabes\s+de\s+m[ií]|what\s+do\s+you\s+know\s+about\s+me", RegexOptions.IgnoreCase);

    public static bool IsRemindTask(string text) =>
        Regex.IsMatch(text ?? "", @"recu[eé]rdame|remind\s+me", RegexOptions.IgnoreCase);

    public static bool TryRemember(string text, out string fact)
    {
        fact = "";
        if (string.IsNullOrWhiteSpace(text) || IsRemindTask(text)) return false;
        var match = Regex.Match(text.Trim(), @"^(recuerda(?:\s+que)?|remember(?:\s+that)?)\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        fact = match.Success ? match.Groups[2].Value.Trim().TrimEnd('.') : "";
        return fact.Length > 0;
    }

    public static bool TryForget(string text, out string query)
    {
        query = "";
        if (string.IsNullOrWhiteSpace(text) || IsRemindTask(text)) return false;
        var match = Regex.Match(text.Trim(), @"^(olv[ií]da(?:lo|r)?(?:\s+que)?|forget(?:\s+that| it)?)\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success) return false;
        query = match.Groups[2].Value.Trim().TrimEnd('.');
        return true;
    }

    public static bool IsStandaloneFocusQuestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (IsRemindTask(text) || TryRemember(text, out _) || TryForget(text, out _)) return false;
        if (!FocusHistory.TryGuessPeriod(text, out _)) return false;
        string lower = text.ToLowerInvariant();
        string[] writes = ["añade", "agrega", "crea", "borra", "elimina", "completa", "marca", "add ", "create ", "delete ", "complete ", "mark "];
        return !writes.Any(verb => lower.Contains(verb));
    }
}
