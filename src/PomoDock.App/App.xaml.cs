using System.Diagnostics;
using System.IO;
using System.Windows;

namespace PomoDock.App;

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
        mutex = new Mutex(true, "Local\\PomoDock.Desktop.SingleInstance", out bool created);
        if (!created) { Dialogs.Alert(null, "POMODOCK YA ESTÁ ABIERTO", "Cierra la instancia actual desde la bandeja o la ventana principal."); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            try { File.AppendAllText(Path.Combine(PomoDock.App.MainWindow.DataPath, "errors.log"), $"{DateTimeOffset.Now:O} {args.Exception}\n"); } catch { }
            Dialogs.Alert(MainWindow, "ERROR RECUPERABLE", "No se pudo completar la acción. Tus datos guardados se conservan.\n\n" + args.Exception.Message);
            args.Handled = true;
        };
        Native.WindowLease.Recover(Path.Combine(PomoDock.App.MainWindow.DataPath, "windows.json"));
        MainWindow = new MainWindow(); MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { mutex?.Dispose(); base.OnExit(e); }
}
