using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NAudio.Wave;
using PomoDock.Core;
using Whisper.net;

namespace PomoDock.App;

/// <summary>
/// Hold-to-talk voice note for the agent chat: one live preview callback, cancel discards audio.
/// Reuses the same local Whisper model as field dictation.
/// </summary>
internal sealed class AgentVoiceNote : IDisposable
{
    private const int SampleRate = 16_000;
    private const string ModelFile = "ggml-small-q5_1.bin";
    private const long ModelBytes = 190_085_487;

    private readonly MainWindow owner;
    private readonly Action<string> onPreview;
    private WaveInEvent? capture;
    private WaveFileWriter? writer;
    private string? path;
    private bool cancelled;
    private bool recording;
    private bool disposed;

    public AgentVoiceNote(MainWindow owner, Action<string> onPreview)
    {
        this.owner = owner;
        this.onPreview = onPreview;
    }

    public void Start()
    {
        if (recording || disposed) return;
        cancelled = false;
        path = Path.Combine(Path.GetTempPath(), "pomodock-agent-voice-" + Guid.NewGuid().ToString("N") + ".wav");
        capture = new WaveInEvent { WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 50 };
        writer = new WaveFileWriter(path, capture.WaveFormat);
        capture.DataAvailable += (_, e) =>
        {
            if (cancelled || writer is null) return;
            writer.Write(e.Buffer, 0, e.BytesRecorded);
        };
        capture.StartRecording();
        recording = true;
        onPreview(L.T("agent.voiceListening"));
    }

    public void Cancel()
    {
        cancelled = true;
        StopCapture();
        CleanupFile();
        onPreview(L.T("agent.voiceCancelled"));
    }

    public async Task<string> StopAndTranscribeAsync()
    {
        if (cancelled) return "";
        StopCapture();
        if (path is null || !File.Exists(path) || new FileInfo(path).Length < 8_000)
        {
            CleanupFile();
            return "";
        }

        var model = Path.Combine(owner.Store.DirectoryPath, "models", ModelFile);
        if (!File.Exists(model) || new FileInfo(model).Length != ModelBytes)
        {
            CleanupFile();
            onPreview(L.T("agent.voiceNeedModel"));
            return "";
        }

        onPreview(L.T("agent.voiceTranscribing"));
        try
        {
            using var factory = WhisperFactory.FromPath(model);
            await using var processor = factory.CreateBuilder().WithLanguage(Strings.Code).Build();
            await using var audio = File.OpenRead(path);
            var text = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(audio))
                text.Append(segment.Text);
            var clean = Clean(text.ToString());
            onPreview(clean);
            return clean;
        }
        finally
        {
            CleanupFile();
        }
    }

    private void StopCapture()
    {
        if (!recording) return;
        recording = false;
        try { capture?.StopRecording(); } catch { /* ignore */ }
        capture?.Dispose();
        capture = null;
        writer?.Dispose();
        writer = null;
    }

    private void CleanupFile()
    {
        try { if (path is not null && File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
        path = null;
    }

    private static string Clean(string value)
    {
        value = Regex.Replace(value ?? "", @"\s+", " ").Trim();
        return value;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Cancel();
    }
}
