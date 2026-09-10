using System.Media;
using System.IO;
using System.Windows.Threading;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>Small generated PCM sounds keep the app self-contained and avoid shipping audio assets.</summary>
public sealed class SoundEngine : IDisposable
{
    private readonly Settings settings;
    private SoundPlayer? noisePlayer;
    private MemoryStream? noiseStream;
    private readonly List<(SoundPlayer Player, MemoryStream Stream, DateTime Expires)> transient = [];
    private readonly DispatcherTimer cleanup = new() { Interval = TimeSpan.FromSeconds(2) };
    public SoundEngine(Settings settings)
    {
        this.settings = settings;
        cleanup.Tick += (_, _) =>
        {
            foreach (var item in transient.Where(x => x.Expires <= DateTime.UtcNow).ToArray())
            {
                item.Player.Dispose(); item.Stream.Dispose(); transient.Remove(item);
            }
        };
        cleanup.Start();
    }
    public void Button(string kind)
    {
        if (!settings.ButtonSounds) return;
        switch (kind) { case "start": Tone(720, 85, .18); break; case "pause": Tone(430, 75, .14); break; case "skip": Tone(560, 55, .12); break; case "reset": Tone(290, 65, .12); break; case "phase": Tone(640, 70, .15); break; }
    }
    public void Completed(Phase phase)
    {
        if (!settings.AlarmEnabled || !settings.Sound) return;
        int repeats = Math.Clamp(settings.AlarmRepeats, 1, 8);
        for (int i = 0; i < repeats; i++) Tone(phase == Phase.Focus ? 880 : 620, 180, .27, i * 210);
    }
    /// <summary>A two-note chime for a calendar reminder, distinct from the pomodoro alarm.</summary>
    public void Reminder()
    {
        if (!settings.Sound) return;
        Tone(784, 150, .24);
        Tone(1046, 240, .22, 165);
    }
    public void StartNoise()
    {
        if (!settings.WhiteNoise || settings.WhiteNoiseVolume <= 0 || noisePlayer is not null) return;
        noiseStream = PcmNoise(4, settings.WhiteNoiseVolume / 100d);
        noisePlayer = new SoundPlayer(noiseStream); noisePlayer.Load(); noisePlayer.PlayLooping();
    }
    public void StopNoise()
    {
        noisePlayer?.Stop(); noisePlayer?.Dispose(); noiseStream?.Dispose(); noisePlayer = null; noiseStream = null;
    }
    private void Tone(double frequency, int milliseconds, double volume, int delay = 0)
    {
        var stream = PcmTone(frequency, milliseconds, volume);
        var player = new SoundPlayer(stream); player.Load();
        transient.Add((player, stream, DateTime.UtcNow.AddMilliseconds(delay + milliseconds + 500)));
        if (delay == 0) player.Play(); else _ = Task.Delay(delay).ContinueWith(_ => { try { player.Play(); } catch (ObjectDisposedException) { } }, TaskScheduler.FromCurrentSynchronizationContext());
    }
    private static MemoryStream PcmTone(double frequency, int milliseconds, double volume)
    {
        const int rate = 22050; int count = rate * milliseconds / 1000; var samples = new short[count];
        for (int i = 0; i < count; i++) { double envelope = Math.Min(1, Math.Min(i / (rate * .008), (count - i) / (rate * .02))); samples[i] = (short)(Math.Sin(2 * Math.PI * frequency * i / rate) * short.MaxValue * volume * envelope); }
        return Wave(samples, rate);
    }
    private static MemoryStream PcmNoise(int seconds, double volume)
    {
        const int rate = 22050; int count = rate * seconds; var samples = new short[count]; var random = new Random(31);
        double last = 0;
        for (int i = 0; i < count; i++) { double raw = random.NextDouble() * 2 - 1; last = last * .94 + raw * .06; samples[i] = (short)(last * short.MaxValue * volume); }
        return Wave(samples, rate);
    }
    private static MemoryStream Wave(short[] samples, int rate)
    {
        var stream = new MemoryStream(); using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, true))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples.Length * 2); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(samples.Length * 2); foreach (var sample in samples) writer.Write(sample);
        }
        stream.Position = 0; return stream;
    }
    public void Dispose() { StopNoise(); cleanup.Stop(); foreach (var item in transient) { item.Player.Dispose(); item.Stream.Dispose(); } transient.Clear(); }
}
