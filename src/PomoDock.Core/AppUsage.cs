using System.Text.RegularExpressions;

namespace PomoDock.Core;

/// <summary>Time spent in one foreground app during a focus session.</summary>
public sealed class SessionAppUse
{
    public string Process { get; set; } = "";
    public string Title { get; set; } = "";
    public double Seconds { get; set; }
    public string Label => AppUsage.Label(Process, Title);
}

/// <summary>Groups and names the windows that actually had keyboard focus while a pomodoro ran.</summary>
public static class AppUsage
{
    private static readonly HashSet<string> IdleProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm", "LockApp", "LogonUI", "ScreenClippingHost", "ShellExperienceHost", "SearchHost",
        "StartMenuExperienceHost", "TextInputHost", "SystemSettings", "CrossDeviceResume",
        "idle"
    };

    public static bool IsIdle(string process, string title)
    {
        if (string.IsNullOrWhiteSpace(process) || IdleProcesses.Contains(process)) return true;
        if (process.Equals("explorer", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(title))
            return true;
        return false;
    }

    public static string PrettyProcess(string process)
    {
        string key = (process ?? "").Trim();
        if (key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) key = key[..^4];
        return key.ToLowerInvariant() switch
        {
            "brave" => "Brave",
            "chrome" => "Chrome",
            "msedge" => "Edge",
            "firefox" => "Firefox",
            "code" => "VS Code",
            "devenv" => "Visual Studio",
            "pomodock" => "PomoDock",
            "telegram" => "Telegram",
            "discord" => "Discord",
            "slack" => "Slack",
            "spotify" => "Spotify",
            "notepad" => "Notepad",
            "calc" or "calculatorapp" or "calculator" => "Calculadora",
            "applicationframehost" => "App",
            "idle" => "Inactivo",
            "windowsterminal" => "Terminal",
            "powershell" or "pwsh" => "PowerShell",
            "cmd" => "CMD",
            "" => "Desconocido",
            _ => char.ToUpperInvariant(key[0]) + key[1..]
        };
    }

    private static readonly string[] TitleSuffixes =
    [
        "Visual Studio Code", "Google Chrome", "Microsoft Edge", "Mozilla Firefox",
        "Brave", "Firefox", "Chrome", "Edge", "Notepad", "Discord", "Telegram", "Slack",
        "Spotify", "Windows Terminal", "PowerShell"
    ];

    public static string CleanTitle(string process, string title)
    {
        string text = Regex.Replace(title ?? "", @"\s+", " ").Trim();
        if (text.Length == 0) return "";
        var suffixes = TitleSuffixes.Append(PrettyProcess(process)).Append(process).Append(process + ".exe");
        foreach (var sep in new[] { " - ", " — ", " – " })
        {
            foreach (var suffix in suffixes)
            {
                if (string.IsNullOrWhiteSpace(suffix)) continue;
                string tail = sep + suffix;
                if (text.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
                    text = text[..^tail.Length].Trim();
            }
        }
        if (text.Length > 88) text = text[..85].TrimEnd() + "…";
        return text;
    }

    public static string Label(string process, string title)
    {
        string app = PrettyProcess(process);
        string page = CleanTitle(process, title);
        if (page.Length == 0 || page.Equals(app, StringComparison.OrdinalIgnoreCase) || page.Equals(process, StringComparison.OrdinalIgnoreCase))
            return app;
        return app + " · " + page;
    }

    public static string Summarize(IEnumerable<SessionAppUse> apps, int take = 3)
    {
        var top = Combine(apps).Take(Math.Max(1, take)).ToList();
        if (top.Count == 0) return "";
        return string.Join("  ·  ", top.Select(item => item.Label + " " + Minutes(item.Seconds)));
    }

    public static List<SessionAppUse> Combine(IEnumerable<SessionAppUse> apps) =>
        CombineSessions(new[] { new Session { Apps = apps.ToList() } });

    public static List<SessionAppUse> CombineSessions(IEnumerable<Session> sessions)
    {
        var times = new Dictionary<(string Process, string Title), double>();
        foreach (var session in sessions)
        {
            if (session.Apps is null) continue;
            foreach (var item in session.Apps)
            {
                if (item.Seconds <= 0) continue;
                var key = Key(item.Process, item.Title);
                times[key] = times.GetValueOrDefault(key) + item.Seconds;
            }
        }
        return times
            .Select(pair => new SessionAppUse { Process = pair.Key.Process, Title = pair.Key.Title, Seconds = pair.Value })
            .OrderByDescending(item => item.Seconds)
            .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string Minutes(double seconds)
    {
        double minutes = Math.Max(0, seconds) / 60;
        if (minutes < 1) return "<1m";
        if (minutes < 10) return minutes.ToString("0.0") + "m";
        return Math.Round(minutes).ToString("0") + "m";
    }

    internal static (string Process, string Title) Key(string process, string title)
    {
        string proc = (process ?? "").Trim();
        if (proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) proc = proc[..^4];
        if (proc.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(title))
            proc = CleanTitle("app", title);
        string page = CleanTitle(proc, title);
        if (IsIdle(proc, page)) return ("idle", "");
        return (proc.ToLowerInvariant(), page);
    }
}

/// <summary>Accumulates foreground samples until a session is stored.</summary>
public sealed class AppUsageLedger
{
    private readonly Dictionary<(string Process, string Title), double> times = [];

    public void Add(string process, string title, double seconds)
    {
        if (seconds <= 0) return;
        var key = AppUsage.Key(process, title);
        times[key] = times.GetValueOrDefault(key) + seconds;
    }

    public IReadOnlyList<SessionAppUse> Snapshot() =>
        times
            .Select(pair => new SessionAppUse { Process = pair.Key.Process, Title = pair.Key.Title, Seconds = pair.Value })
            .OrderByDescending(item => item.Seconds)
            .ToList();

    public List<SessionAppUse> Take()
    {
        var copy = Snapshot().ToList();
        times.Clear();
        return copy;
    }
}
