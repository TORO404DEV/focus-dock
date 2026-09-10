using System.Text;

namespace PomoDock.Core;

/// <summary>What a sound is for. Every sound setting picks from one kind only.</summary>
public enum SoundKind { Alarm, Ambient, Reminder, Click }

public sealed record SoundInfo(string Id, SoundKind Kind, string Name, string Detail);

/// <summary>16-bit PCM, interleaved when stereo, ready to become a WAV file.</summary>
public sealed record SoundClip(short[] Samples, int Channels, int Rate, bool Loops)
{
    public double Seconds => Samples.Length / (double)(Channels * Rate);

    public SoundClip Scaled(double gain)
    {
        var scaled = new short[Samples.Length];
        for (int i = 0; i < Samples.Length; i++)
            scaled[i] = (short)Math.Clamp(Math.Round(Samples[i] * gain), short.MinValue, short.MaxValue);
        return this with { Samples = scaled };
    }
}

/// <summary>
/// PomoDock's sound library. Every sound is synthesised from a few lines of maths — sines, filtered
/// noise, a plucked-string model — so the app ships no audio files, owes no licences, and renders
/// exactly the same samples every time a given sound is asked for.
/// </summary>
public static class SoundLibrary
{
    public const int Rate = 22050;
    /// <summary>Raised whenever any sound changes, so cached files are rendered again.</summary>
    public const int Revision = 1;
    private const double LoopSeconds = 16;
    private const double LoopFade = 0.5;

    /// <summary>The moments a click pack gives a voice to.</summary>
    public static readonly string[] ClickActions = ["start", "pause", "skip", "reset", "phase"];

    /// <summary>Which sound is which, in the order they are offered. Ids are stored; names are shown.</summary>
    private static readonly (string Id, SoundKind Kind)[] Sounds =
    [
        ("classic", SoundKind.Alarm), ("bell", SoundKind.Alarm), ("chime", SoundKind.Alarm),
        ("bowl", SoundKind.Alarm), ("marimba", SoundKind.Alarm), ("harp", SoundKind.Alarm),
        ("gong", SoundKind.Alarm), ("digital", SoundKind.Alarm), ("kitchen", SoundKind.Alarm),
        ("birds", SoundKind.Alarm), ("arcade", SoundKind.Alarm), ("pulse", SoundKind.Alarm),

        ("white", SoundKind.Ambient), ("pink", SoundKind.Ambient), ("brown", SoundKind.Ambient),
        ("rain", SoundKind.Ambient), ("storm", SoundKind.Ambient), ("waves", SoundKind.Ambient),
        ("wind", SoundKind.Ambient), ("stream", SoundKind.Ambient), ("fire", SoundKind.Ambient),
        ("fan", SoundKind.Ambient), ("clock", SoundKind.Ambient), ("binaural", SoundKind.Ambient),

        ("call-two-note", SoundKind.Reminder), ("call-ding", SoundKind.Reminder), ("call-glass", SoundKind.Reminder),
        ("call-marimba", SoundKind.Reminder), ("call-bubbles", SoundKind.Reminder), ("call-knock", SoundKind.Reminder),

        ("click-soft", SoundKind.Click), ("click-wood", SoundKind.Click), ("click-mech", SoundKind.Click),
        ("click-retro", SoundKind.Click), ("click-bubble", SoundKind.Click), ("click-glass", SoundKind.Click)
    ];

    /// <summary>
    /// The library as the panel shows it. It is resolved on every read rather than cached in a
    /// field, so switching language renames the sounds without restarting; the ids never move.
    /// </summary>
    public static IReadOnlyList<SoundInfo> Catalog =>
        [.. Sounds.Select(sound => new SoundInfo(sound.Id, sound.Kind, L.T("sound." + sound.Id), L.T("sound." + sound.Id + "Detail")))];

    public static IEnumerable<SoundInfo> OfKind(SoundKind kind) => Catalog.Where(sound => sound.Kind == kind);

