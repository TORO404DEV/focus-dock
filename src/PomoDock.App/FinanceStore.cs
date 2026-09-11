using System.Runtime.CompilerServices;
using System.Text.Json;
using PomoDock.Core;

namespace PomoDock.App;

internal sealed class FinanceStore
{
    public const string StateKey = Store.FinanceKey;
    private static readonly ConditionalWeakTable<Store, FinanceStore> shared = new();
    private readonly Store store;
    public FinanceBook Book { get; }
    public event Action? Changed;

    public static FinanceStore For(Store store) => shared.GetValue(store, key => new FinanceStore(key));

    private FinanceStore(Store store)
    {
        this.store = store;
        FinanceBook? book = null;
        try { book = store.Read<FinanceBook>(StateKey); }
        catch (JsonException) { }
        Book = book ?? new FinanceBook();
        Book.Normalize();
    }

    public void Save()
    {
        Book.Normalize();
        Book.UpdatedUtc = DateTime.UtcNow;
        store.Write(StateKey, Book);
        Changed?.Invoke();
    }

    public int Merge(FinanceBook? incoming)
    {
        if (incoming is null) return 0;
        int added = Book.MergeFrom(incoming);
        Save();
        return added;
    }

    public MoneyEntry AddEntry(MoneyFlow flow, decimal amount, string title, DateOnly date, string category = "", string notes = "")
    {
        var entry = Book.Add(flow, amount, title, date, category, notes);
        Save();
        return entry;
    }

    public RecurringMoney AddRecurring(string title, decimal amount, bool subscription, string category = "", MoneyFlow flow = MoneyFlow.Expense, int day = 1, MoneyRepeat repeat = MoneyRepeat.Monthly)
    {
        var item = Book.AddRecurring(title, amount, subscription, category, flow, day, repeat);
        Save();
        return item;
    }

    public void SetCurrency(string code)
    {
        Book.Currency = string.IsNullOrWhiteSpace(code) ? "USD" : code.Trim().ToUpperInvariant();
        Save();
    }

    public void RemoveEntry(MoneyEntry entry)
    {
        Book.RemoveEntry(entry.Id);
        Save();
    }

    public void Archive(RecurringMoney item)
    {
        item.Archived = true;
        Save();
    }

    public MoneyEntry? MarkPaid(RecurringMoney item, DateOnly month)
    {
        var entry = Book.MarkPaid(item, month);
        if (entry is not null) Save();
        return entry;
    }
}
