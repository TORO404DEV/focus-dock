using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PomoDock.Core.Agent;

/// <summary>
/// Deterministic focus totals over <see cref="Store.Sessions"/>. The agent must never invent
/// these numbers: every period and project filter is resolved here.
/// </summary>
public static class FocusSummary
{
    public sealed class Request
    {
        public FocusPeriodKind Period { get; set; } = FocusPeriodKind.ThisWeek;
        public DateOnly? From { get; set; }
        public DateOnly? To { get; set; }
        public string? Project { get; set; }
        public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class Result
    {
        public FocusPeriodKind Period { get; init; }
        public DateOnly From { get; init; }
        public DateOnly To { get; init; }
        public string? Project { get; init; }
        public double FocusMinutes { get; init; }
        public double FocusHours => FocusMinutes / 60d;
        public int SessionCount { get; init; }
        public int FocusDays { get; init; }
        public int Completed { get; init; }
        public int Partial { get; init; }
        public Dictionary<string, double> ByProjectMinutes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public string SummaryText { get; init; } = "";
    }

    public static (DateOnly From, DateOnly To) ResolveRange(FocusPeriodKind period, DateTimeOffset now, TimeZoneInfo zone, DateOnly? customFrom = null, DateOnly? customTo = null)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var today = DateOnly.FromDateTime(local.DateTime);
        return period switch
        {
            FocusPeriodKind.Today => (today, today),
            FocusPeriodKind.Yesterday => (today.AddDays(-1), today.AddDays(-1)),
            FocusPeriodKind.ThisWeek => (WeekStartMonday(today), today),
            FocusPeriodKind.LastWeek => LastWeek(today),
            FocusPeriodKind.ThisMonth => (new DateOnly(today.Year, today.Month, 1), today),
            FocusPeriodKind.LastMonth => LastMonth(today),
            FocusPeriodKind.AllTime => (DateOnly.MinValue, today),
            FocusPeriodKind.Custom => (
                customFrom ?? throw new ArgumentException("custom period needs From"),
                customTo ?? throw new ArgumentException("custom period needs To")),
            _ => (today, today)
        };
    }

    /// <summary>Monday is day 0 of the focus week, matching Habits and the report UI.</summary>
    public static DateOnly WeekStartMonday(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    public static (DateOnly From, DateOnly To) LastWeek(DateOnly today)
    {
        var thisMonday = WeekStartMonday(today);
        var lastMonday = thisMonday.AddDays(-7);
        return (lastMonday, thisMonday.AddDays(-1));
    }

    public static (DateOnly From, DateOnly To) LastMonth(DateOnly today)
    {
        var firstThisMonth = new DateOnly(today.Year, today.Month, 1);
        var lastMonthEnd = firstThisMonth.AddDays(-1);
        var lastMonthStart = new DateOnly(lastMonthEnd.Year, lastMonthEnd.Month, 1);
        return (lastMonthStart, lastMonthEnd);
    }

    public static FocusPeriodKind ParsePeriod(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return FocusPeriodKind.ThisWeek;
        var key = raw.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return key switch
        {
            "today" or "hoy" => FocusPeriodKind.Today,
            "yesterday" or "ayer" => FocusPeriodKind.Yesterday,
            "this_week" or "thisweek" or "esta_semana" or "semana" => FocusPeriodKind.ThisWeek,
            "last_week" or "lastweek" or "semana_pasada" => FocusPeriodKind.LastWeek,
            "this_month" or "thismonth" or "este_mes" or "mes" => FocusPeriodKind.ThisMonth,
            "last_month" or "lastmonth" or "ultimo_mes" or "último_mes" or "mes_pasado" => FocusPeriodKind.LastMonth,
            "all_time" or "alltime" or "todo" or "historial" => FocusPeriodKind.AllTime,
            "custom" or "rango" => FocusPeriodKind.Custom,
            _ => Enum.TryParse<FocusPeriodKind>(raw, true, out var parsed) ? parsed : FocusPeriodKind.ThisWeek
        };
    }

    public static Result Summarize(IEnumerable<Session> sessions, Request request)
    {
        var (from, to) = ResolveRange(request.Period, request.Now, request.Zone, request.From, request.To);
        if (to < from) (from, to) = (to, from);

        var projectFilter = string.IsNullOrWhiteSpace(request.Project) ? null : request.Project.Trim();
        var focus = sessions.Where(s => s.Phase == Phase.Focus).ToList();
        if (projectFilter is not null)
            focus = focus.Where(s => string.Equals(s.Project, projectFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        var daily = Reports.Daily(focus, request.Zone);
        var inRange = daily.Where(pair => pair.Key >= from && pair.Key <= to).ToList();
        double minutes = inRange.Sum(pair => pair.Value);
        int focusDays = inRange.Count(pair => pair.Value > 0);

        // Session count uses the same day window as minutes (local start date).
        var rangedSessions = focus.Where(s =>
        {
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(s.Started, request.Zone).DateTime);
            return day >= from && day <= to;
        }).ToList();
        var outcomes = Reports.Outcomes(rangedSessions);

        var byProject = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in rangedSessions)
        {
            var contrib = Reports.MinutesIn(session, from, to, request.Zone);
            if (contrib <= 0) continue;
            byProject[session.Project] = byProject.GetValueOrDefault(session.Project) + contrib;
        }

        var result = new Result
        {
            Period = request.Period,
            From = from,
            To = to,
            Project = projectFilter,
            FocusMinutes = minutes,
            SessionCount = rangedSessions.Count,
            FocusDays = focusDays,
            Completed = outcomes.Completed,
            Partial = outcomes.Partial,
            ByProjectMinutes = byProject.OrderByDescending(p => p.Value).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase)
        };
        return new Result
        {
            Period = result.Period,
            From = result.From,
            To = result.To,
            Project = result.Project,
            FocusMinutes = result.FocusMinutes,
            SessionCount = result.SessionCount,
            FocusDays = result.FocusDays,
            Completed = result.Completed,
            Partial = result.Partial,
            ByProjectMinutes = result.ByProjectMinutes,
            SummaryText = Format(result)
        };
    }

