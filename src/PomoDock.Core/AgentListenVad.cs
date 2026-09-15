namespace PomoDock.Core;

public enum AgentVadDecision { None, BeginUtterance, Continue, EndSilence, AbortMusic, EndTimeout }

/// <summary>
/// Adaptive energy VAD: noise floor + envelope continuity. Continuous music stays
/// high with little dip-to-floor, so it never counts as a spoken turn.
/// </summary>
public sealed class AgentListenVad
{
    public const int FrameMilliseconds = 50;
    private const int Window = 20;
    private const int OnsetFrames = 3;
    private const int MusicConfirmFrames = 16;
    private const int QuietIdle = 12;
    private const int QuietArmed = 14;
    private const int MinIdle = 8;
    private const int MinArmed = 10;
    private const int MaxIdle = 160;
    private const int MaxArmed = 200;

    private readonly Queue<double> recent = new();
    private readonly List<double> capture = [];
    private int loud;
    private int quiet;
    private int captureFrames;
    private bool sawQuiet = true;

    public bool Armed { get; set; }
    public bool Capturing { get; private set; }
    public double NoiseFloor { get; private set; } = 0.008;
    public double LastRms { get; private set; }

    public AgentVadDecision Push(double rms)
    {
        LastRms = rms;
        if (rms < 0) rms = 0;
        recent.Enqueue(rms);
        while (recent.Count > Window) recent.Dequeue();
        AdaptFloor(rms);

        if (!Capturing) return Idle(rms);
        capture.Add(rms);
        captureFrames++;
        double speech = SpeechThreshold();
        if (rms < speech * 0.55) quiet++;
        else quiet = 0;

        if (LooksLikeMusic())
        {
            ResetCapture();
            return AgentVadDecision.AbortMusic;
        }

        int minFrames = Armed ? MinArmed : MinIdle;
        int maxFrames = Armed ? MaxArmed : MaxIdle;
        int needQuiet = Armed ? QuietArmed : QuietIdle;
        if (quiet >= needQuiet && captureFrames >= minFrames)
        {
            ResetCapture();
            return AgentVadDecision.EndSilence;
        }
        if (captureFrames >= maxFrames)
        {
            ResetCapture();
            return AgentVadDecision.EndTimeout;
        }
        return AgentVadDecision.Continue;
    }

    public void ResetCapture()
    {
        Capturing = false;
        loud = 0;
        quiet = 0;
        captureFrames = 0;
        capture.Clear();
    }

    public void Reset()
    {
        Armed = false;
        sawQuiet = true;
        NoiseFloor = 0.008;
        recent.Clear();
        ResetCapture();
    }

    private AgentVadDecision Idle(double rms)
    {
        double speech = SpeechThreshold();
        if (rms < speech * 0.7)
        {
            sawQuiet = true;
            loud = 0;
            return AgentVadDecision.None;
        }
        loud++;
        if (loud < OnsetFrames) return AgentVadDecision.None;
        if (LooksLikeSteadyNoise())
        {
            loud = 0;
            return AgentVadDecision.None;
        }
        if (!Armed && !sawQuiet) return AgentVadDecision.None;

        Capturing = true;
        quiet = 0;
        captureFrames = 0;
        capture.Clear();
        foreach (double sample in recent) capture.Add(sample);
        captureFrames = capture.Count;
        sawQuiet = false;
        return AgentVadDecision.BeginUtterance;
    }

    private void AdaptFloor(double rms)
    {
        if (rms <= NoiseFloor * 1.7 || rms < 0.014)
        {
            double rate = rms < NoiseFloor ? 0.18 : 0.04;
            NoiseFloor += (rms - NoiseFloor) * rate;
        }
        NoiseFloor = Math.Clamp(NoiseFloor, 0.003, 0.04);
    }

    private double SpeechThreshold() =>
        Math.Max(Math.Max(NoiseFloor * 2.6, NoiseFloor + 0.01), 0.015);

    private bool LooksLikeSteadyNoise()
    {
        if (recent.Count < 8) return Continuity(recent) > 0.72;
        return LooksLikeMusicWindow(recent);
    }

    private bool LooksLikeMusic()
    {
        if (captureFrames < MusicConfirmFrames) return false;
        return LooksLikeMusicWindow(capture);
    }

    private bool LooksLikeMusicWindow(IReadOnlyCollection<double> samples)
    {
        if (samples.Count < 8) return false;
        double min = double.MaxValue, max = 0, sum = 0;
        foreach (double value in samples)
        {
            if (value < min) min = value;
            if (value > max) max = value;
            sum += value;
        }
        double mean = sum / samples.Count;
        double floor = NoiseFloor;
        if (mean < Math.Max(floor * 2.2, 0.02)) return false;
        if (min <= floor * 2.15) return false;
        double continuity = Continuity(samples);
        return continuity >= 0.56 && min > floor * 2.15;
    }

    private static double Continuity(IReadOnlyCollection<double> samples)
    {
        if (samples.Count == 0) return 0;
        var ordered = samples.OrderBy(value => value).ToList();
        double p20 = At(ordered, 0.20);
        double p80 = At(ordered, 0.80);
        if (p80 < 1e-6) return 0;
        return p20 / p80;
    }

    private static double At(List<double> ordered, double percentile)
    {
        double index = percentile * (ordered.Count - 1);
        int low = (int)index;
        int high = Math.Min(low + 1, ordered.Count - 1);
        double mix = index - low;
        return ordered[low] * (1 - mix) + ordered[high] * mix;
    }
}
