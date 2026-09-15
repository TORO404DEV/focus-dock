using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PomoDock.Core;

namespace PomoDock.App.Native;

public sealed record AppLaunchTarget(string Id, string Name, string ExePath, string ProcessName, bool IsBrowser)
{
    public ImageSource? Icon { get; init; }
    public string Title => Name;
    public string ProcessLabel => $"{Path.GetFileName(ExePath)}  ·  {ProcessName}";
    public override string ToString() => $"{Name}   [{ProcessName}]";
}

internal static class AppLaunch
{
    private static List<AppLaunchTarget>? catalog;
    private static DateTime catalogAt;

    public static List<AppLaunchTarget> KnownInstalled()
    {
        var list = new List<AppLaunchTarget>();
        foreach (var spec in Specs())
        {
            string? path = spec.Paths.FirstOrDefault(File.Exists) ?? ReadAppPath(spec.Process + ".exe");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            list.Add(new AppLaunchTarget(spec.Id, spec.Name, path, spec.Process, spec.Browser) { Icon = IconOf(path) });
        }
        return list;
    }

    public static IReadOnlyList<AppLaunchTarget> Catalog()
    {
        if (catalog is not null && DateTime.UtcNow - catalogAt < TimeSpan.FromMinutes(2)) return catalog;
        var list = new List<AppLaunchTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(IEnumerable<AppLaunchTarget> items)
        {
            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.ExePath) || !seen.Add(item.ExePath)) continue;
                list.Add(item);
            }
        }
        Add(KnownInstalled());
        Add(FromStartMenu());
        catalog = list;
        catalogAt = DateTime.UtcNow;
        return list;
    }

    public static void InvalidateCatalog() { catalog = null; }

    public static AppLaunchTarget? Resolve(string? query, bool searchMenu = false)
    {
        string? id = AppLaunchNames.CanonicalId(query);
        if (id is null) return null;
        IEnumerable<AppLaunchTarget> source = searchMenu ? Catalog() : KnownInstalled();
        var hit = Match(source, id, query);
        if (hit is not null) return hit;
        string? fromRegistry = ReadAppPath(id + ".exe");
        if (!string.IsNullOrWhiteSpace(fromRegistry) && File.Exists(fromRegistry))
            return new AppLaunchTarget(id, TitleOf(id), fromRegistry, Path.GetFileNameWithoutExtension(fromRegistry), AppLaunchNames.IsBrowser(id)) { Icon = IconOf(fromRegistry) };
        return null;
    }

    public static AppLaunchTarget? FirstBrowser(bool searchMenu = false)
    {
        var source = searchMenu ? Catalog() : KnownInstalled();
        foreach (var id in new[] { "brave", "chrome", "msedge", "firefox" })
        {
            var hit = source.FirstOrDefault(app => app.Id == id);
            if (hit is not null) return hit;
        }
        return source.FirstOrDefault(app => app.IsBrowser);
    }

    public static async Task<WindowCandidate> OpenNewWindowAsync(AppLaunchTarget target, string? url, CancellationToken cancellationToken)
    {
        if (!File.Exists(target.ExePath))
            throw new InvalidOperationException(L.T("widgets.launchMissing", target.Name));

        var names = ProcessNamesOf(target);
        var before = WindowLease.OwnedVisible(names).Select(item => item.Handle).ToHashSet();
        var start = new ProcessStartInfo
        {
            FileName = target.ExePath,
            Arguments = BuildArgs(target, url),
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(target.ExePath) is { Length: > 0 } dir ? dir : Environment.CurrentDirectory
        };
        try { Process.Start(start); }
        catch (Exception ex) { throw new InvalidOperationException(L.T("widgets.launchFailed", target.Name, ex.Message)); }

        WindowCandidate? best = null;
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(250, cancellationToken);
            var fresh = WindowLease.OwnedVisible(names).Where(item => !before.Contains(item.Handle)).ToList();
            if (fresh.Count == 0) continue;
            best = PickBest(fresh, url);
            if (!IsTransientTitle(best.Title)) break;
        }
        if (best is null)
            throw new TimeoutException(L.T("widgets.launchTimeout", target.Name));
        return WindowLease.Describe(best.Handle) ?? best;
    }

    private static AppLaunchTarget? Match(IEnumerable<AppLaunchTarget> source, string id, string? query)
    {
        var list = source as IList<AppLaunchTarget> ?? source.ToList();
        return list.FirstOrDefault(app => app.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(app => app.ProcessName.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(app => app.Name.Equals(query?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(app => app.Name.Contains(id, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildArgs(AppLaunchTarget target, string? url)
    {
        bool hasUrl = !string.IsNullOrWhiteSpace(url);
        if (target.Id == "firefox")
            return hasUrl ? "-new-window " + Quote(url!) : "-new-window";
        if (target.IsBrowser)
            return hasUrl ? "--new-window " + Quote(url!) : "--new-window";
        if (target.Id == "spotify" && hasUrl)
            return Quote(url!);
        return "";
    }

    private static string Quote(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : value;

    private static string[] ProcessNamesOf(AppLaunchTarget target) => target.Id switch
    {
        "calc" => ["calc", "CalculatorApp", "ApplicationFrameHost"],
        "spotify" => ["Spotify", "SpotifyWebHelper"],
        "discord" => ["Discord", "Update"],
        _ => [target.ProcessName]
    };

    private static WindowCandidate PickBest(List<WindowCandidate> windows, string? url)
    {
        IEnumerable<WindowCandidate> ranked = windows;
        if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            string host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
            var named = windows.Where(item =>
                item.Title.Contains(host, StringComparison.OrdinalIgnoreCase) ||
                item.Title.Contains("YouTube", StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count > 0) ranked = named;
        }
        return ranked.OrderBy(IsTransientTitle).ThenByDescending(Area).First();
    }

    private static bool IsTransientTitle(WindowCandidate candidate) => IsTransientTitle(candidate.Title);
    private static bool IsTransientTitle(string title) =>
        string.IsNullOrWhiteSpace(title) ||
        title.Equals("New Tab", StringComparison.OrdinalIgnoreCase) ||
        title.Equals("Nueva pestaña", StringComparison.OrdinalIgnoreCase) ||
        title.Equals("about:blank", StringComparison.OrdinalIgnoreCase);

    private static int Area(WindowCandidate candidate)
    {
        if (!Win32.GetWindowRect(candidate.Handle, out var rect)) return 0;
        return Math.Max(0, rect.Right - rect.Left) * Math.Max(0, rect.Bottom - rect.Top);
    }

    private readonly record struct Spec(string Id, string Name, string Process, bool Browser, string[] Paths);

    private static IEnumerable<Spec> Specs()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        yield return new("brave", "Brave", "brave", true, [
            Path.Combine(local, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
            Path.Combine(pf, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
            Path.Combine(pf86, @"BraveSoftware\Brave-Browser\Application\brave.exe")
        ]);
        yield return new("chrome", "Google Chrome", "chrome", true, [
            Path.Combine(local, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe")
        ]);
        yield return new("msedge", "Microsoft Edge", "msedge", true, [
            Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe")
        ]);
        yield return new("firefox", "Firefox", "firefox", true, [
            Path.Combine(pf, @"Mozilla Firefox\firefox.exe"),
            Path.Combine(pf86, @"Mozilla Firefox\firefox.exe")
        ]);
        yield return new("spotify", "Spotify", "Spotify", false, [
            Path.Combine(roaming, @"Spotify\Spotify.exe"),
            Path.Combine(local, @"Microsoft\WindowsApps\Spotify.exe")
        ]);
        yield return new("code", "Visual Studio Code", "Code", false, [
            Path.Combine(local, @"Programs\Microsoft VS Code\Code.exe"),
            Path.Combine(pf, @"Microsoft VS Code\Code.exe")
        ]);
        yield return new("telegram", "Telegram", "Telegram", false, [
            Path.Combine(roaming, @"Telegram Desktop\Telegram.exe"),
            Path.Combine(local, @"Telegram Desktop\Telegram.exe")
        ]);
        string? discord = Newest(Path.Combine(local, "Discord"), "Discord.exe");
        yield return new("discord", "Discord", "Discord", false, discord is null ? [] : [discord]);
        yield return new("slack", "Slack", "slack", false, [
            Path.Combine(local, @"slack\slack.exe")
        ]);
        yield return new("whatsapp", "WhatsApp", "WhatsApp", false, [
            Path.Combine(local, @"WhatsApp\WhatsApp.exe")
        ]);
        yield return new("notepad", "Notepad", "notepad", false, [
            Path.Combine(win, @"System32\notepad.exe")
        ]);
        yield return new("calc", "Calculator", "calc", false, [
            Path.Combine(win, @"System32\calc.exe")
        ]);
    }

    private static string? Newest(string directory, string fileName)
    {
        if (!Directory.Exists(directory)) return null;
        try
        {
            return Directory.EnumerateFiles(directory, fileName, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static string? ReadAppPath(string exeName)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName);
                if (key?.GetValue("") is not string raw || string.IsNullOrWhiteSpace(raw)) continue;
                string path = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
                if (File.Exists(path)) return path;
            }
            catch { }
        }
        return null;
    }

    private static List<AppLaunchTarget> FromStartMenu()
    {
        var list = new List<AppLaunchTarget>();
        var skip = new Regex(@"uninstall|unins000|help|readme|documentation|release notes|website|homepage|license|support", RegexOptions.IgnoreCase);
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return list;
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell is null) return list;
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
                     })
            {
                if (!Directory.Exists(root)) continue;
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (var lnk in files)
                {
                    string name = Path.GetFileNameWithoutExtension(lnk);
                    if (skip.IsMatch(name)) continue;
                    string target = ShortcutTarget(shell, lnk);
                    if (target.Length == 0 || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    try { if (!File.Exists(target)) continue; } catch { continue; }
                    string process = Path.GetFileNameWithoutExtension(target);
                    if (process is "msiexec" or "cmd" or "powershell" or "pwsh" or "wscript" or "cscript") continue;
                    string id = AppLaunchNames.CanonicalId(process) ?? process.ToLowerInvariant();
                    list.Add(new AppLaunchTarget(id, name, target, process, AppLaunchNames.IsBrowser(id)) { Icon = IconOf(target) });
                    if (list.Count >= 140) return list;
                }
            }
        }
        catch { }
        finally
        {
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
        return list;
    }

    private static string ShortcutTarget(object shell, string lnk)
    {
        object? shortcut = null;
        try
        {
            shortcut = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [lnk]);
            if (shortcut is null) return "";
            return shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string ?? "";
        }
        catch { return ""; }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
        }
    }

    private static ImageSource? IconOf(string path)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();
            return source;
        }
        catch { return null; }
    }

    private static string TitleOf(string id) => id switch
    {
        "brave" => "Brave",
        "chrome" => "Google Chrome",
        "msedge" => "Microsoft Edge",
        "firefox" => "Firefox",
        "code" => "Visual Studio Code",
        "calc" => "Calculator",
        _ => char.ToUpperInvariant(id[0]) + id[1..]
    };
}