    /// <summary>
    /// Looks a sound up by its id. It walks the id table rather than the catalog, so the hot path
    /// that plays a sound never builds thirty-six translated records to read one <c>Kind</c>.
    /// </summary>
    public static SoundInfo? Find(string? id)
    {
        foreach (var sound in Sounds)
            if (string.Equals(sound.Id, id, StringComparison.Ordinal))
                return new SoundInfo(sound.Id, sound.Kind, L.T("sound." + sound.Id), L.T("sound." + sound.Id + "Detail"));
        return null;
    }

    public static string Default(SoundKind kind) => kind switch
    {
        SoundKind.Alarm => "classic",
        SoundKind.Ambient => "white",
        SoundKind.Reminder => "call-two-note",
        _ => "click-soft"
    };

    /// <summary>The id if it names a sound of that kind, the kind's default otherwise.</summary>
    public static string Valid(string? id, SoundKind kind) => Find(id) is { } sound && sound.Kind == kind ? sound.Id : Default(kind);

    public static SoundClip Render(string id, string? action = null)
    {
        var sound = Find(id) ?? throw new ArgumentException($"Unknown sound '{id}'.", nameof(id));
        return sound.Kind switch
        {
            SoundKind.Ambient => Ambient(id),
            SoundKind.Click => Click(id, action ?? "start"),
            _ => OneShot(id)
        };
    }

