using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;
using PomoDock.Core;
using Whisper.net;

namespace PomoDock.App;

/// <summary>Hold to talk, release to insert, drag off the button to cancel. One preview in the composer.</summary>
internal sealed class AgentHoldMic : IDisposable
{
    private readonly MainWindow owner;
    private readonly TextBox input;
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private WaveInEvent? capture;
    private MemoryStream? buffer;
    private DateTime started;
    private bool holding;
    private bool cancelled;
    private string original = "";
    private Button? button;
    private object? buttonContent;

    public AgentHoldMic(MainWindow owner, TextBox input)
    {
        this.owner = owner;
        this.input = input;
        clock.Tick += (_, _) =>
        {
            if (button is null || !holding) return;
            var elapsed = DateTime.UtcNow - started;
            button.Content = elapsed.TotalSeconds < 1 ? "●" : elapsed.ToString(@"m\:ss");
        };
    }

    public void Begin(Button source)
    {
        if (holding) return;
        try
        {
            if (WaveInEvent.DeviceCount == 0) throw new InvalidOperationException(L.T("dictation.noMicrophone"));
            cancelled = false; holding = true; original = input.Text; started = DateTime.UtcNow;
            button = source; buttonContent = source.Content;
            buffer = new MemoryStream();
            capture = new WaveInEvent { DeviceNumber = -1, BufferMilliseconds = 80, WaveFormat = new WaveFormat(16_000, 16, 1) };
            capture.DataAvailable += (_, e) => buffer?.Write(e.Buffer, 0, e.BytesRecorded);
            capture.StartRecording();
            source.Background = (Brush)Application.Current.Resources["Accent"];
            source.Content = "●";
            clock.Start();
            owner.Status(L.T("agent.recording"));
        }
        catch (Exception ex)
        {
            holding = false;
            Dialogs.Alert(owner, L.T("agent.title"), ex.Message);
        }
    }

    public async Task End(Func<Task>? send)
    {
        if (!holding) return;
        holding = false;
        clock.Stop();
        RestoreButton();
        try { capture?.StopRecording(); } catch { }
        capture?.Dispose(); capture = null;
        var pcm = buffer?.ToArray() ?? [];
        buffer?.Dispose(); buffer = null;
        if (cancelled)
        {
            input.Text = original;
            owner.Status("");
            return;
        }
        if ((DateTime.UtcNow - started).TotalMilliseconds < 280 || pcm.Length < 8_000)
        {
            input.Text = original;
            owner.Status(L.T("agent.holdHint"));
            return;
        }
        input.Text = original;
        owner.Status(L.T("dictation.preparing"));
        try
        {
            string text = await Transcribe(pcm);
            if (!string.IsNullOrWhiteSpace(text))
                input.Text = original.Length == 0 ? text : original.TrimEnd() + " " + text;
            input.CaretIndex = input.Text.Length;
            owner.Status("");
            if (send is not null && input.Text.Trim().Length > 0) await send();
        }
        catch (Exception ex)
        {
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
        RestoreButton();
        owner.Status(L.T("agent.cancelled"));
    }

    private void RestoreButton()
    {
        if (button is null) return;
        button.ClearValue(Control.BackgroundProperty);
        button.Content = buttonContent ?? "●";
        button = null;
    }

    private async Task<string> Transcribe(byte[] pcm)
    {
        string path = Path.Combine(owner.Store.DirectoryPath, "models", "ggml-small-q5_1.bin");
        if (!File.Exists(path)) throw new FileNotFoundException(L.T("dictation.error", path));
        string wave = Path.Combine(Path.GetTempPath(), $"pomodock-agent-mic-{Guid.NewGuid():N}.wav");
        await using (var writer = new WaveFileWriter(wave, new WaveFormat(16_000, 16, 1)))
            writer.Write(pcm, 0, pcm.Length);
        try
        {
            using var factory = WhisperFactory.FromPath(path);
            await using var processor = factory.CreateBuilder().WithLanguage(Strings.Code).Build();
            await using var audio = File.OpenRead(wave);
            var parts = new System.Text.StringBuilder();
            await foreach (var result in processor.ProcessAsync(audio))
                if (!string.IsNullOrWhiteSpace(result.Text)) parts.Append(result.Text.Trim()).Append(' ');
            return parts.ToString().Trim();
        }
        finally { try { File.Delete(wave); } catch { } }
    }

    public void Dispose()
    {
        clock.Stop();
        Cancel();
    }
}
