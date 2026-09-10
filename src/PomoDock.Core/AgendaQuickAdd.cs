using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PomoDock.Core;

/// <summary>
/// Turns one typed line into an event: "Dentista mañana a las 17:30 durante 45m".
/// Whatever it cannot read stays in the title, so a fast note never loses words.
/// </summary>
public static class AgendaQuickAdd
{
    private static Regex Pattern(string expression) => new(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AllDayWords = Pattern(@"\btodo\s+el\s+d[ií]a\b");
    private static readonly Regex ReminderWords = Pattern(@"\b(?:aviso|avisar|av[ií]same|recu[eé]rdame|recordar|alerta)\s+(\d{1,3})\s*(m|min|mins|minutos?|h|hs|horas?|d|d[ií]as?)(?:\s+antes)?\b");
    private static readonly Regex EveryWeekday = Pattern(@"\btodos\s+los\s+(lunes|martes|mi[ée]rcoles|jueves|viernes|s[áa]bados?|domingos?)\b");
    private static readonly Regex EveryNDays = Pattern(@"\bcada\s+(\d{1,2})\s+d[ií]as\b");
    private static readonly Regex EveryDay = Pattern(@"\b(?:cada\s+d[ií]a|todos\s+los\s+d[ií]as|a\s+diario|diariamente)\b");
    private static readonly Regex EveryWeek = Pattern(@"\b(?:cada\s+(\d{1,2})\s+semanas|cada\s+semana|semanalmente)\b");
    private static readonly Regex EveryMonth = Pattern(@"\b(?:cada\s+(\d{1,2})\s+meses|cada\s+mes|mensualmente)\b");
    private static readonly Regex EveryYear = Pattern(@"\b(?:cada\s+(\d{1,2})\s+a[ñn]os|cada\s+a[ñn]o|anualmente)\b");
    private static readonly Regex SpanHoursExplicit = Pattern(@"\b(?:durante|por)\s+(\d{1,2})(?:[.,](\d))?\s*h\b");
    private static readonly Regex SpanMinutes = Pattern(@"\b(?:durante\s+|por\s+)?(\d{1,3})\s*(?:mins?|minutos?|m)\b");
    private static readonly Regex SpanHours = Pattern(@"\b(?:durante\s+|por\s+)?(\d{1,2})(?:[.,](\d))?\s*(?:horas?|hs)\b");
    private static readonly Regex TimeSpoken = Pattern(@"\ba\s+las?\s+(\d{1,2})(?:[:.](\d{2}))?\s*(am|pm|h)?\b");
    private static readonly Regex TimeClock = Pattern(@"\b(\d{1,2}):(\d{2})\s*(am|pm)?\b");
    private static readonly Regex TimeMeridiem = Pattern(@"\b(\d{1,2})\s*(am|pm)\b");
    private static readonly Regex TimeHour = Pattern(@"\b(\d{1,2})\s*h\b");
    private static readonly Regex Today = Pattern(@"\bhoy\b");
    private static readonly Regex DayAfterTomorrow = Pattern(@"\bpasado\s+ma[ñn]ana\b");
    private static readonly Regex Tomorrow = Pattern(@"\bma[ñn]ana\b");
    private static readonly Regex Weekday = Pattern(@"\b(?:el\s+|este\s+|pr[óo]ximo\s+|el\s+pr[óo]ximo\s+)?(lunes|martes|mi[ée]rcoles|jueves|viernes|s[áa]bado|domingo)\b");
    private static readonly Regex NumericDate = Pattern(@"\b(\d{1,2})[/-](\d{1,2})(?:[/-](\d{2,4}))?\b");
    private static readonly Regex NamedDate = Pattern(@"\b(\d{1,2})\s+de\s+(enero|febrero|marzo|abril|mayo|junio|julio|agosto|septiembre|setiembre|octubre|noviembre|diciembre)(?:\s+(?:de\s+)?(\d{4}))?\b");
    private static readonly Regex LeadingFiller = Pattern(@"^(?:el|la|los|las|de|del|a|al|en|para|este|esta|pr[óo]xim[oa])\s+");
    private static readonly Regex TrailingFiller = Pattern(@"\s+(?:el|la|los|las|de|del|a|al|en|para|este|esta|pr[óo]xim[oa])$");

    private static readonly string[] Months =
        ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];

