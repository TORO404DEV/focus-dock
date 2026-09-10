using System.Globalization;
using System.Text.Json.Serialization;

namespace PomoDock.Core;

/// <summary>How often a habit is expected. Streaks and rates only judge the days the habit is due.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HabitCadence
{
    /// <summary>Every single day.</summary>
    Daily,
    /// <summary>Only the selected weekdays.</summary>
    Selected,
    /// <summary>A number of free days inside each Monday-to-Sunday week.</summary>
    Weekly
}

/// <summary>
/// Every habit in the workspace. It lives in the database instead of inside a widget so
/// closing, moving or re-creating a card never destroys history.
/// </summary>
public sealed class HabitBook
{
    public int Version { get; set; } = 1;
    public List<Habit> Habits { get; set; } = [];
    /// <summary>Awards already celebrated, so a restart does not replay old confetti.</summary>
    public List<string> Awards { get; set; } = [];
    /// <summary>Widget payloads already folded in, keyed by widget id.</summary>
    public List<string> Imported { get; set; } = [];
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public IEnumerable<Habit> Active => Habits.Where(habit => !habit.Archived).OrderBy(habit => habit.Order);
    public IEnumerable<Habit> Archived => Habits.Where(habit => habit.Archived).OrderBy(habit => habit.Order);

    public void Normalize()
    {
        Habits ??= [];
        Awards ??= [];
        Imported ??= [];
        foreach (var habit in Habits) habit.Normalize();
        int order = 0;
        foreach (var habit in Habits.OrderBy(habit => habit.Order).ToList()) habit.Order = order++;
    }

    /// <summary>Moves a habit one slot up or down inside its own list (active or archived).</summary>
    public bool Move(Habit habit, int direction)
    {
        var siblings = (habit.Archived ? Archived : Active).ToList();
        int index = siblings.IndexOf(habit), next = index + direction;
        if (index < 0 || next < 0 || next >= siblings.Count) return false;
        (siblings[index].Order, siblings[next].Order) = (siblings[next].Order, siblings[index].Order);
        return true;
    }

    /// <summary>Monday is the first day of the week everywhere in the habit widget.</summary>
    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>
    /// Folds a backup in without losing anything on either side: habits that are not here yet
    /// are added, and a habit present in both keeps every day either copy recorded.
    /// </summary>
    public int MergeFrom(HabitBook? other)
    {
        if (other is null) return 0;
        other.Normalize();
        int added = 0;
        foreach (var incoming in other.Habits)
        {
            var existing = Habits.FirstOrDefault(habit => habit.Id == incoming.Id);
            if (existing is null)
            {
                incoming.Order = Habits.Count == 0 ? 0 : Habits.Max(habit => habit.Order) + 1;
                Habits.Add(incoming);
                added++;
                continue;
            }
            foreach (var (day, count) in incoming.Log)
            {
                int kept = existing.Log.TryGetValue(day, out int value) ? value : 0;
                existing.Log[day] = Math.Max(kept, Math.Min(existing.Target, count));
            }
        }
        foreach (var award in other.Awards)
            if (!Awards.Contains(award)) Awards.Add(award);
        Normalize();
        return added;
    }

    /// <summary>
    /// Folds a pre-database widget payload in. Habits already tracked keep their settings and
    /// only gain the missing days, so importing the same widget twice changes nothing.
    /// </summary>
    public int Absorb(HabitWidgetData? legacy)
    {
        int added = 0;
        foreach (var item in legacy?.Items ?? [])
        {
            var name = (item.Name ?? "").Trim();
            if (name.Length == 0) continue;
            var habit = Habits.FirstOrDefault(existing => string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase));
            if (habit is null)
            {
                habit = new Habit { Name = name, Order = Habits.Count == 0 ? 0 : Habits.Max(other => other.Order) + 1 };
                Habits.Add(habit);
                added++;
            }
            foreach (var day in item.CompletedDates ?? [])
                habit.Log[day] = Math.Max(habit.Log.TryGetValue(day, out int count) ? count : 0, 1);
        }
        Normalize();
        return added;
    }
}

