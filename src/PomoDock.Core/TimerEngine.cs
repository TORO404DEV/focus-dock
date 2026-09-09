namespace PomoDock.Core;

/// <summary>Monotonic deltas measure work. Wall clock is only used to place segments in reports.</summary>
public sealed class TimerEngine
{
    public Phase Phase { get; private set; } = Phase.Focus;
    public bool Running { get; private set; }
    public Session? Active { get; private set; }
    public int CompletedFocus { get; private set; }
    public double Remaining => Math.Max(0, (Active?.PlannedSeconds ?? settings.Duration(Phase)) - (Active?.Seconds ?? 0));
    public event Action<Session>? Finished;
    public event Action? GapDetected;
    private readonly Settings settings;
    private double lastTick;
    private DateTimeOffset lastWall;
    public TimerEngine(Settings settings) => this.settings = settings;
    public void Start(DateTimeOffset utc, double monotonic, WorkTask? task = null)
    {
        if (Running) return;
        Active ??= new Session { Started = utc, Phase = Phase, PlannedSeconds = settings.Duration(Phase), Task = task?.Name ?? "Enfoque libre", TaskId = task?.Id, Project = task?.Project ?? "Sin proyecto" };
        lastTick = monotonic;
        lastWall = utc;
        Running = true;
    }
    public void Tick(DateTimeOffset utc, double monotonic)
    {
        if (!Running || Active is null) return;
        double delta = monotonic - lastTick;
        // A suspended/blocked machine is not evidence of focused work. Preserve only confirmed ticks.
        if (delta > 10 || delta < 0 || Math.Abs((utc - lastWall).TotalSeconds - delta) > 5)
        {
            Running = false;
            Active.Pauses++;
            GapDetected?.Invoke();
            return;
        }
        delta = Math.Min(delta, Remaining);
        if (delta > 0)
        {
            var end = lastWall.AddSeconds(delta);
            var segments = Active.Segments;
            if (segments.Count > 0 && Math.Abs((segments[^1].End - lastWall).TotalMilliseconds) < 2)
                segments[^1] = segments[^1] with { End = end };
            else segments.Add(new(lastWall, end));
        }
        lastTick = monotonic;
        lastWall = utc;
        if (Remaining <= 0.001) Finish(Outcome.Completed, utc);
    }
    public void Pause(DateTimeOffset utc, double monotonic)
    {
        if (!Running) return;
        Tick(utc, monotonic);
        if (Running && Active is not null) { Running = false; Active.Pauses++; }
    }
    public void Finish(Outcome outcome, DateTimeOffset utc)
    {
        Running = false;
        var session = Active;
        Active = null;
        if (session is null || session.Seconds < 0.001) return;
        session.Outcome = outcome;
        session.Ended = utc;
        if (outcome == Outcome.Completed && session.Phase == Phase.Focus) CompletedFocus++;
        Finished?.Invoke(session);
    }
    public void Select(Phase phase, DateTimeOffset utc, double monotonic)
    {
        Pause(utc, monotonic);
        Finish(Outcome.Partial, utc);
        Phase = phase;
    }
    public Phase NextPhase() => Phase == Phase.Focus
        ? (CompletedFocus > 0 && CompletedFocus % settings.LongInterval == 0 ? Phase.LongBreak : Phase.ShortBreak)
        : Phase.Focus;
    public void Restore(Session session)
    {
        if (session.Seconds >= session.PlannedSeconds) return;
        Active = session;
        Phase = session.Phase;
        Running = false;
        Active.Pauses++;
    }
}
