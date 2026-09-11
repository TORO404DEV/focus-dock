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
        MainWindow? main = null; Process? fixture = null; Process? fixture2 = null; Window? harness = null; Window? dualHarness = null; Window? calendarHarness = null; Window? todoHarness = null; Window? notesHarness = null; Window? statsHarness = null; TasksWindow? tasksHarness = null; SettingsWindow? settingsPanel = null;
        void Assert(bool condition, string label) { if (!condition) throw new Exception(label); results.Add("PASS " + label); }
        try
        {
            main = new MainWindow(Path.Combine(directory, "data"), true); Application.Current.MainWindow = main;
            main.Settings.ReduceMotion = true; main.Show();
            await Task.Delay(350);
            Assert(main.IsLoaded, "native shell loads");
            Assert(!main.HeaderClockText.Contains("POMODOCK", StringComparison.OrdinalIgnoreCase) && main.HeaderClockText.Contains(DateTime.Now.Year.ToString()), "workspace header shows the live date and time");
            Render(main, Path.Combine(directory, "main-light.png"));

            // A real imported account can have dozens of projects. They belong in a compact rail,
            // while the task list keeps its own full-height workspace and searchable rows.
            for (int index = 1; index <= 32; index++)
            {
                string project = $"Proyecto de muestra {index:00}";
                main.Settings.Projects.Add(project);
                main.Settings.Tasks.Add(new WorkTask { Name = $"Siguiente acción concreta {index:00}", Project = project, Estimate = index % 4 + 1 });
            }
            main.Settings.Tasks.Add(new WorkTask { Name = "Revisar prioridades de la semana", Project = "Proyecto de muestra 01", Estimate = 3, Template = true });
            tasksHarness = new TasksWindow(main, main.Settings.Tasks.First());
            tasksHarness.Show(); await Task.Delay(250);
            Assert(tasksHarness.VisibleProjectRowsForDiagnostics >= 30 && tasksHarness.ProjectRailScrollsForDiagnostics, "large project collections stay inside a scrolling project rail");
            Assert(tasksHarness.VisibleTaskRowsForDiagnostics >= 30, "task workspace renders the imported task collection");
            Render(tasksHarness, Path.Combine(directory, "tasks-projects.png"));
            tasksHarness.Close(); tasksHarness = null;
            main.ToggleTimer(); await Task.Delay(1200); main.ToggleTimer(); Assert(main.Timer.Active!.Seconds >= 1, "UI start and pause record monotonic work");
            main.Sounds.Completed(Phase.ShortBreak); await Task.Delay(80);
            Assert(main.Sounds.ActiveAlarmVoicesForDiagnostics > 0, "a completed break starts its timer alarm");
            main.ToggleTimer();
            Assert(main.Sounds.ActiveAlarmVoicesForDiagnostics == 0, "starting pomodoro silences the alarm and cancels its pending repeats");
            main.ToggleTimer();
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
            var todoCard = main.AddCard(new() { Kind = "todo", Title = "TO DO", Value = SeedTasks() }, true);
            main.AddCard(new() { Kind = "habits", Title = "HÁBITOS" }, true);
            SeedAgenda(main.Store);
            main.AddCard(new() { Kind = "calendar", Title = "AGENDA" }, true);
            await Task.Delay(100); Render(main, Path.Combine(directory, "widgets.png"));

            // Notes: a strike line that really draws, a checklist that takes clicks, a history that outlives the card.
            var noteCard = main.AddCard(new() { Kind = "notes", Title = "NOTAS", Value = "Comprar pan y leche" }, true);
            await Task.Delay(150);
            var noteEditor = Descendant<NotesEditor>(noteCard)!;
            noteEditor.SelectAllForDiagnostics(); noteEditor.ToggleStrikeForDiagnostics();
            Assert(noteEditor.SelectionIsStruckForDiagnostics, "the strike button crosses out plain text");
            noteEditor.ToggleStrikeForDiagnostics();
            Assert(!noteEditor.SelectionIsStruckForDiagnostics, "a second press removes the strike line");
            noteEditor.AddChecklistToFirstLineForDiagnostics(); await Task.Delay(100);
            var box = noteEditor.FirstChecklistBoxForDiagnostics!;
            Assert(box.IsEnabled, "checklist boxes inside a note are enabled, not greyed out");
            box.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            noteEditor.FlushForDiagnostics();
            Assert(noteCard.Config.Value.Contains("\"IsChecked\":true"), "ticking a checklist box is saved with the note");
            notesHarness = new Window { Title = "PomoDock notes integration test", Content = new NotesEditor(main, new WidgetConfig { Kind = "notes", Value = noteCard.Config.Value }), Width = 420, Height = 300, ShowInTaskbar = false };
            notesHarness.Show(); await Task.Delay(200);
            Render(notesHarness, Path.Combine(directory, "notes-checklist.png"));
            notesHarness.Close(); notesHarness = null;
            var noteId = noteCard.Config.Id;
            main.RemoveCard(noteCard);
            var remembered = NoteArchiveStore.For(main.Store).Archive.Find(noteId);
            Assert(remembered is { IsOpen: false } && remembered.Text.Contains("Comprar pan"), "a closed note stays in the history with its text");
            var reopened = NotesHistory.Reopen(main, remembered!);
            Assert(reopened is not null && reopened.Config.Id == noteId && NoteArchiveStore.For(main.Store).Archive.Find(noteId)!.IsOpen,
                "a closed note reopens from the history as the same note");
            notesHarness = NotesHistory.Build(main, noteId);
            notesHarness.Show(); await Task.Delay(250);
            Render(notesHarness, Path.Combine(directory, "notes-history.png"));
            notesHarness.Close(); notesHarness = null;
            main.RemoveCard(reopened!);

            var todo = Descendant<TodoBoard>(todoCard);
            Assert(todo is not null && todo.RowCount == 5, "the To Do card rebuilds a stored list into rows");
            // A card sits at its canvas offset, so it is photographed in a harness of its own size.
            todoHarness = new Window { Title = "PomoDock To Do integration test", Content = new TodoBoard(main, new WidgetConfig { Kind = "todo", Title = "TO DO", Value = todoCard.Config.Value }), Width = 430, Height = 500, ShowInTaskbar = false };
            todoHarness.Show(); await Task.Delay(200);
            Render(todoHarness, Path.Combine(directory, "todo.png"));
            todoHarness.Close(); todoHarness = null;

            // The calendar: three views over the same events, and a reminder that really rings.
            var agenda = AgendaStore.For(main.Store);
            var todayKey = DateOnly.FromDateTime(DateTime.Now);
            Assert(agenda.Book.OnDay(todayKey).Count >= 3, "calendar resolves every event landing on today");
            var calendar = new CalendarWidget(main, new WidgetConfig { Kind = "calendar", Title = "AGENDA" });
            calendarHarness = new Window { Title = "PomoDock calendar integration test", Content = calendar, Width = 600, Height = 660, ShowInTaskbar = false };
            calendarHarness.Show(); await Task.Delay(250);
            Render(calendarHarness, Path.Combine(directory, "calendar-month.png"));
            calendar.ShowView("week"); await Task.Delay(250);
            Render(calendarHarness, Path.Combine(directory, "calendar-week.png"));
            calendar.ShowView("agenda"); await Task.Delay(250);
            Render(calendarHarness, Path.Combine(directory, "calendar-agenda.png"));
            // Dragged out to fill a vertical monitor, the card has to grow its type, not just its boxes.
            double compact = calendar.Scale;
            calendar.ShowView("month");
            calendarHarness.Width = 900; calendarHarness.Height = 1380; await Task.Delay(400);
            Assert(calendar.Scale > compact * 1.5, "the calendar scales its type with the room it is given");
            Render(calendarHarness, Path.Combine(directory, "calendar-large.png"));
            calendar.ShowView("week"); await Task.Delay(300);
            Render(calendarHarness, Path.Combine(directory, "calendar-large-week.png"));
            calendarHarness.Close(); calendarHarness = null;

            var reminders = AgendaReminders.For(main.Store);
            // Dated To Do tasks live in the calendar too, and one due today rings at 09:00. Whatever
            // is already due rings and is dismissed first, so the checks below count only their own.
            reminders.Pulse(); await Task.Delay(100);
            AgendaToast.CloseAll(); await Task.Delay(100);
            int historyBeforeReminder = main.NotificationHistoryCount;
            var soon = new AgendaEvent { Title = "Llamada de prueba", Start = DateTime.Now.AddMinutes(2), Minutes = 30, Color = "violet", Reminders = [10] };
            agenda.Book.Events.Add(soon); agenda.Save();
            reminders.Pulse(); await Task.Delay(200);
            Assert(AgendaToast.OpenCount == 1, "a reminder that came due raises a notification card");
            Assert(main.NotificationHistoryCount == historyBeforeReminder + 1 && main.NotificationUnreadCount > 0,
                "a reminder is retained in the local notification history");
            Assert(main.IsNotificationBannerVisible, "a reminder appears temporarily beneath the workspace header");
            Render(main.NotificationBannerSurface, Path.Combine(directory, "notification-banner.png"));
            int pageBehindNotifications = main.CurrentWorkspacePageIndex;
            main.OpenNotificationsForDiagnostics(); await Task.Delay(100);
            Assert(main.IsNotificationCenterOpen && main.NotificationUnreadCount == 0,
                "the bell opens the retained history and marks what the user saw as read");
            main.NavigatePageForDiagnostics(1); await Task.Delay(80);
            Assert(main.CurrentWorkspacePageIndex == pageBehindNotifications,
                "the notification history blocks background page navigation");
            Render(main.NotificationHistorySurface, Path.Combine(directory, "notification-history.png"));
            main.CloseNotificationsForDiagnostics();
            Render(AgendaToast.Newest!, Path.Combine(directory, "calendar-reminder.png"));
            int delivered = agenda.Book.Delivered.Count;
            reminders.Pulse(); await Task.Delay(100);
            Assert(AgendaToast.OpenCount == 1 && agenda.Book.Delivered.Count == delivered, "a delivered reminder never rings twice");
            var cue = agenda.Book.Cues(DateTime.Now.AddHours(-1), DateTime.Now).First(entry => entry.Event.Id == soon.Id);
            agenda.Book.Snoozed[cue.Key] = DateTime.Now.AddSeconds(-1);
            reminders.Pulse(); await Task.Delay(200);
            Assert(AgendaToast.OpenCount == 2 && !agenda.Book.Snoozed.ContainsKey(cue.Key), "a postponed reminder rings again when its time arrives");
            AgendaToast.CloseAll(); await Task.Delay(100);
            Assert(AgendaToast.OpenCount == 0, "notification cards close with the app");
            agenda.Remove(soon);
            for (int d = 0; d < 14; d++)
            {
                var end = DateTimeOffset.Now.AddDays(-d).AddHours(-1);
                main.Store.Save(new() { Started = end.AddMinutes(-25), Ended = end, PlannedSeconds = 1500, Outcome = Outcome.Completed, Project = d % 2 == 0 ? "Demo / producto" : "Demo / aprender", Task = "Sesión de demostración", Segments = [new(end.AddMinutes(-25), end)] });
            }
            // The stats card has its own layout at monitor-widget sizes and expands its type and
            // week when there is room. Width-only changes used to leave the original small scale.
            var responsiveStats = new WidgetCard(main, new WidgetConfig { Kind = "stats", Title = "MI ENFOQUE", Width = 310, Height = 190 });
            statsHarness = new Window { Title = "PomoDock responsive stats test", Content = responsiveStats, Width = 330, Height = 230, ShowInTaskbar = false };
            statsHarness.Show(); await Task.Delay(200);
            int compactStatsKey = responsiveStats.StatsLayoutKeyForDiagnostics;
            Render(responsiveStats, Path.Combine(directory, "stats-compact.png"));
            statsHarness.Width = 680; statsHarness.Height = 440; await Task.Delay(250);
            Assert(responsiveStats.StatsLayoutKeyForDiagnostics != compactStatsKey, "focus stats rebuilds when only its available width changes");
            Render(responsiveStats, Path.Combine(directory, "stats-wide.png"));
            responsiveStats.Release(); statsHarness.Close(); statsHarness = null;
            main.ShowReportForDiagnostics(); await Task.Delay(100);
            Assert(main.IsReportModalOpen, "report opens inside the main window modal layer");
            Assert(main.ReportModalFitsVisibleScreen, "report modal is fully visible and is not clipped by the popup surface");
            Assert(main.ReportVisibleSessionCount >= 14, "report summary loads persisted focus history");
            int pagesBehindReport = main.WorkspacePageCount;
            main.NavigatePageForDiagnostics(1);
            await Task.Delay(80);
            Assert(main.WorkspacePageCount == pagesBehindReport, "report modal blocks background page navigation");
            Render(main.ReportModalSurface!, Path.Combine(directory, "report.png"));
            Assert(main.ShowReportChartHoverForDiagnostics(), "report chart exposes period and project values on hover");
            Render(main.ReportModalSurface!, Path.Combine(directory, "report-hover.png"));
            main.ShowReportDetailForDiagnostics(); await Task.Delay(100);
            Assert(main.ReportVisibleSessionCount >= 14, "report detail keeps the selected period data");
            Render(main.ReportModalSurface!, Path.Combine(directory, "report-detail.png"));
            main.HideReportForDiagnostics();
            Assert(!main.IsReportModalOpen, "report modal closes without a second app window");

            // The control panel: four rooms, a rank read from real history, and instant changes.
            var standing = FocusProfile.Of(main.Store.Sessions());
            Assert(standing.Hours > 5 && standing.Level >= 3 && standing.ToNext > 0, "the control panel reads a rank from real focus history");
            // The timer regression silenced the app; the panel is worth seeing with sound on.
            main.Settings.Sound = true; main.Settings.WhiteNoise = true;
            settingsPanel = new SettingsWindow(main);
            settingsPanel.Show(); await Task.Delay(250);
            int pageBehindWindow = main.CurrentWorkspacePageIndex;
            Assert(main.WorkspaceNavigationIsBlocked, "a visible PomoDock owner window locks workspace navigation");
            main.NavigatePageForDiagnostics(1);
            await Task.Delay(80);
            Assert(main.CurrentWorkspacePageIndex == pageBehindWindow && main.WorkspacePageCount == pagesBehindReport,
                "wheel, swipe and navigation commands cannot change pages behind an open window");
            foreach (var room in new[] { "rhythm", "sound", "look", "space" })
            {
                settingsPanel.ShowSection(room); await Task.Delay(150);
                Render(settingsPanel, Path.Combine(directory, $"settings-{room}.png"));
            }
            int keptFocus = main.Settings.FocusMinutes, keptInterval = main.Settings.LongInterval;
            settingsPanel.ShowSection("rhythm");
            settingsPanel.ChooseRhythm(1); await Task.Delay(120);
            Assert(main.Settings.FocusMinutes == 50 && main.Settings.ShortMinutes == 10 && main.Settings.LongInterval == 3,
                "choosing a rhythm applies the whole preset at once");
            settingsPanel.Undo(); await Task.Delay(120);
            Assert(main.Settings.FocusMinutes == keptFocus && main.Settings.LongInterval == keptInterval,
                "undo returns every setting to how the panel found it");
            settingsPanel.Close(); settingsPanel = null;
            var retainedWindow = main.AddCard(new() { Kind = "window", Title = "Retained page fixture", X = 24, Y = 24, Width = 280, Height = 260 }, false);
            await retainedWindow.Attach(foreign);
            Assert(retainedWindow.IsExternalAttached, "page navigation fixture embeds a real external window");
            int initialPages = main.WorkspacePageCount;
            Assert(main.AddPageForDiagnostics(), "a populated workspace unlocks a new page");
            await Task.Delay(320);
            Assert(main.WorkspacePageCount == initialPages + 1 && main.CurrentWorkspacePageIsBlank && !main.CurrentWorkspacePageHasTimer,
                "new workspace pages start completely blank");
            Assert(main.EmptyPageIsFrameless, "empty workspace page has no surrounding placeholder border");
            Render(main, Path.Combine(directory, "page-blank.png"));
            Assert(!main.AddPageForDiagnostics(), "a blank last page prevents adding another one after it");
            main.AddTimerForDiagnostics();
            Assert(main.CurrentWorkspacePageHasTimer && !main.CurrentWorkspacePageIsBlank, "timer can be added as the first widget on a blank page");
            main.RemoveTimerForDiagnostics();
            Assert(!main.CurrentWorkspacePageHasTimer && main.CurrentWorkspacePageIsBlank, "timer widget can be removed from its page");
            main.AddTimerForDiagnostics();
            Render(main, Path.Combine(directory, "page-timer.png"));
            Assert(main.ShowSwipeMidpointForDiagnostics(-1), "mouse carousel exposes both canvases while a slow swipe is in progress");
            Render(main, Path.Combine(directory, "page-swipe.png"));
            main.CancelSwipeForDiagnostics();
            Assert(main.AddPageForDiagnostics(), "adding a widget unlocks the following page");
            await Task.Delay(320);
            Assert(main.CurrentWorkspacePageIsBlank, "each subsequently created page is blank too");
            main.AddCard(new() { Kind = "notes", Title = "EDGE PAGE" }, true);
            main.SwitchPageForDiagnostics(0);
            await Task.Delay(320);
            Assert(!main.CurrentWorkspacePageIsBlank, "page navigation restores the original canvas automatically");
            Assert(retainedWindow.IsExternalAttached, "page navigation preserves embedded external windows");
            int pagesBeforeEdgeGesture = main.WorkspacePageCount;
            main.NavigatePageForDiagnostics(-1);
            await Task.Delay(320);
            Assert(main.WorkspacePageCount == pagesBeforeEdgeGesture + 1 && main.CurrentWorkspacePageIsBlank,
                "navigating beyond a populated edge creates a blank canvas in that direction");
            // A blank page on the left must not block a new one on the right: one blank canvas per side.
            int pagesWithLeftBlank = main.WorkspacePageCount;
            main.SwitchPageForDiagnostics(pagesWithLeftBlank - 1);
            await Task.Delay(320);
            main.NavigatePageForDiagnostics(1);
            await Task.Delay(320);
            Assert(main.WorkspacePageCount == pagesWithLeftBlank + 1 && main.CurrentWorkspacePageIsBlank && main.FirstWorkspacePageIsBlank,
                "a blank page on the left still lets the right edge open its own blank page");
            main.NavigatePageForDiagnostics(1);
            await Task.Delay(320);
            Assert(main.WorkspacePageCount == pagesWithLeftBlank + 1, "two blank pages never sit side by side at the same edge");
            main.SwitchPageForDiagnostics(1);
            await Task.Delay(320);
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
            AgendaToast.CloseAll();
            settingsPanel?.Close(); tasksHarness?.Close(); statsHarness?.Close(); notesHarness?.Close(); todoHarness?.Close(); calendarHarness?.Close(); dualHarness?.Close(); harness?.Close(); main?.Close();
            if (fixture is not null) { if (!fixture.HasExited) fixture.CloseMainWindow(); fixture.Dispose(); }
            if (fixture2 is not null) { if (!fixture2.HasExited) fixture2.CloseMainWindow(); fixture2.Dispose(); }
            Application.Current.Shutdown(Environment.ExitCode);
        }
    }
    /// <summary>A task list with every state the card can show: overdue, urgent, dated and done.</summary>
    private static string SeedTasks()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var book = new TodoBook();
        var late = book.Add("Enviar la propuesta al cliente", today)!;
        late.Due = today.AddDays(-1); late.Priority = TodoPriority.High;
        var review = book.Add("Revisar el informe trimestral", today)!;
        review.Due = today; review.Priority = TodoPriority.Medium;
        book.Add("Comprar café para la oficina", today)!.Due = today.AddDays(1);
        book.Add("Actualizar el currículum", today);
        book.Add("Responder los correos pendientes", today)!.SetDone(true);
        book.SortByUrgency(today);
        return JsonSerializer.Serialize(book);
    }

    /// <summary>First element of a kind inside a visual tree, used to reach a widget's own board.</summary>
    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (Descendant<T>(VisualTreeHelper.GetChild(root, index)) is { } found) return found;
        return null;
    }

    /// <summary>A believable week so the calendar screenshots show real shapes, not an empty grid.</summary>
    private static void SeedAgenda(Store store)
    {
        var agenda = AgendaStore.For(store);
        var today = DateTime.Now.Date;
        agenda.Book.Events.Clear();
        agenda.Book.Events.AddRange(
        [
            new AgendaEvent { Title = "Revisión de producto", Start = today.AddHours(10), Minutes = 60, Color = "blue", Location = "Sala 2", Reminders = [10] },
            new AgendaEvent { Title = "Bloque de enfoque", Start = today.AddHours(12), Minutes = 90, Color = "green", Repeat = RepeatKind.Weekly, Days = [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday], Reminders = [5] },
            new AgendaEvent { Title = "Comida con el equipo", Start = today.AddHours(14).AddMinutes(30), Minutes = 60, Color = "amber" },
            new AgendaEvent { Title = "Gimnasio", Start = today.AddHours(19), Minutes = 60, Color = "teal", Repeat = RepeatKind.Daily, Reminders = [30] },
            new AgendaEvent { Title = "Entrega del informe", AllDay = true, Start = today.AddDays(1), Color = "red", Reminders = [1440] },
            new AgendaEvent { Title = "Pago del alquiler", AllDay = true, Start = today.AddDays(3), Color = "violet", Repeat = RepeatKind.Monthly, Reminders = [1440] }
        ]);
        agenda.Save();
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
