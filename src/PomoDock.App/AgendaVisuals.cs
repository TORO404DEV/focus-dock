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
    /// <summary>Month and weekday names follow the language the user chose, not the system locale.</summary>
    public static CultureInfo Culture => Strings.Culture;

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

    public static string MonthLabel(DateOnly day) => day.ToString("MMMM yyyy", Culture).ToUpper(Culture);
    public static string DayLabel(DateOnly day) => day.ToString("ddd dd MMM", Culture).ToUpper(Culture).Replace(".", "");
    public static string WeekdayLabel(DateOnly day) => day.ToString("ddd", Culture).ToUpper(Culture).Replace(".", "");
    /// <summary>The long form differs per language, so the whole pattern is a translated string.</summary>
    public static string LongDayLabel(DateOnly day) => day.ToString(L.T("date.longDay"), Culture).ToUpper(Culture);

    /// <summary>"TODAY", "TOMORROW", "YESTERDAY" — or an empty string when the day speaks for itself.</summary>
    public static string Relative(DateOnly day, DateOnly today) => (day.DayNumber - today.DayNumber) switch
    {
        0 => L.T("common.today"),
        1 => L.T("common.tomorrow"),
        -1 => L.T("common.yesterday"),
        _ => ""
    };

    /// <summary>A compact countdown used by the toast and the agenda list.</summary>
    public static string Countdown(DateTime start, DateTime now)
    {
        var delta = start - now;
        if (delta.TotalSeconds <= 30) return L.T("common.now");
        if (delta.TotalMinutes < 60) return L.T("common.inMinutes", Math.Max(1, Math.Round(delta.TotalMinutes)));
        if (delta.TotalHours < 24) return delta.Minutes == 0 ? L.T("common.inHours", (int)delta.TotalHours) : L.T("common.inHoursMinutes", (int)delta.TotalHours, delta.Minutes);
        return L.T("common.inDays", (int)delta.TotalDays);
    }

    public static string DurationLabel(int minutes)
    {
        if (minutes % 1440 == 0 && minutes >= 1440) return minutes == 1440 ? L.T("common.day") : L.T("common.days", minutes / 1440);
        if (minutes < 60) return L.T("common.minutesShort", minutes);
        return minutes % 60 == 0 ? L.T("common.hoursShort", minutes / 60) : L.T("common.hoursMinutes", minutes / 60, minutes % 60);
    }
}
