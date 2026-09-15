using System.Text.RegularExpressions;

namespace PomoDock.Core;

/// <summary>
/// Whisper hallucinations and non-speech tags that must not reach the wake listener or dictation.
/// </summary>
public static class WhisperTranscript
{
    private static readonly HashSet<string> JunkFolded = new(StringComparer.Ordinal)
    {
        "blank audio", "music", "musica", "musica de fondo", "typing", "keyboard clacking",
        "coyote howling", "ukrainian idiom", "applause", "laughing", "laughter", "silence",
        "silencio", "subtitle", "subtitles", "thank you", "thanks", "thanks for watching",
        "you", "please subscribe", "like and subscribe", "upbeat music", "background music", "music playing"
    };

    public static bool IsJunk(string? text)
    {
        string raw = (text ?? "").Trim();
        if (raw.Length == 0) return true;
        if (Regex.IsMatch(raw, @"^[?\s¿¡.,;:…\-–—]+$")) return true;
        if (Regex.IsMatch(raw, @"^(\[[^\]]+\]|\([^)]+\)|\s)+$")) return true;
        string folded = AgentWakePhrase.Fold(raw);
        if (folded.Length == 0) return true;
        if (JunkFolded.Contains(folded)) return true;
        if (folded.StartsWith("subtitle", StringComparison.Ordinal)) return true;
        if (folded.StartsWith("thanks for watching", StringComparison.Ordinal)) return true;
        return false;
    }
}
