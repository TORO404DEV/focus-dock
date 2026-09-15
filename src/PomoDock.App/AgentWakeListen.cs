using System.IO;
using NAudio.Wave;
using PomoDock.Core;
using Whisper.net;

namespace PomoDock.App;

internal enum AgentListenState { Off, Idle, Armed, Working }

/// <summary>
/// Always-on microphone: idle wake-spotting (no ✦ pulse), then ARM to capture a command
/// until real silence. Music is aborted by the VAD and never sent to Whisper.
/// </summary>
internal sealed class AgentWakeListen : IDisposable
{
    private const int SampleRate = 16_000;
    private const int LogCap = 40;
    private readonly MainWindow owner;
    private readonly object gate = new();
    private readonly AgentListenVad vad = new();
    private readonly List<AgentListenLogEntry> history = [];
    private WaveInEvent? capture;
    private MemoryStream? speech;
    private readonly Queue<byte[]> preroll = new();
    private WhisperFactory? factory;
    private bool enabled;
    private int pauseDepth;
    private bool transcribing;
    private int armedQuiet;
    private DateTime lastMusicLog = DateTime.MinValue;
    private DateTime lastMissLog = DateTime.MinValue;
    private bool disposed;

    public AgentListenState State { get; private set; } = AgentListenState.Off;
    public bool Enabled => enabled;
    public IReadOnlyList<AgentListenLogEntry> History { get { lock (gate) return history.ToList(); } }
    public event Action? Changed;
    public event Action<AgentListenLogEntry>? Logged;

    public AgentWakeListen(MainWindow owner) => this.owner = owner;

    public void SetEnabled(bool on)
    {
        if (disposed) return;
        if (!on)
        {
            enabled = false;
            StopCapture();
            ResetUtterance();
            vad.Reset();
            SetState(AgentListenState.Off);
            return;
        }
        if (enabled && capture is not null) return;
        if (WaveInEvent.DeviceCount == 0) throw new InvalidOperationException(L.T("agent.listenNoMic"));
        string model = ModelPath();
        if (!File.Exists(model)) throw new FileNotFoundException(L.T("agent.listenNoModel"), model);
        enabled = true;
        if (pauseDepth == 0) StartCapture();
        else SetState(AgentListenState.Idle);
    }

    public void Pause()
    {
        pauseDepth++;
        StopCapture();
        ResetUtterance();
        vad.ResetCapture();
        if (enabled) SetState(vad.Armed ? AgentListenState.Armed : AgentListenState.Idle);
    }

    public void Resume()
    {
        pauseDepth = Math.Max(0, pauseDepth - 1);
        if (pauseDepth == 0 && enabled && !disposed) StartCapture();
    }

    private void StartCapture()
    {
        if (capture is not null || pauseDepth > 0 || !enabled) return;
        var device = new WaveInEvent { DeviceNumber = -1, BufferMilliseconds = 50, WaveFormat = new WaveFormat(SampleRate, 16, 1) };
        device.DataAvailable += OnAudio;
        try
        {
            device.StartRecording();
            capture = device;
            SetState(vad.Armed ? AgentListenState.Armed : AgentListenState.Idle);
        }
        catch
        {
            device.DataAvailable -= OnAudio;
            device.Dispose();
            throw;
        }
    }

    private void StopCapture()
    {
        if (capture is null) return;
        try { capture.DataAvailable -= OnAudio; capture.StopRecording(); } catch { }
        capture.Dispose();
        capture = null;
    }

