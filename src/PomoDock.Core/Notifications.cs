namespace PomoDock.Core;

/// <summary>One reminder that reached the user. It is history, never the task's completion state.</summary>
public sealed class ReminderNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CueKey { get; set; } = "";
    public Guid EventId { get; set; }
    public Guid? TaskId { get; set; }
    public string Title { get; set; } = "";
    public DateTime ScheduledLocal { get; set; }
    public DateTime DeliveredLocal { get; set; }
    public bool Read { get; set; }

    public void Normalize()
    {
        Title = (Title ?? "").Trim();
        CueKey = (CueKey ?? "").Trim();
        if (DeliveredLocal == default) DeliveredLocal = DateTime.Now;
        if (ScheduledLocal == default) ScheduledLocal = DeliveredLocal;
    }
}

/// <summary>A bounded, persistent inbox of reminders shown by PomoDock.</summary>
public sealed class NotificationHistory
{
    public const int Capacity = 200;
    public int Version { get; set; } = 1;
    public List<ReminderNotification> Items { get; set; } = [];

    public int Unread => Items.Count(item => !item.Read);

    public void Normalize()
    {
        Items ??= [];
        foreach (var item in Items) item.Normalize();
        Items.RemoveAll(item => item.Title.Length == 0);
        Items = Items.OrderByDescending(item => item.DeliveredLocal).Take(Capacity).ToList();
    }

    public ReminderNotification Add(ReminderCue cue, DateTime delivered)
    {
        var item = new ReminderNotification
        {
            CueKey = cue.Key,
            EventId = cue.Event.Id,
            TaskId = cue.Event.TaskId,
            Title = cue.Event.Title,
            ScheduledLocal = cue.Start,
            DeliveredLocal = delivered
        };
        Items.Insert(0, item);
        Normalize();
        return item;
    }

    public void MarkAllRead()
    {
        foreach (var item in Items) item.Read = true;
    }

    public bool MarkRead(Guid id)
    {
        var item = Items.FirstOrDefault(entry => entry.Id == id);
        if (item is null) return false;
        item.Read = true;
        return true;
    }
}