    /// <summary>
    /// Reads a line into a draft event. Returns null when nothing is left to call it, so the caller
    /// can fall back to the full editor instead of saving a nameless entry.
    /// </summary>
    public static AgendaEvent? Parse(string text, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string rest = text;

        bool allDay = Take(ref rest, AllDayWords) is not null;

        int? reminder = null;
        if (Take(ref rest, ReminderWords) is { } alert)
        {
            int value = Number(alert.Groups[1]);
            string unit = alert.Groups[2].Value.ToLowerInvariant();
            reminder = unit.StartsWith('d') ? value * 1440 : unit.StartsWith('h') ? value * 60 : value;
        }

        var repeat = RepeatKind.None;
        int interval = 1;
        DayOfWeek? repeatDay = null;
        if (Take(ref rest, EveryWeekday) is { } weekly)
        {
            repeat = RepeatKind.Weekly;
            repeatDay = DayOf(weekly.Groups[1].Value);
        }
        else if (Take(ref rest, EveryNDays) is { } everyDays) { repeat = RepeatKind.Daily; interval = Number(everyDays.Groups[1]); }
        else if (Take(ref rest, EveryDay) is not null) repeat = RepeatKind.Daily;
        else if (Take(ref rest, EveryWeek) is { } week) { repeat = RepeatKind.Weekly; interval = Number(week.Groups[1], 1); }
        else if (Take(ref rest, EveryMonth) is { } month) { repeat = RepeatKind.Monthly; interval = Number(month.Groups[1], 1); }
        else if (Take(ref rest, EveryYear) is { } year) { repeat = RepeatKind.Yearly; interval = Number(year.Groups[1], 1); }

        int? minutes = null;
        if (Take(ref rest, SpanHoursExplicit) is { } longHours) minutes = Hours(longHours);
        else if (Take(ref rest, SpanMinutes) is { } shortSpan) minutes = Number(shortSpan.Groups[1]);
        else if (Take(ref rest, SpanHours) is { } hours) minutes = Hours(hours);

        TimeOnly? time = null;
        if (Take(ref rest, TimeSpoken) is { } spoken) time = Clock(Number(spoken.Groups[1]), Number(spoken.Groups[2]), spoken.Groups[3].Value);
        else if (Take(ref rest, TimeClock) is { } clock) time = Clock(Number(clock.Groups[1]), Number(clock.Groups[2]), clock.Groups[3].Value);
        else if (Take(ref rest, TimeMeridiem) is { } meridiem) time = Clock(Number(meridiem.Groups[1]), 0, meridiem.Groups[2].Value);
        else if (Take(ref rest, TimeHour) is { } hour) time = Clock(Number(hour.Groups[1]), 0, "h");

        var today = DateOnly.FromDateTime(now);
        DateOnly? date = null;
        if (Take(ref rest, DayAfterTomorrow) is not null) date = today.AddDays(2);
        else if (Take(ref rest, Today) is not null) date = today;
        else if (Take(ref rest, Tomorrow) is not null) date = today.AddDays(1);
        else if (Take(ref rest, NamedDate) is { } named) date = FromParts(Number(named.Groups[1]), MonthOf(named.Groups[2].Value), Number(named.Groups[3], 0), today);
        else if (Take(ref rest, NumericDate) is { } numeric) date = FromParts(Number(numeric.Groups[1]), Number(numeric.Groups[2]), Number(numeric.Groups[3], 0), today);
        else if (Take(ref rest, Weekday) is { } named2) date = NextWeekday(DayOf(named2.Groups[1].Value), today, now, time, named2.Value.Contains("róxim", StringComparison.OrdinalIgnoreCase) || named2.Value.Contains("roxim", StringComparison.OrdinalIgnoreCase));

        string title = Title(rest);
        if (title.Length == 0) return null;

        allDay = allDay || time is null;
        var day = date ?? today;
        var start = allDay ? day.ToDateTime(TimeOnly.MinValue) : day.ToDateTime(time!.Value);
        // A bare time that already passed belongs to tomorrow, never to a moment behind us.
        if (!allDay && date is null && start <= now) start = start.AddDays(1);

        var draft = new AgendaEvent
        {
            Title = title,
            AllDay = allDay,
            Start = start,
            Minutes = allDay ? 1440 : Math.Clamp(minutes ?? 60, 5, 1440),
            Repeat = repeat,
            Interval = Math.Clamp(interval, 1, 99),
            Days = repeatDay is { } fixedDay ? [fixedDay] : repeat == RepeatKind.Weekly ? [start.DayOfWeek] : [],
            Reminders = [reminder ?? (allDay ? 0 : 10)]
        };
        if (repeatDay is { } target && start.DayOfWeek != target)
        {
            // "todos los martes" starts on the next Tuesday, not on the day it was typed.
            var first = NextWeekday(target, DateOnly.FromDateTime(start), now, TimeOnly.FromDateTime(start), false);
            draft.Start = first.ToDateTime(TimeOnly.FromDateTime(start));
        }
        draft.Normalize();
        return draft;
    }