    private void OnAudio(object? sender, WaveInEventArgs e)
    {
        if (pauseDepth > 0 || transcribing || !enabled) return;
        var copy = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);
        double rms = Rms(copy);
        byte[]? pcm = null;
        AgentVadDecision decision;
        bool armed;
        lock (gate)
        {
            preroll.Enqueue(copy);
            while (preroll.Count > 8) preroll.Dequeue();
            decision = vad.Push(rms);
            armed = vad.Armed;
            if (vad.Capturing) speech?.Write(copy, 0, copy.Length);

            if (decision == AgentVadDecision.BeginUtterance)
            {
                speech?.Dispose();
                speech = new MemoryStream();
                foreach (var chunk in preroll) speech.Write(chunk, 0, chunk.Length);
                armedQuiet = 0;
                if (armed) owner.Dispatcher.BeginInvoke(() => SetState(AgentListenState.Armed));
                return;
            }

            if (!vad.Capturing && armed)
            {
                armedQuiet++;
                if (armedQuiet >= 160)
                {
                    vad.Armed = false;
                    armedQuiet = 0;
                    ResetUtterance();
                    Note(AgentListenLogKind.Timeout);
                    owner.Dispatcher.BeginInvoke(() => SetState(enabled ? AgentListenState.Idle : AgentListenState.Off));
                }
                return;
            }

            if (decision is AgentVadDecision.AbortMusic)
            {
                ResetUtterance();
                if (ShouldThrottle(ref lastMusicLog, 4)) Note(AgentListenLogKind.Music);
                owner.Dispatcher.BeginInvoke(() => SetState(enabled
                    ? (vad.Armed ? AgentListenState.Armed : AgentListenState.Idle)
                    : AgentListenState.Off));
                return;
            }

            if (decision is not (AgentVadDecision.EndSilence or AgentVadDecision.EndTimeout)) return;
            pcm = speech?.ToArray() ?? [];
            ResetUtterance();
            if (pcm.Length < 12_000)
            {
                pcm = null;
                if (decision == AgentVadDecision.EndTimeout && !armed && ShouldThrottle(ref lastMusicLog, 4))
                    Note(AgentListenLogKind.Music);
            }
            else transcribing = true;
        }

