using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows.Threading;
using PomoDock.App.Native;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>Resolves a YouTube search to the first watch URL and clicks play inside the embedded browser.</summary>
internal static class YouTubePlay
{
    private static readonly HttpClient http = Create();

    private static HttpClient Create()
    {
        var client = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "es-MX,es;q=0.9,en;q=0.8");
        return client;
    }

    public static async Task<string?> ResolveWatchAsync(string? url, string? query, CancellationToken cancellationToken)
    {
        if (YouTubeWatch.IsWatch(url)) return url;
        string topic = (query ?? "").Trim();
        if (topic.Length == 0) topic = YouTubeWatch.QueryFromUrl(url) ?? "";
        if (topic.Length == 0 && LaunchUrl.LooksLikeLofi(url ?? "")) topic = "lofi hip hop radio live";
        if (topic.Length == 0) return null;

        string? id = await SearchInnertubeAsync(topic, cancellationToken).ConfigureAwait(false)
            ?? await SearchHtmlAsync(topic, cancellationToken).ConfigureAwait(false);
        return id is null ? null : YouTubeWatch.WatchUrl(id);
    }

    public static async Task ActivateAsync(Dispatcher dispatcher, nint hwnd, bool watchPage, CancellationToken cancellationToken)
    {
        if (hwnd == 0) return;
        await Task.Delay(watchPage ? 1800 : 2800, cancellationToken).ConfigureAwait(false);
        if (watchPage)
        {
            await dispatcher.InvokeAsync(() => WindowInput.ClickClient(hwnd, 0.50, 0.42));
            return;
        }
        bool invoked = await dispatcher.InvokeAsync(() => WindowInput.InvokeFirstYouTubeResult(hwnd));
        if (invoked) return;
        await dispatcher.InvokeAsync(() => WindowInput.ClickClient(hwnd, 0.24, 0.46));
        await Task.Delay(1600, cancellationToken).ConfigureAwait(false);
        await dispatcher.InvokeAsync(() => WindowInput.ClickClient(hwnd, 0.50, 0.42));
    }

    private static async Task<string?> SearchInnertubeAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            const string endpoint = "https://www.youtube.com/youtubei/v1/search?prettyPrint=false";
            string body = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20240815.00.00\",\"hl\":\"es\",\"gl\":\"MX\"}},\"query\":"
                + JsonEscape(query) + "}";
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return YouTubeWatch.FirstVideoId(json);
        }
        catch { return null; }
    }

    private static async Task<string?> SearchHtmlAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            string url = "https://www.youtube.com/results?hl=es&gl=MX&search_query=" + Uri.EscapeDataString(query);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Cookie", "CONSENT=YES+cb.20210328-17-p0.es+FX+123");
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return YouTubeWatch.FirstVideoId(html);
        }
        catch { return null; }
    }

    private static string JsonEscape(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (char c in value)
        {
            builder.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => c < 32 ? "\\u" + ((int)c).ToString("x4") : c.ToString()
            });
        }
        builder.Append('"');
        return builder.ToString();
    }
}
