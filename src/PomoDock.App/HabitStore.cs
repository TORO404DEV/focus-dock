using System.Runtime.CompilerServices;
using System.Text.Json;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// The habit book lives in the database, not inside the widget payload. Closing a card,
/// switching workspace pages or opening a second habit widget never loses a single day.
/// </summary>
internal sealed class HabitStore
{
    private const string StateKey = "habits";
    private static readonly ConditionalWeakTable<Store, HabitStore> shared = new();
    private readonly Store store;
    public HabitBook Book { get; }
    /// <summary>Raised after every write so every open habit widget stays in sync.</summary>
    public event Action? Changed;

    public static HabitStore For(Store store) => shared.GetValue(store, key => new HabitStore(key));

    private HabitStore(Store store)
    {
        this.store = store;
        HabitBook? book = null;
        try { book = store.Read<HabitBook>(StateKey); }
        catch (JsonException) { }
        Book = book ?? new HabitBook();
        Book.Normalize();
        Seal();
    }

    /// <summary>
    /// Records awards a book already deserved before it was ever scored, so history imported
    /// from an old widget is not announced as if it had just happened.
    /// </summary>
    private void Seal()
    {
        if (Book.Awards.Count > 0 || !Book.Habits.Any(habit => habit.Log.Count > 0)) return;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var progress = Book.Habits.ToDictionary(habit => habit.Id, habit => HabitProgress.For(habit, today));
        foreach (var award in HabitStats.Compute(Book, progress, today).Awards) Book.Awards.Add(award.Id);
        store.Write(StateKey, Book);
    }

    public void Save()
    {
        Book.Normalize();
        Book.UpdatedUtc = DateTime.UtcNow;
        store.Write(StateKey, Book);
        Changed?.Invoke();
    }

    public Habit Add(string name)
    {
        var habit = new Habit { Name = name, Order = Book.Habits.Count == 0 ? 0 : Book.Habits.Max(item => item.Order) + 1 };
        Book.Habits.Add(habit);
        return habit;
    }

    /// <summary>
    /// Folds a pre-database widget payload into the shared book, once per widget. The old
    /// payload is dropped afterwards so the same days can never be imported twice.
    /// </summary>
    public bool ImportLegacy(WidgetConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Value)) return false;
        var origin = config.Id.ToString();
        var payload = config.Value;
        config.Value = "";
        if (Book.Imported.Contains(origin)) return true;
        Book.Imported.Add(origin);
        HabitWidgetData? legacy = null;
        try { legacy = JsonSerializer.Deserialize<HabitWidgetData>(payload); }
        catch (JsonException) { }
        Book.Absorb(legacy);
        Seal();
        Save();
        return true;
    }
}