        if (pcm is null)
        {
            owner.Dispatcher.BeginInvoke(() => SetState(enabled
                ? (armed ? AgentListenState.Armed : AgentListenState.Idle)
                : AgentListenState.Off));
            return;
        }
        owner.Dispatcher.BeginInvoke(() => SetState(AgentListenState.Working));
        var payload = pcm;
        _ = Task.Run(() => TranscribeThenDispatch(payload));
    }

    private void ResetUtterance()
    {
        speech?.Dispose();
        speech = null;
        vad.ResetCapture();
    }

    private void SetState(AgentListenState next)
    {
        if (State == next) return;
        State = next;
        Changed?.Invoke();
    }

    private async Task TranscribeThenDispatch(byte[] speechPcm)
    {
        try
        {
            string text = await Transcribe(speechPcm);
            _ = owner.Dispatcher.BeginInvoke(() => Finish(text));
        }
        catch (Exception ex)
        {
            _ = owner.Dispatcher.BeginInvoke(() =>
            {
                lock (gate) transcribing = false;
                SetState(enabled ? (vad.Armed ? AgentListenState.Armed : AgentListenState.Idle) : AgentListenState.Off);
                Note(AgentListenLogKind.Error, ex.Message);
                owner.Status(ex.Message);
            });
        }
    }

    private void Finish(string text)
    {
        lock (gate) transcribing = false;
        string trimmed = (text ?? "").Trim();
        if (IsNoise(trimmed))
        {
            if (trimmed.Length > 0 && ShouldThrottle(ref lastMissLog, 4))
                Note(AgentListenLogKind.Noise, trimmed);
            SetState(enabled ? (vad.Armed ? AgentListenState.Armed : AgentListenState.Idle) : AgentListenState.Off);
            return;
        }
        if (trimmed.Length > 0) Note(AgentListenLogKind.Transcript, trimmed);

        string? spoken = AgentWakePhrase.TakeSpokenOrder(trimmed, owner.Settings.AgentWakeName);
        if (vad.Armed)
        {
            if (spoken is { Length: 0 })
            {
                armedQuiet = 0;
                Note(AgentListenLogKind.Armed);
                SetState(AgentListenState.Armed);
                return;
            }
            vad.Armed = false;
            armedQuiet = 0;
            SetState(enabled ? AgentListenState.Idle : AgentListenState.Off);
            if (spoken is null)
            {
                if (trimmed.Length > 0 && ShouldThrottle(ref lastMissLog, 2.5))
                    Note(AgentListenLogKind.Miss, trimmed);
                return;
            }
            Note(AgentListenLogKind.Command, spoken);
            owner.HandleWakeCommand(spoken);
            return;
        }

        if (spoken is null)
        {
            if (trimmed.Length > 0 && ShouldThrottle(ref lastMissLog, 2.5))
                Note(AgentListenLogKind.Miss, trimmed);
            SetState(enabled ? AgentListenState.Idle : AgentListenState.Off);
            return;
        }

        if (AgentWakePhrase.Heard(trimmed, owner.Settings.AgentWakeName))
            Note(AgentListenLogKind.Wake, owner.Settings.AgentWakeName);
        if (spoken.Length == 0)
        {
            vad.Armed = true;
            armedQuiet = 0;
            Note(AgentListenLogKind.Armed);
            SetState(AgentListenState.Armed);
            owner.HandleWakeCommand("");
            return;
        }

        Note(AgentListenLogKind.Command, spoken);
        SetState(enabled ? AgentListenState.Idle : AgentListenState.Off);
        owner.HandleWakeCommand(spoken);
    }

    private async Task<string> Transcribe(byte[] pcm)
    {
        string path = ModelPath();
        string waveFile = Path.Combine(Path.GetTempPath(), $"pomodock-listen-{Guid.NewGuid():N}.wav");
        await using (var writer = new WaveFileWriter(waveFile, new WaveFormat(SampleRate, 16, 1)))
            writer.Write(pcm, 0, pcm.Length);
        try
        {
            factory ??= WhisperFactory.FromPath(path);
            await using var processor = factory.CreateBuilder().WithLanguage("auto").Build();
            await using var audio = File.OpenRead(waveFile);
            var parts = new System.Text.StringBuilder();
            await foreach (var result in processor.ProcessAsync(audio))
                if (!string.IsNullOrWhiteSpace(result.Text)) parts.Append(result.Text.Trim()).Append(' ');
            return parts.ToString().Trim();
        }
        finally { try { File.Delete(waveFile); } catch { } }
    }

    private string ModelPath() => Path.Combine(owner.Store.DirectoryPath, "models", "ggml-small-q5_1.bin");

    private void Note(AgentListenLogKind kind, string detail = "")
    {
        var entry = AgentListenLogEntry.Now(kind, detail);
        lock (gate)
        {
            history.Add(entry);
            while (history.Count > LogCap) history.RemoveAt(0);
        }
        owner.Dispatcher.BeginInvoke(() => Logged?.Invoke(entry));
    }

    private static bool ShouldThrottle(ref DateTime last, double seconds)
    {
        var now = DateTime.UtcNow;
        if (now - last < TimeSpan.FromSeconds(seconds)) return false;
        last = now;
        return true;
    }

    private static bool IsNoise(string text)
    {
        string folded = AgentWakePhrase.Fold(text);
        return WhisperTranscript.IsJunk(text)
            || folded.Length == 0
            || folded is "thank you" or "thanks" or "thanks for watching" or "you" or "music"
            || folded.StartsWith("subtitle", StringComparison.Ordinal)
            || folded is "subtitles" or "applause" or "laughing" or "musica" or "musica de fondo";
    }

    private static double Rms(byte[] buffer)
    {
        if (buffer.Length < 4) return 0;
        double sum = 0;
        int samples = buffer.Length / 2;
        for (int i = 0; i < buffer.Length - 1; i += 2)
        {
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            double n = sample / 32768.0;
            sum += n * n;
        }
        return Math.Sqrt(sum / Math.Max(1, samples));
    }

    public void Dispose()
    {
        disposed = true;
        enabled = false;
        StopCapture();
        ResetUtterance();
        vad.Reset();
        try { factory?.Dispose(); } catch { }
        factory = null;
        State = AgentListenState.Off;
    }
}
