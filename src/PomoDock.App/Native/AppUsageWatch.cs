using System.Diagnostics;
using System.Text;
using PomoDock.Core;

namespace PomoDock.App.Native;

/// <summary>
/// Watches the foreground window while a focus pomodoro is running and attributes elapsed time
/// to that process. The ledger is attached to the session when it ends.
/// </summary>
internal sealed class AppUsageWatch
{
    private readonly AppUsageLedger ledger = new();
    private string? currentProcess;
    private string? currentTitle;
    private DateTimeOffset since;

    public void Observe(bool recording, DateTimeOffset now)
    {
        if (!recording)
        {
            Flush(now);
            return;
        }
        var (process, title) = ReadForeground();
        if (currentProcess is not null &&
            (!string.Equals(process, currentProcess, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(title, currentTitle, StringComparison.Ordinal)))
            Flush(now);
        if (currentProcess is null)
        {
            currentProcess = process;
            currentTitle = title;
            since = now;
            return;
        }
        Credit(now);
    }

    public List<SessionAppUse> Take(DateTimeOffset now)
    {
        Flush(now);
        return ledger.Take();
    }

    private void Credit(DateTimeOffset now)
    {
        if (currentProcess is null) return;
        double seconds = (now - since).TotalSeconds;
        if (seconds <= 0) return;
        // A hung dispatcher or a sleep without PowerChanged must not dump a whole pomodoro on one title.
        ledger.Add(currentProcess, currentTitle ?? "", Math.Min(seconds, 15));
        since = now;
    }

    private void Flush(DateTimeOffset now)
    {
        Credit(now);
        currentProcess = null;
        currentTitle = null;
    }

    private static (string Process, string Title) ReadForeground()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == 0) return ("idle", "");
        var text = new StringBuilder(1024);
        Win32.GetWindowText(hwnd, text, text.Capacity);
        string title = text.ToString();
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return ("idle", title);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            string name = process.ProcessName ?? "";
            if (AppUsage.IsIdle(name, title)) return ("idle", "");
            return (name, title);
        }
        catch
        {
            return ("idle", title);
        }
    }
}
