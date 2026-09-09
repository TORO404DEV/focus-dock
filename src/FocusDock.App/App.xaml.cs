using System.Diagnostics;
using System.IO;
using System.Windows;

namespace FocusDock.App;

public partial class App : Application
{
    private Mutex? mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length >= 3 && e.Args[0] == "--guardian")
        {
            try { Process.GetProcessById(int.Parse(e.Args[1])).WaitForExit(); } catch (ArgumentException) { }
            Native.WindowLease.Recover(e.Args[2]); Shutdown(); return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--fixture") { Diagnostics.RunFixture(e.Args[1]); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--self-test") { Diagnostics.Run(e.Args[1]); return; }
        mutex = new Mutex(true, "Local\\FocusDock.Desktop.SingleInstance", out bool created);
        if (!created) { MessageBox.Show("FOCUS DOCK ya está abierto."); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            try { File.AppendAllText(Path.Combine(FocusDock.App.MainWindow.DataPath, "errors.log"), $"{DateTimeOffset.Now:O} {args.Exception}\n"); } catch { }
            MessageBox.Show("No se pudo completar la acción. Tus datos guardados se conservan.\n" + args.Exception.Message, "FOCUS DOCK");
            args.Handled = true;
        };
        Native.WindowLease.Recover(Path.Combine(FocusDock.App.MainWindow.DataPath, "windows.json"));
        MainWindow = new MainWindow(); MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { mutex?.Dispose(); base.OnExit(e); }
}
