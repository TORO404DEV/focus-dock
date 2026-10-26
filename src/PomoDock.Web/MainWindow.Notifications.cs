using PomoDock.Core;

namespace PomoDock.Web;

/// <summary>A button on an alert card. Primary is the inked one.</summary>
internal sealed record AlertAction(string Label, Action Run, bool Primary = false);

/// <summary>One centered, sticky card per unread notification. It waits until the user acts.</summary>
internal sealed record AlertCard(Guid Id, string Lead, string Title, string Detail, IReadOnlyList<AlertAction> Actions);

public partial class MainWindow
{
    private NotificationStore notifications = null!;
    private readonly List<AlertCard> alerts = [];
    private bool notificationsOpen, bannerVisible;
    private string bannerLead = "", bannerTitle = "";
    private int bannerGeneration;
    private CancellationTokenSource? timerAlertSnooze;
    private Phase lastTimerAlertCompleted = Phase.Focus;
    private Phase lastTimerAlertNext = Phase.ShortBreak;
    private bool timerAlertArmed;

    private void InitializeNotifications()
    {
        notifications = new NotificationStore(Store);
        notifications.Changed += () => _ = InvokeAsync(StateHasChanged);
    }

    /// <summary>The bell as a text glyph: WPF draws emoji in one colour, and so does this.</summary>
    private string BellText => notifications.History.Unread > 0 ? $"🔔︎ {Math.Min(notifications.History.Unread, 99)}" : "🔔︎";

    private string BellTip => notifications.History.Unread > 0
        ? L.T("notifications.openUnread", notifications.History.Unread)
        : L.T("notifications.open");

    internal void AnnounceTimerComplete(Phase completed, Phase next)
    {
        string title = completed == Phase.Focus
            ? L.T("notifications.timerFocusTitle")
            : L.T("notifications.timerBreakTitle");
        var note = notifications.AddAlert("timer", completed == Phase.Focus ? "timer:focus" : "timer:break", title, DateTime.Now);
        timerAlertSnooze?.Cancel();
        lastTimerAlertCompleted = completed;
        lastTimerAlertNext = next;
        timerAlertArmed = true;
        PresentTimerAlert(note, completed, next);
    }

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
        string detail = L.T("notifications.timerNext", nextName, minutes);
        PresentAlert(new AlertCard(note.Id,
            completed == Phase.Focus ? L.T("notifications.bannerFocus") : L.T("notifications.bannerBreak"),
            title, detail, actions));
        // Away in another tab, the page itself cannot be seen: the system notification is the flash.
        if (Browser.PageHidden()) Browser.Notify(title, detail);
    }

    private void PresentAlert(AlertCard card)
    {
        if (alerts.Any(item => item.Id == card.Id)) return;
        while (alerts.Count >= 5) alerts.RemoveAt(0);
        alerts.Add(card);
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
        UpdateTimer();
        AnimateTimer();
        SaveState();
        Status(L.T("status.running"));
    }

    private void SnoozeTimerAlert(int minutes, Phase completed, Phase next)
    {
        timerAlertSnooze?.Cancel();
        var snooze = timerAlertSnooze = new CancellationTokenSource();
        _ = Snooze();
        Status(L.T("notifications.timerSnoozed", minutes));

        async Task Snooze()
        {
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, minutes)), snooze.Token); }
            catch (TaskCanceledException) { return; }
            await InvokeAsync(() =>
            {
                if (!timerAlertArmed) return;
                var note = notifications.AddAlert("timer", completed == Phase.Focus ? "timer:focus" : "timer:break",
                    completed == Phase.Focus ? L.T("notifications.timerFocusTitle") : L.T("notifications.timerBreakTitle"),
                    DateTime.Now);
                PresentTimerAlert(note, completed, next);
                Status(L.T("notifications.timerSnoozed", minutes));
                StateHasChanged();
            });
        }
    }

    internal void AcknowledgeTimerAlerts()
    {
        timerAlertArmed = false;
        timerAlertSnooze?.Cancel();
        Sounds.StopAlarm();
    }

    private void AcknowledgeAlert(AlertCard card, AlertAction? action)
    {
        action?.Run();
        AcknowledgeNotification(card.Id);
        // Closing the card is hearing it: the bell stops ringing once the person has seen it.
        Sounds.StopAlarm();
    }

    internal void AcknowledgeNotification(Guid id)
    {
        notifications.MarkRead(id);
        alerts.RemoveAll(item => item.Id == id);
    }

    /// <summary>Restores centered cards for anything still unread after a reload or a missed alert.</summary>
    private void ShowUnreadNotificationModals()
    {
        foreach (var note in notifications.History.Items.Where(item => !item.Read).Reverse())
        {
            string detail = note.DeliveredLocal.ToString("ddd dd MMM · HH:mm", Strings.Culture).ToUpper(Strings.Culture);
            PresentAlert(new AlertCard(note.Id, L.T("notifications.pendingLead"), note.Title, detail,
                [new AlertAction(L.T("agenda.toastDone"), () => { }, true)]));
        }
    }

    private async void ShowNotificationBanner(string lead, string title)
    {
        bannerLead = lead;
        bannerTitle = title;
        bannerVisible = true;
        int generation = ++bannerGeneration;
        await Task.Delay(TimeSpan.FromSeconds(7));
        if (generation != bannerGeneration || bannerHovered) return;
        bannerVisible = false;
        StateHasChanged();
    }

    private bool bannerHovered;

    private void BannerLeave()
    {
        bannerHovered = false;
        ShowNotificationBanner(bannerLead, bannerTitle);
    }

    private void NotificationsClick()
    {
        notificationsOpen = !notificationsOpen;
        if (notificationsOpen)
        {
            HideNotificationBanner();
            notifications.MarkAllRead();
            alerts.Clear();
        }
    }

    private void NotificationBannerClick()
    {
        HideNotificationBanner();
        notificationsOpen = true;
        notifications.MarkAllRead();
        alerts.Clear();
    }

    private void HideNotificationBanner()
    {
        bannerGeneration++;
        bannerVisible = false;
    }

    private void CloseNotificationCenter()
    {
        notificationsOpen = false;
        HideNotificationBanner();
    }

    private static string NotificationKind(ReminderNotification item) => item.Kind == "timer"
        ? L.T("notifications.timer")
        : L.T(item.TaskId is null ? "notifications.event" : "notifications.task");
}
