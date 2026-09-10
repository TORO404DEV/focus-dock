using System.Text.Json;
using PomoDock.Core;

/// <summary>Habit book rules: cadence-aware streaks, counted days, experience and awards.</summary>
internal static class HabitTests
{
    private static readonly DateOnly Today = new(2026, 9, 9); // Wednesday

    public static void Run(Action<string, Action> test, Action<double, double> equal, Action<bool, string> assert)
    {
        void Test(string name, Action body) => test(name, body);

        Test("the habit week always starts on Monday", () =>
        {
            for (int i = 0; i < 7; i++)
            {
                var start = HabitBook.WeekStart(Today.AddDays(-i));
                assert(start.DayOfWeek == DayOfWeek.Monday, "week start must be Monday");
                assert(start <= Today.AddDays(-i) && Today.AddDays(-i) < start.AddDays(7), "day must sit inside its own week");
            }
            assert(HabitBook.WeekStart(Today) == new DateOnly(2026, 9, 7), "9 Sep 2026 belongs to the week of 7 Sep");
        });

        Test("the habit book survives a save and reload", () =>
        {
            var book = new HabitBook();
            var habit = new Habit { Name = "Leer", Cadence = HabitCadence.Selected, Days = [DayOfWeek.Monday, DayOfWeek.Friday], Target = 2 };
            habit.SetCount(Today, 2);
            book.Habits.Add(habit);
            book.Awards.Add("first");
            var copy = JsonSerializer.Deserialize<HabitBook>(JsonSerializer.Serialize(book, Store.JsonOptions))!;
            copy.Normalize();
            var reloaded = copy.Habits.Single();
            assert(reloaded.Name == "Leer" && reloaded.Target == 2, "name and target must survive");
            assert(reloaded.Cadence == HabitCadence.Selected && reloaded.Days.Count == 2, "cadence must survive");
            assert(reloaded.IsComplete(Today) && copy.Awards.Contains("first"), "log and awards must survive");
        });

        Test("a day only counts once every repetition is done", () =>
        {
            var habit = new Habit { Name = "Agua", Target = 3 };
            equal(1, habit.Advance(Today));
            equal(2, habit.Advance(Today));
            assert(!habit.IsComplete(Today), "two of three is not a completed day");
            equal(3, habit.Advance(Today));
            assert(habit.IsComplete(Today), "the third repetition completes the day");
            equal(0, habit.Advance(Today));
            assert(habit.Log.Count == 0, "an emptied day leaves no trace in the log");
            habit.SetCount(Today, 9);
            equal(3, habit.Count(Today));
        });

        Test("a pending today never breaks a daily streak", () =>
        {
            var habit = new Habit { Name = "Caminar", CreatedUtc = new DateTime(2026, 9, 1) };
            foreach (int back in new[] { 3, 2, 1 }) habit.SetCount(Today.AddDays(-back), 1);
            equal(3, HabitProgress.For(habit, Today).Streak);
            habit.SetCount(Today, 1);
            var progress = HabitProgress.For(habit, Today);
            equal(4, progress.Streak);
            assert(progress.DoneToday && progress.DueToday, "today is done and due");
            habit.SetCount(Today.AddDays(-2), 0);
            equal(2, HabitProgress.For(habit, Today).Streak);
            equal(2, HabitProgress.For(habit, Today).Best);
        });

        Test("free days do not break a habit tied to weekdays", () =>
        {
            var habit = new Habit
            {
                Name = "Gimnasio",
                Cadence = HabitCadence.Selected,
                Days = [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday],
                CreatedUtc = new DateTime(2026, 8, 25)
            };
            foreach (var day in new[] { new DateOnly(2026, 9, 2), new DateOnly(2026, 9, 4), new DateOnly(2026, 9, 7) }) habit.SetCount(day, 1);
            var progress = HabitProgress.For(habit, Today);
            equal(3, progress.Streak); // Wednesday 9th is still pending and Tue/Thu are not due
            assert(progress.DueToday, "Wednesday is a scheduled day");
            assert(!habit.IsDue(new DateOnly(2026, 9, 8)), "Tuesday is a free day");
            habit.SetCount(new DateOnly(2026, 9, 4), 0);
            equal(1, HabitProgress.For(habit, Today).Streak);
        });

        Test("a weekly habit counts weeks, not days", () =>
        {
            var habit = new Habit { Name = "Correr", Cadence = HabitCadence.Weekly, TimesPerWeek = 3, CreatedUtc = new DateTime(2026, 8, 24) };
            foreach (var day in new[] { new DateOnly(2026, 8, 31), new DateOnly(2026, 9, 2), new DateOnly(2026, 9, 5) }) habit.SetCount(day, 1);
            habit.SetCount(new DateOnly(2026, 9, 7), 1);
            habit.SetCount(new DateOnly(2026, 9, 8), 1);
            var progress = HabitProgress.For(habit, Today);
            equal(1, progress.Streak); // the running week has not reached three yet
            assert(progress.StreakInWeeks, "weekly habits report weeks");
            equal(2, progress.WeekDone);
            habit.SetCount(Today, 1);
            equal(2, HabitProgress.For(habit, Today).Streak);
        });

        Test("experience grows with the streak and turns into levels", () =>
        {
            var habit = new Habit { Name = "Escribir", CreatedUtc = new DateTime(2026, 9, 1) };
            foreach (int back in new[] { 2, 1, 0 }) habit.SetCount(Today.AddDays(-back), 1);
            var progress = HabitProgress.For(habit, Today);
            equal(42, progress.Xp); // 12 + 14 + 16
            var book = new HabitBook { Habits = [habit] };
            var stats = HabitStats.Compute(book, new Dictionary<Guid, HabitProgress> { [habit.Id] = progress }, Today);
            equal(42, stats.Xp);
            equal(1, stats.Level);
            equal(50, stats.LevelSpan);
            equal(8, stats.ToNextLevel);
            equal(2, HabitStats.LevelFor(50));
            equal(3, HabitStats.LevelFor(150));
            assert(stats.Awards.Any(award => award.Id == "first"), "the first logged day unlocks the first award");
        });

        Test("the board streak only counts days where everything due was done", () =>
        {
            var reading = new Habit { Name = "Leer", CreatedUtc = new DateTime(2026, 9, 1) };
            var water = new Habit { Name = "Agua", CreatedUtc = new DateTime(2026, 9, 1), Order = 1 };
            foreach (int back in new[] { 2, 1 }) { reading.SetCount(Today.AddDays(-back), 1); water.SetCount(Today.AddDays(-back), 1); }
            reading.SetCount(Today, 1);
            var book = new HabitBook { Habits = [reading, water] };
            var progress = book.Habits.ToDictionary(item => item.Id, item => HabitProgress.For(item, Today));
            var stats = HabitStats.Compute(book, progress, Today);
            equal(2, stats.Streak); // today is half done, which leaves the streak intact
            equal(2, stats.DueToday);
            equal(1, stats.DoneToday);
            water.SetCount(Today, 1);
            var complete = HabitStats.Compute(book, book.Habits.ToDictionary(item => item.Id, item => HabitProgress.For(item, Today)), Today);
            equal(3, complete.Streak);
            equal(2, complete.DoneToday);
        });

        Test("days filled in for the past still count as constancy", () =>
        {
            // The habit is created today, then last week is filled in from memory.
            var habit = new Habit { Name = "Meditar", CreatedUtc = new DateTime(2026, 9, 9) };
            for (int back = 1; back <= 6; back++) habit.SetCount(Today.AddDays(-back), 1);
            var progress = HabitProgress.For(habit, Today);
            equal(6, progress.Streak);
            equal(6, progress.Total);
            equal(1, progress.Rate); // every day since the earliest mark is complete
            equal(new DateOnly(2026, 9, 3).DayNumber, habit.Epoch().DayNumber);
        });

        Test("habits saved inside an old widget are absorbed without duplicates", () =>
        {
            var legacy = new HabitWidgetData();
            var walking = new HabitItem { Name = "Caminar" };
            walking.SetComplete(Today.AddDays(-1), true);
            walking.SetComplete(Today, true);
            legacy.Items.Add(walking);

            var book = new HabitBook();
            equal(1, book.Absorb(legacy));
            var habit = book.Habits.Single();
            assert(habit.Name == "Caminar" && habit.IsComplete(Today), "the widget history became a real habit");
            equal(2, HabitProgress.For(habit, Today).Streak);

            // The same payload again, plus one day the book already knows.
            habit.SetCount(Today.AddDays(-2), 1);
            equal(0, book.Absorb(legacy));
            equal(1, book.Habits.Count);
            equal(3, HabitProgress.For(book.Habits.Single(), Today).Streak);
            equal(0, book.Absorb(null));
        });

        Test("archived habits keep their history and leave the active list", () =>
        {
            var book = new HabitBook();
            var first = new Habit { Name = "Uno", Order = 0 };
            var second = new Habit { Name = "Dos", Order = 1 };
            first.SetCount(Today, 1);
            book.Habits.Add(first);
            book.Habits.Add(second);
            assert(book.Move(second, -1), "the second habit can move up");
            book.Normalize();
            assert(book.Active.First().Name == "Dos", "order must follow the move");
            first.Archived = true;
            assert(book.Active.Count() == 1 && book.Archived.Single().Name == "Uno", "archived habits leave the active list");
            assert(book.Archived.Single().IsComplete(Today), "archiving never touches the log");
        });
    }
}
