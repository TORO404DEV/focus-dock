using System.Windows;
using System.Windows.Media;

namespace PomoDock.App;

/// <summary>A widget that can draw itself again when the app changes skin.</summary>
internal interface IReskinnable
{
    /// <summary>
    /// Draw again in the skin that is on now. Theme brushes repaint themselves, but a colour a
    /// widget mixed — a faded grid line, a wash behind a chip — was mixed from the old palette.
    /// </summary>
    void Reskin();
}

/// <summary>
/// The two skins the app wears. Light is paper and black ink. Dark is not that photo negative —
/// inverting it turns every black bar into a glaring white slab — but a quiet graphite room: the
/// bars sink below the page, the cards rise above it, and borders drop to a whisper so that only
/// what matters (today, a day that is due, a finished habit) is allowed to be bright.
///
/// Every widget paints itself from these keys, so a colour is decided here once and nowhere else.
/// </summary>
internal static class Theme
{
    /// <summary>
    /// Paints a palette into the application resources.
    ///
    /// It repaints the brushes that are already there instead of putting new ones in their place.
    /// Half the app keeps the brush it was handed when it drew itself — a habit cell, a task row, a
    /// calendar chip — so swapping the objects left all of that wearing the old skin until it was
    /// drawn again. Changing the colour inside the brush every one of them is holding turns the
    /// whole app over at once.
    /// </summary>
    public static void Apply(ResourceDictionary resources, bool dark, string accentColor)
    {
        var accent = Parse(accentColor, Rgb(0xD7D9D1));
        foreach (var (key, color) in dark ? Dark(accent) : Light(accent))
        {
            if (resources[key] is SolidColorBrush brush && !brush.IsFrozen) brush.Color = color;
            else resources[key] = new SolidColorBrush(color);
        }
    }

    /// <summary>Paper and black ink: the printed look the app was drawn for. Unchanged.</summary>
    private static (string Key, Color Color)[] Light(Color accent) =>
    [
        ("Paper", Rgb(0xF1F0E9)), ("Surface", Rgb(0xFAF9F3)), ("Raised", Rgb(0xE0DFD6)),
        ("Ink", Rgb(0x171916)), ("Muted", Rgb(0x66695E)),
        ("Line", Rgb(0x171916)), ("Edge", Rgb(0x171916)),
        ("Chrome", Rgb(0x171916)), ("ChromeInk", Rgb(0xF1F0E9)),
        ("Accent", accent), ("AccentInk", Contrast(accent))
    ];

    /// <summary>
    /// Graphite. Three depths carry the layout without a single hard line: the chrome bars at
    /// 0x0C, the page at 0x12, the cards at 0x1C. Borders split in two — <c>Edge</c> only
    /// separates, <c>Line</c> still emphasises — because one border cannot do both jobs in the
    /// dark: a card outlined as loudly as today's cell is exactly what makes a dark theme shout.
    /// </summary>
    private static (string Key, Color Color)[] Dark(Color accent) =>
    [
        ("Paper", Rgb(0x121311)), ("Surface", Rgb(0x1C1E1B)), ("Raised", Rgb(0x272B24)),
        ("Ink", Rgb(0xECEBE3)), ("Muted", Rgb(0x8E9387)),
        ("Line", Rgb(0x8A9080)), ("Edge", Rgb(0x34382F)),
        ("Chrome", Rgb(0x0C0D0B)), ("ChromeInk", Rgb(0xECEBE3)),
        ("Accent", accent), ("AccentInk", Contrast(accent))
    ];

    /// <summary>Black or paper, whichever can be read on the colour the user picked.</summary>
    public static Color Contrast(Color color) => Luminance(color) > .45 ? Rgb(0x171916) : Rgb(0xF1F0E9);

    /// <summary>
    /// Lays a colour over another. The timer's phase colours are pastels chosen for paper; in the
    /// dark they are laid over the card instead of painted onto it, so the phase is still legible
    /// as a colour while the numbers stay light on dark.
    /// </summary>
    public static Color Mix(Color top, Color bottom, double amount)
    {
        double part = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(top.R * part + bottom.R * (1 - part)),
            (byte)Math.Round(top.G * part + bottom.G * (1 - part)),
            (byte)Math.Round(top.B * part + bottom.B * (1 - part)));
    }

    /// <summary>Perceived brightness, 0 for black and 1 for white.</summary>
    public static double Luminance(Color color) =>
        (0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B));

    private static double Channel(byte value)
    {
        double part = value / 255.0;
        return part <= 0.03928 ? part / 12.92 : Math.Pow((part + 0.055) / 1.055, 2.4);
    }

    public static Color Parse(string? value, Color fallback)
    {
        try { return value is null ? fallback : (Color)ColorConverter.ConvertFromString(value); }
        catch { return fallback; }
    }

    /// <summary>Reads a theme colour that is on screen right now.</summary>
    public static Color Of(string key) =>
        Application.Current?.Resources[key] is SolidColorBrush brush ? brush.Color : Colors.Transparent;

    private static Color Rgb(int hex) => Color.FromRgb((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
