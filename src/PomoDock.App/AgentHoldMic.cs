using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.Wave;
using PomoDock.Core;
using Whisper.net;

namespace PomoDock.App;

/// <summary>
/// Hold-to-talk as a voice note: the strip shows time and a live level. Release inserts
/// one transcription into the composer. Drag off the strip to cancel.
/// </summary>
internal sealed class AgentHoldMic : IDisposable
{
    public event Action<bool>? RecordingChanged;

    private readonly MainWindow owner;
    private readonly TextBox input;
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly double[] peaks = new double[28];
    private WaveInEvent? capture;
    private MemoryStream? buffer;
    private DateTime started;
    private bool holding;
    private bool cancelled;
    private string original = "";
    private double lastPeak;

    public Border Strip { get; }
    public bool Holding => holding;

    private readonly TextBlock clockLabel = new();
    private readonly TextBlock hint = new();
    private readonly TextBlock recDot = new();
    private readonly StackPanel wave = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

    public AgentHoldMic(MainWindow owner, TextBox input)
    {
        this.owner = owner;
        this.input = input;
        for (int i = 0; i < peaks.Length; i++)
            wave.Children.Add(new Rectangle
            {
                Width = 3, Height = 4, Margin = new Thickness(1, 0, 1, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = AgendaVisuals.Resource("Ink"), RadiusX = 1, RadiusY = 1
            });

        clockLabel.FontFamily = new FontFamily("Consolas");
        clockLabel.FontSize = 14;
        clockLabel.FontWeight = FontWeights.Bold;
        clockLabel.VerticalAlignment = VerticalAlignment.Center;
        clockLabel.Margin = new Thickness(0, 0, 14, 0);
        hint.FontFamily = new FontFamily("Consolas");
        hint.FontSize = 8;
        hint.Foreground = AgendaVisuals.Resource("Muted");
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.Margin = new Thickness(12, 0, 0, 0);
        hint.TextWrapping = TextWrapping.Wrap;

        var row = new Grid { Margin = new Thickness(12, 0, 12, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        recDot.Text = "●"; recDot.FontSize = 16; recDot.FontWeight = FontWeights.Bold;
        recDot.VerticalAlignment = VerticalAlignment.Center; recDot.Margin = new Thickness(0, 0, 8, 0);
        var live = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        live.Children.Add(recDot);
        live.Children.Add(clockLabel);
        row.Children.Add(live);
        Grid.SetColumn(wave, 1); row.Children.Add(wave);
        Grid.SetColumn(hint, 2); row.Children.Add(hint);

        Strip = new Border
        {
            BorderBrush = AgendaVisuals.Resource("Edge"),
            BorderThickness = new Thickness(2),
            Background = AgendaVisuals.Resource("Accent"),
            Height = 56,
            Visibility = Visibility.Collapsed,
            Child = row
        };
        Strip.MouseLeave += (_, _) =>
        {
            if (holding && Mouse.LeftButton == MouseButtonState.Pressed) Cancel();
        };

        clock.Tick += (_, _) =>
        {
            if (!holding) return;
            clockLabel.Text = (DateTime.UtcNow - started).ToString(@"m\:ss");
            recDot.Opacity = 0.3 + 0.7 * (0.5 + 0.5 * Math.Sin((DateTime.UtcNow - started).TotalSeconds * 7));
            PaintWave();
        };
    }

    private Func<Task>? onRelease;

    public void Begin(Func<Task>? send = null)
    {
        if (holding) return;
        onRelease = send;
        try
        {
            if (WaveInEvent.DeviceCount == 0) throw new InvalidOperationException(L.T("dictation.noMicrophone"));
            cancelled = false; holding = true; original = input.Text; started = DateTime.UtcNow; lastPeak = 0;
            Array.Clear(peaks);
            buffer = new MemoryStream();
            capture = new WaveInEvent { DeviceNumber = -1, BufferMilliseconds = 50, WaveFormat = new WaveFormat(16_000, 16, 1) };
            capture.DataAvailable += OnAudio;
            capture.StartRecording();
            clockLabel.Text = "0:00";
            hint.Text = L.T("agent.recordingHint");
            Strip.Visibility = Visibility.Visible;
            clock.Start();
            RecordingChanged?.Invoke(true);
        }
        catch (Exception ex)
        {
            holding = false;
            Strip.Visibility = Visibility.Collapsed;
            owner.Status(ex.Message);
        }
    }

    public async Task End(Func<Task>? send)
    {
        if (!holding) return;
        holding = false;
        clock.Stop();
        try { capture?.StopRecording(); } catch { }
        capture?.Dispose(); capture = null;
        var pcm = buffer?.ToArray() ?? [];
        buffer?.Dispose(); buffer = null;
        RecordingChanged?.Invoke(false);

        if (cancelled)
        {
            input.Text = original;
            HideStrip();
            return;
        }
        if ((DateTime.UtcNow - started).TotalMilliseconds < 280 || pcm.Length < 8_000)
        {
            input.Text = original;
            HideStrip();
            owner.Status(L.T("agent.holdHint"));
            return;
        }

        hint.Text = L.T("agent.transcribing");
        clockLabel.Text = L.T("agent.voiceNote");
        try
        {
            string text = await Transcribe(pcm);
            HideStrip();
            if (!string.IsNullOrWhiteSpace(text))
                input.Text = original.Length == 0 ? text : original.TrimEnd() + " " + text;
            else input.Text = original;
            input.CaretIndex = input.Text.Length;
            input.Focus();
            if (send is not null && input.Text.Trim().Length > 0) await send();
        }
        catch (Exception ex)
        {
            HideStrip();
            input.Text = original;
            owner.Status(ex.Message);
        }
    }

    public void Cancel()
    {
        if (!holding) return;
        cancelled = true;
        clock.Stop();
        try { capture?.StopRecording(); } catch { }
        capture?.Dispose(); capture = null;
        buffer?.Dispose(); buffer = null;
        holding = false;
        input.Text = original;
        HideStrip();
        RecordingChanged?.Invoke(false);
    }

    private void HideStrip()
    {
        Strip.Visibility = Visibility.Collapsed;
        Array.Clear(peaks);
    }

    private void OnAudio(object? sender, WaveInEventArgs e)
    {
        buffer?.Write(e.Buffer, 0, e.BytesRecorded);
        if (e.BytesRecorded < 4) return;
        double sum = 0;
        int samples = e.BytesRecorded / 2;
        for (int i = 0; i < e.BytesRecorded - 1; i += 2)
        {
            short sample = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
            double n = sample / 32768.0;
            sum += n * n;
        }
        lastPeak = Math.Sqrt(sum / Math.Max(1, samples));
    }

    private void PaintWave()
    {
        Array.Copy(peaks, 1, peaks, 0, peaks.Length - 1);
        peaks[^1] = lastPeak;
        for (int i = 0; i < wave.Children.Count && i < peaks.Length; i++)
        {
            if (wave.Children[i] is not Rectangle bar) continue;
            bar.Height = Math.Clamp(4 + peaks[i] * 36, 4, 36);
            bar.Fill = AgendaVisuals.Resource("Ink");
        }
    }

    private async Task<string> Transcribe(byte[] pcm)
    {
        string path = System.IO.Path.Combine(owner.Store.DirectoryPath, "models", "ggml-small-q5_1.bin");
        if (!File.Exists(path)) throw new FileNotFoundException(L.T("dictation.error", path));
        string waveFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pomodock-agent-mic-{Guid.NewGuid():N}.wav");
        await using (var writer = new WaveFileWriter(waveFile, new WaveFormat(16_000, 16, 1)))
            writer.Write(pcm, 0, pcm.Length);
        try
        {
            using var factory = WhisperFactory.FromPath(path);
            await using var processor = factory.CreateBuilder().WithLanguage(Strings.Code).Build();
            await using var audio = File.OpenRead(waveFile);
            var parts = new System.Text.StringBuilder();
            await foreach (var result in processor.ProcessAsync(audio))
                if (!string.IsNullOrWhiteSpace(result.Text)) parts.Append(result.Text.Trim()).Append(' ');
            return parts.ToString().Trim();
        }
        finally { try { File.Delete(waveFile); } catch { } }
    }

    public void Dispose()
    {
        clock.Stop();
        Cancel();
    }
}
