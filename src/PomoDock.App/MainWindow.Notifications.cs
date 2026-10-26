using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PomoDock.App.Native;
using PomoDock.Core;

namespace PomoDock.App;

public partial class MainWindow
{
    private NotificationStore notifications = null!;
    private readonly DispatcherTimer notificationBannerLife = new() { Interval = TimeSpan.FromSeconds(7) };
    private DispatcherTimer? timerAlertSnooze;
    private Phase lastTimerAlertCompleted = Phase.Focus;
    private Phase lastTimerAlertNext = Phase.ShortBreak;
    private bool timerAlertArmed;
    private static readonly SolidColorBrush UnreadAlertFill = FreezeBrush(0xC4, 0x3B, 0x3B);
    private static readonly SolidColorBrush UnreadAlertEdge = FreezeBrush(0x8F, 0x22, 0x22);
    private static readonly SolidColorBrush UnreadAlertInk = FreezeBrush(0xFF, 0xFF, 0xFF);

    private static SolidColorBrush FreezeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void InitializeNotifications()
    {
        notifications = new NotificationStore(Store);
        notifications.Changed += RefreshNotificationChrome;
        notificationBannerLife.Tick += (_, _) => HideNotificationBanner();
        NotificationBanner.MouseEnter += (_, _) => notificationBannerLife.Stop();
        NotificationBanner.MouseLeave += (_, _) =>
        {
            if (NotificationBanner.Visibility == Visibility.Visible) notificationBannerLife.Start();
        };
        NotificationsPopup.Opened += (_, _) =>
        {
            RenderNotificationHistory();
            notifications.MarkAllRead();
            NotificationModal.CloseAll();
            BringPopupToFront(NotificationsPopup);
        };
        Loaded += (_, _) => Dispatcher.BeginInvoke(ShowUnreadNotificationModals);
        RefreshNotificationChrome();
    }

    /// <summary>
    /// Records and surfaces a due reminder. This deliberately does not touch its task: firing a
    /// reminder is information, while finishing the task remains an explicit user decision.
    /// </summary>
    internal ReminderNotification NotificationArrived(ReminderCue cue, DateTime now)
    {
        var note = notifications.Add(cue, now);
        ShowNotificationBanner(
            L.T(cue.Event.TaskId is null ? "notifications.bannerEvent" : "notifications.bannerTask"),
            cue.Event.Title);
        if (IsAwayFromWorkspace()) FlashForAttention();
        return note;
    }

    internal void PresentReminderModal(
        ReminderNotification note,
        ReminderCue cue,
        DateTime now,
        Action<ReminderCue, int> snooze,
        Action<ReminderCue> complete)
    {
        NotificationModal.PresentReminder(this, note, cue, now, snooze, complete, AcknowledgeNotification);
    }

    internal void AnnounceTimerComplete(Phase completed, Phase next)
    {
        string title = completed == Phase.Focus
            ? L.T("notifications.timerFocusTitle")
            : L.T("notifications.timerBreakTitle");
        var note = notifications.AddAlert("timer", completed == Phase.Focus ? "timer:focus" : "timer:break", title, DateTime.Now);
        timerAlertSnooze?.Stop();
        lastTimerAlertCompleted = completed;
        lastTimerAlertNext = next;
        timerAlertArmed = true;
        PresentTimerAlert(note, completed, next);
    }

    internal void AnnounceTimerCompleteForDiagnostics(Phase completed, Phase next) =>
        AnnounceTimerComplete(completed, next);

