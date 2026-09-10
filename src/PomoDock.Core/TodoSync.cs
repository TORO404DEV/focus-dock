namespace PomoDock.Core;

/// <summary>
/// Keeps a dated task and its calendar entry in step. The list owns the words and the urgency;
/// the calendar owns reminders and repetition, so a task rings exactly like an appointment.
/// </summary>
public static class TodoSync
{
    /// <summary>
    /// Builds or refreshes the entry that carries a dated task. A new entry starts from what the
    /// line said — its reminder, its rhythm, its place — and afterwards the task's own fields win.
    /// </summary>
    public static AgendaEvent ToEvent(TodoTask task, AgendaEvent? existing, AgendaEvent? draft)
    {
        if (task.Due is not { } day) throw new ArgumentException("Solo una tarea con fecha va al calendario.", nameof(task));
        var item = existing ?? draft ?? new AgendaEvent { Reminders = [task.At is null ? 0 : 10] };
        item.TaskId = task.Id;
        item.Title = task.Title;
        item.Color = task.ColorKey();
        if (item.Repeat == RepeatKind.None)
        {
            bool wasAllDay = item.AllDay;
            item.AllDay = task.At is null;
            item.Start = task.At is { } at ? day.ToDateTime(at) : day.ToDateTime(TimeOnly.MinValue);
            // An all-day entry that gained an hour needs an ordinary length, not a whole day.
            if (wasAllDay && !item.AllDay) item.Minutes = 60;
            item.Done = task.Done ? [AgendaEvent.Key(day)] : [];
        }
        item.Normalize();
        return item;
    }

    /// <summary>Finishing a repeating task checks off this time and brings the next one forward.</summary>
    public static void Complete(TodoTask task, AgendaEvent? item)
    {
        if (item is { Repeat: not RepeatKind.None } && task.Due is { } due)
        {
            item.SetDone(due, true);
            if (NextOpen(item, due.AddDays(1)) is { } next)
            {
                task.Due = next;
                task.SetDone(false);
                return;
            }
        }
        task.SetDone(true);
    }

    /// <summary>
    /// Carries what happened in the calendar back to the task: a moved entry, a renamed one, or a
    /// reminder answered with "listo". Returns whether anything changed.
    /// </summary>
    public static bool FromEvent(TodoTask task, AgendaEvent item)
    {
        bool changed = false;
        if (item.Title.Length > 0 && item.Title != task.Title) { task.Title = item.Title; changed = true; }
        TimeOnly? at = item.AllDay ? null : TimeOnly.FromDateTime(item.Start);
        if (task.At != at) { task.At = at; changed = true; }

        if (item.Repeat == RepeatKind.None)
        {
            var day = DateOnly.FromDateTime(item.Start);
            if (task.Due != day) { task.Due = day; changed = true; }
            bool done = item.IsDone(day);
            if (task.Done != done) { task.SetDone(done); changed = true; }
            return changed;
        }

        // A series: the task points at its first occurrence still open.
        var from = task.Due ?? DateOnly.FromDateTime(item.Start);
        bool stillOpen = item.Starts(from, from).Any() && !item.IsDone(from) && !item.IsCancelled(from);
        if (stillOpen) return changed;
        if (NextOpen(item, from) is { } next)
        {
            if (task.Due != next) { task.Due = next; changed = true; }
            if (task.Done) { task.SetDone(false); changed = true; }
        }
        else if (!task.Done) { task.SetDone(true); changed = true; }
        return changed;
    }

    private static DateOnly? NextOpen(AgendaEvent item, DateOnly from) =>
        item.Starts(from, from.AddYears(2))
            .Where(day => !item.IsDone(day) && !item.IsCancelled(day))
            .Select(day => (DateOnly?)day)
            .FirstOrDefault();
}
