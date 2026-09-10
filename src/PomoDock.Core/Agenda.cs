using System.Globalization;
using System.Text.Json.Serialization;

namespace PomoDock.Core;

/// <summary>How an event repeats. Every occurrence of a series is derived from its first day.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RepeatKind { None, Daily, Weekly, Monthly, Yearly }

/// <summary>A colour an event can wear. It lives next to the model so widget and toast agree.</summary>
public sealed record AgendaColor(string Key, string Name, string Hex);

public static class AgendaPalette
{
    public static readonly AgendaColor[] All =
    [
        new("ink", "Tinta", "#171916"),
        new("blue", "Azul", "#3C6E9F"),
        new("green", "Verde", "#4E7A52"),
        new("amber", "Ámbar", "#B8862B"),
        new("red", "Rojo", "#B5493C"),
        new("violet", "Violeta", "#6C5A9E"),
        new("teal", "Turquesa", "#2F7D7A")
    ];
    public static AgendaColor Of(string? key) => All.FirstOrDefault(color => string.Equals(color.Key, key, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}

/// <summary>
/// One entry in the agenda. A repeating event is stored once and its occurrences are expanded on
/// demand, so a year of a daily routine costs a single row instead of 365.
/// </summary>
public sealed class AgendaEvent
{
    /// <summary>All-day reminders have no start time of their own, so they ring mid-morning.</summary>
    public const int AllDayHour = 9;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Location { get; set; } = "";
    public string Color { get; set; } = "ink";
    public bool AllDay { get; set; }
    /// <summary>Local start of the first occurrence. All-day events ignore the time part.</summary>
    public DateTime Start { get; set; }
    /// <summary>Length in minutes. All-day events are stored in whole days.</summary>
    public int Minutes { get; set; } = 60;
    public RepeatKind Repeat { get; set; }
    /// <summary>Every N days / weeks / months / years.</summary>
    public int Interval { get; set; } = 1;
    /// <summary>Weekdays a weekly series lands on. Empty means the weekday of the first day.</summary>
    public List<DayOfWeek> Days { get; set; } = [];
    public DateOnly? Until { get; set; }
    /// <summary>Total occurrences of the series. 0 means it never ends on its own.</summary>
    public int Count { get; set; }
    /// <summary>Minutes before the start that should ring. 0 rings exactly on time.</summary>
    public List<int> Reminders { get; set; } = [10];
    /// <summary>ISO first-days of the occurrences already checked off.</summary>
    public List<string> Done { get; set; } = [];
    /// <summary>ISO first-days removed from the series without deleting the rest.</summary>
    public List<string> Cancelled { get; set; } = [];
    /// <summary>The To Do task this entry carries, when it came from a dated task.</summary>
    public Guid? TaskId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public static string Key(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static bool TryDay(string key, out DateOnly day) => DateOnly.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    /// <summary>Monday is the first day of the week everywhere in the agenda.</summary>
    public static int Ordinal(DayOfWeek day) => ((int)day + 6) % 7;
    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-Ordinal(day.DayOfWeek));

    public void Normalize()
    {
        Title = (Title ?? "").Trim();
        Notes = (Notes ?? "").Trim();
        Location = (Location ?? "").Trim();
        Days ??= []; Reminders ??= []; Done ??= []; Cancelled ??= [];
        Color = AgendaPalette.Of(Color).Key;
        Interval = Math.Clamp(Interval, 1, 99);
        Count = Math.Clamp(Count, 0, 999);
        if (AllDay)
        {
            Start = Start.Date;
            Minutes = Math.Clamp((int)Math.Ceiling(Math.Max(1, Minutes) / 1440d) * 1440, 1440, 1440 * 90);
        }
        else Minutes = Math.Clamp(Minutes, 5, 1440 * 14);
        Days = Days.Where(day => Enum.IsDefined(day)).Distinct().OrderBy(Ordinal).ToList();
        if (Repeat == RepeatKind.Weekly && Days.Count == 0) Days = [Start.DayOfWeek];
        Reminders = Reminders.Where(minutes => minutes >= 0 && minutes <= 1440 * 14).Distinct().OrderBy(minutes => minutes).ToList();
        Done = Done.Where(key => TryDay(key, out _)).Distinct(StringComparer.Ordinal).ToList();
        Cancelled = Cancelled.Where(key => TryDay(key, out _)).Distinct(StringComparer.Ordinal).ToList();
    }

    public bool IsDone(DateOnly day) => Done.Contains(Key(day), StringComparer.Ordinal);
    public bool IsCancelled(DateOnly day) => Cancelled.Contains(Key(day), StringComparer.Ordinal);
    public void SetDone(DateOnly day, bool done)
    {
        var key = Key(day);
        Done.RemoveAll(value => string.Equals(value, key, StringComparison.Ordinal));
        if (done) Done.Add(key);
    }
    public void Cancel(DateOnly day)
    {
        var key = Key(day);
        if (!Cancelled.Contains(key, StringComparer.Ordinal)) Cancelled.Add(key);
    }

    /// <summary>Days a single occurrence touches, so a night shift or a trip shows on every one.</summary>
    public int SpanDays => AllDay
        ? Math.Max(1, (int)Math.Ceiling(Math.Max(1, Minutes) / 1440d))
        : 1 + (int)((Start.TimeOfDay.TotalMinutes + Math.Max(5, Minutes) - 1) / 1440);

    /// <summary>The instant reminders count back from.</summary>
    public DateTime AnchorFor(DateOnly day) => AllDay
        ? day.ToDateTime(new TimeOnly(AllDayHour, 0))
        : day.ToDateTime(TimeOnly.FromDateTime(Start));

    public DateTime StartOn(DateOnly day) => AllDay ? day.ToDateTime(TimeOnly.MinValue) : day.ToDateTime(TimeOnly.FromDateTime(Start));
    public DateTime EndOn(DateOnly day) => StartOn(day).AddMinutes(AllDay ? Math.Max(1440, Minutes) : Math.Max(5, Minutes));

    /// <summary>
    /// First days of every occurrence between two dates, ascending. The walk fast-forwards to the
    /// window instead of replaying the series from its origin, so old daily routines stay cheap.
    /// </summary>
    public IEnumerable<DateOnly> Starts(DateOnly from, DateOnly to)
    {
        var first = DateOnly.FromDateTime(Start);
        var limit = Until is { } until && until < to ? until : to;
        if (first > limit) yield break;
        switch (Repeat)
        {
            case RepeatKind.None:
                if (first >= from && first <= limit) yield return first;
                yield break;

            case RepeatKind.Daily:
            {
                int step = Math.Max(1, Interval);
                int index = from > first ? (from.DayNumber - first.DayNumber) / step : 0;
                for (; Count <= 0 || index < Count; index++)
                {
                    var day = first.AddDays(index * step);
                    if (day > limit) yield break;
                    if (day >= from) yield return day;
                }
                yield break;
            }

            case RepeatKind.Weekly:
            {
                var set = (Days.Count > 0 ? Days : [Start.DayOfWeek]).Distinct().OrderBy(Ordinal).ToList();
                int step = Math.Max(1, Interval) * 7;
                var anchor = WeekStart(first);
                // The first week is partial: weekdays before the very first day were never occurrences.
                int missing = set.Count(day => anchor.AddDays(Ordinal(day)) < first);
                int weeks = from > anchor ? (WeekStart(from).DayNumber - anchor.DayNumber) / step : 0;
                int index = weeks == 0 ? 0 : weeks * set.Count - missing;
                for (var week = anchor.AddDays(weeks * step); week <= limit; week = week.AddDays(step))
                {
                    foreach (var weekday in set)
                    {
                        var day = week.AddDays(Ordinal(weekday));
                        if (day < first) continue;
                        if (day > limit) yield break;
                        if (Count > 0 && index >= Count) yield break;
                        index++;
                        if (day >= from) yield return day;
                    }
                }
                yield break;
            }

            case RepeatKind.Monthly:
            case RepeatKind.Yearly:
            {
                int period = Math.Max(1, Interval) * (Repeat == RepeatKind.Yearly ? 12 : 1);
                int elapsed = (from.Year - first.Year) * 12 + from.Month - first.Month;
                // One period of slack absorbs a month whose length clamped the day backwards.
                int index = from > first ? Math.Max(0, elapsed / period - 1) : 0;
                for (; Count <= 0 || index < Count; index++)
                {
                    var day = Shift(first, index * period);
                    if (day > limit) yield break;
                    if (day >= from) yield return day;
                }
                yield break;
            }
        }
    }

    /// <summary>Months added while keeping the day of the month, clamped to shorter months.</summary>
    private static DateOnly Shift(DateOnly first, int months)
    {
        var target = new DateOnly(first.Year, first.Month, 1).AddMonths(months);
        return new DateOnly(target.Year, target.Month, Math.Min(first.Day, DateTime.DaysInMonth(target.Year, target.Month)));
    }

    /// <summary>Every occurrence visible in a date range, one entry per day it covers.</summary>
    public IEnumerable<AgendaOccurrence> Occurrences(DateOnly from, DateOnly to)
    {
        int span = SpanDays;
        foreach (var day in Starts(from.AddDays(1 - span), to))
        {
            if (IsCancelled(day)) continue;
            var start = StartOn(day);
            var end = EndOn(day);
            for (int offset = 0; offset < span; offset++)
            {
                var covered = day.AddDays(offset);
                if (covered < from || covered > to) continue;
                yield return new AgendaOccurrence(this, day, covered, start, end, offset > 0);
            }
        }
    }

    public AgendaEvent Copy()
    {
        var copy = (AgendaEvent)MemberwiseClone();
        copy.Days = [.. Days]; copy.Reminders = [.. Reminders]; copy.Done = [.. Done]; copy.Cancelled = [.. Cancelled];
        return copy;
    }

    public string RepeatLabel() => Repeat switch
    {
        RepeatKind.Daily => Interval == 1 ? "Cada día" : $"Cada {Interval} días",
        RepeatKind.Weekly => WeeklyLabel(),
        RepeatKind.Monthly => Interval == 1 ? "Cada mes" : $"Cada {Interval} meses",
        RepeatKind.Yearly => Interval == 1 ? "Cada año" : $"Cada {Interval} años",
        _ => "No se repite"
    };

    private string WeeklyLabel()
    {
        var days = (Days.Count > 0 ? Days : [Start.DayOfWeek]).OrderBy(Ordinal).Select(ShortDay);
        var cadence = Interval == 1 ? "Cada semana" : $"Cada {Interval} semanas";
        return $"{cadence} · {string.Join(" ", days)}";
    }

    public static string ShortDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "L", DayOfWeek.Tuesday => "M", DayOfWeek.Wednesday => "X", DayOfWeek.Thursday => "J",
        DayOfWeek.Friday => "V", DayOfWeek.Saturday => "S", _ => "D"
    };

    /// <summary>Spanish label for a reminder offset, phrased for timed or all-day events.</summary>
    public string ReminderLabel(int minutes)
    {
        if (AllDay)
        {
            if (minutes <= 0) return $"Ese día a las {AllDayHour:00}:00";
            if (minutes % 1440 == 0) return minutes == 1440 ? "1 día antes" : $"{minutes / 1440} días antes";
        }
        if (minutes <= 0) return "Al empezar";
        if (minutes < 60) return $"{minutes} min antes";
        if (minutes % 1440 == 0) return minutes == 1440 ? "1 día antes" : $"{minutes / 1440} días antes";
        if (minutes % 60 == 0) return minutes == 60 ? "1 hora antes" : $"{minutes / 60} horas antes";
        return $"{minutes / 60} h {minutes % 60} min antes";
    }
}

/// <summary>One event landing on one day. <c>Series</c> identifies which occurrence it is.</summary>
public sealed record AgendaOccurrence(AgendaEvent Event, DateOnly Series, DateOnly Day, DateTime Start, DateTime End, bool Continuation)
{
    public string Title => Event.Title;
    public bool AllDay => Event.AllDay;
    public bool Done => Event.IsDone(Series);
    public bool Multiday => Event.SpanDays > 1;
    public bool IsPast(DateTime now) => End <= now;
    public bool IsNow(DateTime now) => Start <= now && now < End;