    private void PresentTimerAlert(ReminderNotification note, Phase completed, Phase next)
    {
        string title = completed == Phase.Focus
            ? L.T("notifications.timerFocusTitle")
            : L.T("notifications.timerBreakTitle");
        ShowNotificationBanner(
            L.T(completed == Phase.Focus ? "notifications.bannerFocus" : "notifications.bannerBreak"),
            title);
        string nextName = next == Phase.Focus
            ? L.T("timer.phaseFocusName")
            : next == Phase.ShortBreak ? L.T("timer.phaseShort") : L.T("timer.phaseLong");
        int minutes = Math.Max(1, (int)Math.Round(Settings.Duration(next) / 60));
        var actions = new List<AlertAction>();
        if (completed == Phase.Focus)
        {
            actions.Add(new AlertAction(L.T("notifications.actionStartBreak"), () => StartPhaseFromAlert(next), true));
            actions.Add(new AlertAction(L.T("notifications.actionAnotherFocus"), () => StartPhaseFromAlert(Phase.Focus)));
        }
        else
        {
            actions.Add(new AlertAction(L.T("notifications.actionStartFocus"), () => StartPhaseFromAlert(Phase.Focus), true));
        }
        actions.Add(new AlertAction(L.T("agenda.snoozeButton", 5), () => SnoozeTimerAlert(5, completed, next)));
        NotificationModal.PresentAlert(
            this,
            note,
            completed == Phase.Focus ? L.T("notifications.bannerFocus") : L.T("notifications.bannerBreak"),
            title,
            L.T("notifications.timerNext", nextName, minutes),
            "rose",
            actions,
            AcknowledgeNotification);
        if (IsAwayFromWorkspace()) FlashForAttention();
    }

    private void StartPhaseFromAlert(Phase phase)
    {
        AcknowledgeTimerAlerts();
        if (Timer.Phase != phase) Timer.Select(phase, DateTimeOffset.UtcNow, Monotonic);
        if (!Timer.Running)
        {
            Timer.Start(DateTimeOffset.UtcNow, Monotonic, selectedTask);
            if (phase == Phase.Focus) Sounds.StartNoise();
        }
        appUsage.Observe(Timer.Running && Timer.Phase == Phase.Focus, DateTimeOffset.UtcNow);
        UpdateTimer();
        AnimateTimer();
        SaveState();
        Status(L.T("status.running"));
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void SnoozeTimerAlert(int minutes, Phase completed, Phase next)
    {
        timerAlertSnooze?.Stop();
        timerAlertSnooze = new DispatcherTimer { Interval = TimeSpan.FromMinutes(Math.Max(1, minutes)) };
        timerAlertSnooze.Tick += (_, _) =>
        {
            timerAlertSnooze.Stop();
            if (!timerAlertArmed) return;
            var note = notifications.AddAlert("timer", completed == Phase.Focus ? "timer:focus" : "timer:break",
                completed == Phase.Focus ? L.T("notifications.timerFocusTitle") : L.T("notifications.timerBreakTitle"),
                DateTime.Now);
            PresentTimerAlert(note, completed, next);
            Status(L.T("notifications.timerSnoozed", minutes));
        };
        timerAlertSnooze.Start();
        Status(L.T("notifications.timerSnoozed", minutes));
    }

    internal void AcknowledgeTimerAlerts()
    {
        timerAlertArmed = false;
        timerAlertSnooze?.Stop();
        AgendaToast.DismissAlerts();
    }

    internal AgentToolResult SnoozeTimerAlertFromAgent(int minutes)
    {
        minutes = Math.Clamp(minutes <= 0 ? 5 : minutes, 1, 120);
        if (!timerAlertArmed && timerAlertSnooze is null)
            return AgentResult(false, EsPages() ? "No hay una alerta de pomodoro que posponer." : "There is no pomodoro alert to snooze.");
        SnoozeTimerAlert(minutes, lastTimerAlertCompleted, lastTimerAlertNext);
        AgendaToast.DismissAlerts();
        NotificationModal.CloseAll();
        return AgentResult(true, EsPages()
            ? $"Pospué la alerta de pomodoro {minutes} minutos."
            : $"Snoozed the pomodoro alert for {minutes} minutes.");
    }

    internal AgentToolResult SnoozeCalendarFromAgent(JsonElement args)
    {
        string title = TextArg(args, "title");
        if (title.Length == 0) title = TextArg(args, "name");
        int minutes = 5;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("minutes", out var value) && value.TryGetInt32(out int parsed))
            minutes = parsed;
        minutes = Math.Clamp(minutes <= 0 ? 5 : minutes, 1, 120);
        if (!AgendaReminders.For(Store).TrySnooze(title, minutes, out string which))
            return AgentResult(false, EsPages() ? "No encontré un recordatorio que posponer." : "No reminder to snooze.");
        return AgentResult(true, EsPages()
            ? $"Pospué «{which}» {minutes} minutos."
            : $"Snoozed “{which}” for {minutes} minutes.");
    }

