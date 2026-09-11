namespace PomoDock.Core;

/// <summary>Qwen3 may emit a &lt;think&gt; block. The user never sees it; JSON parsing ignores it.</summary>
public static class AgentThink
{
    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string value = text;
        while (true)
        {
            int open = IndexOf(value, "<think>");
            if (open < 0) break;
            int close = IndexOf(value, "</think>", open + 7);
            if (close >= 0)
            {
                value = value[..open] + " " + value[(close + 8)..];
                continue;
            }
            int json = value.IndexOf('{', open);
            value = json >= 0 ? value[..open] + value[json..] : value[..open];
            break;
        }
        return value.Trim();
    }

    public static bool LooksLikeJson(string? text) => Strip(text).Contains('{');

    private static int IndexOf(string text, string token, int start = 0) =>
        text.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
}
