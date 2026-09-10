using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PomoDock.App.Native;
using PomoDock.Core;

namespace PomoDock.App;

internal static class Diagnostics
{
    public static void RunFixture(string path)
    {
        var window = new Window { Title = "PomoDock disposable integration fixture", Width = 360, Height = 440, Content = new TextBox { Text = "Disposable window fixture. No user app is involved.", AcceptsReturn = true } };
        Application.Current.MainWindow = window;
        window.SourceInitialized += (_, _) => File.WriteAllText(path, new WindowInteropHelper(window).Handle.ToInt64().ToString());
        window.Show();
    }
    public static async void Run(string directory)
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Directory.CreateDirectory(directory);
        var results = new List<string>();
        MainWindow? main = null; Process? fixture = null; Process? fixture2 = null; Window? harness = null; Window? dualHarness = null;
        void Assert(bool condition, string label) { if (!condition) throw new Exception(label); results.Add("PASS " + label); }
        try
        {
            main = new MainWindow(Path.Combine(directory, "data"), true); Application.Current.MainWindow = main;
            main.Settings.ReduceMotion = true; main.Show();
            await Task.Delay(350);
            Assert(main.IsLoaded, "native shell loads");
            Assert(!main.HeaderClockText.Contains("POMODOCK", StringComparison.OrdinalIgnoreCase) && main.HeaderClockText.Contains(DateTime.Now.Year.ToString()), "workspace header shows the live date and time");
            Render(main, Path.Combine(directory, "main-light.png"));
            main.ToggleTimer(); await Task.Delay(1200); main.ToggleTimer(); Assert(main.Timer.Active!.Seconds >= 1, "UI start and pause record monotonic work");
            if (main.Settings.Fullscreen) main.ToggleFullscreen();
            main.ToggleFullscreen(); await Task.Delay(150);
            var hwnd = new WindowInteropHelper(main).Handle; Win32.GetWindowRect(hwnd, out var fullRect); var monitor = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
            Assert(Math.Abs(fullRect.Right - fullRect.Left - monitor.Width) < 3 && Math.Abs(fullRect.Bottom - fullRect.Top - monitor.Height) < 3, "fullscreen fills current monitor");
            main.ToggleFullscreen();
            main.Settings.Dark = true; main.ApplyTheme(); await Task.Delay(100); Render(main, Path.Combine(directory, "main-dark.png"));
            main.Settings.Dark = false; main.ApplyTheme();
            var fixturePath = Path.Combine(directory, "fixture-hwnd.txt");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--fixture"); start.ArgumentList.Add(fixturePath); fixture = Process.Start(start)!;
            for (int i = 0; i < 100 && !File.Exists(fixturePath); i++) await Task.Delay(100);
            Assert(File.Exists(fixturePath), "disposable foreign process created");
            nint foreign = (nint)long.Parse(File.ReadAllText(fixturePath));
            Win32.GetWindowRect(foreign, out var originalRect); var originalStyle = Win32.GetWindowLongPtr(foreign, Win32.GWL_STYLE);
            var host = new ExternalWindowHost();
            harness = new Window { Title = "PomoDock hosting integration test", Content = host, Width = 450, Height = 520 }; harness.Show(); await Task.Delay(150);
            host.Attach(foreign, Path.Combine(directory, "journal.json")); await Task.Delay(150);
            Assert(host.Alive && Win32.GetParent(foreign) == host.Handle, "real cross-process HWND embedded");
            harness.Width = 570; harness.Height = 620; await Task.Delay(150);
            Win32.GetWindowRect(foreign, out var resized); Assert(resized.Right - resized.Left > originalRect.Right - originalRect.Left, "foreign window follows widget resize");
            host.CropTop = 40; host.Resize(); await Task.Delay(100);
            Win32.GetWindowRect(foreign, out var cropped); Assert(cropped.Top < resized.Top, "crop moves live content within the host");
            host.Detach();
            Assert(Win32.IsWindow(foreign) && Win32.GetParent(foreign) == 0, "detach preserves foreign window");
            Assert(Win32.GetWindowLongPtr(foreign, Win32.GWL_STYLE) == originalStyle, "original window styles restored");
            Win32.GetWindowRect(foreign, out var restored); Assert(restored.Left == originalRect.Left && restored.Right == originalRect.Right, "original bounds restored");
            host.Dispose(); harness.Close(); harness = null;
            var fixturePath2 = Path.Combine(directory, "fixture-hwnd-2.txt");
            var start2 = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start2.ArgumentList.Add("--fixture"); start2.ArgumentList.Add(fixturePath2); fixture2 = Process.Start(start2)!;
            for (int i = 0; i < 100 && !File.Exists(fixturePath2); i++) await Task.Delay(100);
            Assert(File.Exists(fixturePath2), "second disposable foreign process created");
            nint foreign2 = (nint)long.Parse(File.ReadAllText(fixturePath2));
            var dualSurface = new Canvas();
            var dualA = new ExternalWindowHost { Width = 320, Height = 300 };
            var dualB = new ExternalWindowHost { Width = 320, Height = 300 };
            dualSurface.Children.Add(dualA); dualSurface.Children.Add(dualB); Canvas.SetLeft(dualA, 10); Canvas.SetTop(dualA, 10); Canvas.SetLeft(dualB, 80); Canvas.SetTop(dualB, 70);
            dualHarness = new Window { Title = "PomoDock dual host integration test", Content = dualSurface, Width = 520, Height = 420 }; dualHarness.Show(); await Task.Delay(150);
            dualA.Attach(foreign, Path.Combine(directory, "journal-dual-a.json")); dualB.Attach(foreign2, Path.Combine(directory, "journal-dual-b.json")); await Task.Delay(150);
            Assert(Win32.GetParent(dualA.Handle) == Win32.GetParent(dualB.Handle), "overlapping hosted widgets share a native parent");
            dualA.BringToFront(); await Task.Delay(50); dualB.BringToFront(); await Task.Delay(50);
            Assert(Win32.GetWindow(dualA.Handle, 3) == dualB.Handle, "second hosted widget can move above the first");
            dualA.BringToFront(); await Task.Delay(50);
            Assert(Win32.GetWindow(dualB.Handle, 3) == dualA.Handle, "first hosted widget can move back above the second");
            dualA.Detach(); dualB.Detach(); dualA.Dispose(); dualB.Dispose(); dualHarness.Close(); dualHarness = null;
            var embeddedCard = main.AddCard(new() { Kind = "window", Title = "Timer input fixture", X = 180, Y = 160, Width = 280, Height = 330 }, false);
            await embeddedCard.Attach(foreign);
            Assert(embeddedCard.IsExternalAttached, "timer regression uses a real embedded process on the main canvas");
            await main.VerifyTimerInputAsync(embeddedCard, Assert);
            main.RemoveCard(embeddedCard);
            main.AddCard(new() { Kind = "notes", Title = "MI SIGUIENTE PASO", Value = "Una cosa a la vez.\n\n1. Elegir el siguiente resultado\n2. Iniciar una sesión\n3. Revisar lo aprendido" }, true);
            main.AddCard(new() { Kind = "stats", Title = "MI ENFOQUE" }, true);
            main.AddCard(new() { Kind = "todo", Title = "TO DO" }, true);
            main.AddCard(new() { Kind = "habits", Title = "HÁBITOS" }, true);
            await Task.Delay(100); Render(main, Path.Combine(directory, "widgets.png"));
            for (int d = 0; d < 14; d++)
            {
                var end = DateTimeOffset.Now.AddDays(-d).AddHours(-1);
                main.Store.Save(new() { Started = end.AddMinutes(-25), Ended = end, PlannedSeconds = 1500, Outcome = Outcome.Completed, Project = d % 2 == 0 ? "Demo / producto" : "Demo / aprender", Task = "Sesión de demostración", Segments = [new(end.AddMinutes(-25), end)] });
            }
            main.ShowReportForDiagnostics(); await Task.Delay(100);
            Assert(main.IsReportModalOpen, "report opens inside the main window modal layer");
            Assert(main.ReportModalFitsVisibleScreen, "report modal is fully visible and is not clipped by the popup surface");
            Assert(main.ReportVisibleSessionCount >= 14, "report summary loads persisted focus history");
            Render(main.ReportModalSurface!, Path.Combine(directory, "report.png"));
            Assert(main.ShowReportChartHoverForDiagnostics(), "report chart exposes period and project values on hover");
            Render(main.ReportModalSurface!, Path.Combine(directory, "report-hover.png"));
            main.ShowReportDetailForDiagnostics(); await Task.Delay(100);
            Assert(main.ReportVisibleSessionCount >= 14, "report detail keeps the selected period data");
            Render(main.ReportModalSurface!, Path.Combine(directory, "report-detail.png"));
            main.HideReportForDiagnostics();
            Assert(!main.IsReportModalOpen, "report modal closes without a second app window");
            var retainedWindow = main.AddCard(new() { Kind = "window", Title = "Retained page fixture", X = 24, Y = 24, Width = 280, Height = 260 }, false);
            await retainedWindow.Attach(foreign);
            Assert(retainedWindow.IsExternalAttached, "page navigation fixture embeds a real external window");
            int initialPages = main.WorkspacePageCount;
            Assert(main.AddPageForDiagnostics(), "a populated workspace unlocks a new page");
            await Task.Delay(320);
            Assert(main.WorkspacePageCount == initialPages + 1 && main.CurrentWorkspacePageIsBlank && !main.CurrentWorkspacePageHasTimer,
                "new workspace pages start completely blank");
            Render(main, Path.Combine(directory, "page-blank.png"));
            Assert(!main.AddPageForDiagnostics(), "a blank workspace prevents adding another page");
            main.AddTimerForDiagnostics();
            Assert(main.CurrentWorkspacePageHasTimer && !main.CurrentWorkspacePageIsBlank, "timer can be added as the first widget on a blank page");
            Render(main, Path.Combine(directory, "page-timer.png"));
            Assert(main.AddPageForDiagnostics(), "adding a widget unlocks the following page");
            await Task.Delay(320);
            Assert(main.CurrentWorkspacePageIsBlank, "each subsequently created page is blank too");
            main.SwitchPageForDiagnostics(0);
            await Task.Delay(320);
            Assert(!main.CurrentWorkspacePageIsBlank, "page navigation restores the original canvas automatically");
            Assert(retainedWindow.IsExternalAttached, "page navigation preserves embedded external windows");
            main.RemoveCard(retainedWindow);
            main.SaveState();
            var persistedWidgets = main.Store.Read<Settings>("settings")!.Widgets;
            Assert(new[] { "notes", "stats", "todo", "habits" }.All(kind => persistedWidgets.Any(widget => widget.Kind == kind)), "widget layout persisted");
            var savedTimer = main.Store.Read<Settings>("settings")!.TimerWidget;
            Assert(savedTimer.Kind == "timer" && savedTimer.Width >= 360 && savedTimer.Height >= 300, "permanent timer widget layout persisted");
            main.Close(); main = null;
            using var recovered = new Store(Path.Combine(directory, "data")); Assert(recovered.Read<Session>("checkpoint") is not null, "paused session survives normal close");
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { success = true, tests = results }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { success = false, tests = results, error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = 1;
        }
        finally
        {
            dualHarness?.Close(); harness?.Close(); main?.Close();
            if (fixture is not null) { if (!fixture.HasExited) fixture.CloseMainWindow(); fixture.Dispose(); }
            if (fixture2 is not null) { if (!fixture2.HasExited) fixture2.CloseMainWindow(); fixture2.Dispose(); }
            Application.Current.Shutdown(Environment.ExitCode);
        }
    }
    internal static void Render(FrameworkElement element, string file)
    {
        element.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY)),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(file); encoder.Save(output);
    }
}