    public static byte[] Wave(SoundClip clip)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, true))
        {
            int bytes = clip.Samples.Length * 2;
            writer.Write("RIFF"u8); writer.Write(36 + bytes); writer.Write("WAVE"u8);
            writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)clip.Channels);
            writer.Write(clip.Rate); writer.Write(clip.Rate * clip.Channels * 2); writer.Write((short)(clip.Channels * 2)); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(bytes);
            foreach (var sample in clip.Samples) writer.Write(sample);
        }
        return stream.ToArray();
    }

    // ------------------------------------------------------------------ alarms and reminders

    private static SoundClip OneShot(string id)
    {
        var random = new Random(Seed(id));
        float[] b;
        switch (id)
        {
            case "classic":
                b = Buffer(0.62);
                Tone(b, 0.00, 0.18, 880, 1);
                Tone(b, 0.27, 0.18, 880, 1);
                break;
            case "bell":
                b = Buffer(2.6);
                Bell(b, 0, 587.3, 1, 1.4);
                break;
            case "chime":
                b = Buffer(2.2);
                foreach (var (at, pitch) in new[] { (0.0, 1046.5), (0.22, 1318.5), (0.44, 1568.0) }) Chime(b, at, pitch, 1);
                break;
            case "bowl":
                b = Buffer(4.8);
                // Two partials a hair apart beat against each other: that slow wobble is the bowl.
                foreach (var (ratio, amp, tau) in new[] { (1.0, 1.0, 3.2), (1.006, 0.8, 3.0), (2.76, 0.45, 1.9), (2.768, 0.3, 1.8), (5.40, 0.2, 0.9), (8.93, 0.08, 0.5) })
                    Partial(b, 0, 196 * ratio, amp, tau, attack: 0.03);
                break;
            case "marimba":
                b = Buffer(1.5);
                foreach (var (at, pitch) in new[] { (0.0, 659.3), (0.12, 784.0), (0.24, 987.8), (0.36, 1318.5) }) Marimba(b, at, pitch, 1);
                break;
            case "harp":
                b = Buffer(2.6);
                foreach (var (at, pitch) in new[] { (0.0, 523.3), (0.1, 659.3), (0.2, 784.0), (0.3, 1046.5), (0.4, 1318.5) }) Pluck(b, at, pitch, 0.8, random);
                break;
            case "gong":
                b = Buffer(5.0);
                foreach (var (ratio, amp, tau, attack) in new[] { (1.0, 1.0, 3.6, 0.02), (1.52, 0.7, 3.0, 0.06), (2.03, 0.5, 2.4, 0.12), (2.64, 0.4, 2.0, 0.2), (3.31, 0.3, 1.6, 0.25), (4.19, 0.22, 1.2, 0.3), (5.08, 0.15, 0.9, 0.3), (6.21, 0.1, 0.7, 0.3) })
                    Partial(b, 0, 98 * ratio, amp, tau, attack);
                NoiseHit(b, 0, 0.06, 0.4, random, smooth: 0.15);
                break;
            case "digital":
                b = Buffer(1.25);
                for (int i = 0; i < 4; i++) Square(b, i * 0.16, 0.09, 2093, 1, harmonics: 3);
                break;
            case "kitchen":
                b = Buffer(1.8);
                // The hammer of a wind-up timer strikes about twenty times a second.
                for (double at = 0; at < 1.45; at += 1 / 22.0) Bell(b, at, 2350, 0.5, 0.07);
                break;
            case "birds":
                b = Buffer(2.2);
                for (int i = 0; i < 3; i++) Sweep(b, i * 0.11, 0.07, 2800 + random.Next(300), 3900 + random.Next(300), 0.8);
                Sweep(b, 0.55, 0.22, 3300, 2600, 0.7, vibratoHz: 28, vibratoDepth: 180);
                Sweep(b, 0.85, 0.22, 3200, 2500, 0.6, vibratoHz: 30, vibratoDepth: 160);
                for (int i = 0; i < 6; i++) Sweep(b, 1.3 + i * 0.07, 0.05, 3000, 3700, 0.7);
                break;
            case "arcade":
                b = Buffer(0.95);
                foreach (var (at, pitch) in new[] { (0.0, 523.3), (0.09, 659.3), (0.18, 784.0), (0.27, 1046.5) }) Square(b, at, 0.08, pitch, 0.8);
                Square(b, 0.36, 0.4, 1046.5, 0.8);
                Square(b, 0.36, 0.4, 1318.5, 0.5);
                break;
            case "pulse":
                b = Buffer(2.0);
                foreach (double at in new[] { 0.0, 1.0 })
                {
                    Swell(b, at, 0.8, 330, 1);
                    Swell(b, at, 0.8, 495, 0.5);
                }
                break;
            case "call-two-note":
                b = Buffer(0.5);
                Tone(b, 0, 0.15, 784, 1);
                Tone(b, 0.165, 0.24, 1046.5, 0.9);
                break;
            case "call-ding":
                b = Buffer(1.3);
                Bell(b, 0, 1760, 1, 0.45);
                break;
            case "call-glass":
                b = Buffer(1.4);
                Partial(b, 0, 2637, 1, 0.55, attack: 0.002);
                Partial(b, 0, 2645, 0.7, 0.5, attack: 0.002);
                Partial(b, 0, 5274, 0.25, 0.2, attack: 0.002);
                break;
            case "call-marimba":
                b = Buffer(1.0);
                Marimba(b, 0, 784, 1);
                Marimba(b, 0.14, 1174.7, 1);
                break;
            case "call-bubbles":
                b = Buffer(0.55);
                for (int i = 0; i < 3; i++) Sweep(b, i * 0.12, 0.08, 420 + i * 120, 950 + i * 220, 1);
                break;
            case "call-knock":
                b = Buffer(0.5);
                Knock(b, 0, 190, random);
                Knock(b, 0.17, 180, random);
                break;
            default:
                throw new ArgumentException($"Unknown sound '{id}'.", nameof(id));
        }
        FadeTail(b, 0.01);
        return new SoundClip(ToPcm(b, 0.85), 1, Rate, false);
    }

    // ------------------------------------------------------------------ click packs

    private static SoundClip Click(string id, string action)
    {
        var random = new Random(Seed(id + action));
        // Start rises and pause falls; the other actions sit in between, each with its own voice.
        double pitch = action switch { "start" => 1.0, "pause" => 0.72, "skip" => 0.86, "reset" => 0.6, _ => 1.12 };
        float[] b;
        switch (id)
        {
            case "click-soft":
            {
                // The tones PomoDock always used, kept exactly so the default pack sounds the same.
                var (hertz, milliseconds) = action switch { "start" => (720, 85), "pause" => (430, 75), "skip" => (560, 55), "reset" => (290, 65), _ => (640, 70) };
                b = Buffer(milliseconds / 1000.0 + 0.01);
                Tone(b, 0, milliseconds / 1000.0, hertz, 1);
                break;
            }
            case "click-wood":
                b = Buffer(0.14);
                Knock(b, 0, 900 * pitch, random);
                break;
            case "click-mech":
                b = Buffer(0.09);
                Tap(b, 0, 2600 * pitch, random);
                break;
            case "click-retro":
                b = Buffer(0.12);
                if (action == "start") { Square(b, 0, 0.04, 880, 1); Square(b, 0.045, 0.05, 1318.5, 1); }
                else if (action == "pause") { Square(b, 0, 0.04, 1318.5, 1); Square(b, 0.045, 0.05, 880, 1); }
                else Square(b, 0, 0.06, 1046.5 * pitch, 1);
                break;
            case "click-bubble":
                b = Buffer(0.1);
                if (action is "pause" or "reset") Sweep(b, 0, 0.08, 1100 * pitch, 380 * pitch, 1);
                else Sweep(b, 0, 0.08, 380 * pitch, 1100 * pitch, 1);
                break;
            case "click-glass":
                b = Buffer(0.22);
                Partial(b, 0, 3520 * pitch, 1, 0.06, attack: 0.001);
                Partial(b, 0, 3528 * pitch, 0.5, 0.05, attack: 0.001);
                break;
            default:
                throw new ArgumentException($"Unknown click pack '{id}'.", nameof(id));
        }
        FadeTail(b, 0.005);
        return new SoundClip(ToPcm(b, 0.8), 1, Rate, false);
    }

    // ------------------------------------------------------------------ ambiences

    /// <summary>
    /// Every ambience is rendered half a second longer than its loop and the extra tail is
    /// crossfaded into the head, so the last sample leads straight into the first: no click
    /// and no gap at the seam, however long the session runs.
    /// </summary>
    private static SoundClip Ambient(string id)
    {
        int loop = (int)(LoopSeconds * Rate), fade = (int)(LoopFade * Rate), length = loop + fade;
        var noise = new Noise(Seed(id));
        var random = new Random(Seed(id) ^ 0x5F3759DF);

        if (id == "binaural")
        {
            var left = new float[length];
            var right = new float[length];
            for (int i = 0; i < length; i++)
            {
                double t = i / (double)Rate;
                double bed = noise.Pink() * 0.35;
                left[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 200 * t) + bed);
                right[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 210 * t) + bed);
            }
            // Whole cycles fit the loop exactly, so a straight crossfade keeps the tones steady.
            var l = Seamless(left, loop, fade, equalPower: false);
            var r = Seamless(right, loop, fade, equalPower: false);
            var both = new float[loop * 2];
            for (int i = 0; i < loop; i++) { both[2 * i] = l[i]; both[2 * i + 1] = r[i]; }
            return new SoundClip(ToPcmRms(both, 0.16, 0.9), 2, Rate, true);
        }

        var b = new float[length];
        bool periodic = false;
        switch (id)
        {
            case "white":
            {
                // The same one-pole filtered hiss PomoDock always played.
                double last = 0;
                for (int i = 0; i < length; i++) { last = last * 0.94 + noise.White() * 0.06; b[i] = (float)last; }
                break;
            }
            case "pink":
                for (int i = 0; i < length; i++) b[i] = (float)noise.Pink();
                break;
            case "brown":
                for (int i = 0; i < length; i++) b[i] = (float)noise.Brown();
                break;
            case "rain":
                Rain(b, noise, random, drops: 55, bed: 0.55);
                break;
            case "storm":
                Rain(b, noise, random, drops: 120, bed: 0.9);
                Thunder(b, 3.2, noise, 1.6);
                Thunder(b, 11.0, noise, 1.1);
                break;
            case "waves":
            {
                double hiss = 0;
                for (int i = 0; i < length; i++)
                {
                    double t = i / (double)Rate;
                    double swell = 0.5 - 0.5 * Math.Cos(2 * Math.PI * t / 8);
                    double body = noise.Brown() * 0.8 + noise.Pink() * 0.5 * swell;
                    double white = noise.White();
                    hiss += 0.5 * (white - hiss);
                    b[i] = (float)(body * (0.25 + 0.75 * Math.Pow(swell, 1.5)) + (white - hiss) * 0.25 * Math.Pow(swell, 3));
                }
                break;
            }
            case "wind":
            {
                var band = new BandPass();
                for (int i = 0; i < length; i++)
                {
                    double t = i / (double)Rate;
                    if (i % 64 == 0) band.Tune(420 + 260 * Math.Sin(2 * Math.PI * t / 16) + 140 * Math.Sin(2 * Math.PI * 3 * t / 16 + 1.3), 1.6);
                    double gust = 0.45 + 0.55 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * t / 8 + 0.7));
                    b[i] = (float)(band.Next(noise.White()) * gust + noise.Brown() * 0.15);
                }
                break;
            }
            case "stream":
            {
                var band = new BandPass();
                band.Tune(1400, 0.7);
                for (int i = 0; i < length; i++) b[i] = (float)(band.Next(noise.Pink()) * 1.4 + noise.Brown() * 0.1);
                double seconds = length / (double)Rate;
                for (double at = 0; at < seconds; at += Gap(random, 22))
                {
                    double hertz = 500 + random.NextDouble() * 900;
                    Sweep(b, at, 0.015 + random.NextDouble() * 0.03, hertz, hertz * (1.4 + random.NextDouble() * 0.6), 0.05 + random.NextDouble() * 0.1);
                }
                break;
            }
            case "fire":
            {
                double low = 0;
                for (int i = 0; i < length; i++) { low += 0.06 * (noise.Brown() - low); b[i] = (float)(low * 2.2); }
                double seconds = length / (double)Rate;
                for (double at = 0; at < seconds; at += Gap(random, 45))
                    NoiseHit(b, at, 0.0008 + random.NextDouble() * 0.0012, 0.08 + random.NextDouble() * 0.25, random);
                for (double at = 0; at < seconds; at += Gap(random, 5))
                    NoiseHit(b, at, 0.002 + random.NextDouble() * 0.004, 0.5 + random.NextDouble() * 0.6, random, smooth: 0.7);
                break;
            }
            case "fan":
            {
                double low = 0;
                for (int i = 0; i < length; i++)
                {
                    double t = i / (double)Rate;
                    low += 0.2 * (noise.Pink() - low);
                    double blades = 1 + 0.06 * Math.Sin(2 * Math.PI * 12 * t);
                    b[i] = (float)((noise.Brown() * 0.6 + low * 0.5) * blades + 0.05 * Math.Sin(2 * Math.PI * 100 * t) + 0.025 * Math.Sin(2 * Math.PI * 200 * t));
                }
                break;
            }
            case "clock":
                periodic = true;
                for (int second = 0; second * Rate < length; second++) Tick(b, second, second % 2 == 0 ? 2900 : 2000, random);
                break;
            default:
                throw new ArgumentException($"Unknown ambience '{id}'.", nameof(id));
        }
        var seamless = Seamless(b, loop, fade, equalPower: !periodic);
        // A clock is mostly silence: judged by its average it would come out deafening.
        var pcm = periodic ? ToPcm(seamless, 0.55) : ToPcmRms(seamless, 0.16, 0.9);
        return new SoundClip(pcm, 1, Rate, true);
    }

    private static void Rain(float[] b, Noise noise, Random random, int drops, double bed)
    {
        double low = 0;
        for (int i = 0; i < b.Length; i++) { low += 0.35 * (noise.Pink() - low); b[i] += (float)(bed * low); }
        double seconds = b.Length / (double)Rate;
        for (double at = 0; at < seconds; at += Gap(random, drops))
        {
            if (random.NextDouble() < 0.12) Partial(b, at, 1800 + random.NextDouble() * 2400, 0.08 + random.NextDouble() * 0.1, 0.012, attack: 0.0005);
            else NoiseHit(b, at, 0.004 + random.NextDouble() * 0.004, 0.15 + random.NextDouble() * 0.45, random, smooth: 0.6);
        }
    }

    private static void Thunder(float[] b, double at, Noise noise, double amp)
    {
        int from = (int)(at * Rate), count = (int)(4.5 * Rate);
        double low = 0;
        for (int i = 0; i < count && from + i < b.Length; i++)
        {
            double t = i / (double)Rate;
            double rolling = Math.Min(1, t / 0.5) * Math.Exp(-t / 1.4) * (1 + 0.5 * Math.Sin(2 * Math.PI * 2.3 * t));
            low += 0.03 * (noise.Brown() - low);
            b[from + i] += (float)(amp * rolling * low * 4);
        }
    }

    private static void Tick(float[] b, double at, double hertz, Random random)
    {
        NoiseHit(b, at, 0.003, 0.6, random, smooth: 0.9);
        Partial(b, at, hertz, 0.5, 0.006, attack: 0.0003);
        Partial(b, at, hertz * 1.7, 0.2, 0.004, attack: 0.0003);
    }

    // ------------------------------------------------------------------ building blocks

    private static float[] Buffer(double seconds) => new float[(int)Math.Ceiling(seconds * Rate)];

    /// <summary>Waiting time until the next random event, for a given average rate per second.</summary>
    private static double Gap(Random random, double perSecond) => -Math.Log(1 - random.NextDouble()) / perSecond;

    /// <summary>
    /// A sine that rises in a few milliseconds and dies away with time constant tau. Given a
    /// length, it is cut there with a short release instead of ringing on.
    /// </summary>
    private static void Partial(float[] b, double at, double hertz, double amp, double tau, double attack = 0.004, double length = 0)
    {
        if (hertz <= 0 || hertz >= Rate / 2.2) return;
        int from = (int)(at * Rate);
        int count = length > 0 ? (int)(length * Rate) : (int)((tau * 9.2 + attack) * Rate);
        int release = Math.Max(1, (int)(0.01 * Rate));
        double step = 2 * Math.PI * hertz / Rate;
        for (int i = 0; i < count && from + i < b.Length; i++)
        {
            double t = i / (double)Rate;
            double envelope = Math.Min(1, t / attack) * Math.Exp(-t / tau);
            if (length > 0 && count - i < release) envelope *= (count - i) / (double)release;
            b[from + i] += (float)(amp * envelope * Math.Sin(step * i));
        }
    }

    private static void Tone(float[] b, double at, double length, double hertz, double amp) =>
        Partial(b, at, hertz, amp, 1000, 0.008, length);

    /// <summary>A square wave built from its odd harmonics, stopped short of harshness.</summary>
    private static void Square(float[] b, double at, double length, double hertz, double amp, int harmonics = 9)
    {
        for (int h = 1; h <= harmonics; h += 2) Partial(b, at, hertz * h, amp / h, 1000, 0.003, length);
    }

    private static readonly (double Ratio, double Amp, double Decay)[] BellPartials =
        [(0.5, 0.35, 1.6), (1.0, 1.0, 1.0), (1.19, 0.45, 0.7), (1.5, 0.3, 0.6), (2.0, 0.55, 0.5), (2.51, 0.25, 0.35), (3.01, 0.18, 0.28), (4.2, 0.1, 0.18)];

    /// <summary>A bell's partials are not harmonic; that slight dissonance is what makes it a bell.</summary>
    private static void Bell(float[] b, double at, double hertz, double amp, double tau)
    {
        foreach (var (ratio, gain, decay) in BellPartials) Partial(b, at, hertz * ratio, amp * gain, tau * decay, attack: 0.002);
    }

    private static void Chime(float[] b, double at, double hertz, double amp)
    {
        Partial(b, at, hertz, amp, 0.9, 0.003);
        Partial(b, at, hertz * 2.76, amp * 0.18, 0.3, 0.002);
        Partial(b, at, hertz * 5.4, amp * 0.07, 0.12, 0.002);
    }

    private static void Marimba(float[] b, double at, double hertz, double amp)
    {
        Partial(b, at, hertz, amp, 0.32, 0.002);
        Partial(b, at, hertz * 3.93, amp * 0.3, 0.07, 0.001);
        Partial(b, at, hertz * 9.2, amp * 0.08, 0.02, 0.001);
    }

    private static void Swell(float[] b, double at, double length, double hertz, double amp)
    {
        int from = (int)(at * Rate), count = (int)(length * Rate);
        double step = 2 * Math.PI * hertz / Rate;
        for (int i = 0; i < count && from + i < b.Length; i++)
        {
            double shape = Math.Sin(Math.PI * i / count);
            b[from + i] += (float)(amp * shape * shape * Math.Sin(step * i));
        }
    }

    /// <summary>Karplus–Strong: a burst of noise circulating in a short delay line becomes a plucked string.</summary>
    private static void Pluck(float[] b, double at, double hertz, double amp, Random random, double damping = 0.996)
    {
        int size = Math.Max(2, (int)Math.Round(Rate / hertz));
        var line = new double[size];
        for (int i = 0; i < size; i++) line[i] = random.NextDouble() * 2 - 1;
        int from = (int)(at * Rate);
        for (int i = 0; from + i < b.Length; i++)
        {
            int k = i % size;
            double value = line[k];
            line[k] = damping * 0.5 * (line[k] + line[(k + 1) % size]);
            b[from + i] += (float)(amp * value);
        }
    }

    private static void Sweep(float[] b, double at, double length, double from, double to, double amp, double vibratoHz = 0, double vibratoDepth = 0)
    {
        int start = (int)(at * Rate), count = Math.Max(1, (int)(length * Rate));
        double phase = 0;
        for (int i = 0; i < count && start + i < b.Length; i++)
        {
            double t = i / (double)count;
            double hertz = from + (to - from) * t + vibratoDepth * Math.Sin(2 * Math.PI * vibratoHz * i / Rate);
            phase += 2 * Math.PI * hertz / Rate;
            b[start + i] += (float)(amp * Math.Sin(Math.PI * t) * Math.Sin(phase));
        }
    }

    /// <summary>A short burst of noise, softened by a one-pole filter: drops, crackles, the attack of a knock.</summary>
    private static void NoiseHit(float[] b, double at, double duration, double amp, Random random, double smooth = 1)
    {
        int from = (int)(at * Rate), count = Math.Max(1, (int)(duration * Rate));
        double low = 0, tau = count / 3.0;
        for (int i = 0; i < count && from + i < b.Length; i++)
        {
            low += smooth * ((random.NextDouble() * 2 - 1) - low);
            b[from + i] += (float)(amp * Math.Exp(-i / tau) * low);
        }
    }

    private static void Knock(float[] b, double at, double hertz, Random random)
    {
        Partial(b, at, hertz, 1, 0.03, 0.0005);
        Partial(b, at, hertz * 2.3, 0.4, 0.015, 0.0005);
        NoiseHit(b, at, 0.006, 0.6, random, smooth: 0.35);
    }

    private static void Tap(float[] b, double at, double hertz, Random random)
    {
        NoiseHit(b, at, 0.005, 1, random);
        Partial(b, at, 140, 0.5, 0.012, 0.0005);
        Partial(b, at, hertz, 0.3, 0.004, 0.0003);
    }

    private static void FadeTail(float[] b, double seconds)
    {
        int count = Math.Min(b.Length, (int)(seconds * Rate));
        for (int i = 0; i < count; i++) b[b.Length - 1 - i] *= i / (float)count;
    }

    private static float[] Seamless(float[] raw, int loop, int fade, bool equalPower)
    {
        var result = new float[loop];
        Array.Copy(raw, result, loop);
        for (int i = 0; i < fade; i++)
        {
            double t = i / (double)fade;
            double rising = equalPower ? Math.Sin(Math.PI / 2 * t) : t;
            double falling = equalPower ? Math.Cos(Math.PI / 2 * t) : 1 - t;
            result[i] = (float)(raw[i] * rising + raw[loop + i] * falling);
        }
        return result;
    }

    private static short[] ToPcm(float[] b, double peakTarget)
    {
        double peak = 0;
        foreach (var sample in b) peak = Math.Max(peak, Math.Abs(sample));
        double gain = peak > 0 ? peakTarget / peak : 0;
        var pcm = new short[b.Length];
        for (int i = 0; i < b.Length; i++) pcm[i] = (short)Math.Round(b[i] * gain * short.MaxValue);
        return pcm;
    }

    /// <summary>Levels a texture by its average loudness, with a soft ceiling that never clips.</summary>
    private static short[] ToPcmRms(float[] b, double rmsTarget, double ceiling)
    {
        double sum = 0;
        foreach (var sample in b) sum += sample * (double)sample;
        double rms = Math.Sqrt(sum / Math.Max(1, b.Length));
        double gain = rms > 0 ? rmsTarget / rms : 0;
        var pcm = new short[b.Length];
        for (int i = 0; i < b.Length; i++) pcm[i] = (short)Math.Round(ceiling * Math.Tanh(b[i] * gain / ceiling) * short.MaxValue);
        return pcm;
    }

    /// <summary>A seed that never changes between runs, so a sound always renders the same samples.</summary>
    private static int Seed(string text)
    {
        int hash = 17;
        foreach (char character in text) hash = unchecked(hash * 31 + character);
        return hash & 0x7FFFFFFF;
    }

    private sealed class Noise(int seed)
    {
        private readonly Random random = new(seed);
        private double b0, b1, b2, b3, b4, b5, b6, brown;

        public double White() => random.NextDouble() * 2 - 1;

        /// <summary>Paul Kellet's filter: equal energy per octave.</summary>
        public double Pink()
        {
            double white = White();
            b0 = 0.99886 * b0 + white * 0.0555179;
            b1 = 0.99332 * b1 + white * 0.0750759;
            b2 = 0.96900 * b2 + white * 0.1538520;
            b3 = 0.86650 * b3 + white * 0.3104856;
            b4 = 0.55000 * b4 + white * 0.5329522;
            b5 = -0.7616 * b5 - white * 0.0168980;
            double pink = b0 + b1 + b2 + b3 + b4 + b5 + b6 + white * 0.5362;
            b6 = white * 0.115926;
            return pink * 0.11;
        }

        /// <summary>Leaky integration of white noise: most of the energy sinks to the bass.</summary>
        public double Brown()
        {
            brown = (brown + 0.02 * White()) / 1.02;
            return brown * 3.5;
        }
    }

    private sealed class BandPass
    {
        private double x1, x2, y1, y2, b0, b2, a1, a2;

        public void Tune(double center, double q)
        {
            double w = 2 * Math.PI * center / Rate, alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
            b0 = alpha / a0; b2 = -alpha / a0; a1 = -2 * Math.Cos(w) / a0; a2 = (1 - alpha) / a0;
        }

        public double Next(double x)
        {
            double y = b0 * x + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
            return y;
        }
    }
}
