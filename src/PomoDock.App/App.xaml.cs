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
        if (e.Args.Length >= 5 && e.Args[0] == "--dictation-test")
        {
            Diagnostics.RunDictation(e.Args[1], e.Args[2], e.Args[3], e.Args[4]);
            return;
        }
        if (e.Args.Length >= 3 && e.Args[0] == "--agent-model-test")
        {
            AgentDiagnostics.RunModel(e.Args[1], e.Args[2]);
            return;
        }
        if (e.Args.Length >= 3 && e.Args[0] == "--agent-voice-test")
        {
            AgentDiagnostics.RunVoice(e.Args[1], e.Args[2]);
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--fixture") { Diagnostics.RunFixture(e.Args[1]); return; }
        if (e.Args.Length >= 2 && e.Args[0] == "--self-test") { Diagnostics.Run(e.Args[1]); return; }
        mutex = new Mutex(true, "Local\\PomoDock.Desktop.SingleInstance", out bool created);
        if (!created) { Dialogs.Alert(null, PomoDock.Core.L.T("app.alreadyOpenTitle"), PomoDock.Core.L.T("app.alreadyOpenBody")); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            try { File.AppendAllText(Path.Combine(PomoDock.App.MainWindow.DataPath, "errors.log"), $"{DateTimeOffset.Now:O} {args.Exception}\n"); } catch { }
            Dialogs.Alert(MainWindow, PomoDock.Core.L.T("app.errorTitle"), PomoDock.Core.L.T("app.errorBody", args.Exception.Message));
            args.Handled = true;
        };
        Native.WindowLease.Recover(Path.Combine(PomoDock.App.MainWindow.DataPath, "windows.json"));
        MainWindow = new MainWindow(); MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { mutex?.Dispose(); base.OnExit(e); }
}