    private static Match? Take(ref string text, Regex pattern)
    {
        var match = pattern.Match(text);
        if (!match.Success) return null;
        text = string.Concat(text.AsSpan(0, match.Index), " ", text.AsSpan(match.Index + match.Length));
        return match;
    }

    private static int Number(Group group, int fallback = 0) =>
        group.Success && int.TryParse(group.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

    private static int Hours(Match match)
    {
        int whole = Number(match.Groups[1]);
        int tenths = Number(match.Groups[2]);
        return Math.Max(5, whole * 60 + tenths * 6);
    }

    /// <summary>
    /// Reads a spoken hour. Without am/pm, an early hour means the afternoon: "a las 4" is 16:00,
    /// which is what the words mean in Spanish far more often than four in the morning.
    /// </summary>
    private static TimeOnly Clock(int rawHour, int rawMinute, string suffix)
    {
        int hour = Math.Clamp(rawHour, 0, 23);
        int minute = Math.Clamp(rawMinute, 0, 59);
        string mark = suffix.ToLowerInvariant();
        if (mark == "pm" && hour < 12) hour += 12;
        else if (mark == "am" && hour == 12) hour = 0;
        else if (mark is not ("am" or "pm") && hour is >= 1 and <= 6) hour += 12;
        return new TimeOnly(hour % 24, minute);
    }

    private static int MonthOf(string word)
    {
        var name = Normalize(word);
        if (name == "setiembre") name = "septiembre";
        return Array.IndexOf(Months, name) + 1;
    }

    private static DayOfWeek DayOf(string word) => Normalize(word) switch
    {
        "lunes" => DayOfWeek.Monday,
        "martes" => DayOfWeek.Tuesday,
        "miercoles" => DayOfWeek.Wednesday,
        "jueves" => DayOfWeek.Thursday,
        "viernes" => DayOfWeek.Friday,
        "sabado" or "sabados" => DayOfWeek.Saturday,
        _ => DayOfWeek.Sunday
    };

    private static DateOnly NextWeekday(DayOfWeek target, DateOnly from, DateTime now, TimeOnly? time, bool forceNext)
    {
        int delta = (AgendaEvent.Ordinal(target) - AgendaEvent.Ordinal(from.DayOfWeek) + 7) % 7;
        if (delta == 0)
        {
            var candidate = from.ToDateTime(time ?? TimeOnly.MaxValue);
            if (forceNext || candidate <= now) delta = 7;
        }
        return from.AddDays(delta);
    }

    private static DateOnly? FromParts(int day, int month, int year, DateOnly today)
    {
        if (month is < 1 or > 12) return null;
        if (year is > 0 and < 100) year += 2000;
        int resolved = year > 0 ? year : today.Year;
        if (day < 1 || day > DateTime.DaysInMonth(resolved, month)) return null;
        var date = new DateOnly(resolved, month, day);
        // A bare day and month that already passed points at next year, the way a diary reads.
        if (year <= 0 && date < today) date = date.AddYears(1);
        return date;
    }

    /// <summary>Strips accents so "miércoles" and "miercoles" resolve to the same weekday.</summary>
    private static string Normalize(string word)
    {
        var lowered = word.ToLowerInvariant();
        var builder = new System.Text.StringBuilder(lowered.Length);
        foreach (char character in lowered.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark) builder.Append(character);
        return builder.ToString();
    }

    private static string Title(string rest)
    {
        string text = Regex.Replace(rest, @"\s+", " ").Trim();
        text = text.Trim(' ', '-', '–', '—', ':', ',', '.', ';');
        for (int guard = 0; guard < 6 && LeadingFiller.IsMatch(text); guard++) text = LeadingFiller.Replace(text, "", 1).Trim();
        for (int guard = 0; guard < 6 && TrailingFiller.IsMatch(text); guard++) text = TrailingFiller.Replace(text, "", 1).Trim();
        text = text.Trim(' ', '-', '–', '—', ':', ',', '.', ';');
        return text.Length == 0 ? "" : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
    }
}
