namespace PomoDock.Core;

/// <summary>The model may emit a think block. The user never sees it; JSON parsing ignores it.</summary>
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

    public static string LiveText(string? raw)
    {
        string stripped = Strip(raw);
        string action = JsonString(stripped, "action").Trim();
        if (action.Length > 0 && action is not "finish" and not "plan") return "";
        return JsonString(stripped, "message");
    }

    public static bool LooksLikeJson(string? text) => Strip(text).Contains('{');

    private static string JsonString(string json, string key)
    {
        string needle = "\"" + key + "\"";
        int at = json.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return "";
        int colon = json.IndexOf(':', at + needle.Length);
        if (colon < 0) return "";
        int quote = json.IndexOf('"', colon + 1);
        if (quote < 0) return "";
        var output = new System.Text.StringBuilder();
        for (int i = quote + 1; i < json.Length; i++)
        {
            char c = json[i];
            if (c == '\\' && i + 1 < json.Length)
            {
                char next = json[++i];
                output.Append(next == 'n' || next == 'r' ? ' ' : next);
                continue;
            }
            if (c == '"') break;
            output.Append(c);
        }
        return output.ToString();
    }

    private static int IndexOf(string text, string token, int start = 0) =>
        text.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
}