    /// <summary>Minutes from midnight on this day, clamped for the days a long event runs into.</summary>
    public double DayStartMinutes => Continuation ? 0 : Start.TimeOfDay.TotalMinutes;
    public double DayEndMinutes
    {
        get
        {
            var midnight = Day.AddDays(1).ToDateTime(TimeOnly.MinValue);
            return End >= midnight ? 1440 : End.TimeOfDay.TotalMinutes;
        }
    }

    public string TimeLabel()
    {
        if (AllDay) return "TODO EL DÍA";
        if (Continuation) return $"HASTA {End:HH:mm}";
        return Multiday ? $"{Start:HH:mm} → {End:dd/MM HH:mm}" : $"{Start:HH:mm} – {End:HH:mm}";
    }
}

/// <summary>A single reminder that has to ring: one event, one occurrence, one offset.</summary>
public sealed record ReminderCue(AgendaEvent Event, DateOnly Series, DateTime Start, int Minutes, DateTime FireAt)
{
    public string Key => $"{Event.Id}|{AgendaEvent.Key(Series)}|{Minutes}";
}

/// <summary>
/// The whole agenda. It lives in the database instead of inside a widget payload, so closing a
/// card, changing workspace page or opening a second calendar never loses an appointment.
/// </summary>
public sealed class AgendaBook
{
    public int Version { get; set; } = 1;
    public List<AgendaEvent> Events { get; set; } = [];
    /// <summary>Reminder cues already delivered, so a restart never rings the same one twice.</summary>
    public List<string> Delivered { get; set; } = [];
    /// <summary>Cue key → the moment a postponed reminder should ring again.</summary>
    public Dictionary<string, DateTime> Snoozed { get; set; } = [];
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public void Normalize()
    {
        Events ??= []; Delivered ??= []; Snoozed ??= [];
        foreach (var item in Events) item.Normalize();
        Events.RemoveAll(item => item.Title.Length == 0);
        // Old cues are worthless and would grow the state row forever.
        var floor = DateOnly.FromDateTime(DateTime.Now.AddDays(-45));
        Delivered = Delivered.Where(key => CueDay(key) is not { } day || day >= floor).Distinct(StringComparer.Ordinal).ToList();
        foreach (var stale in Snoozed.Where(pair => pair.Value < DateTime.Now.AddDays(-2)).Select(pair => pair.Key).ToList()) Snoozed.Remove(stale);
    }

