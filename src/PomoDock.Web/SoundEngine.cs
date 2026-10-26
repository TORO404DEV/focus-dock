using PomoDock.Core;

namespace PomoDock.Web;

/// <summary>
/// Plays PomoDock's generated sound library, exactly as the desktop does: every sound is the same
/// synthesis from <see cref="SoundLibrary"/>, rendered once in WebAssembly and handed to the page's
/// audio context as a WAV. Volume is applied by the browser, so one rendering serves every level.
/// </summary>
public sealed class SoundEngine
{
    private readonly Settings settings;
    private readonly Browser browser;
    /// <summary>Waves the page has already decoded; their bytes never cross the bridge twice.</summary>
    private readonly HashSet<string> sent = [];
    private readonly Dictionary<string, double> lengths = [];
    private string loopKey = "";
    /// <summary>The running ambience belongs to a focus session, not to a preview.</summary>
    private bool session;
    private CancellationTokenSource? preview;
    private int alarmGeneration;

    public SoundEngine(Settings settings, Browser browser)
    {
        this.settings = settings;
        this.browser = browser;
    }

    // ------------------------------------------------------------------ what the app asks for

    public void Button(string action)
    {
        if (settings.Sound && settings.ButtonSounds) PlayOnce(settings.ClickSound, action, settings.EffectsVolume);
    }

    public void Completed(Phase phase)
    {
        StopAlarm();
        if (!settings.Sound || !settings.AlarmEnabled) return;
        var id = phase == Phase.Focus ? settings.FocusEndSound : settings.BreakEndSound;
        if (SoundLibrary.Find(id) is null) return;
        if (!lengths.TryGetValue(id, out double seconds)) lengths[id] = seconds = SoundLibrary.Render(id).Seconds;
        int generation = alarmGeneration;
        // Long sounds are struck again before they fade, the way a real bell is rung.
        var gap = TimeSpan.FromSeconds(Math.Min(seconds, 2.4) + 0.15);
        PlayOnce(id, null, settings.AlarmVolume, true);
        for (int i = 1; i < Math.Clamp(settings.AlarmRepeats, 1, 8); i++)
            _ = After(gap * i, () => { if (generation == alarmGeneration) PlayOnce(id, null, settings.AlarmVolume, true); });
    }

    /// <summary>
    /// Acknowledges a completed timer alarm. It silences the voice that is playing and invalidates
    /// every repeat that has not fired yet, while leaving clicks, reminders and ambience alone.
    /// </summary>
    public void StopAlarm()
    {
        alarmGeneration++;
        browser.AudioStopAlarms();
    }

    public void Reminder()
    {
        if (settings.Sound) PlayOnce(settings.ReminderSound, null, settings.AlarmVolume);
    }

    /// <summary>Starts the session ambience, if one is chosen. The name predates the sound library.</summary>
    public void StartNoise()
    {
        session = true;
        ApplyAmbience();
    }

    public void StopNoise()
    {
        session = false;
        CancelPreview();
        StopLoop();
    }

    /// <summary>Follows a settings change made mid-session: another ambience, another volume, or silence.</summary>
    public void Refresh()
    {
        if (preview is null && session) ApplyAmbience();
    }

    /// <summary>Plays any sound of the library once, for the control panel. Ambiences play for a few seconds.</summary>
    public void Preview(string id)
    {
        switch (SoundLibrary.Find(id)?.Kind)
        {
            case SoundKind.Ambient:
                CancelPreview();
                StartLoop(id, settings.WhiteNoiseVolume);
                var cancel = preview = new CancellationTokenSource();
                _ = After(TimeSpan.FromSeconds(4), () =>
                {
                    if (cancel.IsCancellationRequested) return;
                    preview = null;
                    StopLoop();
                    if (session) ApplyAmbience();
                });
                break;
            case SoundKind.Click:
                PlayOnce(id, "start", settings.EffectsVolume);
                break;
            case SoundKind.Alarm:
            case SoundKind.Reminder:
                PlayOnce(id, null, settings.AlarmVolume);
                break;
        }
    }

    private void ApplyAmbience()
    {
        if (!settings.Sound || !settings.WhiteNoise || settings.WhiteNoiseVolume <= 0) { StopLoop(); return; }
        StartLoop(settings.AmbientSound, settings.WhiteNoiseVolume);
    }

    private void CancelPreview()
    {
        if (preview is null) return;
        preview.Cancel();
        preview = null;
    }

    // ------------------------------------------------------------------ one-shots

    private void PlayOnce(string id, string? action, int volume, bool alarm = false)
    {
        if (volume <= 0 || SoundLibrary.Find(id) is null) return;
        var key = action is null ? id : $"{id}-{action}";
        browser.AudioPlay(key, Wave(key, () => SoundLibrary.Render(id, action)), volume, alarm);
    }

    /// <summary>The WAV the first time a sound is asked for; afterwards the page already holds it.</summary>
    private byte[]? Wave(string key, Func<SoundClip> render)
    {
        var tagged = $"{key}-r{SoundLibrary.Revision}";
        if (sent.Contains(tagged) && browser.AudioHas(key)) return null;
        sent.Add(tagged);
        var clip = render();
        lengths[key] = clip.Seconds;
        return SoundLibrary.Wave(clip);
    }

    // ------------------------------------------------------------------ the ambience loop

    private void StartLoop(string id, int volume)
    {
        var key = $"{id}@{volume}";
        if (loopKey == key) return;
        loopKey = key;
        browser.AudioLoop(id, Wave(id, () => SoundLibrary.Render(id)), volume);
    }

    private void StopLoop()
    {
        loopKey = "";
        browser.AudioStopLoop();
    }

    private static async Task After(TimeSpan delay, Action action)
    {
        await Task.Delay(delay);
        action();
    }
}