    public static string Format(Result result)
    {
        var culture = CultureInfo.CurrentCulture;
        var hours = result.FocusHours.ToString("0.##", culture);
        var range = result.From == result.To
            ? result.From.ToString("dd MMM yyyy", culture)
            : $"{result.From.ToString("dd MMM yyyy", culture)} – {result.To.ToString("dd MMM yyyy", culture)}";
        var project = result.Project is null ? "" : $" · proyecto «{result.Project}»";
        if (result.SessionCount == 0)
            return $"0 h de enfoque en {range}{project} (sin sesiones).";
        return $"{hours} h de enfoque en {range}{project} · {result.SessionCount} sesiones · {result.FocusDays} días · {result.Completed} completadas / {result.Partial} parciales.";
    }

    public static string ToJson(Result result) => JsonSerializer.Serialize(result, Store.JsonOptions);

    /// <summary>Heuristic router used when no LLM is available or as a pre-check.</summary>
    public static bool LooksLikeFocusHistoryQuestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().ToLowerInvariant();
        bool hours = t.Contains("hora") || t.Contains("hour") || t.Contains("enfoq") || t.Contains("focus") || t.Contains("minut");
        bool when = t.Contains("mes") || t.Contains("semana") || t.Contains("hoy") || t.Contains("ayer")
            || t.Contains("month") || t.Contains("week") || t.Contains("today") || t.Contains("yesterday")
            || t.Contains("cuánt") || t.Contains("cuant") || t.Contains("how much") || t.Contains("how many");
        return hours && when;
    }

    public static FocusPeriodKind InferPeriodFromText(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        if (t.Contains("último mes") || t.Contains("ultimo mes") || t.Contains("mes pasado") || t.Contains("last month") || t.Contains("previous month"))
            return FocusPeriodKind.LastMonth;
        if (t.Contains("este mes") || t.Contains("this month"))
            return FocusPeriodKind.ThisMonth;
        if (t.Contains("semana pasada") || t.Contains("last week"))
            return FocusPeriodKind.LastWeek;
        if (t.Contains("esta semana") || t.Contains("this week"))
            return FocusPeriodKind.ThisWeek;
        if (t.Contains("ayer") || t.Contains("yesterday"))
            return FocusPeriodKind.Yesterday;
        if (t.Contains("hoy") || t.Contains("today"))
            return FocusPeriodKind.Today;
        if (t.Contains("siempre") || t.Contains("historial") || t.Contains("all time") || t.Contains("ever"))
            return FocusPeriodKind.AllTime;
        return FocusPeriodKind.ThisWeek;
    }
}
