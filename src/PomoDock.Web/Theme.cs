using System.Globalization;

namespace PomoDock.Web;

/// <summary>A colour as the desktop's WPF <c>Color</c> holds it: three bytes.</summary>
public readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>
/// The colour arithmetic of the desktop's Theme. The two palettes themselves live in
/// pomodock.css as custom properties; what stays here is what has to be computed from the
/// colours a person picks: the readable ink on their accent and the phase wash in the dark.
/// </summary>
internal static class Theme
{
    /// <summary>The graphite card the dark skin lays the phase colours over.</summary>
    public static readonly Rgb DarkSurface = new(0x1C, 0x1E, 0x1B);

    /// <summary>Black or paper, whichever can be read on the colour the user picked.</summary>
    public static Rgb Contrast(Rgb color) => Luminance(color) > .45 ? new(0x17, 0x19, 0x16) : new(0xF1, 0xF0, 0xE9);

    /// <summary>
    /// Lays a colour over another. The timer's phase colours are pastels chosen for paper; in the
    /// dark they are laid over the card instead of painted onto it, so the phase is still legible
    /// as a colour while the numbers stay light on dark.
    /// </summary>
    public static Rgb Mix(Rgb top, Rgb bottom, double amount)
    {
        double part = Math.Clamp(amount, 0, 1);
        return new(
            (byte)Math.Round(top.R * part + bottom.R * (1 - part)),
            (byte)Math.Round(top.G * part + bottom.G * (1 - part)),
            (byte)Math.Round(top.B * part + bottom.B * (1 - part)));
    }

    /// <summary>Perceived brightness, 0 for black and 1 for white.</summary>
    public static double Luminance(Rgb color) =>
        0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);

    private static double Channel(byte value)
    {
        double part = value / 255.0;
        return part <= 0.03928 ? part / 12.92 : Math.Pow((part + 0.055) / 1.055, 2.4);
    }

    /// <summary>Reads #RGB, #RRGGBB or #AARRGGBB, the forms WPF's converter accepts for a picked colour.</summary>
    public static bool TryParse(string? value, out Rgb color)
    {
        color = new(0xD7, 0xD9, 0xD1);
        var text = (value ?? "").Trim().TrimStart('#');
        if (text.Length == 3) text = string.Concat(text.Select(c => $"{c}{c}"));
        if (text.Length == 8) text = text[2..];
        if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex)) return false;
        color = new((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
        return true;
    }

    public static string Hex(Rgb color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
