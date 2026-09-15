using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PomoDock.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MoneyFlow { Income, Expense }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MoneyRepeat { Monthly, Yearly }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FinanceSpan { Week, Month, Quarter, Year }

public static class MoneyFx
{
    public const string Usd = "USD";
    public const string Mxn = "MXN";
    public const decimal FallbackUsdToMxn = 18.00m;
    public static readonly string[] Supported = [Usd, Mxn];

    public static string Normalize(string? code, string fallback = Usd)
    {
        string value = (code ?? "").Trim().ToUpperInvariant();
        if (value is "USD" or "US" or "DLL" or "DLLS") return Usd;
        if (value is "MXN" or "MX" or "PESO" or "PESOS") return Mxn;
        return fallback is Usd or Mxn ? fallback : Usd;
    }

    public static bool IsSupported(string? code)
    {
        string value = (code ?? "").Trim().ToUpperInvariant();
        return value is "USD" or "US" or "DLL" or "DLLS" or "MXN" or "MX" or "PESO" or "PESOS";
    }
}

/// <summary>A recorded movement. Amount is always positive; <see cref="Flow"/> says which way it goes.</summary>
public sealed class MoneyEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public MoneyFlow Flow { get; set; } = MoneyFlow.Expense;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string Title { get; set; } = "";
    public string Category { get; set; } = "other";
    public string Notes { get; set; } = "";
    public Guid? RecurringId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public decimal Signed => Flow == MoneyFlow.Income ? Amount : -Amount;

    public void Normalize()
    {
        Title = (Title ?? "").Trim();
        Category = FinanceBook.NormalizeCategory(Category, Flow);
        Notes = (Notes ?? "").Trim();
        Currency = string.IsNullOrWhiteSpace(Currency) ? "" : MoneyFx.Normalize(Currency);
        if (Amount < 0) { Amount = Math.Abs(Amount); Flow = MoneyFlow.Expense; }
        Amount = decimal.Round(Amount, 2, MidpointRounding.AwayFromZero);
    }
}

