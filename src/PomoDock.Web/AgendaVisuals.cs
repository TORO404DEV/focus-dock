using System.Globalization;
using PomoDock.Core;

namespace PomoDock.Web;

/// <summary>
/// Shared looks and wording for the calendar: colours, dates and relative day names.
/// Widget, editor and reminder cards all read from here so they never drift apart.
/// </summary>
internal static class AgendaVisuals
{
    /// <summary>Month and weekday names follow the language the user chose, not the browser.</summary>
    public static CultureInfo Culture => Strings.Culture;

    public static string Solid(string? key) => AgendaPalette.Of(key).Hex;

    /// <summary>A faint version of the same colour, for chip and block backgrounds.</summary>
    public static string Wash(string? key)
    {
        Theme.TryParse(AgendaPalette.Of(key).Hex, out var color);
        return string.Create(CultureInfo.InvariantCulture, $"rgba({color.R},{color.G},{color.B},{38 / 255.0:0.###})");
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

    /// <summary>A compact countdown used by the reminder cards and the agenda list.</summary>
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
