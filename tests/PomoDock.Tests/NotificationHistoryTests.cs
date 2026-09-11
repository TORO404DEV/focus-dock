using PomoDock.Core;

internal static class NotificationHistoryTests
{
    public static void Run(Action<string, Action> test, Action<double, double> equal, Action<bool, string> assert)
    {
        test("notification history is persistent state, not task completion", () =>
        {
            var task = new TodoTask { Title = "Sacar la basura", Due = new DateOnly(2026, 9, 9), At = new TimeOnly(12, 10) };
            var item = TodoSync.ToEvent(task, null, new AgendaEvent
            {
                Title = task.Title,
                Start = new DateTime(2026, 9, 9, 12, 10, 0),
                Reminders = [0]
            });
            var cue = new ReminderCue(item, new DateOnly(2026, 9, 9), item.Start, 0, item.Start);
            var inbox = new NotificationHistory();

            var notification = inbox.Add(cue, item.Start);
            equal(1, inbox.Items.Count);
            equal(1, inbox.Unread);
            assert(notification.TaskId == task.Id && notification.Title == task.Title, "el historial enlaza el recordatorio con su tarea");
            assert(!task.Done && !item.IsDone(cue.Series), "guardar la notificación no tacha la tarea");

            inbox.MarkAllRead();
            equal(0, inbox.Unread);
            assert(!task.Done, "leer la notificación tampoco tacha la tarea");
        });

        test("notification history keeps the newest two hundred entries", () =>
        {
            var inbox = new NotificationHistory();
            for (int index = 0; index < NotificationHistory.Capacity + 7; index++)
            {
                var item = new AgendaEvent { Title = $"Aviso {index}", Start = new DateTime(2026, 9, 9).AddMinutes(index), Reminders = [0] };
                item.Normalize();
                inbox.Add(new ReminderCue(item, DateOnly.FromDateTime(item.Start), item.Start, 0, item.Start), item.Start);
            }
            equal(NotificationHistory.Capacity, inbox.Items.Count);
            assert(inbox.Items[0].Title == $"Aviso {NotificationHistory.Capacity + 6}", "el más nuevo aparece primero");
        });
    }
}
