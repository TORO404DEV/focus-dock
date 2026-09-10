using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// Shared looks and wording for the calendar: colours, Spanish dates and relative day names.
/// Widget, editor and reminder toast all read from here so they never drift apart.
/// </summary>
internal static class AgendaVisuals
{
    /// <summary>The interface is Spanish, so month and weekday names never follow the system locale.</summary>
    public static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");

    private static readonly Dictionary<string, SolidColorBrush> solids = [];
    private static readonly Dictionary<string, SolidColorBrush> washes = [];

    public static SolidColorBrush Solid(string? key)
    {
        var color = AgendaPalette.Of(key);
        if (solids.TryGetValue(color.Key, out var cached)) return cached;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color.Hex)!);
        brush.Freeze(); solids[color.Key] = brush;
        return brush;
    }

    /// <summary>A faint version of the same colour, for chip and block backgrounds.</summary>
    public static SolidColorBrush Wash(string? key)
    {
        var color = AgendaPalette.Of(key);
        if (washes.TryGetValue(color.Key, out var cached)) return cached;
        var source = (Color)ColorConverter.ConvertFromString(color.Hex)!;
        var brush = new SolidColorBrush(Color.FromArgb(38, source.R, source.G, source.B));
        brush.Freeze(); washes[color.Key] = brush;
        return brush;
    }

    public static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>
    /// A translucent version of a theme brush, for grid lines and washes. It reads the colour the
    /// theme is wearing right now, so the light and dark palettes both stay legible.
    /// </summary>
    public static Brush Fade(string key, byte alpha)
    {
        if (Application.Current.Resources[key] is not SolidColorBrush source) return Resource(key);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, source.Color.R, source.Color.G, source.Color.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Wraps a text box with a hint that shows while it is empty. WPF has no placeholder of its
    /// own, and an empty box gives no clue that it understands "mañana a las 10".
    /// </summary>
    public static Grid WithHint(TextBox box, string hint)
    {
        var label = new TextBlock
        {
            Text = hint,
            Foreground = Resource("Muted"),
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(box.Padding.Left + 3, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        // The hint tracks the box, so a card that scales its type never leaves the placeholder behind.
        label.SetBinding(TextBlock.FontSizeProperty, new System.Windows.Data.Binding(nameof(TextBox.FontSize)) { Source = box });
        void Sync() => label.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Sync();
        Sync();
        var wrap = new Grid();
        wrap.Children.Add(box);
        wrap.Children.Add(label);
        return wrap;
    }

    public static string MonthLabel(DateOnly day) => day.ToString("MMMM yyyy", Spanish).ToUpper(Spanish);
    public static string DayLabel(DateOnly day) => day.ToString("ddd dd MMM", Spanish).ToUpper(Spanish).Replace(".", "");
    public static string WeekdayLabel(DateOnly day) => day.ToString("ddd", Spanish).ToUpper(Spanish).Replace(".", "");
    public static string LongDayLabel(DateOnly day) => day.ToString("dddd d 'de' MMMM", Spanish).ToUpper(Spanish);

    /// <summary>"HOY", "MAÑANA", "AYER" — or an empty string when the day speaks for itself.</summary>
    public static string Relative(DateOnly day, DateOnly today) => (day.DayNumber - today.DayNumber) switch
    {
        0 => "HOY",
        1 => "MAÑANA",
        -1 => "AYER",
        _ => ""
    };

    /// <summary>A compact countdown used by the toast and the agenda list.</summary>
    public static string Countdown(DateTime start, DateTime now)
    {
        var delta = start - now;
        if (delta.TotalSeconds <= 30) return "AHORA";
        if (delta.TotalMinutes < 60) return $"EN {Math.Max(1, Math.Round(delta.TotalMinutes))} MIN";
        if (delta.TotalHours < 24) return delta.Minutes == 0 ? $"EN {(int)delta.TotalHours} H" : $"EN {(int)delta.TotalHours} H {delta.Minutes} MIN";
        return $"EN {(int)delta.TotalDays} DÍAS";
    }

    public static string DurationLabel(int minutes)
    {
        if (minutes % 1440 == 0 && minutes >= 1440) return minutes == 1440 ? "1 día" : $"{minutes / 1440} días";
        if (minutes < 60) return $"{minutes} min";
        return minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";
    }
}
