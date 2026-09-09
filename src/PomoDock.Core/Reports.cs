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
}
