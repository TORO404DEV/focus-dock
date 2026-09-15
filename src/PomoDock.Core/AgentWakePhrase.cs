using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PomoDock.Core;

/// <summary>
/// Pulls a spoken command out of a Whisper transcript.
/// The wake name is optional: a clear order still runs. “pomodoro” is the timer, not a wake.
/// </summary>
public static class AgentWakePhrase
{
    public const string DefaultName = "pomodock";
    private const string Greetings = "hey|hi|hello|eh|ey|ei|oye|oy|ok|okay|hola|ej|ea|ah|oh|uh|um|wey|guey|wei|ye|ya|a|el|la|lo";
    private static readonly HashSet<string> GreetingSet = new(Greetings.Split('|'), StringComparer.Ordinal);

    public static string Fold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var normalized = text.Normalize(NormalizationForm.FormD);
        var chars = new StringBuilder(normalized.Length);
        foreach (char c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            chars.Append(char.ToLowerInvariant(c));
        }
        string folded = Regex.Replace(chars.ToString(), @"[^a-z0-9]+", " ").Trim();
        return Regex.Replace(folded, @"\s+", " ");
    }

    /// <summary>
    /// Null = nothing to run. Empty = wake only (arm). Otherwise the instruction.
    /// The wake name is optional: a clear order runs even if it was skipped or mangled.
    /// </summary>
    public static string? TakeSpokenOrder(string? transcript, string? wakeName = DefaultName)
    {
        if (WhisperTranscript.IsJunk(transcript)) return null;
        string raw = (transcript ?? "").Trim();
        if (raw.Length == 0) return null;
        string? woken = TakeCommand(raw, wakeName);
        if (woken is not null)
        {
            if (woken.Length == 0) return "";
            if (AgentIntent.LooksLikeSpokenOrder(woken)) return AgentIntent.ClipSpokenOrder(woken);
            if (AgentIntent.LooksLikeSpokenOrder(raw)) return AgentIntent.ClipSpokenOrder(raw);
            return "";
        }
        if (!AgentIntent.LooksLikeSpokenOrder(raw)) return null;
        return AgentIntent.ClipSpokenOrder(raw);
    }

    /// <summary>
    /// Null = the wake name was not heard. Empty = only the wake name. Otherwise the instruction.
    /// </summary>
    public static string? TakeCommand(string? transcript, string? wakeName = DefaultName)
    {
        string folded = Fold(transcript);
        if (folded.Length == 0) return null;
        if (!TryFindWake(folded, wakeName, out int start, out int length)) return null;
        string rest = (folded[..start] + " " + folded[(start + length)..]).Trim();
        rest = Regex.Replace(rest, @"\s+", " ");
        return rest;
    }

    public static bool Heard(string? transcript, string? wakeName = DefaultName) =>
        TakeCommand(transcript, wakeName) is not null;

    private static bool TryFindWake(string folded, string? wakeName, out int start, out int length)
    {
        start = 0;
        length = 0;
        int bestStart = int.MaxValue;
        int bestLength = 0;
        foreach (var hit in FindWakeSpans(folded, wakeName))
        {
            if (hit.start < bestStart || (hit.start == bestStart && hit.length > bestLength))
            {
                bestStart = hit.start;
                bestLength = hit.length;
            }
        }
        if (bestLength == 0) return false;
        start = bestStart;
        length = bestLength;
        return true;
    }

    private static IEnumerable<(int start, int length)> FindWakeSpans(string folded, string? wakeName)
    {
        string name = Fold(string.IsNullOrWhiteSpace(wakeName) ? DefaultName : wakeName);
        if (name.Length == 0) name = DefaultName;
        string compact = Compact(name);
        bool isDefault = compact == DefaultName;

        foreach (string alias in Aliases(wakeName))
        {
            string pattern = $@"(?:(?<=^)|(?<=\s))(?:(?:{Greetings})\s+)?{Regex.Escape(alias)}(?=\s|$)";
            foreach (Match match in Regex.Matches(folded, pattern, RegexOptions.CultureInvariant))
                yield return (match.Index, match.Length);
        }

        var words = Regex.Matches(folded, @"[a-z0-9]+");
        for (int i = 0; i < words.Count; i++)
        {
            var word = words[i];
            int from = word.Index;
            int to = word.Index + word.Length;
            bool hit = isDefault ? LooksLikeDefaultWake(word.Value) : LooksLikeCustomWake(word.Value, compact);
            if (!hit && i + 1 < words.Count && !GreetingSet.Contains(word.Value))
            {
                string next = words[i + 1].Value;
                if (!IsTimerWord(word.Value) && !IsTimerWord(next))
                {
                    string joined = word.Value + next;
                    if (isDefault ? LooksLikeDefaultWake(joined) : LooksLikeCustomWake(joined, compact))
                    {
                        hit = true;
                        to = words[i + 1].Index + words[i + 1].Length;
                    }
                }
            }
            if (!hit) continue;
            if (i > 0 && GreetingSet.Contains(words[i - 1].Value))
                from = words[i - 1].Index;
            yield return (from, to - from);
        }
    }

    /// <summary>
    /// Whisper routinely hears “PomoDock” as hipómodo, pomalog, pomodorka, pomodok, …
    /// Consonant skeleton P-M-T-K (pomo + dock) with a small edit distance covers that
    /// without treating the timer word “pomodoro” as a wake.
    /// </summary>
    private static bool LooksLikeDefaultWake(string token)
    {
        string value = Compact(token);
        if (value.Length < 4 || value.Length > 16) return false;
        if (IsTimerWord(value) || value.Contains("pomodoro", StringComparison.Ordinal)) return false;
        if (LooksLikePomoName(value)) return true;
        if (value.Length >= 7 && Levenshtein(value, DefaultName) <= 3) return true;
        if (!Regex.IsMatch(value, @"m[aeiouy]+[dltr]", RegexOptions.CultureInvariant)) return false;
        string skeleton = Skeleton(value);
        if (skeleton.Length < 3) return false;
        int distance = Levenshtein(skeleton, "PMTK");
        if (distance > 1) return false;
        bool dockish = value.IndexOfAny(['c', 'k', 'g', 'q', 'x']) >= 0;
        return dockish || value.Length >= 8;
    }

    private static bool LooksLikeCustomWake(string token, string compactName)
    {
        string value = Compact(token);
        if (value == compactName) return true;
        if (IsTimerWord(value) || compactName.Length < 5 || value.Length < 5) return false;
        int allowed = compactName.Length <= 6 ? 1 : 2;
        return Levenshtein(value, compactName) <= allowed;
    }

    /// <summary>
    /// Short “pomo…” slips (pomodo, pomog) count; “pomada” and similar words do not.
    /// </summary>
    private static bool LooksLikePomoName(string value)
    {
        if (!(value.StartsWith("pomo") || value.StartsWith("bomo") || value.StartsWith("pama") || value.StartsWith("pumo")))
            return false;
        if (value is "pomada" or "pomadas") return false;
        if (value.Length >= 7) return true;
        return value is "pomo" or "pomog" or "pomod" or "pomodo" or "bomod" or "bomodo" or "pamod" or "pamodo";
    }

    private static bool IsTimerWord(string token) =>
        token.StartsWith("pomodor", StringComparison.Ordinal) && token.Length <= 11;

    private static string Compact(string value) => value.Replace(" ", "", StringComparison.Ordinal);

    private static string Skeleton(string value)
    {
        var chars = new StringBuilder(value.Length);
        char last = '\0';
        foreach (char c in value)
        {
            char mapped = c switch
            {
                'p' or 'b' or 'f' or 'v' or 'h' => 'P',
                'm' or 'n' => 'M',
                'd' or 't' => 'T',
                'c' or 'k' or 'q' or 'g' or 'x' or 'j' => 'K',
                'l' or 'r' => 'L',
                's' or 'z' => 'S',
                _ => '\0'
            };
            if (mapped == '\0' || mapped == last) continue;
            chars.Append(mapped);
            last = mapped;
        }
        return chars.ToString();
    }

    private static int Levenshtein(string left, string right)
    {
        if (left.Length == 0) return right.Length;
        if (right.Length == 0) return left.Length;
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (int j = 0; j <= right.Length; j++) previous[j] = j;
        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= right.Length; j++)
            {
                int cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }

    internal static IEnumerable<string> Aliases(string? wakeName)
    {
        string name = Fold(string.IsNullOrWhiteSpace(wakeName) ? DefaultName : wakeName);
        if (name.Length == 0) name = DefaultName;
        yield return name;
        if (name == DefaultName)
        {
            yield return "pomo dock";
            yield return "pomodoc";
            yield return "pomo doc";
            yield return "pomo dog";
            yield return "pomo duck";
            yield return "pomoduck";
            yield return "pomodok";
            yield return "pomo duk";
            yield return "pomo dak";
            yield return "pomodoque";
            yield return "promo dock";
            yield return "promo doc";
            yield return "pamo dock";
            yield return "bomo dock";
            yield return "bomodock";
            yield return "pomolog";
            yield return "poma log";
            yield return "pomalog";
            yield return "pomodorka";
            yield return "pomo dorka";
            yield return "pomodo";
            yield return "pomog";
            yield return "pomo";
            yield return "pomod";
            yield return "pomoda";
            yield return "hipo modo";
        }
    }
}
