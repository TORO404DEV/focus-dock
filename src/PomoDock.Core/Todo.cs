using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PomoDock.Core;

/// <summary>How much a task is pulling. Only two levels above normal: more would stop meaning anything.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TodoPriority { None, Medium, High }

/// <summary>
/// One task. The field names match the older payload on purpose, so a list written by a previous
/// version loads as it is and simply gains the new fields with their defaults.
/// </summary>
public sealed class TodoTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public bool Done { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DoneUtc { get; set; }
    public TodoPriority Priority { get; set; }
    public DateOnly? Due { get; set; }
    /// <summary>Position inside the list. The board keeps it dense and starting at zero.</summary>
    public int Order { get; set; }

    public void Normalize()
    {
        Title = (Title ?? "").Trim();
        if (!Done) DoneUtc = null;
        else DoneUtc ??= DateTime.UtcNow;
    }

    public bool IsOverdue(DateOnly today) => !Done && Due is { } due && due < today;
    public bool IsDueToday(DateOnly today) => !Done && Due == today;

    public void SetDone(bool done)
    {
        Done = done;
        DoneUtc = done ? DateTime.UtcNow : null;
    }

    /// <summary>One click steps through the levels, so priority never needs a menu.</summary>
    public TodoPriority NextPriority() => Priority switch
    {
        TodoPriority.None => TodoPriority.Medium,
        TodoPriority.Medium => TodoPriority.High,
        _ => TodoPriority.None
    };

    /// <summary>The same idea for dates: no date → today → tomorrow → no date.</summary>
    public DateOnly? NextDue(DateOnly today) => Due switch
    {
        null => today,
        var due when due == today => today.AddDays(1),
        _ => null
    };

    /// <summary>A short badge for the row. Empty when the task has no date at all.</summary>
    public string DueLabel(DateOnly today)
    {
        if (Due is not { } due) return "";
        if (due < today) return Done ? due.ToString("dd/MM", CultureInfo.InvariantCulture) : "VENCIDA";
        if (due == today) return "HOY";
        if (due == today.AddDays(1)) return "MAÑANA";
        return due.ToString("dd/MM", CultureInfo.InvariantCulture);
    }

    public string PriorityLabel() => Priority switch
    {
        TodoPriority.High => "Prioridad alta",
        TodoPriority.Medium => "Prioridad media",
        _ => "Sin prioridad"
    };

    /// <summary>The colour key each level borrows from the shared palette.</summary>
    public string ColorKey() => Priority switch
    {
        TodoPriority.High => "red",
        TodoPriority.Medium => "amber",
        _ => "ink"
    };
}

