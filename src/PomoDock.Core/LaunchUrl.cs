using System.Text.RegularExpressions;

namespace PomoDock.Core;

/// <summary>Turns a typed URL or a loose “play lofi” request into something a browser can open.</summary>
public static class LaunchUrl
{
    public static string? ForBrowser(string? url, string? query)
    {
        string address = (url ?? "").Trim();
        string topic = (query ?? "").Trim();
        if (TryHttp(address, out var absolute)) return absolute;

        string text = topic.Length > 0 ? topic : address;
        if (text.Length == 0) return null;
        if (LooksLikeLofi(text))
            return "https://www.youtube.com/results?search_query=lofi+hip+hop+radio+live";
        return "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(text);
    }

    public static bool LooksLikeLofi(string text) =>
        Regex.IsMatch(text ?? "", @"\blo[\s\-]?fi\b", RegexOptions.IgnoreCase);

    private static bool TryHttp(string value, out string uri)
    {
        uri = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http")
        {
            uri = parsed.AbsoluteUri;
            return true;
        }
        if (value.Contains(' ', StringComparison.Ordinal) || !value.Contains('.')) return false;
        if (!Uri.TryCreate("https://" + value.TrimStart('/'), UriKind.Absolute, out parsed)) return false;
        if (parsed.Scheme != "https" || string.IsNullOrWhiteSpace(parsed.Host) || !parsed.Host.Contains('.'))
            return false;
        uri = parsed.AbsoluteUri;
        return true;
    }
}

/// <summary>Maps what a person says (“brave”, “VS Code”) to a stable program id.</summary>
public static class AppLaunchNames
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["brave"] = "brave",
        ["brave-browser"] = "brave",
        ["brave browser"] = "brave",
        ["chrome"] = "chrome",
        ["google chrome"] = "chrome",
        ["google-chrome"] = "chrome",
        ["edge"] = "msedge",
        ["msedge"] = "msedge",
        ["microsoft edge"] = "msedge",
        ["firefox"] = "firefox",
        ["mozilla"] = "firefox",
        ["mozilla firefox"] = "firefox",
        ["opera"] = "opera",
        ["vivaldi"] = "vivaldi",
        ["spotify"] = "spotify",
        ["vscode"] = "code",
        ["vs code"] = "code",
        ["visual studio code"] = "code",
        ["code"] = "code",
        ["telegram"] = "telegram",
        ["discord"] = "discord",
        ["slack"] = "slack",
        ["notepad"] = "notepad",
        ["calc"] = "calc",
        ["calculator"] = "calc",
        ["calculadora"] = "calc",
        ["whatsapp"] = "whatsapp"
    };

    public static string Normalize(string? query)
    {
        string value = (query ?? "").Trim().Trim('"');
        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
        value = Regex.Replace(value, @"\s+", " ");
        return value;
    }

    public static string? CanonicalId(string? query)
    {
        string n = Normalize(query);
        if (n.Length == 0) return null;
        if (Aliases.TryGetValue(n, out var id)) return id;
        if (n.Contains('\\') || n.Contains('/'))
        {
            string file = Path.GetFileNameWithoutExtension(n);
            return Aliases.TryGetValue(file, out id) ? id : file.ToLowerInvariant();
        }
        return n.ToLowerInvariant();
    }

    public static bool IsBrowser(string? id)
    {
        string key = CanonicalId(id) ?? "";
        return key is "brave" or "chrome" or "msedge" or "firefox" or "opera" or "vivaldi";
    }
}
