using System.Runtime.CompilerServices;
using System.Text.Json;
using PomoDock.Core;

namespace PomoDock.Web;

/// <summary>
/// The agenda lives in storage, not inside a widget payload. Closing a card, switching
/// workspace page or opening a second calendar never loses an appointment, and every open
/// calendar shows the same events.
/// </summary>
internal sealed class AgendaStore
{
    private const string StateKey = WebStore.AgendaKey;
    private static readonly ConditionalWeakTable<WebStore, AgendaStore> shared = new();
    private readonly WebStore store;
    public AgendaBook Book { get; }
    /// <summary>Raised after every visible change so every open calendar redraws.</summary>
    public event Action? Changed;

    public static AgendaStore For(WebStore store) => shared.GetValue(store, key => new AgendaStore(key));

    private AgendaStore(WebStore store)
    {
        this.store = store;
        AgendaBook? book = null;
        try { book = store.Read<AgendaBook>(StateKey); }
        catch (JsonException) { }
        Book = book ?? new AgendaBook();
        Book.Normalize();
    }

    public void Save()
    {
        Book.Normalize();
        Write();
        Changed?.Invoke();
    }

    /// <summary>
    /// Persists reminder bookkeeping without announcing a change: delivered and postponed cues
    /// must not rebuild every calendar on screen while the user is typing in one.
    /// </summary>
    public void Track() => Write();

    private void Write()
    {
        Book.UpdatedUtc = DateTime.UtcNow;
        store.Write(StateKey, Book);
    }

    /// <summary>Folds a backup into the live book, so every open calendar sees it at once.</summary>
    public int Merge(AgendaBook? incoming)
    {
        if (incoming is null) return 0;
        int added = Book.MergeFrom(incoming);
        Save();
        return added;
    }

    public void Add(AgendaEvent item)
    {
        Book.Events.Add(item);
        Save();
    }

    public void Replace(AgendaEvent item)
    {
        int index = Book.Events.FindIndex(existing => existing.Id == item.Id);
        if (index < 0) Book.Events.Add(item); else Book.Events[index] = item;
        Save();
    }

    public void Remove(AgendaEvent item)
    {
        Book.Events.RemoveAll(existing => existing.Id == item.Id);
        Save();
    }
}

/// <summary>
/// The task list lives in storage, not inside a card: closing a To Do widget never takes
/// a task with it. Dated tasks are mirrored as calendar entries, so they show in the calendar
/// and ring through its reminders, and whatever the calendar does comes back to the list.
/// </summary>
internal sealed class TodoStore
{
    private const string StateKey = "todo";
    private static readonly ConditionalWeakTable<WebStore, TodoStore> shared = new();
    private readonly WebStore store;
    private readonly AgendaStore agenda;
    /// <summary>What a freshly typed line said beyond the task, used once to shape its entry.</summary>
    private readonly Dictionary<Guid, AgendaEvent> drafts = [];
    private bool syncing;

    public TodoBook Book { get; }
    /// <summary>Raised after every change, from a card or from the calendar.</summary>
    public event Action? Changed;

    public static TodoStore For(WebStore store) => shared.GetValue(store, key => new TodoStore(key));

    private TodoStore(WebStore store)
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

/// <summary>
/// Keeps the note history in storage. Every note card reports its latest text here, and
/// closing a card only marks its note as closed, so nothing written is lost with the card.
/// </summary>
internal sealed class NoteArchiveStore
{
    private static readonly ConditionalWeakTable<WebStore, NoteArchiveStore> shared = new();
    private readonly WebStore store;
    public NoteArchive Archive { get; }
    /// <summary>Raised after every write so an open history window stays current.</summary>
    public event Action? Changed;

    public static NoteArchiveStore For(WebStore store) => shared.GetValue(store, key => new NoteArchiveStore(key));

    private NoteArchiveStore(WebStore store)
    {
        this.store = store;
        NoteArchive? archive = null;
        try { archive = store.Read<NoteArchive>(WebStore.NotesKey); }
        catch (JsonException) { }
        Archive = archive ?? new NoteArchive();
        Archive.Normalize();
    }

    private void Save()
    {
        Archive.Normalize();
        store.Write(WebStore.NotesKey, Archive);
        Changed?.Invoke();
    }

    /// <summary>Records what a note card holds right now. Nothing is written when nothing changed.</summary>
    public void Track(WidgetConfig config)
    {
        if (Absorb(config)) Save();
    }

    /// <summary>The card is going away: its last words stay in the history, marked as closed.</summary>
    public void Close(WidgetConfig config)
    {
        Absorb(config);
        Archive.Close(config.Id, DateTime.UtcNow);
        Save();
    }

    public void Forget(Guid id)
    {
        if (Archive.Forget(id)) Save();
    }

    /// <summary>
    /// Brings the history up to date with every note on every page and closes the entries no
    /// page holds any more.
    /// </summary>
    public void Sync(Settings settings)
    {
        var notes = settings.WorkspacePages.SelectMany(page => page.Widgets).Where(widget => widget.Kind == "notes").ToList();
        bool changed = false;
        foreach (var note in notes) changed |= Absorb(note);
        changed |= Archive.Reconcile(notes.Select(note => note.Id).ToHashSet(), DateTime.UtcNow) > 0;
        if (changed) Save();
    }

    private bool Absorb(WidgetConfig config)
    {
        var existing = Archive.Find(config.Id);
        if (existing is { IsOpen: true } && existing.Payload == config.Value) return false;
        var (text, color, updated) = Read(config.Value);
        // A fresh, empty note has nothing to remember yet.
        if (existing is null && text.Trim().Length == 0) return false;
        Archive.Track(config.Id, text, color, config.Value, updated ?? existing?.UpdatedUtc ?? DateTime.UtcNow);
        return true;
    }

    /// <summary>A note card's plain text — one line per paragraph, with its checklist marks.</summary>
    public static (string Text, string Color, DateTime? Updated) Read(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return ("", "", null);
        NotesWidgetData? data = null;
        try { data = JsonSerializer.Deserialize<NotesWidgetData>(payload); }
        catch (JsonException) { }
        // Anything that is not a rich note is a plain-text note written by an older version.
        if (data is null || data.Version <= 0) return (payload.Trim(), "", null);
        var blocks = RichText.FromXaml(data.DocumentXaml, data.Checklists);
        return (string.IsNullOrWhiteSpace(data.DocumentXaml) ? "" : RichText.PlainText(blocks), data.Color ?? "", data.UpdatedUtc);
    }
}
