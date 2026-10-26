using System.Globalization;
using PomoDock.Core;

namespace PomoDock.Web;

/// <summary>
/// Rings calendar reminders for as long as PomoDock Web is open. It runs on the window's own
/// clock, so an alert never depends on a calendar widget being visible, or even on its page.
/// </summary>
public partial class MainWindow
{
    private AgendaStore Agenda => AgendaStore.For(Store);

    private void SweepReminders(DateTime now)
    {
        var book = Agenda.Book;
        bool dirty = false;

        foreach (var key in book.Snoozed.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToList())
        {
            book.Snoozed.Remove(key);
            dirty = true;
            if (book.CueFor(key) is { } postponed) Ring(postponed, now);
        }

        foreach (var cue in book.Cues(now.AddHours(-12), now).ToList())
        {
            if (book.Delivered.Contains(cue.Key, StringComparer.Ordinal) || book.Snoozed.ContainsKey(cue.Key)) continue;
            book.Delivered.Add(cue.Key);
            dirty = true;
            // Catch-up has a limit: an event that already passed while the tab was closed stays quiet.
            if (now <= cue.Start.AddMinutes(10)) Ring(cue, now);
        }

        if (dirty) Agenda.Track();
    }

    private void Ring(ReminderCue cue, DateTime now)
    {
        var note = NotificationArrived(cue, now);
        Sounds.Reminder();
        PresentReminder(note, cue, now);
        if (Browser.PageHidden()) Browser.Notify(cue.Event.Title, ReminderWhen(cue));
        StateHasChanged();
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
        return note;
    }

    /// <summary>Calendar / task reminder with LISTO and snooze, matching the desktop's card.</summary>
    private void PresentReminder(ReminderNotification note, ReminderCue cue, DateTime now)
    {
        var item = cue.Event;
        string lead = item.AllDay ? AgendaVisuals.DayLabel(cue.Series) : AgendaVisuals.Countdown(cue.Start, now);
        var actions = new List<AlertAction> { new(L.T("agenda.toastDone"), () => CompleteReminder(cue), true) };
        foreach (int minutes in new[] { 5, 15 })
            actions.Add(new AlertAction(L.T("agenda.snoozeButton", minutes), () => SnoozeReminder(cue, minutes)));
        PresentAlert(new AlertCard(note.Id, L.T("agenda.reminderLead", lead), item.Title, ReminderWhen(cue), actions,
            AgendaVisuals.Solid(item.Color), item.Location, cue.Key));
    }

    private static string ReminderWhen(ReminderCue cue)
    {
        var item = cue.Event;
        return item.AllDay
            ? L.T("agenda.toastAllDay", AgendaVisuals.LongDayLabel(cue.Series))
            : L.T("agenda.toastTime", AgendaVisuals.DayLabel(cue.Series), cue.Start.ToString("HH:mm", CultureInfo.InvariantCulture), item.EndOn(cue.Series).ToString("HH:mm", CultureInfo.InvariantCulture));
    }

    private void SnoozeReminder(ReminderCue cue, int minutes)
    {
        Agenda.Book.Snoozed[cue.Key] = DateTime.Now.AddMinutes(minutes);
        Agenda.Track();
        Status(L.T("agenda.snoozed", cue.Event.Title, minutes));
    }

    private void CompleteReminder(ReminderCue cue)
    {
        cue.Event.SetDone(cue.Series, true);
        Agenda.Save();
        Status(L.T("agenda.markedDone", cue.Event.Title));
    }

    /// <summary>The live cue an unread reminder card still points at, after a reload.</summary>
    private ReminderCue? ResolveReminderCue(ReminderNotification note) =>
        string.IsNullOrWhiteSpace(note.CueKey) ? null : Agenda.Book.CueFor(note.CueKey);
}
