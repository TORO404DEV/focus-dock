using PomoDock.Core;

internal static class LaunchUrlTests
{
    public static void Run(Action<string, Action> test, Action<bool, string> assert)
    {
        test("https urls pass through to the browser", () =>
        {
            assert(LaunchUrl.ForBrowser("https://www.youtube.com/watch?v=abc", "") == "https://www.youtube.com/watch?v=abc", "watch urls stay intact");
        });

        test("a bare host gets https", () =>
        {
            string? url = LaunchUrl.ForBrowser("www.youtube.com/results?search_query=jazz", null);
            assert(url is not null && url.StartsWith("https://www.youtube.com/", StringComparison.Ordinal), "youtube host is opened as https");
        });

        test("http is left as http so a real browser can still open it", () =>
        {
            assert(LaunchUrl.ForBrowser("http://example.com/", "") == "http://example.com/", "http is not forced to https");
        });

        test("a lofi request becomes a youtube search", () =>
        {
            string? url = LaunchUrl.ForBrowser(null, "reproduce musica lofi");
            assert(url is not null && url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) && url.Contains("lofi", StringComparison.OrdinalIgnoreCase), "lofi opens youtube");
            assert(LaunchUrl.LooksLikeLofi("lo-fi hip hop"), "lo-fi counts");
        });

        test("a free query without a url becomes a youtube search", () =>
        {
            string? url = LaunchUrl.ForBrowser("", "piano jazz radio");
            assert(url is not null && url.Contains("search_query=", StringComparison.Ordinal) && url.Contains("piano", StringComparison.OrdinalIgnoreCase), "loose queries search youtube");
        });

        test("an empty request does not invent a page", () =>
        {
            assert(LaunchUrl.ForBrowser(null, "  ") is null, "blank stays blank");
        });

        test("spoken app names map to a stable id", () =>
        {
            assert(AppLaunchNames.CanonicalId("Brave") == "brave", "brave");
            assert(AppLaunchNames.CanonicalId("brave-browser.exe") == "brave", "brave exe");
            assert(AppLaunchNames.CanonicalId("google chrome") == "chrome", "chrome");
            assert(AppLaunchNames.CanonicalId("Microsoft Edge") == "msedge", "edge");
            assert(AppLaunchNames.CanonicalId("VS Code") == "code", "vscode");
            assert(AppLaunchNames.CanonicalId("calculadora") == "calc", "calc");
            assert(AppLaunchNames.IsBrowser("brave") && AppLaunchNames.IsBrowser("msedge") && !AppLaunchNames.IsBrowser("spotify"), "browser ids");
        });
    }
}