    internal void AcknowledgeNotification(Guid id)
    {
        if (notifications.MarkRead(id))
            NotificationModal.Dismiss(id);
        RefreshNotificationChrome();
    }

    /// <summary>Restores centered cards for anything still unread after a restart or missed toast.</summary>
    private void ShowUnreadNotificationModals()
    {
        foreach (var note in notifications.History.Items.Where(item => !item.Read).Reverse())
        {
            if (NotificationModal.IsShowing(note.Id)) continue;
            if (note.Kind == "timer")
            {
                NotificationModal.PresentUnread(this, note, AcknowledgeNotification);
                continue;
            }
            var cue = ResolveReminderCue(note);
            if (cue is { } live)
            {
                var reminders = AgendaReminders.For(Store);
                NotificationModal.PresentReminder(
                    this, note, live, DateTime.Now,
                    (item, minutes) => reminders.SnoozeFromUi(item, minutes),
                    item => reminders.CompleteFromUi(item),
                    AcknowledgeNotification);
            }
            else NotificationModal.PresentUnread(this, note, AcknowledgeNotification);
        }
    }

    private ReminderCue? ResolveReminderCue(ReminderNotification note)
    {
        if (string.IsNullOrWhiteSpace(note.CueKey)) return null;
        return AgendaStore.For(Store).Book.CueFor(note.CueKey);
    }

    private void ShowNotificationBanner(string lead, string title)
    {
        NotificationBannerLead.Text = lead;
        NotificationBannerTitle.Text = title;
        NotificationBanner.Visibility = Visibility.Visible;
        NotificationBanner.Opacity = 1;
        if (!Settings.ReduceMotion)
        {
            NotificationBanner.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }
        NotificationBannerPopup.IsOpen = true;
        BringPopupToFront(NotificationBannerPopup);
        notificationBannerLife.Stop();
        notificationBannerLife.Start();
    }

    private void NotificationsClick(object sender, RoutedEventArgs e)
    {
        NotificationsPopup.IsOpen = !NotificationsPopup.IsOpen;
        if (NotificationsPopup.IsOpen) HideNotificationBanner();
        e.Handled = true;
    }

    private void NotificationBannerClick(object sender, MouseButtonEventArgs e)
    {
        HideNotificationBanner();
        NotificationsPopup.IsOpen = true;
        e.Handled = true;
    }

    private void DismissNotificationBanner(object sender, RoutedEventArgs e)
    {
        HideNotificationBanner();
        e.Handled = true;
    }

    private void CloseNotifications(object sender, RoutedEventArgs e)
    {
        NotificationsPopup.IsOpen = false;
        e.Handled = true;
    }

    private void HideNotificationBanner()
    {
        notificationBannerLife.Stop();
        NotificationBannerPopup.IsOpen = false;
        NotificationBanner.BeginAnimation(OpacityProperty, null);
        NotificationBanner.Visibility = Visibility.Collapsed;
    }

    private void CloseNotificationCenter()
    {
        notificationBannerLife.Stop();
        NotificationsPopup.IsOpen = false;
        HideNotificationBanner();
    }

    private bool IsAwayFromWorkspace()
    {
        if (!IsLoaded || !IsVisible) return true;
        if (WindowState == WindowState.Minimized) return true;
        if (!IsActive) return true;
        if (hwnd != 0 && Win32.GetForegroundWindow() != hwnd) return true;
        return false;
    }

    private void FlashForAttention()
    {
        if (hwnd == 0) return;
        try
        {
            var info = new Win32.FlashInfo
            {
                Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.FlashInfo>(),
                Hwnd = hwnd,
                Flags = Win32.FLASHW_ALL | Win32.FLASHW_TIMERNOFG,
                Count = 3,
                Timeout = 0
            };
            Win32.FlashWindowEx(ref info);
        }
        catch { /* older shells may lack flash */ }
    }

    /// <summary>Same as tapping × on the banner and every toast: hide them and silence the timer alarm.</summary>
    internal void DismissVisibleAlerts()
    {
        AgendaToast.CloseAll();
        NotificationModal.CloseAll();
        AcknowledgeTimerAlerts();
        HideNotificationBanner();
    }

