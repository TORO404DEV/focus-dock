namespace PomoDock.Core;

public static class Reports
{
    public static int AccessDays(IEnumerable<Session> sessions, TimeZoneInfo zone) => sessions
        .Where(session => session.Phase == Phase.Focus)
        .Select(session => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(session.Started, zone).DateTime))
        .Distinct()
        .Count();

    public static Dictionary<DateOnly, double> Daily(IEnumerable<Session> sessions, TimeZoneInfo zone)
    {
        var result = new Dictionary<DateOnly, double>();
        foreach (var segment in sessions.Where(s => s.Phase == Phase.Focus).SelectMany(s => s.Segments))
        {
            var cursor = segment.Start;
            while (cursor < segment.End)
            {
                var local = TimeZoneInfo.ConvertTime(cursor, zone);
                var date = DateOnly.FromDateTime(local.DateTime);
                var nextLocal = DateTime.SpecifyKind(date.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
                while (zone.IsInvalidTime(nextLocal)) nextLocal = nextLocal.AddMinutes(1);
                var boundary = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(nextLocal, zone), TimeSpan.Zero);
                var end = boundary < segment.End ? boundary : segment.End;
                if (end <= cursor) break;
                result[date] = result.GetValueOrDefault(date) + (end - cursor).TotalSeconds / 60;
                cursor = end;
            }
        }
        return result;
    }
    public static double MinutesIn(Session session, DateOnly from, DateOnly to, TimeZoneInfo zone) => Daily([session], zone).Where(p => p.Key >= from && p.Key <= to).Sum(p => p.Value);
    public static int Streak(Dictionary<DateOnly, double> daily, DateOnly today)
    {
        var cursor = daily.GetValueOrDefault(today) > 0 ? today : today.AddDays(-1);
        int days = 0;
        while (daily.GetValueOrDefault(cursor) > 0) { days++; cursor = cursor.AddDays(-1); }
        return days;
    }

    /// <summary>The longest run of consecutive days with focus that ever happened.</summary>
    public static int BestStreak(Dictionary<DateOnly, double> daily)
    {
        var days = daily.Where(pair => pair.Value > 0).Select(pair => pair.Key).OrderBy(day => day).ToList();
        int best = 0, run = 0;
        DateOnly? previous = null;
        foreach (var day in days)
        {
            run = previous is { } last && last.AddDays(1) == day ? run + 1 : 1;
            if (run > best) best = run;
            previous = day;
        }
        return best;
    }

    /// <summary>The single day with the most focus, and how much. Zero when nothing was logged.</summary>
    public static (DateOnly Day, double Minutes) BestDay(Dictionary<DateOnly, double> daily)
    {
        var best = daily.Where(pair => pair.Value > 0).OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).FirstOrDefault();
        return (best.Key, best.Value);
    }

    /// <summary>
    /// Minutes of focus per hour of the day, 0 to 23, split across the hours a session ran through.
    /// It answers "when do I actually work", which no daily total can show.
    /// </summary>
    public static double[] ByHour(IEnumerable<Session> sessions, TimeZoneInfo zone)
    {
        var hours = new double[24];
        foreach (var segment in sessions.Where(s => s.Phase == Phase.Focus).SelectMany(s => s.Segments))
        {
            var cursor = segment.Start;
            while (cursor < segment.End)
            {
                var local = TimeZoneInfo.ConvertTime(cursor, zone);
                var nextHour = cursor.AddMinutes(60 - local.Minute).AddSeconds(-local.Second);
                var end = nextHour < segment.End ? nextHour : segment.End;
                if (end <= cursor) break;
                hours[local.Hour] += (end - cursor).TotalSeconds / 60;
                cursor = end;
            }
        }
        return hours;
    }

    /// <summary>Minutes of focus per weekday, Sunday first to match <see cref="DayOfWeek"/>.</summary>
    public static double[] ByWeekday(Dictionary<DateOnly, double> daily)
    {
        var week = new double[7];
        foreach (var pair in daily) week[(int)pair.Key.DayOfWeek] += pair.Value;
        return week;
    }

    /// <summary>How many pomodoros ran to the end, and how many were cut short.</summary>
    public static (int Completed, int Partial) Outcomes(IEnumerable<Session> sessions)
    {
        var focus = sessions.Where(s => s.Phase == Phase.Focus).ToList();
        return (focus.Count(s => s.Outcome == Outcome.Completed), focus.Count(s => s.Outcome != Outcome.Completed));
    }
}