    private static DateOnly? CueDay(string key)
    {
        var parts = key.Split('|');
        return parts.Length >= 2 && AgendaEvent.TryDay(parts[1], out var day) ? day : null;
    }

    public AgendaEvent? Find(Guid id) => Events.FirstOrDefault(item => item.Id == id);

    /// <summary>Rebuilds a cue from its stored key, used to ring a postponed reminder again.</summary>
    public ReminderCue? CueFor(string key)
    {
        var parts = key.Split('|');
        if (parts.Length != 3 || !Guid.TryParse(parts[0], out var id) || !AgendaEvent.TryDay(parts[1], out var day)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes)) return null;
        var item = Find(id);
        if (item is null || item.IsCancelled(day)) return null;
        var anchor = item.AnchorFor(day);
        return new ReminderCue(item, day, anchor, minutes, anchor.AddMinutes(-minutes));
    }

    /// <summary>Everything happening between two days, all-day entries first and then by hour.</summary>
    public List<AgendaOccurrence> Between(DateOnly from, DateOnly to) => Sort(Events.SelectMany(item => item.Occurrences(from, to)));

    public List<AgendaOccurrence> OnDay(DateOnly day) => Between(day, day);

    /// <summary>The next entries from an instant onwards, for the agenda list and the day panel.</summary>
    public List<AgendaOccurrence> Upcoming(DateTime from, int days, int take = 0)
    {
        var today = DateOnly.FromDateTime(from);
        var items = Between(today, today.AddDays(days))
            .Where(occurrence => occurrence.AllDay ? occurrence.Day >= today : occurrence.End > from)
            .ToList();
        return take > 0 ? items.Take(take).ToList() : items;
    }

