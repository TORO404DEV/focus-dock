using PomoDock.Core;
using System.Text.Json;

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
Test("report access days include zero-duration Pomofocus records", () => {
 var first = new Session { Started = utc, Phase = Phase.Focus };
 var second = new Session { Started = utc.AddDays(1), Phase = Phase.Focus };
 var ignored = new Session { Started = utc.AddDays(2), Phase = Phase.ShortBreak };
 Equal(2, Reports.AccessDays([first, second, ignored], TimeZoneInfo.Utc));
});
Test("todo and habit widgets preserve completion state", () => {
 var today = new DateOnly(2026, 9, 9); var data = new TodoWidgetData { Items = [new() { Title = "Leer", Done = true }] };
 var habit = new HabitItem { Name = "Caminar" }; habit.SetComplete(today.AddDays(-2), true); habit.SetComplete(today.AddDays(-1), true); var habits = new HabitWidgetData { Items = [habit] };
 var copy = JsonSerializer.Deserialize<HabitWidgetData>(JsonSerializer.Serialize(habits))!;
 Assert(copy.Items[0].IsCompleteOn(today.AddDays(-1))); Equal(2, copy.Items[0].CurrentStreak(today)); Assert(data.Items[0].Done);
 copy.Items[0].SetComplete(today, true); Equal(3, copy.Items[0].CurrentStreak(today)); copy.Items[0].SetComplete(today, false); Equal(2, copy.Items[0].CurrentStreak(today));
});
HabitTests.Run(Test, Equal, Assert);
AgendaQuickAddTests.Run(Test, Equal, Assert);
TodoSyncTests.Run(Test, Equal, Assert);
TodoTests.Run(Test, Equal, Assert);
NoteArchiveTests.Run(Test, Equal, Assert);
Test("rich note metadata preserves color, document, and checklists", () => {
 var note = new NotesWidgetData { Color = "mint", DocumentXaml = "<Section><Paragraph>Idea</Paragraph></Section>", Checklists = [new() { ParagraphIndex = 0, IsChecked = true }], UpdatedUtc = utc.UtcDateTime };
 var copy = JsonSerializer.Deserialize<NotesWidgetData>(JsonSerializer.Serialize(note))!;
 Assert(copy.Version == 1 && copy.Color == "mint" && copy.DocumentXaml.Contains("Idea") && copy.Checklists.Single().IsChecked);
});
Test("legacy canvas migrates to a first workspace page", () => {
 var note = new WidgetConfig { Kind = "notes", Title = "Ideas" };
 var settings = new Settings { Widgets = [note], TimerPositionCustomized = true };
 settings.Validate();
 Equal(1, settings.WorkspacePages.Count); Assert(settings.WorkspacePages[0].Name == "INICIO");
 Assert(settings.WorkspacePages[0].Widgets.Single() == note && settings.WorkspacePages[0].TimerWidget is not null);
 Assert(settings.WorkspacePages[0].HasContent && settings.WorkspacePages[0].WidgetCount == 2);
 var blank = new WorkspacePage { Name = "PÁGINA 02" }; Assert(!blank.HasContent && blank.WidgetCount == 0);
});
Test("weekly agenda series lands only on its own weekdays", () => {
 var monday = new DateTime(2026, 9, 7, 9, 0, 0);
 var meeting = new AgendaEvent { Title = "Daily", Start = monday, Minutes = 30, Repeat = RepeatKind.Weekly, Days = [DayOfWeek.Monday, DayOfWeek.Wednesday] };
 meeting.Normalize();
 var days = meeting.Starts(new(2026, 9, 7), new(2026, 9, 20)).ToList();
 Equal(4, days.Count); Assert(days[0] == new DateOnly(2026, 9, 7) && days[3] == new DateOnly(2026, 9, 16));
 var fortnight = meeting.Copy(); fortnight.Interval = 2;
 Equal(2, fortnight.Starts(new(2026, 9, 7), new(2026, 9, 20)).Count());
 // A window far from the first day must fast-forward without losing the rhythm.
 var november = meeting.Starts(new(2026, 11, 1), new(2026, 11, 30)).ToList();
 Equal(9, november.Count); Assert(november.All(day => day.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Wednesday));
 var limited = meeting.Copy(); limited.Count = 3;
 Equal(3, limited.Starts(new(2026, 9, 1), new(2026, 12, 31)).Count());
 var closing = meeting.Copy(); closing.Until = new(2026, 9, 14);
 Equal(3, closing.Starts(new(2026, 9, 1), new(2026, 12, 31)).Count());
});
Test("monthly and yearly series clamp to shorter months", () => {
 var rent = new AgendaEvent { Title = "Alquiler", Start = new DateTime(2026, 1, 31, 10, 0, 0), Repeat = RepeatKind.Monthly };
 rent.Normalize();
 var days = rent.Starts(new(2026, 1, 1), new(2026, 4, 30)).ToList();
 Equal(4, days.Count); Assert(days[1] == new DateOnly(2026, 2, 28) && days[3] == new DateOnly(2026, 4, 30));
 var leap = new AgendaEvent { Title = "Aniversario", Start = new DateTime(2024, 2, 29, 10, 0, 0), Repeat = RepeatKind.Yearly };
 leap.Normalize();
 Assert(leap.Starts(new(2026, 1, 1), new(2026, 12, 31)).Single() == new DateOnly(2026, 2, 28));
});
Test("an event crossing midnight shows on both days", () => {
 var shift = new AgendaEvent { Title = "Turno", Start = new DateTime(2026, 9, 9, 23, 0, 0), Minutes = 120 };
 shift.Normalize();
 Equal(2, shift.SpanDays);
 var book = new AgendaBook { Events = [shift] };
 var next = book.OnDay(new(2026, 9, 10)).Single();
 Assert(next.Continuation); Equal(0, next.DayStartMinutes); Equal(60, next.DayEndMinutes);
 Assert(book.OnDay(new(2026, 9, 9)).Single().TimeLabel().StartsWith("23:00"));
});
Test("reminders ring once per offset and never for a cancelled or finished day", () => {
 var call = new AgendaEvent { Title = "Llamada", Start = new DateTime(2026, 9, 9, 10, 0, 0), Reminders = [10, 60] };
 call.Normalize();
 var book = new AgendaBook { Events = [call] };
 var window = book.Cues(new DateTime(2026, 9, 9, 8, 0, 0), new DateTime(2026, 9, 9, 10, 0, 0)).ToList();
 Equal(2, window.Count); Assert(window.Any(cue => cue.FireAt == new DateTime(2026, 9, 9, 9, 0, 0)));
 Assert(window[0].Key != window[1].Key);
 call.SetDone(new(2026, 9, 9), true);
 Equal(0, book.Cues(new DateTime(2026, 9, 9, 8, 0, 0), new DateTime(2026, 9, 9, 10, 0, 0)).Count());
 call.SetDone(new(2026, 9, 9), false); call.Cancel(new(2026, 9, 9));
 Equal(0, book.Cues(new DateTime(2026, 9, 9, 8, 0, 0), new DateTime(2026, 9, 9, 10, 0, 0)).Count());
 var birthday = new AgendaEvent { Title = "Cumple", AllDay = true, Start = new DateTime(2026, 9, 10), Reminders = [1440] };
 birthday.Normalize();
 var early = new AgendaBook { Events = [birthday] }.Cues(new DateTime(2026, 9, 9, 0, 0, 0), new DateTime(2026, 9, 9, 23, 0, 0)).Single();
 Assert(early.FireAt == new DateTime(2026, 9, 9, 9, 0, 0));
});
Test("a cancelled or postponed cue can be rebuilt from its stored key", () => {
 var call = new AgendaEvent { Title = "Llamada", Start = new DateTime(2026, 9, 9, 10, 0, 0), Reminders = [15] };
 call.Normalize();
 var book = new AgendaBook { Events = [call] };
 var cue = book.Cues(new DateTime(2026, 9, 9, 9, 0, 0), new DateTime(2026, 9, 9, 10, 0, 0)).Single();
 var restored = book.CueFor(cue.Key)!;
 Assert(restored.Event.Id == call.Id && restored.Series == cue.Series && restored.Minutes == 15);
 Assert(book.CueFor("no-es-una-clave") is null);
});
Test("quick add reads Spanish dates, times, duration and repetition", () => {
 var now = new DateTime(2026, 9, 9, 12, 0, 0);
 var dentist = AgendaQuickAdd.Parse("Dentista mañana a las 17:30 durante 45m", now)!;
 Assert(dentist.Title == "Dentista"); Assert(dentist.Start == new DateTime(2026, 9, 10, 17, 30, 0)); Equal(45, dentist.Minutes); Assert(!dentist.AllDay);
 var gym = AgendaQuickAdd.Parse("Gimnasio todos los martes a las 7", now)!;
 Assert(gym.Repeat == RepeatKind.Weekly && gym.Days.Single() == DayOfWeek.Tuesday);
 Assert(gym.Start == new DateTime(2026, 9, 15, 7, 0, 0) && gym.Title == "Gimnasio");
 var rent = AgendaQuickAdd.Parse("Pagar alquiler el 1 de octubre", now)!;
 Assert(rent.AllDay && rent.Start == new DateTime(2026, 10, 1) && rent.Title == "Pagar alquiler");
 // A bare afternoon hour means the afternoon, and a time already gone belongs to tomorrow.
 var call = AgendaQuickAdd.Parse("Llamar a Ana a las 4", now)!;
 Assert(call.Start == new DateTime(2026, 9, 9, 16, 0, 0) && call.Title == "Llamar a Ana");
 Assert(AgendaQuickAdd.Parse("Repaso a las 9:00", now)!.Start == new DateTime(2026, 9, 10, 9, 0, 0));
 var review = AgendaQuickAdd.Parse("Revisión cada mes avisar 2 h antes", now)!;
 Assert(review.Repeat == RepeatKind.Monthly && review.Reminders.Single() == 120 && review.Title == "Revisión");
 Assert(AgendaQuickAdd.Parse("mañana a las 10", now) is null);
 Assert(AgendaQuickAdd.Parse("   ", now) is null);
});
Test("the agenda survives a round trip and counts the day", () => {
 var now = new DateTime(2026, 9, 9, 12, 0, 0);
 var book = new AgendaBook { Events = [
  new() { Title = "Standup", Start = new DateTime(2026, 9, 9, 9, 30, 0), Minutes = 15, Repeat = RepeatKind.Daily, Color = "blue" },
  new() { Title = "Entrega", AllDay = true, Start = new DateTime(2026, 9, 9), Color = "red" } ] };
 book.Normalize();
 var copy = JsonSerializer.Deserialize<AgendaBook>(JsonSerializer.Serialize(book))!;
 copy.Normalize();
 var today = copy.OnDay(new(2026, 9, 9));
 Equal(2, today.Count); Assert(today[0].AllDay && today[0].Title == "Entrega");
 Assert(copy.Events.First(item => item.Title == "Standup").Repeat == RepeatKind.Daily);
 copy.Events[0].SetDone(new(2026, 9, 9), true);
 var counts = copy.Today(now);
 Equal(2, counts.Total); Equal(1, counts.Done); Equal(1, counts.Left);
 // The standup at 9:30 is already over at noon, so only the all-day entry is still ahead.
 Equal(1, copy.Upcoming(now, 1).Count(item => !item.AllDay) + counts.Left - 1);
 Assert(AgendaPalette.Of("red").Hex == "#B5493C" && AgendaPalette.Of("desconocido").Key == "ink");
});
Test("the focus rank counts only real work and knows what comes next", () => {
 var start = new DateTimeOffset(2026, 9, 9, 9, 0, 0, TimeSpan.Zero);
 Session Focused(double minutes) => new() { Phase = Phase.Focus, Segments = [new(start, start.AddMinutes(minutes))] };
 var fresh = FocusProfile.Of([]);
 Assert(fresh.Level == 1 && fresh.Name == "PRIMER PASO", "an empty history still has a standing"); Equal(0, fresh.Hours);
 // Breaks are rest: they never count towards the rank.
 var mixed = FocusProfile.Of([Focused(120), new() { Phase = Phase.ShortBreak, Segments = [new(start, start.AddMinutes(300))] }]);
 Equal(2, mixed.Hours); Assert(mixed.Level == 2 && mixed.Name == "APRENDIZ", "two hours of focus is the second rank");
 Equal(3, mixed.ToNext); Equal(.25, mixed.Share); Assert(FocusProfile.NextName(mixed) == "CONSTANTE");
 var top = FocusProfile.At(1000);
 Assert(top.IsHighest && top.Name == "LEYENDA", "the ladder ends at the highest rank"); Equal(0, top.ToNext); Equal(1, top.Share);
 Assert(FocusProfile.At(-5).Level == 1, "a negative history cannot drop below the first rank");
});
Test("a restored backup adds missing habits and never loses a habit day", () => {
 var day = new DateOnly(2026, 9, 9);
 var reading = new Habit { Name = "Leer" }; reading.Log[Habit.Key(day.AddDays(-1))] = 1;
 var local = new HabitBook { Habits = [reading] };
 var backup = JsonSerializer.Deserialize<HabitBook>(JsonSerializer.Serialize(local))!;
 backup.Habits[0].Log[Habit.Key(day)] = 1;
 backup.Habits.Add(new Habit { Name = "Correr" });
 backup.Awards.Add("first");
 Equal(1, local.MergeFrom(backup));
 Assert(local.Habits.Count == 2 && local.Habits.First(habit => habit.Name == "Leer").Log.Count == 2, "a day known only to the backup is kept");
 Assert(local.Awards.Contains("first"), "awards travel with the backup");
 Equal(0, local.MergeFrom(backup)); Equal(0, local.MergeFrom(null));
});
Test("a restored backup brings back missing events and leaves existing ones alone", () => {
 var local = new AgendaBook { Events = [new() { Title = "Dentista", Start = new DateTime(2026, 9, 10, 17, 30, 0) }] };
 local.Normalize();
 var backup = JsonSerializer.Deserialize<AgendaBook>(JsonSerializer.Serialize(local))!;
 backup.Events[0].Title = "Cambiado en la copia";
 backup.Events.Add(new() { Title = "Revisión", Start = new DateTime(2026, 9, 12, 9, 0, 0) });
 Equal(1, local.MergeFrom(backup));
 Assert(local.Events.Count == 2 && local.Events.Any(item => item.Title == "Dentista"), "the local event keeps its own version");
 Equal(0, local.MergeFrom(backup));
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
 Test("the JSON backup carries habits and calendar events, and old backups still load", () => {
  using var store = new Store(Path.Combine(directory, "full-backup"));
  var habit = new Habit { Name = "Meditar" }; habit.Log[Habit.Key(new DateOnly(2026, 9, 9))] = 1;
  store.Write(Store.HabitsKey, new HabitBook { Habits = [habit] });
  store.Write(Store.AgendaKey, new AgendaBook { Events = [new() { Title = "Entrega", AllDay = true, Start = new DateTime(2026, 9, 11) }] });
  var notes = new NoteArchive(); var closedNote = Guid.NewGuid();
  notes.Track(closedNote, "Idea cerrada", "mint", "{}", new DateTime(2026, 9, 10)); notes.Close(closedNote, new DateTime(2026, 9, 10, 1, 0, 0));
  store.Write(Store.NotesKey, notes);
  string path = Path.Combine(directory, "full-backup.json"); store.ExportJson(path);
  var backup = Store.ReadBackup(path);
  Assert(backup.Habits!.Habits.Single().Name == "Meditar" && backup.Agenda!.Events.Single().Title == "Entrega", "habits and events are in the file");
  Assert(backup.Notes!.Find(closedNote) is { IsOpen: false, Text: "Idea cerrada" }, "closed notes travel in the backup too");
  string legacy = Path.Combine(directory, "legacy-backup.json");
  File.WriteAllText(legacy, "{\"Version\":1,\"Settings\":{},\"Sessions\":[]}");
  var old = Store.ReadBackup(legacy);
  Assert(old.Habits is null && old.Agenda is null && old.Sessions.Count == 0, "a backup from before habits and events still reads");
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
