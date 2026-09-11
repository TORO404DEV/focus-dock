using PomoDock.Core;
using PomoDock.Core.Agent;

/// <summary>Deterministic focus.summarize + period math (Fase 0).</summary>
internal static class FocusSummaryTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    public static void Run(Action<string, Action> test, Action<double, double> equal, Action<bool, string> assert)
    {
        void Test(string name, Action body) => test(name, body);

        Test("week always starts on Monday", () =>
        {
            for (int i = 0; i < 14; i++)
            {
                var day = new DateOnly(2026, 9, 1).AddDays(i);
                var start = FocusSummary.WeekStartMonday(day);
                assert(start.DayOfWeek == DayOfWeek.Monday, "must be Monday");
                assert(start <= day && day < start.AddDays(7), "day inside week");
            }
        });

        Test("last month is the full previous calendar month", () =>
        {
            var (from, to) = FocusSummary.LastMonth(new DateOnly(2026, 9, 11));
            assert(from == new DateOnly(2026, 8, 1) && to == new DateOnly(2026, 8, 31), "August 2026");
            (from, to) = FocusSummary.LastMonth(new DateOnly(2026, 3, 1));
            assert(from == new DateOnly(2026, 2, 1) && to == new DateOnly(2026, 2, 28), "February non-leap");
            (from, to) = FocusSummary.LastMonth(new DateOnly(2024, 3, 15));
            assert(from == new DateOnly(2024, 2, 1) && to == new DateOnly(2024, 2, 29), "February leap");
            (from, to) = FocusSummary.LastMonth(new DateOnly(2026, 1, 5));
            assert(from == new DateOnly(2025, 12, 1) && to == new DateOnly(2025, 12, 31), "December prior year");
        });

        Test("last week is the previous Monday-Sunday block", () =>
        {
            // 11 Sep 2026 is Friday → this week Mon 7; last week Mon 31 Aug – Sun 6 Sep
            var (from, to) = FocusSummary.LastWeek(new DateOnly(2026, 9, 11));
            assert(from == new DateOnly(2026, 8, 31) && to == new DateOnly(2026, 9, 6), "last week range");
        });

        Test("this week runs from Monday through today", () =>
        {
            var now = new DateTimeOffset(2026, 9, 11, 15, 0, 0, TimeSpan.Zero); // Friday
            var (from, to) = FocusSummary.ResolveRange(FocusPeriodKind.ThisWeek, now, Utc);
            assert(from == new DateOnly(2026, 9, 7) && to == new DateOnly(2026, 9, 11), "Mon–Fri");
        });

        Test("parse period tokens including Spanish", () =>
        {
            assert(FocusSummary.ParsePeriod("last_month") == FocusPeriodKind.LastMonth, "last_month");
            assert(FocusSummary.ParsePeriod("último_mes") == FocusPeriodKind.LastMonth, "ultimo_mes");
            assert(FocusSummary.ParsePeriod("esta_semana") == FocusPeriodKind.ThisWeek, "esta_semana");
            assert(FocusSummary.ParsePeriod("hoy") == FocusPeriodKind.Today, "hoy");
            assert(FocusSummary.ParsePeriod("ayer") == FocusPeriodKind.Yesterday, "ayer");
        });

        Test("infer period from natural language", () =>
        {
            assert(FocusSummary.InferPeriodFromText("¿Cuántas horas enfoqué el último mes?") == FocusPeriodKind.LastMonth, "ultimo mes");
            assert(FocusSummary.InferPeriodFromText("how many focus hours last month") == FocusPeriodKind.LastMonth, "last month en");
            assert(FocusSummary.InferPeriodFromText("horas esta semana") == FocusPeriodKind.ThisWeek, "esta semana");
            assert(FocusSummary.InferPeriodFromText("focus today") == FocusPeriodKind.Today, "today");
        });

        Test("empty period returns zeros without inventing", () =>
        {
            var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
            var result = FocusSummary.Summarize([], new FocusSummary.Request
            {
                Period = FocusPeriodKind.LastMonth, Zone = Utc, Now = now
            });
            equal(0, result.FocusMinutes);
            equal(0, result.SessionCount);
            equal(0, result.FocusDays);
            assert(result.SummaryText.Contains("sin sesiones", StringComparison.OrdinalIgnoreCase)
                || result.SummaryText.StartsWith("0 h", StringComparison.OrdinalIgnoreCase), "empty copy");
        });

        Test("last month aggregates only that calendar month", () =>
        {
            var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
            var sessions = new List<Session>
            {
                Focus(new DateTimeOffset(2026, 7, 31, 10, 0, 0, TimeSpan.Zero), 60), // July — out
                Focus(new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero), 30),
                Focus(new DateTimeOffset(2026, 8, 15, 14, 0, 0, TimeSpan.Zero), 90),
                Focus(new DateTimeOffset(2026, 8, 31, 20, 0, 0, TimeSpan.Zero), 15),
                Focus(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero), 120), // September — out
            };
            var result = FocusSummary.Summarize(sessions, new FocusSummary.Request
            {
                Period = FocusPeriodKind.LastMonth, Zone = Utc, Now = now
            });
            equal(135, result.FocusMinutes);
            equal(2.25, result.FocusHours);
            equal(3, result.SessionCount);
            equal(3, result.FocusDays);
            assert(result.From == new DateOnly(2026, 8, 1) && result.To == new DateOnly(2026, 8, 31), "August bounds");
        });

        Test("this week filters from Monday and excludes prior Sunday", () =>
        {
            var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero); // Fri
            var sessions = new List<Session>
            {
                Focus(new DateTimeOffset(2026, 9, 6, 10, 0, 0, TimeSpan.Zero), 40), // Sun — out
                Focus(new DateTimeOffset(2026, 9, 7, 10, 0, 0, TimeSpan.Zero), 25), // Mon
                Focus(new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero), 10),
            };
            var result = FocusSummary.Summarize(sessions, new FocusSummary.Request
            {
                Period = FocusPeriodKind.ThisWeek, Zone = Utc, Now = now
            });
            equal(35, result.FocusMinutes);
            equal(2, result.SessionCount);
        });

        Test("exact project filter is case-insensitive and exclusive", () =>
        {
            var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
            var sessions = new List<Session>
            {
                Focus(new DateTimeOffset(2026, 9, 10, 10, 0, 0, TimeSpan.Zero), 20, "Alpha"),
                Focus(new DateTimeOffset(2026, 9, 10, 11, 0, 0, TimeSpan.Zero), 30, "alpha"),
                Focus(new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero), 50, "Beta"),
            };
            var result = FocusSummary.Summarize(sessions, new FocusSummary.Request
            {
                Period = FocusPeriodKind.ThisWeek, Project = "ALPHA", Zone = Utc, Now = now
            });
            equal(50, result.FocusMinutes);
            equal(2, result.SessionCount);
            assert(result.Project == "ALPHA", "keeps requested project label trimmed form from request path");
        });

        // Project property stores the filter string as passed after Trim — "ALPHA" stays.
        // Actually looking at code: `projectFilter = request.Project.Trim()` so Project == "ALPHA". Good.

        Test("breaks never count as focus minutes", () =>
        {
            var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
            var sessions = new List<Session>
            {
                Focus(new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero), 20),
                new Session
                {
                    Phase = Phase.ShortBreak,
                    Started = new DateTimeOffset(2026, 9, 11, 10, 30, 0, TimeSpan.Zero),
                    Ended = new DateTimeOffset(2026, 9, 11, 10, 35, 0, TimeSpan.Zero),
                    PlannedSeconds = 300,
                    Segments = [new(new DateTimeOffset(2026, 9, 11, 10, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 11, 10, 35, 0, TimeSpan.Zero))]
                }
            };
            var result = FocusSummary.Summarize(sessions, new FocusSummary.Request
            {
                Period = FocusPeriodKind.Today, Zone = Utc, Now = now
            });
            equal(20, result.FocusMinutes);
            equal(1, result.SessionCount);
        });

        Test("historical custom range", () =>
        {
            var sessions = new List<Session>
            {
                Focus(new DateTimeOffset(2025, 1, 2, 10, 0, 0, TimeSpan.Zero), 60),
                Focus(new DateTimeOffset(2025, 6, 1, 10, 0, 0, TimeSpan.Zero), 30),
                Focus(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), 15),
            };
            var result = FocusSummary.Summarize(sessions, new FocusSummary.Request
            {
                Period = FocusPeriodKind.Custom,
                From = new DateOnly(2025, 1, 1),
                To = new DateOnly(2025, 12, 31),
                Zone = Utc,
                Now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero)
            });
            equal(90, result.FocusMinutes);
            equal(2, result.SessionCount);
        });

        Test("all_time uses DateOnly.MinValue through today", () =>
        {
            var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
            var (from, to) = FocusSummary.ResolveRange(FocusPeriodKind.AllTime, now, Utc);
            assert(from == DateOnly.MinValue && to == new DateOnly(2026, 9, 11), "all time bounds");
        });

        Test("timezone shifts the local calendar day", () =>
        {
            var zone = TimeZoneInfo.CreateCustomTimeZone("UTC-6", TimeSpan.FromHours(-6), "UTC-6", "UTC-6");
            // 2026-09-11 02:00 UTC = 2026-09-10 20:00 in UTC-6
            var start = new DateTimeOffset(2026, 9, 11, 2, 0, 0, TimeSpan.Zero);
            var sessions = new List<Session> { Focus(start, 30) };
            var now = new DateTimeOffset(2026, 9, 11, 20, 0, 0, TimeSpan.Zero);
            var today = FocusSummary.Summarize(sessions, new FocusSummary.Request
            {
                Period = FocusPeriodKind.Today, Zone = zone, Now = now
            });
            // local today is Sep 11; session local day is Sep 10 → 0 for today
            equal(0, today.FocusMinutes);
            var yesterday = FocusSummary.Summarize(sessions, new FocusSummary.Request
            {
                Period = FocusPeriodKind.Yesterday, Zone = zone, Now = now
            });
            equal(30, yesterday.FocusMinutes);
        });

        Test("planner routes last-month hours question to focus.summarize", () =>
        {
            var plan = AgentPlanner.FromUserText("¿Cuántas horas enfoqué el último mes?");
            assert(plan.Steps.Count == 1, "one step");
            assert(plan.Steps[0].Call.Name == "focus.summarize", "tool name");
            assert(plan.Steps[0].Call.Arguments["period"] == "last_month", "period arg");
            assert(plan.Steps[0].Kind == AgentToolKind.Read, "read kind");
            assert(!plan.NeedsApproval, "reads need no approval");
        });

        Test("looks like focus history question", () =>
        {
            assert(FocusSummary.LooksLikeFocusHistoryQuestion("¿Cuántas horas enfoqué el último mes?"), "es");
            assert(FocusSummary.LooksLikeFocusHistoryQuestion("How many focus hours this week?"), "en");
            assert(!FocusSummary.LooksLikeFocusHistoryQuestion("añade una tarea"), "not focus");
        });

        // Extra matrix: every period kind resolves without throwing for a fixed "now".
        foreach (FocusPeriodKind period in Enum.GetValues<FocusPeriodKind>())
        {
            if (period == FocusPeriodKind.Custom) continue;
            var captured = period;
            Test($"resolve {captured} does not throw", () =>
            {
                var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
                var (from, to) = FocusSummary.ResolveRange(captured, now, Utc);
                assert(from <= to, "ordered");
            });
        }

        // Minute matrix across several days for this_month
        Test("this month includes only current month days", () =>
        {
            var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
            var sessions = new List<Session>();
            for (int d = 28; d <= 31; d++) // August tail
                sessions.Add(Focus(new DateTimeOffset(2026, 8, d, 10, 0, 0, TimeSpan.Zero), 10));
            for (int d = 1; d <= 11; d++)
                sessions.Add(Focus(new DateTimeOffset(2026, 9, d, 10, 0, 0, TimeSpan.Zero), 10));
            var result = FocusSummary.Summarize(sessions, new FocusSummary.Request
            {
                Period = FocusPeriodKind.ThisMonth, Zone = Utc, Now = now
            });
            equal(110, result.FocusMinutes);
            equal(11, result.SessionCount);
        });

        Test("by-project breakdown sums to total when unfiltered", () =>
        {
            var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
            var sessions = new List<Session>
            {
                Focus(new DateTimeOffset(2026, 9, 10, 10, 0, 0, TimeSpan.Zero), 20, "A"),
                Focus(new DateTimeOffset(2026, 9, 10, 11, 0, 0, TimeSpan.Zero), 30, "B"),
            };
            var result = FocusSummary.Summarize(sessions, new FocusSummary.Request
            {
                Period = FocusPeriodKind.ThisWeek, Zone = Utc, Now = now
            });
            equal(50, result.FocusMinutes);
            equal(20, result.ByProjectMinutes["A"]);
            equal(30, result.ByProjectMinutes["B"]);
        });

        Test("outcomes count completed vs partial in range", () =>
        {
            var now = new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
            var a = Focus(new DateTimeOffset(2026, 9, 10, 10, 0, 0, TimeSpan.Zero), 25);
            a.Outcome = Outcome.Completed;
            var b = Focus(new DateTimeOffset(2026, 9, 10, 11, 0, 0, TimeSpan.Zero), 10);
            b.Outcome = Outcome.Partial;
            var result = FocusSummary.Summarize([a, b], new FocusSummary.Request
            {
                Period = FocusPeriodKind.ThisWeek, Zone = Utc, Now = now
            });
            equal(1, result.Completed);
            equal(1, result.Partial);
        });
    }

    private static Session Focus(DateTimeOffset start, double minutes, string project = "Sin proyecto")
    {
        var end = start.AddMinutes(minutes);
        return new Session
        {
            Phase = Phase.Focus,
            Outcome = Outcome.Completed,
            Project = project,
            Task = "T",
            Started = start,
            Ended = end,
            PlannedSeconds = minutes * 60,
            Segments = [new FocusSegment(start, end)]
        };
    }
}
