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
    public bool Dark { get; set; }
    public bool ReduceMotion { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool TimerAtBottom { get; set; }
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
    public void Validate()
    {
        FocusMinutes = Math.Clamp(FocusMinutes, 1, 180);
        ShortMinutes = Math.Clamp(ShortMinutes, 1, 60);
        LongMinutes = Math.Clamp(LongMinutes, 1, 120);
        LongInterval = Math.Clamp(LongInterval, 1, 12);
        DailyGoalMinutes = Math.Clamp(DailyGoalMinutes, 1, 1440);
        WhiteNoiseVolume = Math.Clamp(WhiteNoiseVolume, 0, 100);
        AlarmRepeats = Math.Clamp(AlarmRepeats, 1, 8);
    }
    public double Duration(Phase phase) => 60 * (phase == Phase.Focus ? FocusMinutes : phase == Phase.ShortBreak ? ShortMinutes : LongMinutes);
}