/// <summary>
/// A card's task list. It stays inside the widget on purpose: two To Do cards are two different
/// lists, which is how people use them — one per page, one per project.
/// </summary>
public sealed class TodoBook
{
    private static readonly Regex Bang = new(@"(?<![^\s])(!{1,3})(?![^\s])", RegexOptions.CultureInvariant);
    private static readonly Regex DayAfterTomorrow = new(@"\bpasado\s+ma[ñn]ana\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Tomorrow = new(@"\bma[ñn]ana\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Today = new(@"\bhoy\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NumericDate = new(@"\b(\d{1,2})[/-](\d{1,2})(?:[/-](\d{2,4}))?\b", RegexOptions.CultureInvariant);
    private static readonly Regex TrailingFiller = new(@"\s+(?:el|la|los|las|de|del|a|al|en|para|este|esta)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public int Version { get; set; } = 1;
    public List<TodoTask> Items { get; set; } = [];
    /// <summary>Which slice the card is showing: all, open, today or done.</summary>
    public string Filter { get; set; } = "all";

    public void Normalize()
    {
        Items ??= [];
        foreach (var task in Items) task.Normalize();
        Items.RemoveAll(task => task.Title.Length == 0);
        if (string.IsNullOrWhiteSpace(Filter) || !Filters.Contains(Filter)) Filter = "all";
        int order = 0;
        foreach (var task in Items.OrderBy(task => task.Order).ToList()) task.Order = order++;
    }

    public static readonly string[] Filters = ["all", "open", "today", "done"];

    /// <summary>Pending work first, in the order the user arranged it; finished tasks sink below.</summary>
    public List<TodoTask> Ordered() => Items
        .OrderBy(task => task.Done ? 1 : 0)
        .ThenBy(task => task.Order)
        .ToList();

    public List<TodoTask> Visible(DateOnly today) => Filter switch
    {
        "open" => Ordered().Where(task => !task.Done).ToList(),
        "today" => Ordered().Where(task => task.IsDueToday(today) || task.IsOverdue(today)).ToList(),
        "done" => Ordered().Where(task => task.Done).ToList(),
        _ => Ordered()
    };

    public (int Total, int Done, int Open, int Overdue, int Today) Counts(DateOnly today) => (
        Items.Count,
        Items.Count(task => task.Done),
        Items.Count(task => !task.Done),
        Items.Count(task => task.IsOverdue(today)),
        Items.Count(task => task.IsDueToday(today)));

    /// <summary>Moves a task one slot inside its own half of the list, pending or finished.</summary>
    public bool Move(TodoTask task, int direction)
    {
        var siblings = Ordered().Where(item => item.Done == task.Done).ToList();
        int index = siblings.IndexOf(task), next = index + direction;
        if (index < 0 || next < 0 || next >= siblings.Count) return false;
        (siblings[index].Order, siblings[next].Order) = (siblings[next].Order, siblings[index].Order);
        return true;
    }

    /// <summary>
    /// A one-shot tidy: what is overdue, then what is due soonest, then what is most urgent.
    /// It rewrites the manual order instead of becoming a mode the user has to remember.
    /// </summary>
    public void SortByUrgency(DateOnly today)
    {
        int order = 0;
        var sorted = Items
            .Where(task => !task.Done)
            .OrderBy(task => task.Due is null ? 1 : 0)
            .ThenBy(task => task.Due ?? today)
            .ThenByDescending(task => (int)task.Priority)
            .ThenBy(task => task.Order)
            .Concat(Ordered().Where(task => task.Done));
        foreach (var task in sorted.ToList()) task.Order = order++;
    }

    public int ClearDone()
    {
        int removed = Items.RemoveAll(task => task.Done);
        Normalize();
        return removed;
    }

    /// <summary>Adds a task from one typed line and puts it at the top, where it can be seen.</summary>
    public TodoTask? Add(string text, DateOnly today)
    {
        var task = Parse(text, today);
        if (task is null) return null;
        task.Order = Items.Count == 0 ? 0 : Items.Min(item => item.Order) - 1;
        Items.Add(task);
        Normalize();
        return task;
    }

    /// <summary>
    /// Reads the shorthand a task list actually needs: "!" marks urgency and "hoy", "mañana" or
    /// a date put it on the calendar. Everything else stays in the title.
    /// </summary>
    public static TodoTask? Parse(string text, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string rest = text;

        var priority = TodoPriority.None;
        foreach (Match match in Bang.Matches(rest))
            if (match.Groups[1].Value.Length >= 2) priority = TodoPriority.High;
            else if (priority == TodoPriority.None) priority = TodoPriority.Medium;
        rest = Bang.Replace(rest, " ");

        DateOnly? due = null;
        if (Take(ref rest, DayAfterTomorrow) is not null) due = today.AddDays(2);
        else if (Take(ref rest, Tomorrow) is not null) due = today.AddDays(1);
        else if (Take(ref rest, Today) is not null) due = today;
        else if (Take(ref rest, NumericDate) is { } date) due = FromParts(date, today);

        string title = Clean(rest);
        return title.Length == 0 ? null : new TodoTask { Title = title, Priority = priority, Due = due };
    }

    private static Match? Take(ref string text, Regex pattern)
    {
        var match = pattern.Match(text);
        if (!match.Success) return null;
        text = string.Concat(text.AsSpan(0, match.Index), " ", text.AsSpan(match.Index + match.Length));
        return match;
    }

    private static DateOnly? FromParts(Match match, DateOnly today)
    {
        if (!int.TryParse(match.Groups[1].Value, out int day) || !int.TryParse(match.Groups[2].Value, out int month)) return null;
        if (month is < 1 or > 12) return null;
        int year = int.TryParse(match.Groups[3].Value, out int typed) ? (typed < 100 ? typed + 2000 : typed) : today.Year;
        if (day < 1 || day > DateTime.DaysInMonth(year, month)) return null;
        var date = new DateOnly(year, month, day);
        // A bare day and month that already went by means the next time it comes round.
        if (!match.Groups[3].Success && date < today) date = date.AddYears(1);
        return date;
    }

    private static string Clean(string text)
    {
        string title = Regex.Replace(text, @"\s+", " ").Trim(' ', '-', '–', ':', ',', ';');
        for (int guard = 0; guard < 4 && TrailingFiller.IsMatch(title); guard++) title = TrailingFiller.Replace(title, "", 1).Trim();
        return title.Length == 0 ? "" : char.ToUpper(title[0], CultureInfo.CurrentCulture) + title[1..];
    }
}