/// <summary>Rent, utilities, Cursor, hosting: expected every month (or year) until archived.</summary>
public sealed class RecurringMoney
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public MoneyFlow Flow { get; set; } = MoneyFlow.Expense;
    public string Category { get; set; } = "software";
    public MoneyRepeat Repeat { get; set; } = MoneyRepeat.Monthly;
    public int DayOfMonth { get; set; } = 1;
    public DateOnly Start { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public DateOnly? Until { get; set; }
    /// <summary>True for AI, hosting, software, media. False for rent and other fixed costs.</summary>
    public bool Subscription { get; set; }
    public bool Archived { get; set; }

    public void Normalize()
    {
        Title = (Title ?? "").Trim();
        Category = FinanceBook.NormalizeCategory(Category, Flow);
        Currency = string.IsNullOrWhiteSpace(Currency) ? "" : MoneyFx.Normalize(Currency);
        if (Amount < 0) { Amount = Math.Abs(Amount); Flow = MoneyFlow.Expense; }
        Amount = decimal.Round(Amount, 2, MidpointRounding.AwayFromZero);
        DayOfMonth = Math.Clamp(DayOfMonth, 1, 28);
        if (Until is { } until && until < Start) Until = Start;
    }

    public bool ActiveOn(DateOnly month)
    {
        if (Archived) return false;
        var first = new DateOnly(month.Year, month.Month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        if (last < Start) return false;
        if (Until is { } until && until < first) return false;
        if (Repeat == MoneyRepeat.Yearly && month.Month != Start.Month) return false;
        return true;
    }

    public DateOnly DueOn(DateOnly month)
    {
        int day = Math.Min(DayOfMonth, DateTime.DaysInMonth(month.Year, month.Month));
        var due = new DateOnly(month.Year, month.Month, day);
        return due < Start ? Start : due;
    }
}

public sealed record MoneyMonthTotal(decimal Income, decimal Expense, decimal Net, int Moves);
public sealed record RecurringDue(RecurringMoney Item, DateOnly Due, bool Recorded);
public sealed record FinanceBar(string Label, decimal Income, decimal Expense);
public sealed record FinanceSlice(string Category, decimal Amount, double Share);
public sealed record FinanceMonth(
    DateOnly Month,
    MoneyMonthTotal Recorded,
    IReadOnlyList<MoneyEntry> Entries,
    IReadOnlyList<RecurringDue> Fixed,
    IReadOnlyList<RecurringDue> Subscriptions,
    decimal RecurringExpense,
    decimal ProjectedNet);

public sealed class FinanceBook
{
    public int Version { get; set; } = 1;
    /// <summary>Currency used to show totals. Each movement keeps its own <see cref="MoneyEntry.Currency"/>.</summary>
    public string Currency { get; set; } = MoneyFx.Usd;
    /// <summary>How many MXN one USD buys. Used to roll mixed books into the display currency.</summary>
    public decimal UsdToMxn { get; set; } = MoneyFx.FallbackUsdToMxn;
    public DateTime? RateUpdatedUtc { get; set; }
    public bool RateManual { get; set; }
    public List<MoneyEntry> Entries { get; set; } = [];
    public List<RecurringMoney> Recurring { get; set; } = [];
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public static readonly string[] IncomeCategories = ["salary", "freelance", "other"];
    public static readonly string[] ExpenseCategories = ["food", "transport", "rent", "utilities", "health", "software", "ai", "hosting", "media", "other"];

    public void Normalize()
    {
        Entries ??= [];
        Recurring ??= [];
        Currency = MoneyFx.Normalize(Currency);
        if (UsdToMxn <= 0) UsdToMxn = MoneyFx.FallbackUsdToMxn;
        UsdToMxn = decimal.Round(UsdToMxn, 4, MidpointRounding.AwayFromZero);
        foreach (var entry in Entries)
        {
            entry.Normalize();
            if (entry.Currency.Length == 0) entry.Currency = Currency;
        }
        foreach (var item in Recurring)
        {
            item.Normalize();
            if (item.Currency.Length == 0) item.Currency = Currency;
        }
        Entries.RemoveAll(entry => entry.Title.Length == 0 || entry.Amount <= 0);
        Recurring.RemoveAll(item => item.Title.Length == 0 || item.Amount <= 0);
    }

    public IEnumerable<RecurringMoney> Active => Recurring.Where(item => !item.Archived).OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase);

    public decimal Convert(decimal amount, string from, string to)
    {
        from = MoneyFx.Normalize(from, Currency);
        to = MoneyFx.Normalize(to, Currency);
        if (from == to) return decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
        decimal rate = UsdToMxn > 0 ? UsdToMxn : MoneyFx.FallbackUsdToMxn;
        decimal usd = from == MoneyFx.Usd ? amount : amount / rate;
        decimal converted = to == MoneyFx.Usd ? usd : usd * rate;
        return decimal.Round(converted, 2, MidpointRounding.AwayFromZero);
    }

    public decimal ToDisplay(decimal amount, string from) => Convert(amount, from, Currency);
    public decimal ToDisplay(MoneyEntry entry) => ToDisplay(entry.Amount, entry.Currency);
    public decimal ToDisplay(RecurringMoney item) => ToDisplay(item.Amount, item.Currency);

    public MoneyEntry Add(MoneyFlow flow, decimal amount, string title, DateOnly date, string category = "", string notes = "", Guid? recurringId = null, string currency = "")
    {
        var entry = new MoneyEntry
        {
            Flow = flow, Amount = amount, Title = title, Date = date, Category = category, Notes = notes,
            RecurringId = recurringId, Currency = MoneyFx.Normalize(currency, Currency)
        };
        entry.Normalize();
        if (entry.Title.Length == 0 || entry.Amount <= 0) throw new ArgumentException("A money entry needs a title and an amount.");
        if (entry.Currency.Length == 0) entry.Currency = Currency;
        Entries.Add(entry);
        return entry;
    }

    public RecurringMoney AddRecurring(string title, decimal amount, bool subscription, string category = "", MoneyFlow flow = MoneyFlow.Expense, int day = 1, MoneyRepeat repeat = MoneyRepeat.Monthly, DateOnly? start = null, string currency = "")
    {
        var item = new RecurringMoney
        {
            Title = title, Amount = amount, Subscription = subscription, Category = category, Flow = flow,
            DayOfMonth = day, Repeat = repeat, Start = start ?? DateOnly.FromDateTime(DateTime.Today),
            Currency = MoneyFx.Normalize(currency, Currency)
        };
        item.Normalize();
        if (item.Currency.Length == 0) item.Currency = Currency;
        Recurring.Add(item);
        return item;
    }

    public bool RemoveEntry(Guid id) => Entries.RemoveAll(entry => entry.Id == id) > 0;

    public RecurringMoney? RecurringBy(string query)
    {
        var exact = Active.Where(item => string.Equals(item.Title, query, StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = exact.Count > 0 ? exact : Active.Where(item => item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    public FinanceMonth Month(DateOnly anyDayInMonth)
    {
        var month = new DateOnly(anyDayInMonth.Year, anyDayInMonth.Month, 1);
        var next = month.AddMonths(1);
        var recorded = Entries.Where(entry => entry.Date >= month && entry.Date < next).OrderByDescending(entry => entry.Date).ThenByDescending(entry => entry.CreatedUtc).ToList();
        decimal income = recorded.Where(entry => entry.Flow == MoneyFlow.Income).Sum(ToDisplay);
        decimal expense = recorded.Where(entry => entry.Flow == MoneyFlow.Expense).Sum(ToDisplay);
        var due = Recurring.Where(item => item.ActiveOn(month))
            .Select(item => new RecurringDue(item, item.DueOn(month), recorded.Any(entry => entry.RecurringId == item.Id)))
            .OrderBy(item => item.Item.Subscription ? 1 : 0)
            .ThenBy(item => item.Due)
            .ToList();
        var fixedCosts = due.Where(item => !item.Item.Subscription && item.Item.Flow == MoneyFlow.Expense).ToList();
        var subs = due.Where(item => item.Item.Subscription && item.Item.Flow == MoneyFlow.Expense).ToList();
        decimal recurringExpense = due.Where(item => item.Item.Flow == MoneyFlow.Expense).Sum(item => ToDisplay(item.Item));
        decimal unpaid = due.Where(item => !item.Recorded && item.Item.Flow == MoneyFlow.Expense).Sum(item => ToDisplay(item.Item));
        decimal unpaidIncome = due.Where(item => !item.Recorded && item.Item.Flow == MoneyFlow.Income).Sum(item => ToDisplay(item.Item));
        return new FinanceMonth(
            month,
            new MoneyMonthTotal(income, expense, income - expense, recorded.Count),
            recorded,
            fixedCosts,
            subs,
            recurringExpense,
            income + unpaidIncome - expense - unpaid);
    }

    public (DateOnly Start, DateOnly End) SpanRange(FinanceSpan span, DateOnly cursor)
    {
        if (span == FinanceSpan.Week)
        {
            int delta = ((int)cursor.DayOfWeek + 6) % 7;
            var start = cursor.AddDays(-delta);
            return (start, start.AddDays(6));
        }
        if (span == FinanceSpan.Quarter)
        {
            int quarter = (cursor.Month - 1) / 3;
            var start = new DateOnly(cursor.Year, quarter * 3 + 1, 1);
            return (start, start.AddMonths(3).AddDays(-1));
        }
        if (span == FinanceSpan.Year)
            return (new DateOnly(cursor.Year, 1, 1), new DateOnly(cursor.Year, 12, 31));
        var month = new DateOnly(cursor.Year, cursor.Month, 1);
        return (month, month.AddMonths(1).AddDays(-1));
    }

    public DateOnly Shift(FinanceSpan span, DateOnly cursor, int steps) => span switch
    {
        FinanceSpan.Week => cursor.AddDays(7 * steps),
        FinanceSpan.Quarter => cursor.AddMonths(3 * steps),
        FinanceSpan.Year => cursor.AddYears(steps),
        _ => cursor.AddMonths(steps)
    };

    public IReadOnlyList<FinanceBar> Series(FinanceSpan span, DateOnly cursor)
    {
        var (start, end) = SpanRange(span, cursor);
        if (span == FinanceSpan.Week)
            return EachDay(start, end).Select(day => Bar(day, day, day.ToString("dd", CultureInfo.InvariantCulture))).ToList();
        if (span == FinanceSpan.Month)
            return WeeksInside(start, end);
        if (span == FinanceSpan.Quarter)
            return MonthsInside(start, 3);
        return MonthsInside(start, 12);
    }

    public IReadOnlyList<FinanceSlice> Categories(DateOnly start, DateOnly end)
    {
        var spent = Entries.Where(entry => entry.Flow == MoneyFlow.Expense && entry.Date >= start && entry.Date <= end)
            .GroupBy(entry => entry.Category)
            .Select(group => new FinanceSlice(group.Key, group.Sum(ToDisplay), 0))
            .Where(slice => slice.Amount > 0)
            .OrderByDescending(slice => slice.Amount)
            .ToList();
        decimal total = spent.Sum(slice => slice.Amount);
        if (total <= 0) return spent;
        return spent.Select(slice => slice with { Share = (double)(slice.Amount / total) }).ToList();
    }

    public int SurplusStreak(DateOnly cursor)
    {
        var month = new DateOnly(cursor.Year, cursor.Month, 1);
        if (Month(month).Recorded.Moves == 0) month = month.AddMonths(-1);
        int streak = 0;
        for (int i = 0; i < 36; i++)
        {
            if (Month(month.AddMonths(-i)).Recorded.Net <= 0) break;
            streak++;
        }
        return streak;
    }

    /// <summary>Marks a recurring cost as paid this month by writing a linked entry, once.</summary>
    public MoneyEntry? MarkPaid(RecurringMoney item, DateOnly month)
    {
        var snapshot = Month(month);
        if (snapshot.Entries.Any(entry => entry.RecurringId == item.Id)) return null;
        return Add(item.Flow, item.Amount, item.Title, item.DueOn(month), item.Category, recurringId: item.Id, currency: item.Currency);
    }

    public int MergeFrom(FinanceBook? other)
    {
        if (other is null) return 0;
        other.Normalize();
        int added = 0;
        foreach (var incoming in other.Entries)
        {
            if (Entries.Any(entry => entry.Id == incoming.Id)) continue;
            Entries.Add(incoming);
            added++;
        }
        foreach (var incoming in other.Recurring)
        {
            if (Recurring.Any(item => item.Id == incoming.Id)) continue;
            Recurring.Add(incoming);
            added++;
        }
        return added;
    }

    public static string NormalizeCategory(string? category, MoneyFlow flow)
    {
        string value = (category ?? "").Trim().ToLowerInvariant();
        if (value.Length == 0) return flow == MoneyFlow.Income ? "salary" : "other";
        value = value switch
        {
            "sueldo" or "nomina" or "nómina" or "salary" or "wage" => "salary",
            "freelance" or "cliente" or "project" => "freelance",
            "comida" or "food" or "groceries" => "food",
            "transporte" or "transport" or "uber" => "transport",
            "renta" or "alquiler" or "rent" or "mortgage" => "rent",
            "luz" or "agua" or "gas" or "utilities" => "utilities",
            "salud" or "health" => "health",
            "software" or "saas" or "app" => "software",
            "ai" or "ia" or "openai" or "chatgpt" or "claude" or "cursor" => "ai",
            "hosting" or "vps" or "domain" or "dominio" or "cloud" => "hosting",
            "media" or "netflix" or "spotify" => "media",
            _ => value
        };
        var allowed = flow == MoneyFlow.Income ? IncomeCategories : ExpenseCategories;
        return allowed.Contains(value) ? value : "other";
    }

    public static string Format(decimal amount, string currency, CultureInfo? culture = null)
    {
        currency = MoneyFx.Normalize(currency);
        culture ??= currency == MoneyFx.Mxn ? CultureInfo.GetCultureInfo("es-MX") : CultureInfo.GetCultureInfo("en-US");
        string number = Math.Abs(amount).ToString("N2", culture);
        return currency == MoneyFx.Mxn ? $"${number} MXN" : $"${number}";
    }

    private FinanceBar Bar(DateOnly start, DateOnly end, string label)
    {
        var slice = Entries.Where(entry => entry.Date >= start && entry.Date <= end).ToList();
        return new FinanceBar(
            label,
            slice.Where(entry => entry.Flow == MoneyFlow.Income).Sum(ToDisplay),
            slice.Where(entry => entry.Flow == MoneyFlow.Expense).Sum(ToDisplay));
    }

    private static IEnumerable<DateOnly> EachDay(DateOnly start, DateOnly end)
    {
        for (var day = start; day <= end; day = day.AddDays(1))
            yield return day;
    }

    private List<FinanceBar> WeeksInside(DateOnly start, DateOnly end)
    {
        var bars = new List<FinanceBar>();
        var cursor = start;
        int week = 1;
        while (cursor <= end)
        {
            var last = cursor.AddDays(6);
            if (last > end) last = end;
            bars.Add(Bar(cursor, last, $"S{week}"));
            cursor = last.AddDays(1);
            week++;
        }
        return bars;
    }

    private List<FinanceBar> MonthsInside(DateOnly start, int count)
    {
        var bars = new List<FinanceBar>();
        for (int i = 0; i < count; i++)
        {
            var month = start.AddMonths(i);
            var last = month.AddMonths(1).AddDays(-1);
            bars.Add(Bar(month, last, month.ToString("MMM", CultureInfo.InvariantCulture).ToUpperInvariant()));
        }
        return bars;
    }
}

/// <summary>Turns a typed line into income, an expense, a fixed cost or a subscription.</summary>
public static class FinanceQuickAdd
{
    private static readonly Regex Amount = new(@"([+-])?\s*\$?\s*(\d+(?:[.,]\d{1,2})?)", RegexOptions.CultureInvariant);

    public static object? Parse(string text, DateOnly today, string fallbackCurrency = "")
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string raw = text.Trim();
        string currency = GuessCurrency(raw) ?? (fallbackCurrency.Length > 0 ? MoneyFx.Normalize(fallbackCurrency) : "");
        bool sub = Starts(raw, "sub ", "suscrip", "subscription ");
        bool yearly = Starts(raw, "anual ", "yearly ", "annual ", "año ");
        bool fixedCost = Starts(raw, "fijo ", "fixed ", "renta ", "alquiler ");
        var match = Amount.Match(raw);
        if (!match.Success) return null;
        decimal amount = decimal.Parse(match.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        if (amount <= 0) return null;
        bool negative = match.Groups[1].Value == "-" || (!match.Groups[1].Success && !Starts(raw, "ingreso", "income", "+", "sueldo", "salario"));
        if (Starts(raw, "ingreso", "income", "sueldo", "salario", "+")) negative = false;
        if (Starts(raw, "gasto", "expense", "-")) negative = true;
        string title = (raw[..match.Index] + raw[(match.Index + match.Length)..]).Trim();
        var conceptInline = Regex.Match(title, @"\bconcepto\s*[:=]?\s*(.+)$", RegexOptions.IgnoreCase);
        if (conceptInline.Success) title = conceptInline.Groups[1].Value.Trim();
        else
        {
            var concept = Regex.Match(title, @"^(?:concepto|concept)\s*[:=]?\s*(.+)$", RegexOptions.IgnoreCase);
            if (concept.Success) title = concept.Groups[1].Value.Trim();
        }
        title = Strip(title, "sub", "suscripción", "suscripcion", "subscription", "fijo", "fixed", "ingreso", "income", "gasto", "expense", "gastos", "ingresos",
            "mensual", "anual", "yearly", "annual", "año", "agrega", "añade", "anade", "registra", "apunta", "crea", "add", "create",
            "por", "favor", "please", "un", "una", "el", "la", "los", "las", "de", "en", "a", "al", "del", "usd", "mxn", "eur", "mx", "us",
            "dolares", "dólares", "dollars", "dlls", "dll", "peso", "pesos", "concepto", "concept");
        title = Regex.Replace(title, @"[,:;]+", " ");
        title = Regex.Replace(title, @"\s+", " ").Trim(' ', '.', ',', '·', '-', '$');
        if (title.Length == 0) title = sub ? "Suscripción" : fixedCost ? "Gasto fijo" : negative ? "Gasto" : "Ingreso";
        var flow = negative ? MoneyFlow.Expense : MoneyFlow.Income;
        string category = FinanceBook.NormalizeCategory(GuessCategory(title, sub, fixedCost, flow), flow);
        if (sub || fixedCost || yearly)
        {
            return new RecurringMoney
            {
                Title = title, Amount = amount, Flow = flow, Category = category, Currency = currency,
                Subscription = sub || IsSubscription(title),
                Repeat = yearly ? MoneyRepeat.Yearly : MoneyRepeat.Monthly,
                DayOfMonth = Math.Clamp(today.Day, 1, 28),
                Start = today
            };
        }
        return new MoneyEntry { Title = title, Amount = amount, Flow = flow, Category = category, Date = today, Currency = currency };
    }

    public static string? GuessCurrency(string raw)
    {
        if (Regex.IsMatch(raw, @"\b(mxn|pesos?|mx)\b", RegexOptions.IgnoreCase)) return MoneyFx.Mxn;
        if (Regex.IsMatch(raw, @"\b(usd|dlls?|d[oó]lares|dollars|us)\b", RegexOptions.IgnoreCase)) return MoneyFx.Usd;
        if (raw.Contains('$')) return MoneyFx.Usd;
        return null;
    }

    private static bool Starts(string text, params string[] prefixes) =>
        prefixes.Any(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static string Strip(string title, params string[] words)
    {
        foreach (var word in words)
            title = Regex.Replace(title, $@"\b{Regex.Escape(word)}\b", " ", RegexOptions.IgnoreCase);
        return Regex.Replace(title, @"\s+", " ").Trim();
    }

    private static string GuessCategory(string title, bool sub, bool fixedCost, MoneyFlow flow)
    {
        string lower = title.ToLowerInvariant();
        if (flow == MoneyFlow.Income) return lower.Contains("freelance") ? "freelance" : "salary";
        if (fixedCost || lower.Contains("renta") || lower.Contains("alquiler") || lower.Contains("rent")) return "rent";
        if (lower.Contains("host") || lower.Contains("vps") || lower.Contains("domain") || lower.Contains("dominio") || lower.Contains("cloudflare")) return "hosting";
        if (lower.Contains("gpt") || lower.Contains("openai") || lower.Contains("claude") || lower.Contains("cursor") || lower.Contains("gemini") || lower.Contains("copilot") || lower.Contains("grok") || lower.Contains("anthropic") || lower.Contains("deepseek") || lower.Contains("ai") || lower.Contains(" ia")) return "ai";
        if (lower.Contains("netflix") || lower.Contains("spotify") || lower.Contains("youtube")) return "media";
        if (lower.Contains("vercel") || lower.Contains("netlify") || lower.Contains("digitalocean") || lower.Contains("railway")) return "hosting";
        if (sub) return "software";
        return "other";
    }

    private static bool IsSubscription(string title)
    {
        string lower = title.ToLowerInvariant();
        return lower.Contains("cursor") || lower.Contains("gpt") || lower.Contains("claude") || lower.Contains("host") ||
               lower.Contains("netlify") || lower.Contains("github") || lower.Contains("spotify") || lower.Contains("copilot") ||
               lower.Contains("grok") || lower.Contains("vercel");
    }
}
