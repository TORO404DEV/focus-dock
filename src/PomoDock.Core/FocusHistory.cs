using System.Globalization;
using System.Text;

namespace PomoDock.Core;

public sealed record FocusDateRange(string Period, DateOnly From, DateOnly To);
public sealed record FocusDayTotal(DateOnly Date, double Minutes);
public sealed record FocusProjectTotal(string Project, double Minutes, int Sessions);
public sealed record FocusSessionSlice(Guid Id, DateTimeOffset Started, DateTimeOffset Ended, string Project, string Task, double Minutes, Outcome Outcome);
public sealed record FocusHistorySummary(
    FocusDateRange Range,
    string? Project,
    double Minutes,
    int Sessions,
    int ActiveDays,
    int Completed,
    int Partial,
    IReadOnlyList<FocusDayTotal> Days,
    IReadOnlyList<FocusProjectTotal> Projects);
public sealed record FocusHistoryQuery(FocusDateRange Range, string? Project, int Total, IReadOnlyList<FocusSessionSlice> Sessions);

/// <summary>Deterministic, timezone-aware focus history queries shared by reports and the local agent.</summary>
public static class FocusHistory
{
    public static FocusDateRange Resolve(
        string? period,
        DateOnly today,
        DateOnly? from = null,
        DateOnly? to = null,
        DateOnly? earliest = null)
    {
        string key = NormalizePeriod(period);
        int mondayOffset = ((int)today.DayOfWeek + 6) % 7;
        var monday = today.AddDays(-mondayOffset);
        FocusDateRange range = key switch
        {
            "today" => new(key, today, today),
            "yesterday" => new(key, today.AddDays(-1), today.AddDays(-1)),
            "this_week" => new(key, monday, today),
            "last_week" => new(key, monday.AddDays(-7), monday.AddDays(-1)),
            "this_month" => new(key, new DateOnly(today.Year, today.Month, 1), today),
            "last_month" => PreviousMonth(today),
            "last_7_days" => new(key, today.AddDays(-6), today),
            "last_30_days" => new(key, today.AddDays(-29), today),
            "all" => new(key, earliest is { } first && first < today ? first : today, today),
            "custom" when from is not null && to is not null => new(key, from.Value, to.Value),
            "custom" => throw new ArgumentException("A custom focus range requires from and to."),
            _ => throw new ArgumentException($"Unknown focus period '{period}'.")
        };
        if (range.To < range.From) throw new ArgumentException("The focus range ends before it starts.");
        return range;
    }

    public static FocusHistorySummary Summarize(
        IEnumerable<Session> source,
        FocusDateRange range,
        TimeZoneInfo zone,
        string? project = null)
    {
        var sessions = Filter(source, project).ToList();
        var slices = Slice(sessions, range, zone).ToList();
        var daily = Reports.Daily(sessions, zone)
            .Where(pair => pair.Key >= range.From && pair.Key <= range.To && pair.Value > 0)
            .OrderBy(pair => pair.Key)
            .Select(pair => new FocusDayTotal(pair.Key, Round(pair.Value)))
            .ToList();
        var projects = slices
            .GroupBy(slice => DisplayProject(slice.Project), StringComparer.OrdinalIgnoreCase)
            .Select(group => new FocusProjectTotal(group.Key, Round(group.Sum(item => item.Minutes)), group.Count()))
            .OrderByDescending(item => item.Minutes)
            .ThenBy(item => item.Project, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new FocusHistorySummary(
            range,
            CleanProject(project),
            Round(slices.Sum(item => item.Minutes)),
            slices.Count,
            daily.Count,
            slices.Count(item => item.Outcome == Outcome.Completed),
            slices.Count(item => item.Outcome != Outcome.Completed),
            daily,
            projects);
    }

    public static FocusHistoryQuery Query(
        IEnumerable<Session> source,
        FocusDateRange range,
        TimeZoneInfo zone,
        string? project = null,
        int limit = 30)
    {
        var slices = Slice(Filter(source, project), range, zone)
            .OrderByDescending(item => item.Started)
            .ToList();
        return new FocusHistoryQuery(range, CleanProject(project), slices.Count, slices.Take(Math.Clamp(limit, 1, 100)).ToList());
    }

    public static DateOnly? EarliestDay(IEnumerable<Session> sessions, TimeZoneInfo zone)
    {
        var first = sessions.Where(session => session.Phase == Phase.Focus)
            .SelectMany(session => session.Segments)
            .OrderBy(segment => segment.Start)
            .FirstOrDefault();
        return first is null ? null : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(first.Start, zone).DateTime);
    }

    private static IEnumerable<Session> Filter(IEnumerable<Session> source, string? project)
    {
        string? requested = CleanProject(project);
        return source.Where(session => session.Phase == Phase.Focus)
            .Where(session => requested is null || string.Equals(DisplayProject(session.Project), requested, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<FocusSessionSlice> Slice(IEnumerable<Session> sessions, FocusDateRange range, TimeZoneInfo zone)
    {
        foreach (var session in sessions)
        {
            double minutes = Reports.MinutesIn(session, range.From, range.To, zone);
            if (minutes <= 0) continue;
            var first = session.Segments.OrderBy(segment => segment.Start).FirstOrDefault();
            var last = session.Segments.OrderByDescending(segment => segment.End).FirstOrDefault();
            var started = first?.Start ?? session.Started;
            var ended = last?.End ?? session.Ended;
            yield return new FocusSessionSlice(
                session.Id,
                TimeZoneInfo.ConvertTime(started, zone),
                TimeZoneInfo.ConvertTime(ended, zone),
                DisplayProject(session.Project),
                string.IsNullOrWhiteSpace(session.Task) ? "Enfoque libre" : session.Task.Trim(),
                Round(minutes),
                session.Outcome);
        }
    }

    private static FocusDateRange PreviousMonth(DateOnly today)
    {
        var firstThisMonth = new DateOnly(today.Year, today.Month, 1);
        var last = firstThisMonth.AddDays(-1);
        return new FocusDateRange("last_month", new DateOnly(last.Year, last.Month, 1), last);
    }

    private static string NormalizePeriod(string? period)
    {
        string value = string.IsNullOrWhiteSpace(period) ? "this_month" : period.Trim().ToLowerInvariant();
        var plain = new StringBuilder();
        foreach (char c in value.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) plain.Append(c);
        value = plain.ToString().Replace('-', '_').Replace(' ', '_');
        return value switch
        {
            "hoy" => "today",
            "ayer" => "yesterday",
            "esta_semana" => "this_week",
            "semana_pasada" or "ultima_semana" => "last_week",
            "este_mes" => "this_month",
            "mes_pasado" or "ultimo_mes" => "last_month",
            "ultimos_7_dias" => "last_7_days",
            "ultimos_30_dias" => "last_30_days",
            "todo" or "todos" or "historico" => "all",
            "personalizado" => "custom",
            _ => value
        };
    }

    private static string DisplayProject(string? project) => string.IsNullOrWhiteSpace(project) ? "Sin proyecto" : project.Trim();
    private static string? CleanProject(string? project) => string.IsNullOrWhiteSpace(project) ? null : project.Trim();
    private static double Round(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
}
