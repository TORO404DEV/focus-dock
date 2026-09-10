using System.IO;
using System.Media;
using System.Windows.Media;
using System.Windows.Threading;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// Plays PomoDock's generated sound library. Nothing ships as an audio file: each sound is
/// synthesised on first use and kept as a small WAV in a local cache. Short sounds go through
/// WPF's MediaPlayer, which mixes, so a reminder or a click never cuts the session ambience —
/// SoundPlayer can only play one sound per process. The ambience itself still loops through
/// SoundPlayer, the one thing that API does without a gap.
/// </summary>
public sealed class SoundEngine : IDisposable
{
    private readonly Settings settings;
    private readonly string cache = Path.Combine(Path.GetTempPath(), "PomoDock", "sounds");
    private readonly Dictionary<string, (string File, double Seconds)> prepared = [];
    private readonly List<MediaPlayer> players = [];
    private readonly HashSet<MediaPlayer> alarmPlayers = [];
    private readonly List<DispatcherTimer> timers = [];
    private readonly List<(SoundPlayer Player, MemoryStream Stream)> fallbacks = [];
    private readonly List<(SoundPlayer Player, MemoryStream Stream)> alarmFallbacks = [];
    private readonly object ambienceLock = new();
    private (string Id, SoundClip Clip)? ambience;
    private SoundPlayer? loop;
    private MemoryStream? loopStream;
    private string loopKey = "";
    private int loopGeneration;
    /// <summary>The running ambience belongs to a focus session, not to a preview.</summary>
    private bool session;
    private DispatcherTimer? preview;
    private bool mediaBroken;
    private int alarmGeneration;

    public SoundEngine(Settings settings) => this.settings = settings;

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
        if (Prepare(id, null) is not { } sound) return;
        int generation = alarmGeneration;
        // Long sounds are struck again before they fade, the way a real bell is rung.
        var gap = TimeSpan.FromSeconds(Math.Min(sound.Seconds, 2.4) + 0.15);
        PlayOnce(id, null, settings.AlarmVolume, true);
        for (int i = 1; i < Math.Clamp(settings.AlarmRepeats, 1, 8); i++)
            After(gap * i, () => { if (generation == alarmGeneration) PlayOnce(id, null, settings.AlarmVolume, true); });
    }

    /// <summary>
    /// Acknowledges a completed timer alarm. It silences the voice that is playing and invalidates
    /// every repeat that has not fired yet, while leaving clicks, reminders and ambience alone.
    /// </summary>
    public void StopAlarm()
    {
        alarmGeneration++;
        foreach (var player in alarmPlayers.ToArray()) Release(player);
        foreach (var entry in alarmFallbacks.ToArray())
        {
            alarmFallbacks.Remove(entry);
            fallbacks.Remove(entry);
            entry.Player.Stop();
            entry.Player.Dispose();
            entry.Stream.Dispose();
        }
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
                preview = After(TimeSpan.FromSeconds(4), () =>
                {
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
        preview.Stop();
        timers.Remove(preview);
        preview = null;
    }

    // ------------------------------------------------------------------ one-shots

    private void PlayOnce(string id, string? action, int volume, bool alarm = false)
    {
        if (volume <= 0 || Prepare(id, action) is not { } sound) return;
        if (mediaBroken) { PlayFallback(id, action, volume, sound.Seconds, alarm); return; }
        var player = new MediaPlayer { Volume = Math.Clamp(volume / 100d, 0, 1) };
        players.Add(player);
        if (alarm) alarmPlayers.Add(player);
        player.MediaEnded += (_, _) => Release(player);
        player.MediaFailed += (_, _) =>
        {
            if (!players.Contains(player)) return;
            // Windows editions without media components still get their sounds, one at a time.
            mediaBroken = true;
            Release(player);
            PlayFallback(id, action, volume, sound.Seconds, alarm);
        };
        player.Open(new Uri(sound.File));
        player.Play();
        // A player that never reports its end is let go anyway.
        After(TimeSpan.FromSeconds(sound.Seconds + 3), () => Release(player));
    }

    private void Release(MediaPlayer player)
    {
        if (!players.Remove(player)) return;
        alarmPlayers.Remove(player);
        player.Close();
    }

    private void PlayFallback(string id, string? action, int volume, double seconds, bool alarm)
    {
        try
        {
            var stream = new MemoryStream(SoundLibrary.Wave(SoundLibrary.Render(id, action).Scaled(volume / 100d)));
            var player = new SoundPlayer(stream);
            var entry = (player, stream);
            fallbacks.Add(entry);
            if (alarm) alarmFallbacks.Add(entry);
            player.Play();
            After(TimeSpan.FromSeconds(seconds + 2), () =>
            {
                alarmFallbacks.Remove(entry);
                if (!fallbacks.Remove(entry)) return;
                player.Dispose(); stream.Dispose();
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
    }

    /// <summary>The sound as a WAV file in the cache, written once and reused for the rest of the life of the app.</summary>
    private (string File, double Seconds)? Prepare(string id, string? action)
    {
        var key = action is null ? id : $"{id}-{action}";
        if (prepared.TryGetValue(key, out var known)) return known;
        if (SoundLibrary.Find(id) is null) return null;
        try
        {
            var clip = SoundLibrary.Render(id, action);
            var file = Path.Combine(cache, $"{key}-r{SoundLibrary.Revision}.wav");
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(cache);
                var temp = $"{file}.{Guid.NewGuid():N}.tmp";
                File.WriteAllBytes(temp, SoundLibrary.Wave(clip));
                File.Move(temp, file, true);
            }
            (string File, double Seconds) entry = (file, clip.Seconds);
            prepared[key] = entry;
            return entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // ------------------------------------------------------------------ the ambience loop

    private async void StartLoop(string id, int volume)
    {
        var key = $"{id}@{volume}";
        if (loop is not null && loopKey == key) return;
        int generation = ++loopGeneration;
        try
        {
            // Sixteen seconds of rain take a moment to synthesise: never on the UI thread.
            var clip = await Task.Run(() => Ambience(id));
            if (generation != loopGeneration) return;
            StopLoopPlayer();
            loopStream = new MemoryStream(SoundLibrary.Wave(clip.Scaled(volume / 100d)));
            loop = new SoundPlayer(loopStream);
            loop.Load();
            loop.PlayLooping();
            loopKey = key;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException) { StopLoopPlayer(); }
    }

    private SoundClip Ambience(string id)
    {
        lock (ambienceLock)
        {
            if (ambience is { } cached && cached.Id == id) return cached.Clip;
            var clip = SoundLibrary.Render(id);
            ambience = (id, clip);
            return clip;
        }
    }

    private void StopLoop()
    {
        loopGeneration++;
        StopLoopPlayer();
    }

    private void StopLoopPlayer()
    {
        loop?.Stop();
        loop?.Dispose();
        loopStream?.Dispose();
        loop = null;
        loopStream = null;
        loopKey = "";
    }

    private DispatcherTimer After(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timers.Add(timer);
        timer.Tick += (_, _) => { timer.Stop(); timers.Remove(timer); action(); };
        timer.Start();
        return timer;
    }

    public void Dispose()
    {
        StopAlarm();
        StopNoise();
        foreach (var timer in timers.ToArray()) timer.Stop();
        timers.Clear();
        foreach (var player in players.ToArray()) Release(player);
        foreach (var (player, stream) in fallbacks) { player.Dispose(); stream.Dispose(); }
        fallbacks.Clear();
    }

    internal int ActiveAlarmVoicesForDiagnostics => alarmPlayers.Count + alarmFallbacks.Count;
}