public sealed class Habit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public HabitCadence Cadence { get; set; } = HabitCadence.Daily;
    /// <summary>Weekdays the habit is due when <see cref="Cadence"/> is <see cref="HabitCadence.Selected"/>.</summary>
    public List<DayOfWeek> Days { get; set; } = [];
    /// <summary>Free days per week when <see cref="Cadence"/> is <see cref="HabitCadence.Weekly"/>.</summary>
    public int TimesPerWeek { get; set; } = 3;
    /// <summary>Repetitions that complete a single day, for habits counted more than once.</summary>
    public int Target { get; set; } = 1;
    public bool Archived { get; set; }
    public int Order { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>ISO day → repetitions done. ISO keys keep the log portable and timezone-proof.</summary>
    public Dictionary<string, int> Log { get; set; } = [];

    public void Normalize()
    {
        Name = (Name ?? "").Trim();
        Days ??= [];
        Log ??= [];
        Days = Days.Where(day => Enum.IsDefined(day)).Distinct().OrderBy(day => ((int)day + 6) % 7).ToList();
        TimesPerWeek = Math.Clamp(TimesPerWeek, 1, 7);
        Target = Math.Clamp(Target, 1, 99);
        if (Cadence == HabitCadence.Selected && Days.Count == 0) Cadence = HabitCadence.Daily;
        foreach (var key in Log.Where(entry => entry.Value <= 0 || !TryParse(entry.Key, out _)).Select(entry => entry.Key).ToList()) Log.Remove(key);
    }

    public static string Key(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static bool TryParse(string key, out DateOnly day) => DateOnly.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    public int Count(DateOnly day) => Log.TryGetValue(Key(day), out int count) ? count : 0;
    public bool IsComplete(DateOnly day) => Count(day) >= Target;

    /// <summary>Whether the habit is due on that day. Weekly habits are free to land anywhere.</summary>
    public bool IsDue(DateOnly day) => Cadence switch
    {
        HabitCadence.Selected => Days.Contains(day.DayOfWeek),
        _ => true
    };

    public void SetCount(DateOnly day, int count)
    {
        var key = Key(day);
        count = Math.Clamp(count, 0, Target);
        if (count <= 0) Log.Remove(key); else Log[key] = count;
    }

    /// <summary>One click moves a day forward: pending → each repetition → complete → pending again.</summary>
    public int Advance(DateOnly day, int direction = 1)
    {
        int count = Count(day) + direction;
        if (count > Target) count = 0;
        if (count < 0) count = Target;
        SetCount(day, count);
        return Count(day);
    }

    public DateOnly? FirstDay()
    {
        DateOnly? first = null;
        foreach (var key in Log.Keys)
            if (TryParse(key, out var day) && (first is null || day < first)) first = day;
        return first;
    }

    /// <summary>Days already fully done, resolved once so the walks below stay allocation free.</summary>
    public HashSet<DateOnly> CompletedDays()
    {
        var days = new HashSet<DateOnly>();
        foreach (var entry in Log)
            if (entry.Value >= Target && TryParse(entry.Key, out var day)) days.Add(day);
        return days;
    }

    public DateOnly CreatedDay() => DateOnly.FromDateTime(CreatedUtc.ToLocalTime());

    /// <summary>The first day this habit can be judged on: days filled in afterwards for the past count too.</summary>
    public DateOnly Epoch()
    {
        var created = CreatedDay();
        var first = FirstDay();
        return first is not null && first.Value < created ? first.Value : created;
    }
}

/// <summary>Everything the widget shows about a single habit, resolved in one pass over its log.</summary>
public sealed class HabitProgress
{
    public int Streak { get; init; }
    public int Best { get; init; }
    public int Total { get; init; }
    public int Xp { get; init; }
    /// <summary>Share of due days completed over the last 30 days, 0 to 1.</summary>
    public double Rate { get; init; }
    public int CountToday { get; init; }
    public bool DoneToday { get; init; }
    public bool DueToday { get; init; }
    /// <summary>Weekly habits count weeks, every other cadence counts days.</summary>
    public bool StreakInWeeks { get; init; }
    public int WeekDone { get; init; }

    public static HabitProgress For(Habit habit, DateOnly today)
    {
        var done = habit.CompletedDays();
        var first = habit.FirstDay();
        bool weekly = habit.Cadence == HabitCadence.Weekly;
        int dayBest = 0, run = 0, xp = 0;

        if (first is not null)
        {
            for (var day = first.Value; day <= today; day = day.AddDays(1))
            {
                bool complete = done.Contains(day);
                if (complete)
                {
                    run++;
                    if (run > dayBest) dayBest = run;
                    // Long runs are worth more: the reward grows with the streak and then settles.
                    xp += 10 + 2 * Math.Min(run, 10);
                }
                // A day still in progress never breaks a run, and free days never count against it.
                else if (habit.IsDue(day) && day != today) run = 0;
            }
        }

        int streak = weekly ? WeekStreak(habit, done, today) : DayStreak(habit, done, today, first);
        int best = weekly ? BestWeeks(habit, done, today) : dayBest;

        return new HabitProgress
        {
            Streak = streak,
            Best = Math.Max(best, streak),
            Total = done.Count,
            Xp = xp,
            Rate = RateOf(habit, done, today),
            CountToday = habit.Count(today),
            DoneToday = done.Contains(today),
            DueToday = habit.IsDue(today),
            StreakInWeeks = weekly,
            WeekDone = WeekCount(done, HabitBook.WeekStart(today))
        };
    }

    private static int DayStreak(Habit habit, HashSet<DateOnly> done, DateOnly today, DateOnly? first)
    {
        if (first is null) return 0;
        var day = today;
        // Today can still be pending without costing the streak.
        if (!done.Contains(day)) day = day.AddDays(-1);
        int streak = 0;
        while (day >= first.Value)
        {
            if (habit.IsDue(day))
            {
                if (!done.Contains(day)) break;
                streak++;
            }
            else if (done.Contains(day)) streak++;
            day = day.AddDays(-1);
        }
        return streak;
    }

    private static int WeekCount(HashSet<DateOnly> done, DateOnly weekStart)
    {
        int count = 0;
        for (int i = 0; i < 7; i++) if (done.Contains(weekStart.AddDays(i))) count++;
        return count;
    }

    private static int WeekStreak(Habit habit, HashSet<DateOnly> done, DateOnly today)
    {
        var first = habit.FirstDay();
        if (first is null) return 0;
        var week = HabitBook.WeekStart(today);
        // The running week only counts once it already met the target.
        if (WeekCount(done, week) < habit.TimesPerWeek) week = week.AddDays(-7);
        var firstWeek = HabitBook.WeekStart(first.Value);
        int streak = 0;
        while (week >= firstWeek && WeekCount(done, week) >= habit.TimesPerWeek) { streak++; week = week.AddDays(-7); }
        return streak;
    }

    private static int BestWeeks(Habit habit, HashSet<DateOnly> done, DateOnly today)
    {
        var first = habit.FirstDay();
        if (first is null) return 0;
        int best = 0, run = 0;
        var current = HabitBook.WeekStart(today);
        for (var week = HabitBook.WeekStart(first.Value); week <= current; week = week.AddDays(7))
        {
            if (WeekCount(done, week) >= habit.TimesPerWeek) { run++; if (run > best) best = run; }
            else if (week != current) run = 0;
        }
        return best;
    }

    private static double RateOf(Habit habit, HashSet<DateOnly> done, DateOnly today)
    {
        var start = today.AddDays(-29);
        var epoch = habit.Epoch();
        if (epoch > start) start = epoch;
        // A day still in progress is not a failure yet, so it stays out of the ratio.
        var last = done.Contains(today) ? today : today.AddDays(-1);
        if (start > last) return 0;
        if (habit.Cadence == HabitCadence.Weekly)
        {
            int span = last.DayNumber - start.DayNumber + 1;
            double expected = habit.TimesPerWeek * span / 7.0;
            int hits = 0;
            for (var day = start; day <= last; day = day.AddDays(1)) if (done.Contains(day)) hits++;
            return expected <= 0 ? 0 : Math.Min(1, hits / expected);
        }
        int due = 0, made = 0;
        for (var day = start; day <= last; day = day.AddDays(1))
        {
            if (!habit.IsDue(day)) continue;
            due++;
            if (done.Contains(day)) made++;
        }
        return due == 0 ? 0 : (double)made / due;
    }
}

public sealed record HabitAward(string Id, string Name, string Detail);

/// <summary>The whole board at a glance: today, level, streak and unlocked awards.</summary>
public sealed class HabitStats
{
    public int DueToday { get; init; }
    public int DoneToday { get; init; }
    public int Xp { get; init; }
    public int Level { get; init; }
    /// <summary>Experience already earned inside the current level.</summary>
    public int LevelXp { get; init; }
    /// <summary>Experience the current level needs in total.</summary>
    public int LevelSpan { get; init; }
    public int Streak { get; init; }
    public int BestStreak { get; init; }
    public int Total { get; init; }
    public double Rate { get; init; }
    public List<HabitAward> Awards { get; init; } = [];
    public double TodayShare => DueToday == 0 ? 0 : Math.Min(1, (double)DoneToday / DueToday);
    public double LevelShare => LevelSpan <= 0 ? 0 : Math.Clamp((double)LevelXp / LevelSpan, 0, 1);
    public int ToNextLevel => Math.Max(0, LevelSpan - LevelXp);

    /// <summary>Experience needed to leave a level. Levels get longer, so late ones stay meaningful.</summary>
    public static int Span(int level) => 50 * level;
    public static int Threshold(int level) => 25 * level * (level - 1);
    public static int LevelFor(int xp)
    {
        int level = 1;
        while (level < 999 && xp >= Threshold(level + 1)) level++;
        return level;
    }

    public static HabitStats Compute(HabitBook book, IReadOnlyDictionary<Guid, HabitProgress> progress, DateOnly today)
    {
        var active = book.Active.ToList();
        int xp = book.Habits.Sum(habit => progress.TryGetValue(habit.Id, out var value) ? value.Xp : 0);
        int level = LevelFor(xp);
        int total = book.Habits.Sum(habit => progress.TryGetValue(habit.Id, out var value) ? value.Total : 0);
        int bestHabitStreak = book.Habits.Select(habit => progress.TryGetValue(habit.Id, out var value) ? value.Best : 0).DefaultIfEmpty(0).Max();
        var (streak, bestStreak) = PerfectDays(active, today);
        return new HabitStats
        {
            DueToday = active.Count(habit => habit.IsDue(today)),
            DoneToday = active.Count(habit => progress.TryGetValue(habit.Id, out var value) && value.DoneToday),
            Xp = xp,
            Level = level,
            LevelXp = xp - Threshold(level),
            LevelSpan = Span(level),
            Streak = streak,
            BestStreak = bestStreak,
            Total = total,
            Rate = active.Count == 0 ? 0 : active.Average(habit => progress.TryGetValue(habit.Id, out var value) ? value.Rate : 0),
            Awards = Earned(active.Count, total, level, bestStreak, bestHabitStreak)
        };
    }

    /// <summary>Perfect days: every habit due that day was completed. Today may still be open.</summary>
    private static (int current, int best) PerfectDays(List<Habit> active, DateOnly today)
    {
        if (active.Count == 0) return (0, 0);
        var done = active.ToDictionary(habit => habit.Id, habit => habit.CompletedDays());
        var start = active.Select(habit => habit.Epoch()).Min();
        int best = 0, run = 0;
        for (var day = start; day <= today; day = day.AddDays(1))
        {
            int due = 0, made = 0;
            foreach (var habit in active)
            {
                if (!habit.IsDue(day) || habit.Epoch() > day) continue;
                due++;
                if (done[habit.Id].Contains(day)) made++;
            }
            if (due > 0 && made == due) { run++; if (run > best) best = run; }
            else if (day != today) run = 0;
        }
        return (run, Math.Max(best, run));
    }

    private static List<HabitAward> Earned(int active, int total, int level, int perfectStreak, int habitStreak)
    {
        var awards = new List<HabitAward>();
        void Add(bool earned, string id, string name, string detail) { if (earned) awards.Add(new HabitAward(id, name, detail)); }
        // The id stays stored so an award already celebrated is not celebrated again in another language.
        void Award(bool earned, string id) => Add(earned, id, L.T("award." + id), L.T("award." + id + "Detail"));
        Award(total >= 1, "first");
        Award(habitStreak >= 7, "week");
        Award(habitStreak >= 30, "month");
        Award(habitStreak >= 100, "hundred");
        Award(total >= 100, "marks");
        Award(perfectStreak >= 7, "perfect");
        Award(active >= 5, "five");
        Award(level >= 5, "level5");
        Award(level >= 10, "level10");
        return awards;
    }
}
