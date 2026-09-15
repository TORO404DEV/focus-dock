using System.Text.RegularExpressions;

namespace PomoDock.Core;

/// <summary>Turns a YouTube search payload or URL into a watch URL for the first video.</summary>
public static class YouTubeWatch
{
    private static readonly Regex VideoRenderer = new(
        @"videoRenderer""\s*:\s*\{\s*""videoId""\s*:\s*""([a-zA-Z0-9_-]{11})""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AnyVideoId = new(
        @"""videoId""\s*:\s*""([a-zA-Z0-9_-]{11})""",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsYouTube(string? url)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri)) return false;
        string host = uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
        return host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)
            || host.Equals("m.youtube.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("music.youtube.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when a widget title, live HWND caption, or URL is actually YouTube —
    /// a bare Brave/Chrome window is not enough.
    /// </summary>
    public static bool LooksLikeYouTubeContext(string? title, string? url = null)
    {
        if (IsYouTube(url) || IsWatch(url) || IsSearchResults(url)) return true;
        string text = (title ?? "").Trim();
        if (text.Length == 0) return false;
        return text.Contains("youtube", StringComparison.OrdinalIgnoreCase)
            || text.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)
            || text.Contains("watch?v=", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsWatch(string? url)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.Trim('/').Length == 11;
        return uri.AbsolutePath.Contains("/watch", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Contains("/shorts/", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Contains("/embed/", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Contains("/live/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSearchResults(string? url)
    {
        if (!IsYouTube(url) || IsWatch(url)) return false;
        if (!Uri.TryCreate(url!.Trim(), UriKind.Absolute, out var uri)) return false;
        return uri.AbsolutePath.Contains("/results", StringComparison.OrdinalIgnoreCase)
            || uri.Query.Contains("search_query=", StringComparison.OrdinalIgnoreCase);
    }

    public static string WatchUrl(string videoId) =>
        "https://www.youtube.com/watch?v=" + videoId;

    public static string? QueryFromUrl(string? url)
    {
        if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri)) return null;
        foreach (string part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            string key = eq < 0 ? part : part[..eq];
            if (!key.Equals("search_query", StringComparison.OrdinalIgnoreCase)) continue;
            string value = eq < 0 ? "" : part[(eq + 1)..];
            string topic = Uri.UnescapeDataString(value.Replace('+', ' ')).Trim();
            return topic.Length == 0 ? null : topic;
        }
        return null;
    }

    public static string? FirstVideoId(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        var preferred = VideoRenderer.Match(payload);
        if (preferred.Success) return preferred.Groups[1].Value;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in AnyVideoId.Matches(payload))
        {
            string id = match.Groups[1].Value;
            if (!seen.Add(id)) continue;
            int around = Math.Max(0, match.Index - 80);
            string prefix = payload.Substring(around, Math.Min(120, payload.Length - around));
            if (prefix.Contains("playlistRenderer", StringComparison.Ordinal)
                || prefix.Contains("compactStationRenderer", StringComparison.Ordinal))
                continue;
            return id;
        }
        return null;
    }
}
