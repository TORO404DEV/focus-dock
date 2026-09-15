using PomoDock.Core;
using System.Globalization;

internal static class FinanceTests
{
    public static void Run(Action<string, Action> test, Action<double, double> equal, Action<bool, string> assert)
    {
        var today = new DateOnly(2026, 9, 11);

        test("finance quick add reads income, expenses, fixed costs and subscriptions", () =>
        {
            var salary = FinanceQuickAdd.Parse("+2500 salario", today) as MoneyEntry;
            assert(salary is { Flow: MoneyFlow.Income, Title: "salario" } && salary.Amount == 2500m, "income from +amount");
            var coffee = FinanceQuickAdd.Parse("-4.50 cafe", today) as MoneyEntry;
            assert(coffee is { Flow: MoneyFlow.Expense } && coffee.Amount == 4.50m, "expense from -amount");
            var rent = FinanceQuickAdd.Parse("fijo renta 800", today) as RecurringMoney;
            assert(rent is { Subscription: false, Flow: MoneyFlow.Expense } && rent.Amount == 800m && rent.Category == "rent", "fixed monthly rent");
            var cursor = FinanceQuickAdd.Parse("sub cursor 20", today) as RecurringMoney;
            assert(cursor is { Subscription: true, Category: "ai" } && cursor.Amount == 20m, "AI subscription");
            var domain = FinanceQuickAdd.Parse("anual dominio 12", today) as RecurringMoney;
            assert(domain is { Repeat: MoneyRepeat.Yearly, Category: "hosting" } && domain.Amount == 12m, "yearly hosting");
        });

        test("a finance month projects unpaid subscriptions without double-counting paid ones", () =>
        {
            var book = new FinanceBook { Currency = "USD" };
            book.Add(MoneyFlow.Income, 3000, "Sueldo", new DateOnly(2026, 9, 1), "salary");
            book.Add(MoneyFlow.Expense, 40, "Comida", new DateOnly(2026, 9, 4), "food");
            var hosting = book.AddRecurring("VPS", 12, subscription: true, category: "hosting", day: 5, start: new DateOnly(2026, 8, 1));
            book.AddRecurring("Renta", 700, subscription: false, category: "rent", day: 1, start: new DateOnly(2026, 1, 1));
            var open = book.Month(today);
            equal(3000, (double)open.Recorded.Income);
            equal(40, (double)open.Recorded.Expense);
            equal(2960, (double)open.Recorded.Net);
            assert(open.Subscriptions.Count == 1 && !open.Subscriptions[0].Recorded, "hosting is due and unpaid");
            assert(open.Fixed.Count == 1 && open.Fixed[0].Item.Title == "Renta", "rent is a fixed cost");
            equal(2248, (double)open.ProjectedNet);
            book.MarkPaid(hosting, today);
            var paid = book.Month(today);
            assert(paid.Subscriptions.Single().Recorded, "marking paid writes one linked entry");
            equal(52, (double)paid.Recorded.Expense);
            equal(2248, (double)paid.ProjectedNet);
            assert(book.MarkPaid(hosting, today) is null, "a subscription is not charged twice in the same month");
        });

        test("archived or future recurring items stay out of this month", () =>
        {
            var book = new FinanceBook();
            book.AddRecurring("Old Netflix", 15, true, "media", start: new DateOnly(2025, 1, 1)).Archived = true;
            book.AddRecurring("Next tool", 30, true, "software", start: new DateOnly(2026, 11, 1));
            var month = book.Month(today);
            assert(month.Subscriptions.Count == 0 && month.RecurringExpense == 0, "only live recurring costs count");
        });

        test("quick add reads usd and mxn on the same line", () =>
        {
            var pesos = FinanceQuickAdd.Parse("+25000 mxn salario", today) as MoneyEntry;
            assert(pesos is { Flow: MoneyFlow.Income, Currency: "MXN" } && pesos.Amount == 25000m, "mxn salary");
            var dollars = FinanceQuickAdd.Parse("-20 usd chatgpt", today) as MoneyEntry;
            assert(dollars is { Flow: MoneyFlow.Expense, Currency: "USD", Category: "ai" } && dollars.Amount == 20m, "usd chatgpt");
            var fallback = FinanceQuickAdd.Parse("-400 uber", today, "MXN") as MoneyEntry;
            assert(fallback is { Currency: "MXN" } && fallback.Amount == 400m, "draft currency when the line is silent");
        });

        test("a mixed book converts totals into the display currency", () =>
        {
            var book = new FinanceBook { Currency = "MXN", UsdToMxn = 20m };
            book.Add(MoneyFlow.Income, 20000, "Sueldo", new DateOnly(2026, 9, 1), "salary", currency: "MXN");
            book.Add(MoneyFlow.Expense, 20, "ChatGPT", new DateOnly(2026, 9, 2), "ai", currency: "USD");
            var mxn = book.Month(today);
            equal(20000, (double)mxn.Recorded.Income);
            equal(400, (double)mxn.Recorded.Expense);
            equal(19600, (double)mxn.Recorded.Net);
            book.Currency = "USD";
            var usd = book.Month(today);
            equal(1000, (double)usd.Recorded.Income);
            equal(20, (double)usd.Recorded.Expense);
            equal(980, (double)usd.Recorded.Net);
        });

        test("surplus streak counts consecutive green months", () =>
        {
            var book = new FinanceBook { Currency = "USD" };
            book.Add(MoneyFlow.Income, 100, "Jul", new DateOnly(2026, 7, 1), "salary");
            book.Add(MoneyFlow.Income, 100, "Ago", new DateOnly(2026, 8, 1), "salary");
            book.Add(MoneyFlow.Expense, 40, "Ago cafe", new DateOnly(2026, 8, 4), "food");
            book.Add(MoneyFlow.Expense, 200, "Sep", new DateOnly(2026, 9, 2), "food");
            assert(book.SurplusStreak(new DateOnly(2026, 8, 15)) == 2, "july and august were green");
            assert(book.SurplusStreak(today) == 0, "september is red so the streak breaks");
        });
    }
}
