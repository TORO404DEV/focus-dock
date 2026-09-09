using PomoDock.Core;

int passed = 0;
void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
void Equal(double expected, double actual) { if (Math.Abs(expected - actual) > .001) throw new Exception($"Expected {expected}, got {actual}"); }
void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
var utc = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
void Advance(TimerEngine timer, int seconds, int offset = 0) { for (int i = 1; i <= seconds; i++) timer.Tick(utc.AddSeconds(offset + i), offset + i); }

Test("pauses never count as work", () => {
 var timer = new TimerEngine(new()); timer.Start(utc, 0); Advance(timer, 10); timer.Pause(utc.AddSeconds(10), 10);
 timer.Start(utc.AddMinutes(5), 300); Advance(timer, 10, 300); Equal(20, timer.Active!.Seconds); Equal(1, timer.Active.Pauses);
});
Test("suspend gap pauses and preserves only confirmed time", () => {
 var timer = new TimerEngine(new()); bool gap = false; timer.GapDetected += () => gap = true; timer.Start(utc, 0); Advance(timer, 5); timer.Tick(utc.AddHours(8), 28800);
 Assert(gap && !timer.Running); Equal(5, timer.Active!.Seconds);
});
Test("wall clock change is not productive time", () => {
 var timer = new TimerEngine(new()); timer.Start(utc, 0); timer.Tick(utc.AddHours(1), 1); Assert(!timer.Running); Equal(0, timer.Active!.Seconds);
});
Test("completion is emitted once and capped to planned duration", () => {
 var timer = new TimerEngine(new() { FocusMinutes = 1 }); int count = 0; Session? result = null; timer.Finished += s => { count++; result = s; }; timer.Start(utc, 0); Advance(timer, 70);
 Equal(1, count); Equal(60, result!.Seconds); Assert(result.Outcome == Outcome.Completed); Equal(1, timer.CompletedFocus);
});
Test("long break occurs after configured completed sessions", () => {
 var timer = new TimerEngine(new() { FocusMinutes = 1, LongInterval = 2 });
 timer.Start(utc, 0); Advance(timer, 60); Assert(timer.NextPhase() == Phase.ShortBreak);
 timer.Start(utc.AddSeconds(60), 60); Advance(timer, 60, 60); Assert(timer.NextPhase() == Phase.LongBreak);
});
Test("skipping saves partial work without counting a completed pomodoro", () => {
 var timer = new TimerEngine(new()); Session? partial = null; timer.Finished += s => partial = s; timer.Start(utc, 0); Advance(timer, 17); timer.Select(Phase.ShortBreak, utc.AddSeconds(17), 17);
 Equal(17, partial!.Seconds); Assert(partial.Outcome == Outcome.Partial); Equal(0, timer.CompletedFocus);
});
Test("active task and planned duration remain frozen", () => {
 var settings = new Settings(); var task = new WorkTask { Name = "Original", Project = "A" }; var timer = new TimerEngine(settings); timer.Start(utc, 0, task); settings.FocusMinutes = 50; task.Name = "Edited";
 Equal(1500, timer.Active!.PlannedSeconds); Assert(timer.Active.Task == "Original");
});
Test("recovery never resumes automatically", () => {
 var timer = new TimerEngine(new()); timer.Restore(new() { PlannedSeconds = 1500, Segments = [new(utc, utc.AddSeconds(10))] }); Assert(!timer.Running); Equal(1490, timer.Remaining);
});
Test("reports split sessions at local midnight and exclude breaks", () => {
 var start = new DateTimeOffset(2026, 9, 9, 23, 50, 0, TimeSpan.Zero);
 var daily = Reports.Daily([new() { Phase = Phase.Focus, Segments = [new(start, start.AddMinutes(20))] }, new() { Phase = Phase.ShortBreak, Segments = [new(start, start.AddMinutes(5))] }], TimeZoneInfo.Utc);
 Equal(10, daily[new(2026, 9, 9)]); Equal(10, daily[new(2026, 9, 10)]);
});
Test("timezone boundaries use the user's calendar", () => {
 var zone = TimeZoneInfo.CreateCustomTimeZone("UTC-6", TimeSpan.FromHours(-6), "UTC-6", "UTC-6");
 var start = new DateTimeOffset(2026, 9, 10, 5, 50, 0, TimeSpan.Zero);
 var daily = Reports.Daily([new() { Segments = [new(start, start.AddMinutes(20))] }], zone);
 Equal(10, daily[new(2026, 9, 9)]); Equal(10, daily[new(2026, 9, 10)]);
});
Test("streak tolerates a not-yet-started today", () => {
 var today = new DateOnly(2026, 9, 9); Equal(2, Reports.Streak(new() { [today.AddDays(-1)] = 10, [today.AddDays(-2)] = 2 }, today));
});
var directory = Path.Combine(Path.GetTempPath(), "PomoDock-tests-" + Guid.NewGuid());
Directory.CreateDirectory(directory);
try
{
 Test("SQLite completion atomically clears recovery checkpoint", () => {
  using var store = new Store(directory); var session = new Session { Started = utc, Ended = utc.AddMinutes(1), PlannedSeconds = 60, Segments = [new(utc, utc.AddMinutes(1))] };
  store.Write("checkpoint", session); store.Complete(session); Assert(store.Read<Session>("checkpoint") is null); Equal(1, store.Sessions().Count); store.Complete(session); Equal(1, store.Sessions().Count);
 });
 Test("export and import preserve history and deduplicate stable IDs", () => {
  using var store = new Store(directory); string path = Path.Combine(directory, "export.json"); store.ExportJson(path); Equal(0, store.ImportJson(path));
  using var second = new Store(Path.Combine(directory, "second")); Equal(1, second.ImportJson(path)); Equal(0, second.ImportJson(path)); Equal(60, second.Sessions()[0].Seconds);
 });
 Test("CSV quotes text and neutralizes formulas", () => {
  string path = Path.Combine(directory, "export.csv"); Store.ExportCsv(path, [new() { Project = "=HYPERLINK(\"evil\")", Task = "comma, and \"quote\"" }]);
  string text = File.ReadAllText(path); Assert(text.Contains("\"'=HYPERLINK")); Assert(text.Contains("comma, and \"\"quote\"\""));
 });
 Test("Pomofocus tab CSV imports focus time and deduplicates rows", () => {
  string importDirectory = Path.Combine(directory, "pomofocus"); string path = Path.Combine(directory, "pomofocus.csv");
  File.WriteAllText(path, "date\tproject\ttask\thours\tstartTime\tendTime\n20260909\t\"Airdrop\"\t\"Task, one\"\t0.83\t11:38\t12:51\n20260909\t\"Airdrop\"\t\"Task, one\"\t0\t13:00\t13:00\n20260909\t\"Airdrop\"\t\"Inferred\"\t0.5\t14:00\t \n");
  using var store = new Store(importDirectory); var settings = new Settings();
  var result = PomofocusCsv.Import(store, settings, path); Equal(3, result.Imported); Equal(0, result.Skipped); Equal(0, result.Invalid); Equal(3, store.Sessions().Count);
  Assert(store.Sessions().Any(s => s.Project == "Airdrop" && s.Task == "Task, one" && Math.Abs(s.Seconds - 2988) < .1)); Assert(settings.Tasks.Count == 2 && settings.Projects.Contains("Airdrop"));
  var again = PomofocusCsv.Import(store, settings, path); Equal(0, again.Imported); Equal(3, again.Skipped); Equal(0, again.Invalid);
 });
}
finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
Console.WriteLine($"\n{passed} tests passed.");
