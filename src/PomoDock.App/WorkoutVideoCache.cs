using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace PomoDock.App;

/// <summary>
/// WPF MediaElement often fails on HTTPS CDN streams; cache MP4s to disk and play locally.
/// </summary>
internal static class WorkoutVideoCache
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "PomoDock/1.0");
        return http;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Task<string>> InFlight = new(StringComparer.OrdinalIgnoreCase);

    internal static string RootDir(string storeDirectory) =>
        Path.Combine(storeDirectory, "workout-videos");

    internal static string LocalPath(string remoteUrl, string storeDirectory) =>
        Path.Combine(RootDir(storeDirectory), FileNameFor(remoteUrl));

    internal static bool IsCached(string remoteUrl, string storeDirectory)
    {
        string path = LocalPath(remoteUrl, storeDirectory);
        return File.Exists(path) && new FileInfo(path).Length > 1024;
    }

    internal static async Task<string> EnsureLocalAsync(string remoteUrl, string storeDirectory, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            throw new ArgumentException("URL vacía");
        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new ArgumentException("Solo HTTPS");

        Directory.CreateDirectory(RootDir(storeDirectory));
        string path = LocalPath(remoteUrl, storeDirectory);
        if (File.Exists(path) && new FileInfo(path).Length > 1024)
            return path;

        Task<string> download;
        lock (Gate)
        {
            if (InFlight.TryGetValue(path, out var existing))
                download = existing;
            else
            {
                download = DownloadAsync(uri, path, cancel);
                InFlight[path] = download;
            }
        }

        try { return await download.ConfigureAwait(false); }
        finally
        {
            lock (Gate)
            {
                if (InFlight.TryGetValue(path, out var current) && ReferenceEquals(current, download))
                    InFlight.Remove(path);
            }
        }
    }

    private static async Task<string> DownloadAsync(Uri uri, string path, CancellationToken cancel)
    {
        string temp = path + ".part";
        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(output, cancel).ConfigureAwait(false);

            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
            return path;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
            throw;
        }
    }

    private static string FileNameFor(string url)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim().ToLowerInvariant()));
        string hash = Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
        string leaf = Path.GetFileName(new Uri(url).AbsolutePath);
        if (string.IsNullOrWhiteSpace(leaf) || leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            leaf = "media.bin";
        string ext = Path.GetExtension(leaf);
        if (string.IsNullOrWhiteSpace(ext) || ext.Length > 8)
            ext = url.Contains("poster", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".mp4";
        string stem = Path.GetFileNameWithoutExtension(leaf);
        if (string.IsNullOrWhiteSpace(stem)) stem = "media";
        return hash + "-" + stem + ext;
    }
}
