using System.Text.Json;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>Persists the notification inbox independently from agenda delivery bookkeeping.</summary>
internal sealed class NotificationStore
{
    private const string StateKey = "notification-history";
    private readonly Store store;

    public NotificationHistory History { get; }
    public event Action? Changed;

    public NotificationStore(Store store)
    {
        this.store = store;
        NotificationHistory? history = null;
        try { history = store.Read<NotificationHistory>(StateKey); }
        catch (JsonException) { }
        History = history ?? new NotificationHistory();
        History.Normalize();
    }

    public ReminderNotification Add(ReminderCue cue, DateTime delivered)
    {
        var item = History.Add(cue, delivered);
        Save();
        return item;
    }

    public void MarkAllRead()
    {
        if (History.Unread == 0) return;
        History.MarkAllRead();
        Save();
    }

    public bool MarkRead(Guid id)
    {
        if (!History.MarkRead(id)) return false;
        Save();
        return true;
    }

    private void Save()
    {
        History.Normalize();
        store.Write(StateKey, History);
        Changed?.Invoke();
    }
}
