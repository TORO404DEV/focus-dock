using PomoDock.Core;

internal static class FocusHistoryTests
{
    private static readonly DateOnly Today = new(2026, 9, 11);

    public static void Run(Action<string, Action> test, Action<double, double> equal, Action<bool, string> assert)
    {
        test("focus periods resolve last month and the current week deterministically", () =>
        {
            var month = FocusHistory.Resolve("último mes", Today);
            assert(month.From == new DateOnly(2026, 8, 1) && month.To == new DateOnly(2026, 8, 31), "last month is the complete previous calendar month");
            var week = FocusHistory.Resolve("this_week", Today);
            assert(week.From == new DateOnly(2026, 9, 7) && week.To == Today, "this week begins Monday and ends today");
        });

        test("focus summary uses real segments and excludes breaks and other months", () =>
        {
            var sessions = new[]
            {
                SessionAt(2026, 8, 3, 90, "PomoDock", Outcome.Completed),
                SessionAt(2026, 8, 31, 30, "PomoDock", Outcome.Partial),
                SessionAt(2026, 9, 1, 240, "PomoDock", Outcome.Completed),
                SessionAt(2026, 8, 5, 60, "Descanso", Outcome.Completed, Phase.ShortBreak)
            };
            var summary = FocusHistory.Summarize(sessions, FocusHistory.Resolve("last_month", Today), TimeZoneInfo.Utc);
            equal(120, summary.Minutes); equal(2, summary.Sessions); equal(2, summary.ActiveDays);
            equal(1, summary.Completed); equal(1, summary.Partial);
            assert(summary.Projects.Single().Project == "PomoDock" && summary.Projects.Single().Minutes == 120, "project totals come from the selected range");
        });

        test("focus history filters projects without mixing similarly named work", () =>
        {
            var sessions = new[]
            {
                SessionAt(2026, 9, 8, 50, "Focus Dock", Outcome.Completed),
                SessionAt(2026, 9, 9, 25, "Focus Dock web", Outcome.Completed),
                SessionAt(2026, 9, 10, 20, "Focus Dock", Outcome.Partial)
            };
            var range = FocusHistory.Resolve("this_week", Today);
            var summary = FocusHistory.Summarize(sessions, range, TimeZoneInfo.Utc, "focus dock");
            equal(70, summary.Minutes); equal(2, summary.Sessions);
            var query = FocusHistory.Query(sessions, range, TimeZoneInfo.Utc, "FOCUS DOCK", 1);
            equal(2, query.Total); equal(1, query.Sessions.Count);
        });

        test("focus history returns an honest empty result for periods without sessions", () =>
        {
            var summary = FocusHistory.Summarize([SessionAt(2026, 9, 2, 45, "A", Outcome.Completed)], FocusHistory.Resolve("last_month", Today), TimeZoneInfo.Utc);
            equal(0, summary.Minutes); equal(0, summary.Sessions); equal(0, summary.ActiveDays);
            assert(summary.Days.Count == 0 && summary.Projects.Count == 0, "empty periods do not invent totals");
        });

        test("a natural last-month question maps to last_month and never this_month", () =>
        {
            assert(FocusHistory.TryGuessPeriod("cuantas horas enfoque el ultimo mes", out var spanish) && spanish == "last_month", "Spanish last month");
            assert(FocusHistory.TryGuessPeriod("How many focus hours last month?", out var english) && english == "last_month", "English last month");
            assert(!FocusHistory.TryGuessPeriod("recuerda que me llamo Ada", out _), "memory is not a focus query");
        });

        test("all-time focus begins on the earliest local segment", () =>
        {
            var sessions = new[] { SessionAt(2026, 7, 4, 10, "A", Outcome.Completed), SessionAt(2026, 8, 4, 10, "B", Outcome.Completed) };
            var earliest = FocusHistory.EarliestDay(sessions, TimeZoneInfo.Utc);
            var range = FocusHistory.Resolve("all", Today, earliest: earliest);
            assert(range.From == new DateOnly(2026, 7, 4) && range.To == Today, "all-time range uses stored history, not an arbitrary epoch");
        });

        test("focus answers name the range and never invent hours", () =>
        {
            var sessions = new[] { SessionAt(2026, 8, 3, 90, "PomoDock", Outcome.Completed) };
            var summary = FocusHistory.Summarize(sessions, FocusHistory.Resolve("last_month", Today), TimeZoneInfo.Utc);
            string text = FocusHistory.Describe(summary, spanish: true);
            assert(text.Contains("1.5 h") && text.Contains("01/08/2026") && text.Contains("31/08/2026"), "Spanish answer uses stored minutes and the previous month");
            assert(AgentIntent.IsStandaloneFocusQuestion("how many hours did I focus last month"), "English last-month question is standalone");
        });
    }

    private static Session SessionAt(int year, int month, int day, double minutes, string project, Outcome outcome, Phase phase = Phase.Focus)
    {
        var start = new DateTimeOffset(year, month, day, 10, 0, 0, TimeSpan.Zero);
        return new Session
        {
            Started = start,
            Ended = start.AddMinutes(minutes),
            Phase = phase,
            Outcome = outcome,
            Project = project,
            Task = "Prueba",
            PlannedSeconds = minutes * 60,
            Segments = [new FocusSegment(start, start.AddMinutes(minutes))]
        };
    }
}
