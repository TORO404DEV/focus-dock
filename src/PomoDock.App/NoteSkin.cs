using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PomoDock.App;

/// <summary>
/// A post-it colour and everything drawn on top of it. The ink flips to light on a dark
/// note, so the text, the toolbar and the card frame stay readable at any colour.
/// </summary>
internal sealed class NoteSkin
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

    public Color Paper { get; }
    public Color Ink { get; }
    public string Hex { get; }
    /// <summary>True when the note is dark enough that the ink had to flip to light.</summary>
    public bool IsDark { get; }

    public SolidColorBrush PaperBrush { get; }
    public SolidColorBrush InkBrush { get; }
    /// <summary>Secondary text: readable but quiet.</summary>
    public SolidColorBrush MutedBrush { get; }
    /// <summary>Borders and separators.</summary>
    public SolidColorBrush LineBrush { get; }
    /// <summary>Buttons and the card body: a shade away from the paper so they read as controls.</summary>
    public SolidColorBrush ChromeBrush { get; }
    public SolidColorBrush SelectionBrush { get; }

    private NoteSkin(Color paper)
    {
        Paper = paper;
        Hex = ToHex(paper);
        IsDark = Luminance(paper) < .45;
        Ink = IsDark ? Color.FromRgb(0xF4, 0xF3, 0xEC) : Color.FromRgb(0x17, 0x19, 0x16);
        PaperBrush = Freeze(paper);
        InkBrush = Freeze(Ink);
        MutedBrush = Freeze(Mix(paper, Ink, .58));
        LineBrush = Freeze(Mix(paper, Ink, IsDark ? .42 : .5));
        ChromeBrush = Freeze(Mix(paper, Ink, IsDark ? .12 : .07));
        SelectionBrush = Freeze(Mix(paper, Ink, .35));
    }

    public static NoteSkin For(string? value) => new(Resolve(value));

    /// <summary>A stored colour is either one of the presets or a plain hex string.</summary>
    public static Color Resolve(string? value)
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
    public static bool TryParse(string? value, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (text[0] != '#') text = "#" + text;
        if (text.Length is not (4 or 7)) return false;
        foreach (char character in text[1..]) if (!Uri.IsHexDigit(character)) return false;
        try { color = (Color)ColorConverter.ConvertFromString(text); }
        catch (FormatException) { return false; }
        color = Color.FromRgb(color.R, color.G, color.B);
        return true;
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>Perceived brightness, gamma corrected, so mid greens do not read as dark.</summary>
    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            double part = value / 255d;
            return part <= .03928 ? part / 12.92 : Math.Pow((part + .055) / 1.055, 2.4);
        }
        return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
    }

    private static Color Mix(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

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

    private static Color FromHsv(double hue, double saturation, double value)
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
        return Color.FromRgb(
            (byte)Math.Round((red + offset) * 255),
            (byte)Math.Round((green + offset) * 255),
            (byte)Math.Round((blue + offset) * 255));
    }

    /// <summary>The colour picker: presets, a full shade grid, a hex field and a live preview.</summary>
    public static string? Pick(Window owner, string current)
    {
        var window = Dialogs.Window(owner, "COLOR DEL POST-IT", 620, 640);
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        window.Content = root;
        root.Children.Add(Dialogs.Heading("COLOR DEL POST-IT"));

        var skin = For(current);
        string chosen = skin.Hex;

        var preview = new Border { BorderThickness = new Thickness(1.5), Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 16) };
        var previewBox = new StackPanel();
        var previewTitle = new TextBlock { Text = "ASÍ SE VERÁ LA NOTA", FontSize = 14, FontWeight = FontWeights.Black };
        var previewText = new TextBlock { Text = "El texto, los botones y el marco del widget siguen al color.", FontSize = 12, Margin = new Thickness(0, 6, 0, 10), TextWrapping = TextWrapping.Wrap };
        var previewTools = new StackPanel { Orientation = Orientation.Horizontal };
        var previewButtons = new List<Button>();
        foreach (var label in new[] { "B", "I", "U" })
        {
            var sample = new Button { Content = label, Width = 28, Height = 26, Padding = new Thickness(0), Margin = new Thickness(0, 0, 5, 0), FontSize = 12 };
            previewButtons.Add(sample);
            previewTools.Children.Add(sample);
        }
        var previewNote = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        previewTools.Children.Add(previewNote);
        previewBox.Children.Add(previewTitle);
        previewBox.Children.Add(previewText);
        previewBox.Children.Add(previewTools);
        preview.Child = previewBox;
        root.Children.Add(preview);

        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        hexRow.Children.Add(new TextBlock { Text = "HEX", FontFamily = new FontFamily("Consolas"), FontSize = 11, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var hex = new TextBox { Text = chosen, Width = 120, Height = 30, Margin = new Thickness(0), Padding = new Thickness(8, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas"), MaxLength = 7 };
        hexRow.Children.Add(hex);
        root.Children.Add(hexRow);

        var grid = new StackPanel();
        root.Children.Add(grid);

        void Show(string value, bool syncField)
        {
            chosen = value;
            var current = For(value);
            preview.Background = current.PaperBrush;
            preview.BorderBrush = current.LineBrush;
            previewTitle.Foreground = current.InkBrush;
            previewText.Foreground = current.InkBrush;
            previewNote.Foreground = current.MutedBrush;
            previewNote.Text = current.IsDark ? "TINTA CLARA · FONDO OSCURO" : "TINTA OSCURA · FONDO CLARO";
            foreach (var sample in previewButtons)
            {
                sample.Background = current.ChromeBrush;
                sample.Foreground = current.InkBrush;
                sample.BorderBrush = current.LineBrush;
            }
            if (syncField && !string.Equals(hex.Text, value, StringComparison.OrdinalIgnoreCase)) hex.Text = value;
        }

        foreach (var row in Shades())
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
            foreach (var value in row)
            {
                var swatch = new Button
                {
                    Width = 46,
                    Height = 30,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, 5, 0),
                    Background = Freeze(Parse(value)),
                    BorderThickness = new Thickness(1),
                    ToolTip = value
                };
                swatch.SetResourceReference(Control.BorderBrushProperty, "Line");
                swatch.Click += (_, _) => Show(value, true);
                line.Children.Add(swatch);
            }
            grid.Children.Add(line);
        }

        var presets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 12) };
        foreach (var preset in Palette)
        {
            var swatch = new Button
            {
                Content = preset.Name.ToUpper(CultureInfo.CurrentCulture),
                Height = 28,
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(0, 0, 5, 0),
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Background = Freeze(Parse(preset.Hex))
            };
            swatch.SetResourceReference(Control.BorderBrushProperty, "Line");
            swatch.Foreground = Freeze(Color.FromRgb(0x17, 0x19, 0x16));
            swatch.Click += (_, _) => Show(preset.Hex, true);
            presets.Children.Add(swatch);
        }
        grid.Children.Add(presets);

        hex.TextChanged += (_, _) => { if (TryParse(hex.Text, out var typed)) Show(ToHex(typed), false); };
        Show(chosen, true);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var save = Dialogs.Button("GUARDAR", () => window.DialogResult = true);
        save.IsDefault = true;
        actions.Children.Add(save);
        var cancel = Dialogs.Button("CANCELAR", () => window.DialogResult = false);
        cancel.IsCancel = true;
        actions.Children.Add(cancel);
        root.Children.Add(actions);

        window.Loaded += (_, _) => Dialogs.Modalize(window);
        return window.ShowDialog() == true ? chosen : null;
    }
}
