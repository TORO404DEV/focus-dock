using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace FocusDock.App.Native;

public record WindowCandidate(nint Handle, string Title, string ProcessName, uint ProcessId)
{
    public override string ToString() => $"{Title}   [{ProcessName}]";
}
public sealed class WindowSnapshot
{
    public long Handle { get; set; }
    public uint ProcessId { get; set; }
    public long ProcessStarted { get; set; }
    public long Style { get; set; }
    public long ExStyle { get; set; }
    public long Owner { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int ShowCmd { get; set; }
    public string Marker { get; set; } = "FocusDock." + Guid.NewGuid().ToString("N");
}

public sealed class WindowLease : IDisposable
{
    private readonly string journal;
    internal WindowSnapshot Snapshot { get; }
    public nint Handle => (nint)Snapshot.Handle;
    private bool disposed;
    private static readonly List<WindowSnapshot> leased = [];
    public static List<WindowCandidate> Candidates()
    {
        var windows = new List<WindowCandidate>();
        Win32.EnumWindows((h, _) =>
        {
            if (!Win32.IsWindowVisible(h)) return true;
            Win32.GetWindowThreadProcessId(h, out var pid);
            if (pid == Environment.ProcessId) return true;
            var text = new StringBuilder(1024); Win32.GetWindowText(h, text, text.Capacity);
            if (text.Length == 0 || (Win32.GetWindowLongPtr(h, Win32.GWL_STYLE).ToInt64() & Win32.WS_CHILD) != 0) return true;
            try { using var p = Process.GetProcessById((int)pid); if (p.ProcessName is "explorer" or "dwm" or "ShellExperienceHost") return true; windows.Add(new(h, text.ToString(), p.ProcessName, pid)); } catch { }
            return true;
        }, 0);
        return windows.OrderBy(w => w.Title).ToList();
    }
    public WindowLease(nint window, nint container, string journal)
    {
        this.journal = journal;
        if (!Win32.IsWindow(window)) throw new InvalidOperationException("La ventana ya no existe.");
        if (!Win32.AreDpiAwarenessContextsEqual(Win32.GetWindowDpiAwarenessContext(window), Win32.GetWindowDpiAwarenessContext(container)))
            throw new InvalidOperationException("Esta ventana usa un modo de escalado incompatible. No se modificó; puedes mantenerla al lado de FOCUS DOCK.");
        Win32.GetWindowThreadProcessId(window, out uint pid);
        if (pid == Environment.ProcessId) throw new InvalidOperationException("Selecciona una ventana de otra aplicación.");
        using var process = Process.GetProcessById((int)pid);
        Win32.GetWindowRect(window, out var rect);
        var placement = new Win32.Placement { Length = Marshal.SizeOf<Win32.Placement>() }; Win32.GetWindowPlacement(window, ref placement);
        Snapshot = new() { Handle = window.ToInt64(), ProcessId = pid, ProcessStarted = process.StartTime.ToUniversalTime().Ticks,
            Style = Win32.GetWindowLongPtr(window, Win32.GWL_STYLE).ToInt64(), ExStyle = Win32.GetWindowLongPtr(window, Win32.GWL_EXSTYLE).ToInt64(),
            Owner = Win32.GetWindow(window, 4).ToInt64(), Left = rect.Left, Top = rect.Top, Width = rect.Right - rect.Left, Height = rect.Bottom - rect.Top, ShowCmd = placement.ShowCmd };
        if (!Win32.SetProp(window, Snapshot.Marker, 1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo acceder a esta ventana. Ejecuta ambas apps con los mismos permisos.");
        leased.Add(Snapshot);
        Persist(); // Store restoration information BEFORE changing the foreign window.
        try
        {
            Win32.ShowWindow(window, 9);
            long style = (Snapshot.Style | Win32.WS_CHILD) & ~(Win32.WS_POPUP | Win32.WS_CAPTION | Win32.WS_THICKFRAME);
            Marshal.SetLastPInvokeError(0);
            Win32.SetWindowLongPtr(window, Win32.GWL_STYLE, (nint)style);
            int error = Marshal.GetLastPInvokeError(); if (error != 0) throw new Win32Exception(error);
            Marshal.SetLastPInvokeError(0);
            Win32.SetParent(window, container);
            error = Marshal.GetLastPInvokeError();
            if (error != 0 || Win32.GetParent(window) != container) throw new Win32Exception(error, "La aplicación no permite incrustar esta ventana.");
            Win32.SetWindowPos(window, 0, 0, 0, 300, 300, Win32.SWP_FRAMECHANGED | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        }
        catch { Dispose(); throw; }
    }
    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        var temp = journal + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(leased));
        File.Move(temp, journal, true);
    }
    public bool IsAlive => IsSameWindow(Snapshot);
    private static bool IsSameWindow(WindowSnapshot s)
    {
        var h = (nint)s.Handle;
        if (!Win32.IsWindow(h) || Win32.GetProp(h, s.Marker) == 0) return false;
        Win32.GetWindowThreadProcessId(h, out var pid);
        if (pid != s.ProcessId) return false;
        try { using var p = Process.GetProcessById((int)pid); return p.StartTime.ToUniversalTime().Ticks == s.ProcessStarted; } catch { return false; }
    }
    private static bool Restore(WindowSnapshot s)
    {
        if (!IsSameWindow(s)) return true;
        var h = (nint)s.Handle;
        Marshal.SetLastPInvokeError(0);
        Win32.SetParent(h, 0);
        if (Marshal.GetLastPInvokeError() != 0) return false;
        Win32.SetWindowLongPtr(h, Win32.GWL_STYLE, (nint)s.Style);
        Win32.SetWindowLongPtr(h, Win32.GWL_EXSTYLE, (nint)s.ExStyle);
        // While WS_CHILD is set, GetParent may return the desktop HWND after SetParent(NULL).
        // Check ownership only after restoring the original top-level style.
        if ((Win32.GetWindowLongPtr(h, Win32.GWL_STYLE).ToInt64() & Win32.WS_CHILD) != 0) return false;
        if (s.Owner != 0 && Win32.IsWindow((nint)s.Owner)) Win32.SetWindowLongPtr(h, Win32.GWLP_HWNDPARENT, (nint)s.Owner);
        Win32.SetWindowPos(h, 0, s.Left, s.Top, s.Width, s.Height, Win32.SWP_FRAMECHANGED | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        Win32.ShowWindow(h, s.ShowCmd == 3 ? 3 : 1);
        Win32.RemoveProp(h, s.Marker);
        return true;
    }
    public void Dispose()
    {
        if (disposed) return;
        if (!Restore(Snapshot)) throw new InvalidOperationException("No se pudo liberar la ventana. La app permanecerá abierta para que puedas recuperarla.");
        disposed = true; leased.Remove(Snapshot); Persist();
    }
    public static void Recover(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            var entries = JsonSerializer.Deserialize<List<WindowSnapshot>>(File.ReadAllText(path)) ?? [];
            var failed = entries.Where(s => !Restore(s)).ToList();
            File.WriteAllText(path, JsonSerializer.Serialize(failed));
        }
        catch (IOException) { }
        catch (JsonException) { }
    }
}

public sealed class ExternalWindowHost : HwndHost
{
    private nint container;
    private WindowLease? lease;
    public double CropTop { get; set; }
    public double CropBottom { get; set; }
    public nint ForeignHandle => lease?.Handle ?? 0;
    public bool Alive => lease?.IsAlive ?? false;
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        container = Win32.CreateWindowEx(0, "static", "FocusDockHost", unchecked((int)(Win32.WS_CHILD | Win32.WS_VISIBLE)), 0, 0, 10, 10, parent.Handle, 0, 0, 0);
        if (container == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(this, container);
    }
    public void Attach(nint hwnd, string journal)
    {
        if (container == 0) throw new InvalidOperationException("Espera a que el panel esté visible.");
        lease?.Dispose(); lease = new WindowLease(hwnd, container, journal); Resize();
    }
    public void Detach() { lease?.Dispose(); lease = null; }
    public void Resize()
    {
        if (lease is null || !lease.IsAlive) return;
        Win32.GetClientRect(container, out var rect);
        var scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleY;
        int top = (int)(CropTop * scale), bottom = (int)(CropBottom * scale);
        Win32.SetWindowPos(lease.Handle, 0, 0, -top, Math.Max(1, rect.Right), Math.Max(1, rect.Bottom + top + bottom), Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
    }
    protected override void OnWindowPositionChanged(Rect rect) { base.OnWindowPositionChanged(rect); Resize(); }
    protected override void DestroyWindowCore(HandleRef hwnd) { Detach(); Win32.DestroyWindow(hwnd.Handle); }
}