    private void RefreshNotificationChrome()
    {
        if (notifications is null) return;
        int unread = notifications.History.Unread;
        NotificationsButton.Content = unread > 0 ? $"🔔 {Math.Min(unread, 99)}" : "🔔";
        NotificationsButton.ToolTip = unread > 0
            ? L.T("notifications.openUnread", unread)
            : L.T("notifications.open");
        if (unread > 0)
        {
            NotificationsButton.Background = UnreadAlertFill;
            NotificationsButton.Foreground = UnreadAlertInk;
            NotificationsButton.BorderBrush = UnreadAlertEdge;
            NotificationsButton.BorderThickness = new Thickness(1.5);
            NotificationHistoryTitle.Text = L.T("notifications.pendingTitle");
            NotificationHistoryMeta.Text = L.T("notifications.pendingMeta", unread);
        }
        else
        {
            NotificationsButton.ClearValue(BackgroundProperty);
            NotificationsButton.ClearValue(ForegroundProperty);
            NotificationsButton.ClearValue(BorderBrushProperty);
            NotificationsButton.ClearValue(BorderThicknessProperty);
            NotificationHistoryTitle.Text = L.T("notifications.title");
            NotificationHistoryMeta.Text = L.T("notifications.meta", notifications.History.Items.Count, unread);
        }
        NotificationsButton.SetValue(AutomationProperties.NameProperty, NotificationsButton.ToolTip?.ToString() ?? L.T("notifications.open"));
        if (NotificationsPopup.IsOpen) RenderNotificationHistory();
    }

    private void RenderNotificationHistory()
    {
        NotificationHistoryList.Children.Clear();
        var items = notifications.History.Items;
        if (items.Count == 0)
        {
            NotificationHistoryList.Children.Add(new TextBlock
            {
                Text = L.T("notifications.empty"),
                Foreground = ResourceBrush("Muted"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 16, 8, 20)
            });
            return;
        }

        foreach (var item in items)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            row.ColumnDefinitions.Add(new ColumnDefinition());

            var marker = new Border
            {
                Background = item.Read ? ResourceBrush("Line") : UnreadAlertFill
            };
            row.Children.Add(marker);

            string kind = item.Kind == "timer"
                ? L.T("notifications.timer")
                : L.T(item.TaskId is null ? "notifications.event" : "notifications.task");
            string delivered = item.DeliveredLocal.ToString("ddd dd MMM · HH:mm", Strings.Culture).ToUpper(Strings.Culture);
            var copy = new StackPanel { Margin = new Thickness(11, 10, 12, 10) };
            copy.Children.Add(new TextBlock
            {
                Text = item.Title,
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap
            });
            copy.Children.Add(new TextBlock
            {
                Text = item.Read
                    ? $"{kind}  ·  {delivered}"
                    : $"{kind}  ·  {delivered}  ·  {L.T("notifications.pendingTitle")}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 8.5,
                Foreground = item.Read ? ResourceBrush("Muted") : UnreadAlertFill,
                Margin = new Thickness(0, 4, 0, 0)
            });
            Grid.SetColumn(copy, 1);
            row.Children.Add(copy);

            var frame = new Border
            {
                BorderBrush = item.Read ? ResourceBrush("Edge") : UnreadAlertEdge,
                BorderThickness = new Thickness(item.Read ? 1 : 1.5),
                Background = ResourceBrush("Paper"),
                Child = row
            };
            NotificationHistoryList.Children.Add(frame);
        }
    }

    private static Brush ResourceBrush(string key) =>
        Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;

    internal bool IsNotificationCenterOpen => NotificationsPopup.IsOpen;
    internal int NotificationHistoryCount => notifications.History.Items.Count;
    internal int NotificationUnreadCount => notifications.History.Unread;
    internal bool IsNotificationBannerVisible => NotificationBannerPopup.IsOpen;
    internal int NotificationModalCount => NotificationModal.OpenCount;
    internal FrameworkElement NotificationBannerSurface => NotificationBanner;
    internal FrameworkElement NotificationHistorySurface => (FrameworkElement)NotificationsPopup.Child;
    internal void OpenNotificationsForDiagnostics() => NotificationsPopup.IsOpen = true;
    internal void CloseNotificationsForDiagnostics() => NotificationsPopup.IsOpen = false;
    internal void HideNotificationBannerForDiagnostics() => HideNotificationBanner();
}
