using System.IO;
using Microsoft.Win32;

namespace PomoDock.App.Native;

/// <summary>HKCU Run entry so PomoDock opens with the signed-in Windows session.</summary>
internal static class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PomoDock";

    /// <summary>Prefer the Start Menu install path so updates keep the same Run target.</summary>
    public static string ExePath
    {
        get
        {
            string installed = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "PomoDock", "PomoDock.exe");
            if (File.Exists(installed)) return installed;
            return Environment.ProcessPath ?? installed;
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch { return false; }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey)
            ?? throw new InvalidOperationException("No pude abrir el registro de inicio de Windows.");
        if (enabled)
        {
            string path = ExePath;
            if (!File.Exists(path))
                throw new FileNotFoundException("No encuentro PomoDock.exe para el inicio de Windows.", path);
            key.SetValue(ValueName, Quote(path));
        }
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Keeps the Run key aligned with the settings toggle (repairs after reinstall).</summary>
    public static void Sync(bool wantEnabled)
    {
        try
        {
            bool have = IsEnabled();
            if (wantEnabled == have)
            {
                if (wantEnabled) SetEnabled(true); // refresh path after install
                return;
            }
            SetEnabled(wantEnabled);
        }
        catch
        {
            // Settings stay; the next toggle or launch can retry.
        }
    }

    private static string Quote(string path) => path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
}
