using System.Runtime.CompilerServices;
using System.Text.Json;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// The task list lives in the database, not inside a card: closing a To Do widget never takes
/// a task with it. Dated tasks are mirrored as calendar entries, so they show in the calendar
/// and ring through its reminders, and whatever the calendar does comes back to the list.
/// </summary>
internal sealed class TodoStore
{
    private const string StateKey = "todo";
    private static readonly ConditionalWeakTable<Store, TodoStore> shared = new();
    private readonly Store store;
    private readonly AgendaStore agenda;
    /// <summary>What a freshly typed line said beyond the task, used once to shape its entry.</summary>
    private readonly Dictionary<Guid, AgendaEvent> drafts = [];
    private bool syncing;

    public TodoBook Book { get; }
    /// <summary>Raised after every change, from a card or from the calendar.</summary>
    public event Action? Changed;

    public static TodoStore For(Store store) => shared.GetValue(store, key => new TodoStore(key));

    private TodoStore(Store store)
    {
        this.store = store;
        agenda = AgendaStore.For(store);
        TodoBook? book = null;
        try { book = store.Read<TodoBook>(StateKey); }
        catch (JsonException) { }
        Book = book ?? new TodoBook();
        Book.Normalize();
        agenda.Changed += FromAgenda;
        // Whatever the calendar did while no list was open: a reminder answered, an entry moved.
        if (Pull()) Write();
    }

    public void Save()
    {
        Book.Normalize();
        Push();
        Write();
        Changed?.Invoke();
    }

    private void Write() => store.Write(StateKey, Book);

    public TodoTask? Add(string text, DateTime now)
    {
        var reading = TodoBook.Read(text, now);
        if (reading is null) return null;
        var task = Book.Insert(reading.Task);
        if (reading.Draft is { } draft) drafts[task.Id] = draft;
        Save();
        return task;
    }

    public void SetDone(TodoTask task, bool done)
    {
        if (done) TodoSync.Complete(task, Linked(task));
        else task.SetDone(false);
        Save();
    }

    public AgendaEvent? Linked(TodoTask task) =>
        task.EventId is { } id ? agenda.Book.Events.FirstOrDefault(item => item.Id == id) : null;

    /// <summary>
    /// Folds a card's old, card-held list into the shared one — once per card — and returns the
    /// filter that card was showing. Afterwards the card keeps only how it looks.
    /// </summary>
    public string Adopt(WidgetConfig config)
    {
        string filter = "all";
        if (!string.IsNullOrWhiteSpace(config.Value))
        {
            try
            {
                using var document = JsonDocument.Parse(config.Value);
                var root = document.RootElement;
                if (root.TryGetProperty("Filter", out var stored) && stored.ValueKind == JsonValueKind.String) filter = stored.GetString() ?? "all";
                string origin = config.Id.ToString();
                if (root.TryGetProperty("Items", out _) && !Book.Imported.Contains(origin))
                {
                    var legacy = JsonSerializer.Deserialize<TodoBook>(config.Value);
                    var known = Book.Items.Select(task => task.Id).ToHashSet();
                    int offset = Book.Items.Count == 0 ? 0 : Book.Items.Max(task => task.Order) + 1;
                    foreach (var task in legacy?.Items ?? [])
                    {
                        if (!known.Add(task.Id)) continue;
                        task.Order += offset;
                        Book.Items.Add(task);
                    }
                    Book.Imported.Add(origin);
                    Save();
                }
            }
            catch (JsonException) { }
        }
        if (!TodoBook.Filters.Contains(filter)) filter = "all";
        config.Value = View(filter);
        return filter;
    }

    public static string View(string filter) => JsonSerializer.Serialize(new { Filter = filter });

    /// <summary>Writes every dated task into the calendar and drops entries whose task is gone.</summary>
    private void Push()
    {
        syncing = true;
        try
        {
            bool touched = false;
            foreach (var task in Book.Items)
            {
                var linked = Linked(task);
                if (task.Due is null)
                {
                    if (linked is not null) { agenda.Book.Events.Remove(linked); touched = true; }
                    task.EventId = null;
                    drafts.Remove(task.Id);
                    continue;
                }
                drafts.Remove(task.Id, out var draft);
                string? before = linked is null ? null : JsonSerializer.Serialize(linked);
                var item = TodoSync.ToEvent(task, linked, linked is null ? draft : null);
                if (linked is null)
                {
                    agenda.Book.Events.Add(item);
                    task.EventId = item.Id;
                    touched = true;
                }
                else if (before != JsonSerializer.Serialize(item)) touched = true;
            }
            var tasks = Book.Items.Select(task => task.Id).ToHashSet();
            touched |= agenda.Book.Events.RemoveAll(item => item.TaskId is { } owner && !tasks.Contains(owner)) > 0;
            if (touched) agenda.Save();
        }
        finally { syncing = false; }
    }

    /// <summary>Reads calendar changes back into the tasks. Returns whether any task changed.</summary>
    private bool Pull()
    {
        bool changed = false;
        foreach (var task in Book.Items.Where(task => task.EventId is not null))
        {
            if (Linked(task) is not { } item)
            {
                // Removed from the calendar: the task stays, off the calendar, instead of coming back.
                task.EventId = null;
                task.Due = null;
                task.At = null;
                changed = true;
                continue;
            }
            changed |= TodoSync.FromEvent(task, item);
        }
        return changed;
    }

    private void FromAgenda()
    {
        if (syncing || !Pull()) return;
        Book.Normalize();
        Write();
        Changed?.Invoke();
    }
}
