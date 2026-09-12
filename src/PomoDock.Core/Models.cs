namespace PomoDock.Core;

public enum Phase { Focus, ShortBreak, LongBreak }
public enum Outcome { Completed, Partial, Recovered }
public record FocusSegment(DateTimeOffset Start, DateTimeOffset End)
{
    public double Seconds => Math.Max(0, (End - Start).TotalSeconds);
}
public sealed class Session
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Started { get; set; }
    public DateTimeOffset Ended { get; set; }
    public Phase Phase { get; set; }
    public Outcome Outcome { get; set; }
    public string Project { get; set; } = "Sin proyecto";
    public string Task { get; set; } = "Enfoque libre";
    public Guid? TaskId { get; set; }
    public double PlannedSeconds { get; set; }
    public int Pauses { get; set; }
    public List<FocusSegment> Segments { get; set; } = [];
    public string Note { get; set; } = "";
    public double? OriginalSeconds { get; set; }
    public double Seconds => Segments.Sum(s => s.Seconds);
}
public sealed class WorkTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Project { get; set; } = "Sin proyecto";
    public int Estimate { get; set; } = 1;
    public bool Done { get; set; }
    public bool Template { get; set; }
    public override string ToString() => $"{Project} / {Name}";
}
public sealed class TodoWidgetData
{
    public List<TodoItem> Items { get; set; } = [];
}
public sealed class NotesWidgetData
{
    public int Version { get; set; } = 1;
    public string Color { get; set; } = "paper";
    public string DocumentXaml { get; set; } = "";
    public List<NotesChecklistState> Checklists { get; set; } = [];
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
public sealed class NotesChecklistState
{
    public int ParagraphIndex { get; set; }
    public bool IsChecked { get; set; }
}
public sealed class TodoItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public bool Done { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
public sealed class HabitWidgetData
{
    public List<HabitItem> Items { get; set; } = [];
}
public sealed class HabitItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    // ISO dates keep the widget portable and avoid timezone ambiguity.
    public List<string> CompletedDates { get; set; } = [];
    public bool IsCompleteOn(DateOnly day) => CompletedDates.Contains(Key(day), StringComparer.Ordinal);
    public void SetComplete(DateOnly day, bool complete)
    {
        var key = Key(day);
        CompletedDates.RemoveAll(value => string.Equals(value, key, StringComparison.Ordinal));
        if (complete) CompletedDates.Add(key);
    }
    public int CurrentStreak(DateOnly today)
    {
        var day = IsCompleteOn(today) ? today : today.AddDays(-1);
        var streak = 0;
        while (IsCompleteOn(day)) { streak++; day = day.AddDays(-1); }
        return streak;
    }
    private static string Key(DateOnly day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
public sealed class WidgetConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "notes";
    public string Title { get; set; } = "Notas";
    public string Value { get; set; } = "";
    public double Weight { get; set; } = 1;
    public double X { get; set; } = 0;
    public double Y { get; set; } = 0;
    public double Width { get; set; } = 0;
    public double Height { get; set; } = 0;
    public bool KeepAlive { get; set; }
    public bool Collapsed { get; set; }
}
public sealed class WorkspacePage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "PÁGINA";
    /// <summary>Column on the infinite page grid. Neighbors sit at Col ± 1 on the same Row.</summary>
    public int Col { get; set; }
    /// <summary>Row on the infinite page grid. Neighbors sit at Row ± 1 on the same Col.</summary>
    public int Row { get; set; }
    public List<WidgetConfig> Widgets { get; set; } = [];
    public WidgetConfig? TimerWidget { get; set; }
    public bool TimerPositionCustomized { get; set; }
    public bool HasContent => TimerWidget is not null || Widgets.Count > 0;
    public int WidgetCount => Widgets.Count + (TimerWidget is null ? 0 : 1);
}
public sealed class Settings
{
    public int FocusMinutes { get; set; } = 25;
    public int ShortMinutes { get; set; } = 5;
    public int LongMinutes { get; set; } = 15;
    public int LongInterval { get; set; } = 4;
    public int DailyGoalMinutes { get; set; } = 120;
    public bool AutoBreak { get; set; }
    public bool AutoFocus { get; set; }
    public bool Sound { get; set; } = true;
    public bool ButtonSounds { get; set; } = true;
    public bool WhiteNoise { get; set; }
    public int WhiteNoiseVolume { get; set; } = 18;
    public bool AlarmEnabled { get; set; } = true;
    public int AlarmRepeats { get; set; } = 3;
    // The sound library: which sound plays for what, and how loud. Ids come from SoundLibrary.
    public string FocusEndSound { get; set; } = "classic";
    public string BreakEndSound { get; set; } = "chime";
    public string ReminderSound { get; set; } = "call-two-note";
    public string ClickSound { get; set; } = "click-soft";
    /// <summary>What plays during focus when <see cref="WhiteNoise"/> is on; the name predates the library.</summary>
    public string AmbientSound { get; set; } = "white";
    /// <summary>Alarms and calendar reminders.</summary>
    public int AlarmVolume { get; set; } = 70;
    /// <summary>Button and habit clicks.</summary>
    public int EffectsVolume { get; set; } = 45;
    /// <summary>Interface language: a code from <see cref="Strings.Catalog"/>, or "system".</summary>
    public string Language { get; set; } = "system";
    /// <summary>Whether the private agent reads its verified result aloud.</summary>
    public bool AgentVoiceEnabled { get; set; } = true;
    /// <summary>Neural speech speed. One is the voice model's natural pace.</summary>
    public double AgentVoiceSpeed { get; set; } = 1.0;
    /// <summary>Whether the agent may store explicit personal memories locally.</summary>
    public bool AgentMemoryEnabled { get; set; } = true;
    /// <summary>DeepSeek API key (DPAPI-sealed). Empty = agent chat unavailable until configured.</summary>
    public string AgentDeepSeekApiKey { get; set; } = "";
    /// <summary>If true, releasing the agent microphone sends the transcript instead of inserting it.</summary>
    public bool AgentSendVoiceOnRelease { get; set; }
    /// <summary>Floating agent widget geometry. It is not a page widget, so work on the canvas stays visible.</summary>
    public WidgetConfig AgentWidget { get; set; } = new() { Kind = "agent", Title = "PomoDock", Width = 360, Height = 520 };
    public bool AgentWidgetVisible { get; set; }
    public bool Dark { get; set; }
    public bool ReduceMotion { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool TimerAtBottom { get; set; }
    // Legacy single-page timer retained for backward-compatible backup migration.
    public WidgetConfig TimerWidget { get; set; } = new() { Kind = "timer", Title = "POMODORO" };
    public bool TimerPositionCustomized { get; set; }
    public double WindowLeft { get; set; } = 120;
    public double WindowTop { get; set; } = 60;
    public double WindowWidth { get; set; } = 620;
    public double WindowHeight { get; set; } = 940;
    public bool Fullscreen { get; set; }
    public string FocusColor { get; set; } = "#D7D9D1";
    public string ShortBreakColor { get; set; } = "#BFD7EA";
    public string LongBreakColor { get; set; } = "#F3C4A8";
    public string AccentColor { get; set; } = "#D7D9D1";
    public List<string> Projects { get; set; } = [];
    public List<WorkTask> Tasks { get; set; } = [];
    public List<WidgetConfig> Widgets { get; set; } = [];
    public Dictionary<string, List<WidgetConfig>> Layouts { get; set; } = [];
    public List<WorkspacePage> WorkspacePages { get; set; } = [];
    public int ActiveWorkspacePage { get; set; }
    /// <summary>Page the agent treats as home / casa / principal when the user asks to go back.</summary>
    public Guid? HomePageId { get; set; }
    public void Validate()
    {
        FocusMinutes = Math.Clamp(FocusMinutes, 1, 180);
        ShortMinutes = Math.Clamp(ShortMinutes, 1, 60);
        LongMinutes = Math.Clamp(LongMinutes, 1, 120);
        LongInterval = Math.Clamp(LongInterval, 1, 12);
        DailyGoalMinutes = Math.Clamp(DailyGoalMinutes, 1, 1440);
        WhiteNoiseVolume = Math.Clamp(WhiteNoiseVolume, 0, 100);
        AlarmRepeats = Math.Clamp(AlarmRepeats, 1, 8);
        AlarmVolume = Math.Clamp(AlarmVolume, 0, 100);
        EffectsVolume = Math.Clamp(EffectsVolume, 0, 100);
        AgentVoiceSpeed = Math.Clamp(AgentVoiceSpeed, 0.75, 1.4);
        FocusEndSound = SoundLibrary.Valid(FocusEndSound, SoundKind.Alarm);
        BreakEndSound = SoundLibrary.Valid(BreakEndSound, SoundKind.Alarm);
        ReminderSound = SoundLibrary.Valid(ReminderSound, SoundKind.Reminder);
        ClickSound = SoundLibrary.Valid(ClickSound, SoundKind.Click);
        AmbientSound = SoundLibrary.Valid(AmbientSound, SoundKind.Ambient);
        Widgets ??= [];
        Layouts ??= [];
        WorkspacePages ??= [];
        if (WorkspacePages.Count == 0)
        {
            WorkspacePages.Add(new WorkspacePage
            {
                Name = "INICIO",
                Widgets = Widgets,
                TimerWidget = TimerWidget,
                TimerPositionCustomized = TimerPositionCustomized
            });
            foreach (var saved in Layouts.Where(pair => pair.Value is { Count: > 0 }))
                WorkspacePages.Add(new WorkspacePage { Name = saved.Key, Widgets = saved.Value });
        }
        for (int i = 0; i < WorkspacePages.Count; i++)
        {
            var page = WorkspacePages[i];
            page.Widgets ??= [];
            page.Widgets.RemoveAll(widget => string.Equals(widget.Kind, "agent", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(page.Name)) page.Name = i == 0 ? "INICIO" : $"PÁGINA {i + 1:00}";
            if (page.TimerWidget is not null)
            {
                page.TimerWidget.Kind = "timer";
                if (string.IsNullOrWhiteSpace(page.TimerWidget.Title)) page.TimerWidget.Title = "POMODORO";
            }
        }
        EnsureWorkspacePageCoordinates();
        ActiveWorkspacePage = Math.Clamp(ActiveWorkspacePage, 0, WorkspacePages.Count - 1);
        if (HomePageId is { } home && WorkspacePages.All(page => page.Id != home))
            HomePageId = WorkspacePages.Count > 0 ? WorkspacePages[0].Id : null;
        AgentWidget ??= new WidgetConfig { Kind = "agent", Title = "PomoDock", Width = 360, Height = 520 };
        AgentWidget.Kind = "agent";
        if (AgentWidget.Width < 280) AgentWidget.Width = 360;
        if (AgentWidget.Height < 280) AgentWidget.Height = 520;
    }

    /// <summary>
    /// Legacy saves were a flat left-to-right list with no Col/Row. Those deserialize as every
    /// page sitting on (0,0). Spread them across row 0 so navigation keeps working, and repair
    /// any later collision the same way.
    /// </summary>
    private void EnsureWorkspacePageCoordinates()
    {
        if (WorkspacePages.Count == 0) return;
        bool collided = WorkspacePages.GroupBy(page => (page.Col, page.Row)).Any(group => group.Count() > 1);
        if (!collided) return;
        for (int i = 0; i < WorkspacePages.Count; i++)
        {
            WorkspacePages[i].Col = i;
            WorkspacePages[i].Row = 0;
        }
    }
    public double Duration(Phase phase) => 60 * (phase == Phase.Focus ? FocusMinutes : phase == Phase.ShortBreak ? ShortMinutes : LongMinutes);
}
