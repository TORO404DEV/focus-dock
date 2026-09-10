using System.Runtime.CompilerServices;
using System.Text.Json;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// The agenda lives in the database, not inside a widget payload. Closing a card, switching
/// workspace page or opening a second calendar never loses an appointment, and every open
/// calendar shows the same events.
/// </summary>
internal sealed class AgendaStore
{
    private const string StateKey = "agenda";
    private static readonly ConditionalWeakTable<Store, AgendaStore> shared = new();
    private readonly Store store;
    public AgendaBook Book { get; }
    /// <summary>Raised after every visible change so every open calendar redraws.</summary>
    public event Action? Changed;

    public static AgendaStore For(Store store) => shared.GetValue(store, key => new AgendaStore(key));

    private AgendaStore(Store store)
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
