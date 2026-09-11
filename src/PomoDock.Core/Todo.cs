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
    /// <summary>Time of day for a timed task. Null means any time on the due day.</summary>
    public TimeOnly? At { get; set; }
    /// <summary>The calendar entry that carries this task, so it shows there and rings.</summary>
    public Guid? EventId { get; set; }

    public void Normalize()
    {
        Title = (Title ?? "").Trim();
        if (Due is null) At = null;
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
        if (due < today) return Done ? due.ToString("dd/MM", CultureInfo.InvariantCulture) : L.T("todo.overdue");
        if (due == today) return L.T("common.today");
        if (due == today.AddDays(1)) return L.T("common.tomorrow");
        return due.ToString("dd/MM", CultureInfo.InvariantCulture);
    }

    public string PriorityLabel() => Priority switch
    {
        TodoPriority.High => L.T("todo.priorityHigh"),
        TodoPriority.Medium => L.T("todo.priorityMedium"),
        _ => L.T("todo.priorityNone")
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
/// The task list. It lives in the database, not inside a card, so closing a To Do widget never
/// takes a task with it, and every To Do card shows the same list. A dated task is also carried
/// by a calendar entry, which is how it reaches the calendar and its reminders.
/// </summary>
public sealed class TodoBook
{
    private static readonly Regex Bang = new(@"(?<![^\s])(!{1,3})(?![^\s])", RegexOptions.CultureInvariant);
    private static readonly Regex ReminderPrefix = new(
        @"^\s*(?:(?:recu[eé]rdame|recordarme|av[ií]same|notif[ií]came)(?:\s+que)?|remind\s+me(?:\s+to)?)\s+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public int Version { get; set; } = 1;
    public List<TodoTask> Items { get; set; } = [];
    /// <summary>Which slice the card is showing: all, open, today or done.</summary>
    public string Filter { get; set; } = "all";
    /// <summary>Cards whose old, card-held list was already folded in.</summary>
    public List<string> Imported { get; set; } = [];

    public void Normalize()
    {
        Items ??= [];
        Imported ??= [];
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

    public List<TodoTask> Visible(DateOnly today) => Visible(today, Filter);

    /// <summary>The slice one card shows. Each card keeps its own filter over the shared list.</summary>
    public List<TodoTask> Visible(DateOnly today, string filter) => filter switch
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
        return task is null ? null : Insert(task);
    }

    /// <summary>Puts a task at the top of the list, where it can be seen.</summary>
    public TodoTask Insert(TodoTask task)
    {
        task.Order = Items.Count == 0 ? 0 : Items.Min(item => item.Order) - 1;
        Items.Add(task);
        Normalize();
        return task;
    }

    /// <summary>A task read from one line, and the calendar draft behind it when it has a day.</summary>
    public sealed record TodoReading(TodoTask Task, AgendaEvent? Draft);

    /// <summary>Reads a line against the start of a day, for callers that only know the date.</summary>
    public static TodoTask? Parse(string text, DateOnly today) => Read(text, today.ToDateTime(TimeOnly.MinValue))?.Task;

    /// <summary>
    /// Reads a task the way the calendar reads an event — "Ir por madera antes de 2pm", "Pagar
    /// luz el viernes 18", "Sacar la basura todos los martes" — plus "!" and "!!" for urgency.
    /// Everything the reader does not consume stays in the title.
    /// </summary>
    public static TodoReading? Read(string text, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var priority = TodoPriority.None;
        foreach (Match match in Bang.Matches(text))
            if (match.Groups[1].Value.Length >= 2) priority = TodoPriority.High;
            else if (priority == TodoPriority.None) priority = TodoPriority.Medium;

        var clean = ReminderPrefix.Replace(Bang.Replace(text, " "), "");
        var reading = AgendaQuickAdd.Read(clean, now);
        if (reading is null) return null;
        var draft = reading.Event;
        // A relative To Do means "notify me then". Calendar appointments normally warn ten
        // minutes before, which would make "Sacar la basura en 10 min" ring immediately.
        if (reading.Relative && !reading.ExplicitReminder) draft.Reminders = [0];
        var task = new TodoTask { Title = draft.Title, Priority = priority };
        if (!reading.Scheduled) return new TodoReading(task, null);
        task.Due = DateOnly.FromDateTime(draft.Start);
        task.At = draft.AllDay ? null : TimeOnly.FromDateTime(draft.Start);
        return new TodoReading(task, draft);
    }
}
