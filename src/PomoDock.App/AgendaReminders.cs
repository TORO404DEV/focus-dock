using System.Runtime.CompilerServices;
using System.Windows.Threading;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// Rings calendar reminders for as long as PomoDock is open. It keeps its own clock so an alert
/// never depends on a calendar widget being visible, or even on the page it was created in.
/// </summary>
internal sealed class AgendaReminders
{
    private static readonly ConditionalWeakTable<Store, AgendaReminders> shared = new();
    private readonly AgendaStore agenda;
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(20) };
    private MainWindow? owner;

    public static AgendaReminders For(Store store) => shared.GetValue(store, key => new AgendaReminders(key));

    private AgendaReminders(Store store)
    {
        agenda = AgendaStore.For(store);
        clock.Tick += (_, _) => Sweep(DateTime.Now);
    }

    /// <summary>Binds the service to the window that hosts the cards. Calling it twice is harmless.</summary>
    public void Attach(MainWindow window)
    {
        owner = window;
        if (clock.IsEnabled) return;
        clock.Start();
        // A reminder that came due moments ago still deserves its card on startup.
        Sweep(DateTime.Now);
    }

    /// <summary>Runs one pass immediately instead of waiting for the next tick. Used by the self test.</summary>
    internal void Pulse() => Sweep(DateTime.Now);

    private void Sweep(DateTime now)
    {
        if (owner is null || !owner.IsLoaded) return;
        var book = agenda.Book;
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
            // Catch-up has a limit: an event that already passed while PomoDock was closed stays quiet.
            if (now <= cue.Start.AddMinutes(10)) Ring(cue, now);
        }

        if (dirty) agenda.Track();
    }

    private void Ring(ReminderCue cue, DateTime now)
    {
        if (owner is null) return;
        owner.NotificationArrived(cue, now);
        owner.Sounds.Reminder();
        AgendaToast.Present(owner, cue, now, Snooze, Complete);
    }

    private void Snooze(ReminderCue cue, int minutes)
    {
        agenda.Book.Snoozed[cue.Key] = DateTime.Now.AddMinutes(minutes);
        agenda.Track();
        AgendaToast.DismissReminder(cue);
        owner?.Status(L.T("agenda.snoozed", cue.Event.Title, minutes));
    }

    internal bool TrySnooze(string query, int minutes, out string title)
    {
        title = "";
        minutes = Math.Clamp(minutes <= 0 ? 5 : minutes, 1, 120);
        var book = agenda.Book;
        ReminderCue? cue = null;
        if (AgendaToast.NewestReminder is { } live
            && (query.Length == 0 || live.Event.Title.Contains(query, StringComparison.OrdinalIgnoreCase)))
            cue = live;
        if (cue is null)
        {
            var window = DateTime.Now;
            cue = book.Cues(window.AddHours(-12), window.AddHours(12))
                .Where(item => query.Length == 0 || item.Event.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => book.Delivered.Contains(item.Key, StringComparer.Ordinal) || book.Snoozed.ContainsKey(item.Key))
                .ThenBy(item => item.Start)
                .FirstOrDefault();
        }
        if (cue is null && query.Length > 0)
        {
            var match = book.Events.FirstOrDefault(item => item.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                var day = DateOnly.FromDateTime(DateTime.Now);
                int offset = match.Reminders.Count > 0 ? match.Reminders[0] : 0;
                cue = new ReminderCue(match, day, match.AnchorFor(day), offset, match.AnchorFor(day).AddMinutes(-offset));
            }
        }
        if (cue is null) return false;
        title = cue.Event.Title;
        Snooze(cue, minutes);
        return true;
    }

    private void Complete(ReminderCue cue)
    {
        cue.Event.SetDone(cue.Series, true);
        agenda.Save();
        owner?.Status(L.T("agenda.markedDone", cue.Event.Title));
    }
}
