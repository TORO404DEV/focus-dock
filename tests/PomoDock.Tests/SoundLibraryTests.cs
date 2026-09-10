using System.Text;
using PomoDock.Core;

/// <summary>Sound library rules: wide, audible, unclipped, seamless loops, identical renders.</summary>
internal static class SoundLibraryTests
{
    public static void Run(Action<string, Action> test, Action<double, double> equal, Action<bool, string> assert)
    {
        test("the sound library is wide and every id is unique", () =>
        {
            var catalog = SoundLibrary.Catalog;
            assert(catalog.Select(sound => sound.Id).Distinct().Count() == catalog.Count, "every id is unique");
            assert(SoundLibrary.OfKind(SoundKind.Alarm).Count() >= 10, "at least ten alarms");
            assert(SoundLibrary.OfKind(SoundKind.Ambient).Count() >= 10, "at least ten ambiences");
            assert(SoundLibrary.OfKind(SoundKind.Reminder).Count() >= 5, "at least five reminder sounds");
            assert(SoundLibrary.OfKind(SoundKind.Click).Count() >= 5, "at least five click packs");
            foreach (var kind in Enum.GetValues<SoundKind>())
                assert(SoundLibrary.Find(SoundLibrary.Default(kind))?.Kind == kind, $"the default {kind} is a real {kind}");
        });

        test("every sound renders audible, unclipped and within its length", () =>
        {
            foreach (var sound in SoundLibrary.Catalog)
            {
                string?[] actions = sound.Kind == SoundKind.Click ? [.. SoundLibrary.ClickActions] : [null];
                foreach (var action in actions)
                {
                    var clip = SoundLibrary.Render(sound.Id, action);
                    int peak = clip.Samples.Max(sample => Math.Abs((int)sample));
                    assert(peak > 3000, $"{sound.Id} {action} is audible");
                    assert(peak < 32000, $"{sound.Id} {action} never clips");
                    var (shortest, longest) = sound.Kind switch
                    {
                        SoundKind.Click => (0.02, 0.5),
                        SoundKind.Reminder => (0.2, 2.5),
                        SoundKind.Alarm => (0.3, 6.0),
                        _ => (8.0, 30.0)
                    };
                    assert(clip.Seconds >= shortest && clip.Seconds <= longest, $"{sound.Id} lasts {clip.Seconds:0.00} s");
                    assert(clip.Loops == (sound.Kind == SoundKind.Ambient), $"only ambiences loop ({sound.Id})");
                }
            }
        });

        test("ambiences loop without a click at the seam", () =>
        {
            foreach (var sound in SoundLibrary.OfKind(SoundKind.Ambient))
            {
                var clip = SoundLibrary.Render(sound.Id);
                int frames = clip.Samples.Length / clip.Channels;
                for (int channel = 0; channel < clip.Channels; channel++)
                {
                    int At(int frame) => clip.Samples[frame * clip.Channels + channel];
                    int steepest = 0;
                    for (int frame = 1; frame < frames; frame++) steepest = Math.Max(steepest, Math.Abs(At(frame) - At(frame - 1)));
                    int seam = Math.Abs(At(0) - At(frames - 1));
                    assert(seam <= steepest + 1, $"{sound.Id} loops cleanly (seam {seam}, steepest step {steepest})");
                }
            }
        });

        test("a sound renders the same samples every time, so a cached file always matches", () =>
        {
            foreach (var sound in SoundLibrary.Catalog)
                assert(SoundLibrary.Render(sound.Id).Samples.SequenceEqual(SoundLibrary.Render(sound.Id).Samples), $"{sound.Id} is deterministic");
        });

        test("wave files carry a correct header, stereo included", () =>
        {
            var clip = SoundLibrary.Render("chime");
            var bytes = SoundLibrary.Wave(clip);
            assert(Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) == "WAVE", "a RIFF/WAVE file");
            equal(1, BitConverter.ToInt16(bytes, 22));
            equal(SoundLibrary.Rate, BitConverter.ToInt32(bytes, 24));
            equal(clip.Samples.Length * 2, BitConverter.ToInt32(bytes, 40));
            equal(44 + clip.Samples.Length * 2, bytes.Length);
            equal(2, BitConverter.ToInt16(SoundLibrary.Wave(SoundLibrary.Render("binaural")), 22));
        });

        test("settings fall back to a real sound when an id is unknown or of the wrong kind", () =>
        {
            var settings = new Settings { FocusEndSound = "no-existe", AmbientSound = "bell", ReminderSound = "call-glass", AlarmVolume = 250, EffectsVolume = -4 };
            settings.Validate();
            assert(settings.FocusEndSound == SoundLibrary.Default(SoundKind.Alarm), "an unknown alarm falls back to the default");
            assert(settings.AmbientSound == SoundLibrary.Default(SoundKind.Ambient), "an alarm cannot play as an ambience");
            assert(settings.ReminderSound == "call-glass", "a valid choice is kept");
            equal(100, settings.AlarmVolume); equal(0, settings.EffectsVolume);
        });
    }
}
