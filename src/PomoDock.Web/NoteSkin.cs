namespace PomoDock.Web;

/// <summary>
/// A post-it colour and everything drawn on top of it. The ink flips to light on a dark
/// note, so the text, the toolbar and the card frame stay readable at any colour.
/// </summary>
public sealed class NoteSkin
{
    public sealed record Preset(string Key, string Name, string Hex);

    public static readonly Preset[] Palette =
    [
        new("paper", "Papel", "#FAF9F3"),
        new("yellow", "Amarillo", "#F3E8A6"),
        new("mint", "Menta", "#CFE4D3"),
        new("blue", "Azul", "#CFE0E8"),
        new("rose", "Rosa", "#EBCFD0"),
        new("lilac", "Lila", "#DDD2E7")
    ];

    public Rgb Paper { get; }
    public Rgb Ink { get; }
    public string Hex { get; }
    /// <summary>True when the note is dark enough that the ink had to flip to light.</summary>
    public bool IsDark { get; }
    public string PaperHex { get; }
    public string InkHex { get; }
    /// <summary>Secondary text: readable but quiet.</summary>
    public string MutedHex { get; }
    /// <summary>Borders and separators.</summary>
    public string LineHex { get; }
    /// <summary>Buttons and the card body: a shade away from the paper so they read as controls.</summary>
    public string ChromeHex { get; }
    public string SelectionHex { get; }

    private NoteSkin(Rgb paper)
    {
        Paper = paper;
        Hex = Theme.Hex(paper);
        IsDark = Theme.Luminance(paper) < .45;
        Ink = IsDark ? new Rgb(0xF4, 0xF3, 0xEC) : new Rgb(0x17, 0x19, 0x16);
        PaperHex = Hex;
        InkHex = Theme.Hex(Ink);
        MutedHex = Theme.Hex(Mix(paper, Ink, .58));
        LineHex = Theme.Hex(Mix(paper, Ink, IsDark ? .42 : .5));
        ChromeHex = Theme.Hex(Mix(paper, Ink, IsDark ? .12 : .07));
        SelectionHex = Theme.Hex(Mix(paper, Ink, .35));
    }

    public static NoteSkin For(string? value) => new(Resolve(value));

    /// <summary>The custom properties a skinned surface reads: paper, ink, muted, line, chrome.</summary>
    public string Variables => $"--note-paper:{PaperHex};--note-ink:{InkHex};--note-muted:{MutedHex};--note-line:{LineHex};--note-chrome:{ChromeHex};--note-selection:{SelectionHex}";

    /// <summary>A stored colour is either one of the presets or a plain hex string.</summary>
    public static Rgb Resolve(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            var preset = Palette.FirstOrDefault(item => string.Equals(item.Key, value, StringComparison.OrdinalIgnoreCase));
            if (preset is not null) return Parse(preset.Hex);
            if (TryParse(value, out var custom)) return custom;
        }
        return Parse(Palette[0].Hex);
    }

    /// <summary>Accepts #RGB, #RRGGBB and the same without the hash.</summary>
    public static bool TryParse(string? value, out Rgb color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim().TrimStart('#');
        if (text.Length is not (3 or 6) || !text.All(Uri.IsHexDigit)) return false;
        return Theme.TryParse(text, out color);
    }

    public static string ToHex(Rgb color) => Theme.Hex(color);

    private static Rgb Mix(Rgb from, Rgb to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return new(
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));
    }

    private static Rgb Parse(string hex) => Theme.TryParse(hex, out var color) ? color : new Rgb(0xFA, 0xF9, 0xF3);

    /// <summary>The colours offered in the picker: ten hues in five tones, plus a grey ramp.</summary>
    public static string[][] Shades()
    {
        double[] hues = [8, 32, 52, 96, 152, 188, 214, 258, 292, 332];
        (double Saturation, double Value)[] tones = [(.20, 1), (.38, .96), (.58, .84), (.70, .58), (.78, .32)];
        var rows = new List<string[]>();
        foreach (var tone in tones)
            rows.Add(hues.Select(hue => ToHex(FromHsv(hue, tone.Saturation, tone.Value))).ToArray());
        rows.Add(["#FFFFFF", "#F1F0E9", "#DCDCD4", "#B7B7AF", "#8A8A83", "#5C5C56", "#3B3B37", "#2A2A27", "#1D1D1A", "#111110"]);
        return rows.ToArray();
    }

    private static Rgb FromHsv(double hue, double saturation, double value)
    {
        double chroma = value * saturation;
        double position = (hue % 360) / 60;
        double second = chroma * (1 - Math.Abs(position % 2 - 1));
        double red = 0, green = 0, blue = 0;
        switch ((int)position)
        {
            case 0: red = chroma; green = second; break;
            case 1: red = second; green = chroma; break;
            case 2: green = chroma; blue = second; break;
            case 3: green = second; blue = chroma; break;
            case 4: red = second; blue = chroma; break;
            default: red = chroma; blue = second; break;
        }
        double offset = value - chroma;
        return new(
            (byte)Math.Round((red + offset) * 255),
            (byte)Math.Round((green + offset) * 255),
            (byte)Math.Round((blue + offset) * 255));
    }
}