    public static List<AgendaOccurrence> Sort(IEnumerable<AgendaOccurrence> items) => items
        .OrderBy(item => item.Day)
        .ThenBy(item => item.AllDay ? 0 : 1)
        .ThenBy(item => item.Start)
        .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    /// <summary>Every reminder whose moment falls inside a window. The caller decides what to ring.</summary>
    public IEnumerable<ReminderCue> Cues(DateTime from, DateTime to)
    {
        foreach (var item in Events)
        {
            if (item.Reminders.Count == 0 || item.Title.Length == 0) continue;
            int widest = item.Reminders.Max();
            var firstDay = DateOnly.FromDateTime(from).AddDays(-1);
            var lastDay = DateOnly.FromDateTime(to.AddMinutes(widest));
            foreach (var day in item.Starts(firstDay, lastDay))
            {
                if (item.IsCancelled(day) || item.IsDone(day)) continue;
                var anchor = item.AnchorFor(day);
                foreach (int minutes in item.Reminders)
                {
                    var fire = anchor.AddMinutes(-minutes);
                    if (fire >= from && fire <= to) yield return new ReminderCue(item, day, anchor, minutes, fire);
                }
            }
        }
    }

    /// <summary>Counts for the widget header: what today holds, what is done and what is still coming.</summary>
    public (int Total, int Done, int Left) Today(DateTime now)
    {
        var items = OnDay(DateOnly.FromDateTime(now));
        int done = items.Count(item => item.Done);
        return (items.Count, done, items.Count(item => !item.Done && !item.IsPast(now)));
    }
}
