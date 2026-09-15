using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Threading;

namespace PomoDock.App.Native;

/// <summary>Clicks inside an embedded HWND (YouTube play) and opens a URL in a new browser tab.</summary>
internal static class WindowInput
{
    public static void ClickClient(nint hwnd, double nx, double ny)
    {
        if (hwnd == 0 || !Win32.IsWindow(hwnd) || !Win32.GetClientRect(hwnd, out var client)) return;
        int width = Math.Max(1, client.Right - client.Left);
        int height = Math.Max(1, client.Bottom - client.Top);
        var point = new Win32.Point
        {
            X = (int)Math.Clamp(width * nx, 8, width - 8),
            Y = (int)Math.Clamp(height * ny, 8, height - 8)
        };
        if (!Win32.ClientToScreen(hwnd, ref point)) return;
        Win32.GetCursorPos(out var restore);
        try
        {
            Win32.SetForegroundWindow(hwnd);
            ClickScreen(point.X, point.Y);
        }
        finally { Win32.SetCursorPos(restore.X, restore.Y); }
    }

    public static bool InvokeFirstYouTubeResult(nint hwnd)
    {
        if (hwnd == 0 || !Win32.IsWindow(hwnd)) return false;
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return false;
            var links = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink));
            int seen = 0;
            foreach (AutomationElement link in links)
            {
                if (++seen > 80) break;
                string name = "";
                try { name = link.Current.Name ?? ""; } catch { continue; }
                if (!LooksLikeVideoTitle(name)) continue;
                if (!link.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern)) continue;
                if (pattern is not InvokePattern invoke) continue;
                invoke.Invoke();
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Opens the URL in a new Chromium tab and closes the tab that was playing before.
    /// Does not click the page first: on YouTube that focuses the in-page search box,
    /// so the watch URL is searched instead of opened.
    /// </summary>
    public static async Task ReplaceTabWithUrlAsync(Dispatcher dispatcher, nint hwnd, string url, CancellationToken cancellationToken)
    {
        if (hwnd == 0 || string.IsNullOrWhiteSpace(url) || !Win32.IsWindow(hwnd)) return;
        string address = url.Trim();
        await dispatcher.InvokeAsync(() =>
        {
            FocusHwnd(hwnd);
            Chord(Win32.VK_CONTROL, Win32.VK_T);
        });
        await Task.Delay(320, cancellationToken).ConfigureAwait(false);
        await dispatcher.InvokeAsync(() =>
        {
            FocusHwnd(hwnd);
            Chord(Win32.VK_CONTROL, Win32.VK_L);
            Chord(Win32.VK_CONTROL, Win32.VK_A);
            TypeText(address);
            Key(Win32.VK_RETURN, false);
            Key(Win32.VK_RETURN, true);
        });
        await Task.Delay(700, cancellationToken).ConfigureAwait(false);
        await dispatcher.InvokeAsync(() =>
        {
            FocusHwnd(hwnd);
            Key(Win32.VK_CONTROL, false);
            Key(Win32.VK_SHIFT, false);
            Key(Win32.VK_TAB, false);
            Key(Win32.VK_TAB, true);
            Key(Win32.VK_SHIFT, true);
            Key(Win32.VK_CONTROL, true);
        });
        await Task.Delay(180, cancellationToken).ConfigureAwait(false);
        await dispatcher.InvokeAsync(() =>
        {
            FocusHwnd(hwnd);
            Chord(Win32.VK_CONTROL, Win32.VK_W);
        });
        await Task.Delay(180, cancellationToken).ConfigureAwait(false);
    }

    public static async Task NavigateUrlAsync(Dispatcher dispatcher, nint hwnd, string url, CancellationToken cancellationToken) =>
        await ReplaceTabWithUrlAsync(dispatcher, hwnd, url, cancellationToken);

    private static void FocusHwnd(nint hwnd)
    {
        if (hwnd == 0 || !Win32.IsWindow(hwnd)) return;
        uint thisThread = Win32.GetCurrentThreadId();
        uint targetThread = Win32.GetWindowThreadProcessId(hwnd, out _);
        nint foreground = Win32.GetForegroundWindow();
        uint foreThread = foreground == 0 ? 0 : Win32.GetWindowThreadProcessId(foreground, out _);
        bool linkedFore = false, linkedTarget = false;
        if (foreThread != 0 && foreThread != thisThread)
            linkedFore = Win32.AttachThreadInput(thisThread, foreThread, true);
        if (targetThread != 0 && targetThread != thisThread && targetThread != foreThread)
            linkedTarget = Win32.AttachThreadInput(thisThread, targetThread, true);
        try
        {
            Win32.SetForegroundWindow(hwnd);
            Win32.SetFocus(hwnd);
        }
        finally
        {
            if (linkedTarget) Win32.AttachThreadInput(thisThread, targetThread, false);
            if (linkedFore) Win32.AttachThreadInput(thisThread, foreThread, false);
        }
    }

    private static void Chord(ushort modifier, ushort key)
    {
        Key(modifier, false);
        Key(key, false);
        Key(key, true);
        Key(modifier, true);
    }

    private static void TypeText(string text)
    {
        foreach (char c in text)
        {
            var down = new Win32.INPUT
            {
                Type = Win32.INPUT_KEYBOARD,
                Union = new Win32.INPUTUNION { Keyboard = new Win32.KEYBDINPUT { Scan = (ushort)c, Flags = Win32.KEYEVENTF_UNICODE } }
            };
            var up = new Win32.INPUT
            {
                Type = Win32.INPUT_KEYBOARD,
                Union = new Win32.INPUTUNION { Keyboard = new Win32.KEYBDINPUT { Scan = (ushort)c, Flags = Win32.KEYEVENTF_UNICODE | Win32.KEYEVENTF_KEYUP } }
            };
            Win32.SendInput(2, [down, up], Marshal.SizeOf<Win32.INPUT>());
        }
    }

    private static void Key(ushort vk, bool up)
    {
        var input = new Win32.INPUT
        {
            Type = Win32.INPUT_KEYBOARD,
            Union = new Win32.INPUTUNION
            {
                Keyboard = new Win32.KEYBDINPUT { Vk = vk, Flags = up ? Win32.KEYEVENTF_KEYUP : 0 }
            }
        };
        Win32.SendInput(1, [input], Marshal.SizeOf<Win32.INPUT>());
    }

    private static bool LooksLikeVideoTitle(string name)
    {
        string text = (name ?? "").Trim();
        if (text.Length < 10) return false;
        if (text.Contains("youtube", StringComparison.OrdinalIgnoreCase)) return false;
        ReadOnlySpan<string> skip =
        [
            "Home", "Inicio", "Shorts", "Subscriptions", "Suscripciones", "Library", "Biblioteca",
            "History", "Historial", "Search", "Buscar", "Filters", "Filtros", "Guide", "Skip navigation",
            "Saltar", "Sign in", "Acceder", "You"
        ];
        foreach (string token in skip)
            if (text.Equals(token, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static void ClickScreen(int x, int y)
    {
        int vx = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        int vy = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        int vw = Math.Max(1, Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN) - 1);
        int vh = Math.Max(1, Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN) - 1);
        int ax = (int)Math.Round((x - vx) * 65535.0 / vw);
        int ay = (int)Math.Round((y - vy) * 65535.0 / vh);
        uint flags = Win32.MOUSEEVENTF_MOVE | Win32.MOUSEEVENTF_ABSOLUTE | Win32.MOUSEEVENTF_VIRTUALDESK;
        var inputs = new[]
        {
            Mouse(ax, ay, flags),
            Mouse(ax, ay, flags | Win32.MOUSEEVENTF_LEFTDOWN),
            Mouse(ax, ay, flags | Win32.MOUSEEVENTF_LEFTUP)
        };
        Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>());
    }

    private static Win32.INPUT Mouse(int x, int y, uint flags) => new()
    {
        Type = Win32.INPUT_MOUSE,
        Union = new Win32.INPUTUNION { Mouse = new Win32.MOUSEINPUT { Dx = x, Dy = y, Flags = flags } }
    };
}
