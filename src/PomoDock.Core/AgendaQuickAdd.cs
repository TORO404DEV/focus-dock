using System.Globalization;
using System.Text.RegularExpressions;

namespace PomoDock.Core;

/// <summary>A read line plus what the reader actually found in it, beyond the title.</summary>
public sealed record QuickAddReading(AgendaEvent Event, bool Dated, bool Timed, bool Repeats, bool Relative = false, bool ExplicitReminder = false)
{
    /// <summary>The line named a day, an hour or a rhythm: something a calendar can hold.</summary>
    public bool Scheduled => Dated || Timed || Repeats;
}

/// <summary>
/// Turns one typed line into an event: "Dentista el viernes 18 a las 4 y media avísame 1 h antes".
/// Whatever it cannot read stays in the title, so a fast note never loses words.
/// </summary>
/// <remarks>
/// Every pattern runs against a folded copy of the line — lowercase, without accents, ñ as n —
/// that keeps the exact length of what was typed. Anything recognised is blanked out of both
/// copies at once, so the title is simply what is left, in the user's own spelling.
/// </remarks>
public static class AgendaQuickAdd
{
    // ------------------------------------------------------------------ vocabulary

    private const string NumberWords =
        "veintisiete|veintiocho|veintinueve|veinticuatro|veinticinco|veintiseis|veintitres|diecisiete|dieciocho|" +
        "diecinueve|dieciseis|veintidos|veintiuno|veintiun|cincuenta|cuarenta|catorce|treinta|quince|cuatro|veinte|" +
        "nueve|siete|trece|cinco|once|doce|diez|ocho|seis|tres|dos|una|uno|un";
    private const string Num = @"(?:\d{1,3}(?!\d)|(?:" + NumberWords + @")(?![a-z]))";
    private const string Weekday = "(?:lunes|martes|miercoles|jueves|viernes|sabado|domingo)";
    private const string Weekdays = "(?:lunes|martes|miercoles|jueves|viernes|sabados?|domingos?)";
    private const string Month =
        @"(?:enero|febrero|marzo|abril|mayo|junio|julio|agosto|septiembre|setiembre|octubre|noviembre|diciembre|" +
        @"sept|ene|feb|mar|abr|may|jun|jul|ago|sep|set|oct|nov|dic)\b\.?";
    private const string DayNumber = @"(?:3[01]|[12]\d|0?[1-9])";
    private const string NotAQuantity = @"(?!\s*(?:h|hrs?|hs|am|pm|a\.m|p\.m|horas?|mins?|minutos?|dias?|semanas?|meses|anos|veces|personas)\b)";

    private static readonly Dictionary<string, int> Numbers = new(StringComparer.Ordinal)
    {
        ["un"] = 1, ["uno"] = 1, ["una"] = 1, ["dos"] = 2, ["tres"] = 3, ["cuatro"] = 4, ["cinco"] = 5,
        ["seis"] = 6, ["siete"] = 7, ["ocho"] = 8, ["nueve"] = 9, ["diez"] = 10, ["once"] = 11, ["doce"] = 12,
        ["trece"] = 13, ["catorce"] = 14, ["quince"] = 15, ["dieciseis"] = 16, ["diecisiete"] = 17,
        ["dieciocho"] = 18, ["diecinueve"] = 19, ["veinte"] = 20, ["veintiun"] = 21, ["veintiuno"] = 21,
        ["veintidos"] = 22, ["veintitres"] = 23, ["veinticuatro"] = 24, ["veinticinco"] = 25,
        ["veintiseis"] = 26, ["veintisiete"] = 27, ["veintiocho"] = 28, ["veintinueve"] = 29,
        ["treinta"] = 30, ["cuarenta"] = 40, ["cincuenta"] = 50
    };

    /// <summary>A clock reading; the prefix keeps the groups apart when two share a pattern.</summary>
    private static string Clock(string p) =>
        @"(?<" + p + @"h>\d{1,2}(?!\d)|(?:" + NumberWords + @")(?![a-z]))(?:\s*[:.]\s*(?<" + p + @"m>\d{2})(?!\d))?" +
        @"(?:\s+y\s+(?<" + p + @"f>media|cuarto|\d{1,2}(?!\d)|(?:" + NumberWords + @")(?![a-z]))(?:\s*min(?:utos)?\b)?" +
        @"|\s+menos\s+(?<" + p + @"l>cuarto|\d{1,2}(?!\d)|(?:" + NumberWords + @")(?![a-z])))?" +
        @"(?:\s*(?<" + p + @"a>a\.?\s?m\b\.?|p\.?\s?m\b\.?)|\s*(?<" + p + @"x>h|hrs?|hs)\b)?" +
        @"(?:\s+(?:de\s+la|del|en\s+la|por\s+la)\s+(?<" + p + @"p>manana|tarde|noche|madrugada|mediodia)\b)?";

    /// <summary>Any single date, with no groups of its own, for ranges and "hasta …".</summary>
    private const string DatePhrase =
        @"(?:(?:" + Weekday + @",?\s+)?(?:el\s+)?(?:dia\s+)?" + DayNumber + @"(?:\s+de)?\s+" + Month + @"(?:,?\s+(?:del?\s+)?\d{4})?" +
        @"|" + Month + @"\s+" + DayNumber + @"(?!\d)(?:,?\s+(?:de\s+)?\d{4})?" +
        @"|\d{4}-\d{1,2}-\d{1,2}" +
        @"|" + DayNumber + @"[/-](?:1[0-2]|0?[1-9])(?:[/-](?:\d{4}|\d{2}))?(?![\d/-])" +
        @"|" + Weekday + @"\s+" + DayNumber + @"(?![\d:])" +
        @"|" + Weekday +
        @"|pasado\s+manana|manana|hoy" +
        @"|(?:el\s+)?fin\s+de\s+mes" +
        @"|(?:dia\s+)?" + DayNumber + @"(?![\d:])" +
        @")";

    private static Regex R(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    // ------------------------------------------------------------------ patterns

    private static readonly Regex ColorTag = R(@"#(?<c>[a-z]+)");
    private static readonly Regex PlaceTag = R(@"@(?<p>[^\s@#]+)");
    private static readonly Regex AllDayWords = R(@"\b(?:durante\s+)?todo\s+el\s+dia\b");

    private static readonly Regex Silent = R(@"\bsin\s+(?:aviso|avisos|recordatorio|recordatorios|alarma|alerta|notificacion)\b");
    private static readonly Regex ReminderWords = R(
        @"\b(?:avisame|avisarme|avisar|avisa|aviso|recuerdame|recordarme|recordar|recordatorio|alerta|alarma|notificame|notificar)" +
        @"(?:\s+con)?\s+(?<n>" + Num + @"|media|un\s+par\s+de)?\s*(?<u>minutos?|mins?|m|horas?|hrs?|hs?|dias?|d|semanas?)\b(?:\s+antes)?");
    private static readonly Regex ReminderBefore = R(
        @"\b(?<n>" + Num + @"|media|un\s+par\s+de)\s*(?<u>minutos?|mins?|m|horas?|hrs?|hs?|dias?|d|semanas?)\s+antes\b");

    private static readonly Regex EveryWeekdayList = R(
        @"\b(?:todos\s+los|cada|los)\s+(?<list>" + Weekdays + @"(?:\s*(?:,|y|e)\s*(?:los\s+)?" + Weekdays + @")+)\b");
    private static readonly Regex Workdays = R(
        @"\b(?:de\s+lunes\s+a\s+viernes|entre\s+semana|(?:todos\s+los\s+|los\s+|en\s+)?dias\s+(?:laborables|habiles|de\s+semana)|cada\s+dia\s+laborable)\b");
    private static readonly Regex Weekends = R(@"\b(?:todos\s+los|los|cada)\s+(?:fines?\s+de\s+semana|findes?)\b");
    private static readonly Regex EveryWeekday = R(@"\b(?:todos\s+los|cada|los)\s+(?<w>" + Weekdays + @")\b");
    private static readonly Regex EveryN = R(@"\bcada\s+(?<n>" + Num + @"|otro|otra)\s+(?<u>dias?|semanas?|mes(?:es)?|anos?)\b");
    private static readonly Regex EveryUnit = R(@"\b(?:cada|todos\s+los|todas\s+las)\s+(?<u>dias?|semanas?|mes(?:es)?|anos?)\b");
    private static readonly Regex RepeatAdverb = R(
        @"\b(?<a>a\s+diario|diariamente|semanalmente|semanal|quincenalmente|quincenal|mensualmente|mensual|anualmente|anual)\b");

    private static readonly Regex Times = R(@"\b(?<n>" + Num + @")\s+veces\b");
    private static readonly Regex UntilMonth = R(@"\bhasta\s+(?:(?:el\s+)?fin(?:al)?\s+de\s+)?(?<m>" + Month + @")(?!\s*\d)");
    private static readonly Regex Until = R(@"\bhasta\s+(?:el\s+)?(?<p>" + DatePhrase + ")");
    private static readonly Regex Lasting = R(@"\b(?:durante|por)\s+(?<n>" + Num + @"|un\s+par\s+de)\s+(?<u>dias?|semanas?|mes(?:es)?|anos?)\b");

    private static readonly Regex InOffset = R(
        @"\b(?:en|dentro\s+de|in)\s+(?<n>" + Num + @"|un\s+par\s+de|media)\s+(?<u>minutos?|mins?|minutes?|horas?|hrs?|hours?|dias?|days?|semanas?|weeks?|mes(?:es)?|months?|anos?|years?)\b");

    private static readonly Regex DateRange = R(
        @"\b(?:del|desde\s+el|desde)\s+(?<a>" + DatePhrase + @")\s+(?:al|hasta\s+el|hasta|a)\s+(?<b>" + DatePhrase + ")");

    private const string NotAUnitAfter = @"(?!\s*(?:dias?|semanas?|meses|anos|personas|veces|km|kg)\b)";
    private static readonly Regex TimeRange = R(
        @"\b(?:de|desde)\s+(?:las?\s+)?" + Clock("a") + @"\s*(?:a|hasta|-)\s*(?:las?\s+)?" + Clock("b") + NotAUnitAfter);
    private static readonly Regex TimeBetween = R(
        @"\bentre\s+(?:las?\s+)?" + Clock("a") + @"\s+y\s+(?:las?\s+)?" + Clock("b") + NotAUnitAfter);
    private static readonly Regex TimeDash = R(
        @"\b(?<ah>\d{1,2})(?::(?<am>\d{2}))?\s*-\s*(?<bh>\d{1,2})(?::(?<bm>\d{2}))?\s*(?:(?<ba>a\.?\s?m\b\.?|p\.?\s?m\b\.?)|(?<bx>h|hrs|hs)\b)");
    private static readonly Regex TimeDashClock = R(@"\b(?<ah>\d{1,2}):(?<am>\d{2})\s*-\s*(?<bh>\d{1,2}):(?<bm>\d{2})(?!\d)");

    private static readonly Regex DurationHours = R(
        @"\b(?:durante|por)\s+(?<h>" + Num + @")(?:[.,](?<t>\d))?\s*(?:h|hrs?|hs|horas?)\b(?:\s*(?:y\s+)?(?<m>\d{1,2})\s*(?:m|mins?|minutos?)\b)?(?<half>\s+y\s+media)?");
    private static readonly Regex DurationCompact = R(@"(?<!\bla\s)(?<!\blas\s)\b(?:durante\s+|por\s+)?(?<h>\d{1,2})\s*h\s*(?<m>\d{1,2})\s*(?:m|min)?\b");
    private static readonly Regex DurationHourAndHalf = R(@"\b(?:durante\s+|por\s+)?(?:una\s+)?hora\s+y\s+media\b");
    private static readonly Regex DurationHalfHour = R(@"\b(?:durante\s+|por\s+)?media\s+hora\b");
    private static readonly Regex DurationOneHour = R(@"\b(?:durante|por)\s+(?:una|1)\s+hora\b");
    private static readonly Regex DurationMinutes = R(@"\b(?:durante\s+|por\s+)?(?<m>\d{1,3})\s*(?:m|mins?|minutos?)\b");
    private static readonly Regex DurationHoursWord = R(@"\b(?:durante\s+|por\s+)?(?<h>" + Num + @")(?:[.,](?<t>\d))?\s*horas\b(?<half>\s+y\s+media)?");
    private static readonly Regex DurationShortHours = R(@"(?<!\bla\s)(?<!\blas\s)\b(?<h>[1-4])\s*h\b(?!\s*\d)");

    private static readonly Regex TimeSpoken = R(@"\b(?:a|para|desde|sobre|como\s+a|antes\s+de)\s+las?\s+" + Clock("t") + @"(?!\d)");
    private static readonly Regex Noon = R(@"\b(?:al|a|en\s+el|para\s+el)\s+mediodia\b");
    private static readonly Regex Midnight = R(@"\b(?:a\s+(?:la\s+)?)?medianoche\b");
    private static readonly Regex TimeColon = R(
        @"\b(?<th>\d{1,2})\s*:\s*(?<tm>\d{2})(?!\d)(?:\s*(?<ta>a\.?\s?m\b\.?|p\.?\s?m\b\.?)|\s*(?<tx>h|hrs?|hs)\b)?" +
        @"(?:\s+(?:de\s+la|del|en\s+la|por\s+la)\s+(?<tp>manana|tarde|noche|madrugada|mediodia)\b)?");
    private static readonly Regex TimeMeridiem = R(@"\b(?<th>\d{1,2})(?:\s*[:.]\s*(?<tm>\d{2}))?\s*(?<ta>a\.?\s?m\b\.?|p\.?\s?m\b\.?)");
    private static readonly Regex TimeHourMark = R(@"\b(?<th>\d{1,2})(?:\s*[:.]\s*(?<tm>\d{2}))?\s*(?<tx>h|hrs|hs)\b");

    private static readonly Regex ThisPeriod = R(@"\besta\s+(?<p>manana|tarde|noche)\b");
    private static readonly Regex Period = R(@"\b(?:por|en|de|a)\s+la\s+(?<p>manana|tarde|noche|madrugada)\b");
    /// <summary>"antes de 2pm", "antes del viernes": a deadline reads as the moment itself.</summary>
    private static readonly Regex DeadlineWords = R(
        @"\b(?:para\s+)?antes\s+del?(?=\s+(?:\d{1,2}\s*(?:[:.]\s*\d{2})?\s*(?:a\.?\s?m\b|p\.?\s?m\b|h\b|hrs\b)|" + Weekday +
        @"|el\s|\d{1,2}\s+de\s|\d{1,2}[/-]|fin\s|manana\b|hoy\b|pasado\b))");

    private static readonly Regex PlaceAfterEn = new(@"(?:^|\s)en\s+(?<p>\S.*)$", RegexOptions.CultureInvariant | RegexOptions.RightToLeft | RegexOptions.ExplicitCapture);
    private static readonly string[] PlaceWords =
    [
        "casa", "mi casa", "la casa", "su casa", "la oficina", "oficina", "el trabajo", "trabajo", "la escuela", "escuela",
        "el colegio", "la universidad", "la uni", "el gimnasio", "el gym", "gym", "el hospital", "la clinica", "el consultorio",
        "el centro", "el restaurante", "el cafe", "la cafeteria", "el parque", "el cine", "el aeropuerto", "la iglesia",
        "el super", "el mercado", "el banco", "zoom", "meet", "teams", "google meet"
    ];

    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "el", "la", "los", "las", "de", "del", "a", "al", "en", "para", "este", "esta", "y", "e", "con", "desde",
        "hasta", "que", "viene", "proximo", "proxima", "por", "sobre", "como", "dia", "siguiente", "el dia", "antes"
    };

    // ------------------------------------------------------------------ dates

    private sealed record Context(DateOnly Today, DateTime Now, TimeOnly? Time);
    private sealed record DateForm(Regex Find, Func<Match, Context, DateOnly?> Resolve);

    /// <summary>Most specific first: a full date beats a weekday, a weekday beats a bare number.</summary>
    private static readonly DateForm[] DateForms =
    [
        new(R(@"\b(?:el\s+)?(?:(?<w>" + Weekday + @"),?\s+)?(?:el\s+)?(?:dia\s+)?(?<d>" + DayNumber + @")(?:\s+de)?\s+(?<m>" + Month + @")(?:,?\s+(?:del?\s+)?(?<y>\d{4}))?"),
            (m, c) => FromParts(Int(m, "d"), MonthOf(m.Groups["m"].Value), Int(m, "y"), c.Today)),
        new(R(@"\b(?<m>" + Month + @")\s+(?<d>" + DayNumber + @")(?!\d)(?:,?\s+(?:de\s+)?(?<y>\d{4}))?"),
            (m, c) => FromParts(Int(m, "d"), MonthOf(m.Groups["m"].Value), Int(m, "y"), c.Today)),
        new(R(@"\b(?<y>\d{4})-(?<mo>1[0-2]|0?[1-9])-(?<d>" + DayNumber + @")(?!\d)"),
            (m, c) => FromParts(Int(m, "d"), Int(m, "mo"), Int(m, "y"), c.Today)),
        new(R(@"\b(?:el\s+)?(?<d>" + DayNumber + @")[/-](?<mo>1[0-2]|0?[1-9])(?:[/-](?<y>\d{4}|\d{2}))?(?![\d/-])"),
            (m, c) => FromParts(Int(m, "d"), Int(m, "mo"), Int(m, "y"), c.Today)),
        new(R(@"\b(?:el\s+)?(?<w>" + Weekday + @")\s+de\s+la\s+(?:proxima\s+semana|semana\s+(?:que\s+viene|proxima|siguiente))\b"),
            (m, c) => AgendaEvent.WeekStart(c.Today).AddDays(7 + AgendaEvent.Ordinal(DayOf(m.Groups["w"].Value)))),
        new(R(@"\b(?:el\s+|este\s+)?(?<w>" + Weekday + @")\s+(?<d>" + DayNumber + @")(?![\d:.])" + NotAQuantity + @"(?!\s+de\s+la\b)"),
            (m, c) => WeekdayWithNumber(DayOf(m.Groups["w"].Value), Int(m, "d"), c.Today)),
        new(R(@"\b(?:(?<mod>este|esta|el\s+proximo|la\s+proxima|proximo|el)\s+)?(?<w>" + Weekday + @")(?<after>\s+(?:proximo|que\s+viene|siguiente))?\b"),
            (m, c) => NextWeekday(DayOf(m.Groups["w"].Value), c.Today, c.Now, c.Time,
                m.Groups["mod"].Value.Contains("proxim", StringComparison.Ordinal) || m.Groups["after"].Success)),
        new(R(@"\b(?:la\s+|para\s+la\s+)?(?:proxima\s+semana|semana\s+(?:que\s+viene|proxima|siguiente))\b"),
            (_, c) => AgendaEvent.WeekStart(c.Today).AddDays(7)),
        new(R(@"\b(?:el\s+|para\s+el\s+)?(?:proximo\s+mes|mes\s+(?:que\s+viene|proximo|siguiente))\b"),
            (_, c) => new DateOnly(c.Today.Year, c.Today.Month, 1).AddMonths(1)),
        new(R(@"\b(?:este\s+|el\s+|la\s+|para\s+el\s+)?(?:fin\s+de\s+semana|finde)\b"),
            (_, c) => c.Today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? c.Today : NextWeekday(DayOfWeek.Saturday, c.Today, c.Now, null, false)),
        new(R(@"\b(?:a\s+|para\s+)?(?:el\s+)?fin(?:al)?\s+de(?:l)?\s+mes\b"),
            (_, c) => new DateOnly(c.Today.Year, c.Today.Month, DateTime.DaysInMonth(c.Today.Year, c.Today.Month))),
        new(R(@"\b(?:el\s+(?:dia\s+)?|dia\s+)(?<d>" + DayNumber + @")(?![\d:./-])" + NotAQuantity + @"(?!\s+(?:de\s+)?" + Month + ")"),
            (m, c) => NextDayOfMonth(Int(m, "d"), c.Today)),
        new(R(@"\bpasado\s+manana\b"), (_, c) => c.Today.AddDays(2)),
        new(R(@"\bhoy\b"), (_, c) => c.Today),
        new(R(@"\bmanana\b"), (_, c) => c.Today.AddDays(1)),
        // Only reachable from a phrase: a bare number means nothing on its own in running text.
        new(R(@"^(?:dia\s+)?(?<d>" + DayNumber + @")$"), (m, c) => NextDayOfMonth(Int(m, "d"), c.Today))
    ];

    // ------------------------------------------------------------------ parse

    /// <summary>
    /// Reads a line into a draft event. Returns null when nothing is left to call it, so the caller
    /// can fall back to the full editor instead of saving a nameless entry.
    /// </summary>
    public static AgendaEvent? Parse(string text, DateTime now) => Read(text, now)?.Event;

    /// <summary>Like <see cref="Parse"/>, and also says whether a day or an hour was really written.</summary>
    public static QuickAddReading? Read(string text, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = new Line(text);
        var today = DateOnly.FromDateTime(now);

        // Tags are explicit, so they go first and never leak into anything else.
        string color = "ink";
        foreach (Match tag in ColorTag.Matches(line.Folded))
        {
            if (ColorFor(tag.Groups["c"].Value) is not { } key) continue;
            color = key;
            line.Cut(tag.Index, tag.Length);
        }
        string location = "";
        if (line.Peek(PlaceTag) is { } placeTag)
        {
            var group = placeTag.Groups["p"];
            location = line.Slice(group.Index, group.Length).Replace('_', ' ').Trim();
            line.Cut(placeTag.Index, placeTag.Length);
        }

        bool allDayWords = line.Take(AllDayWords) is not null;
        line.Take(DeadlineWords);

        bool silent = line.Take(Silent) is not null;
        int? reminder = null;
        if (!silent && (line.Take(ReminderWords) ?? line.Take(ReminderBefore)) is { } alert) reminder = ReminderMinutes(alert);

        // Repetition.
        var repeat = RepeatKind.None;
        int interval = 1;
        var days = new List<DayOfWeek>();
        if (line.Take(EveryWeekdayList) is { } list)
        {
            repeat = RepeatKind.Weekly;
            days = Regex.Matches(list.Groups["list"].Value, Weekdays).Select(item => DayOf(item.Value)).Distinct().ToList();
        }
        else if (line.Take(Workdays) is not null)
        {
            repeat = RepeatKind.Weekly;
            days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
        }
        else if (line.Take(Weekends) is not null)
        {
            repeat = RepeatKind.Weekly;
            days = [DayOfWeek.Saturday, DayOfWeek.Sunday];
        }
        else if (line.Take(EveryWeekday) is { } single)
        {
            repeat = RepeatKind.Weekly;
            days = [DayOf(single.Groups["w"].Value)];
        }
        else if (line.Take(EveryN) is { } everyN)
        {
            repeat = KindOf(everyN.Groups["u"].Value);
            interval = everyN.Groups["n"].Value is "otro" or "otra" ? 2 : ToNumber(everyN.Groups["n"].Value);
        }
        else if (line.Take(EveryUnit) is { } every) repeat = KindOf(every.Groups["u"].Value);
        else if (line.Take(RepeatAdverb) is { } adverb)
        {
            string word = adverb.Groups["a"].Value;
            if (word.StartsWith("quincenal", StringComparison.Ordinal)) { repeat = RepeatKind.Weekly; interval = 2; }
            else repeat = word.StartsWith("a diario", StringComparison.Ordinal) || word.StartsWith("diari", StringComparison.Ordinal) ? RepeatKind.Daily
                : word.StartsWith("semanal", StringComparison.Ordinal) ? RepeatKind.Weekly
                : word.StartsWith("mensual", StringComparison.Ordinal) ? RepeatKind.Monthly
                : RepeatKind.Yearly;
        }

        // A stretch of days: "del 18 al 20 de septiembre".
        DateOnly? spanStart = null, spanEnd = null;
        if (line.Take(DateRange) is { } range)
        {
            var (rangeFirst, rangeLast) = ResolveRange(range.Groups["a"].Value, range.Groups["b"].Value, today, now);
            spanStart = rangeFirst;
            spanEnd = rangeLast;
        }

        // How a series ends, or how long a single stay lasts.
        int count = 0;
        if (repeat != RepeatKind.None && line.Take(Times) is { } times) count = ToNumber(times.Groups["n"].Value);
        int? untilMonth = null;
        string? untilPhrase = null;
        if (line.Take(UntilMonth) is { } untilMonthMatch) untilMonth = MonthOf(untilMonthMatch.Groups["m"].Value);
        else if (line.Take(Until) is { } untilMatch) untilPhrase = untilMatch.Groups["p"].Value;
        (int Amount, string Unit)? lasting = null;
        if (line.Take(Lasting) is { } lastingMatch) lasting = (ToNumber(lastingMatch.Groups["n"].Value), lastingMatch.Groups["u"].Value);

        // "en 30 minutos" is a moment; "en 3 días" is a day.
        DateOnly? date = null;
        TimeOnly? time = null;
        DateTime? exact = null;
        if (line.Take(InOffset) is { } offset)
        {
            string unit = offset.Groups["u"].Value;
            string amount = offset.Groups["n"].Value;
            double value = amount == "media" ? .5 : ToNumber(amount);
            if (unit.StartsWith("mes", StringComparison.Ordinal) || unit.StartsWith("mon", StringComparison.Ordinal)) date = today.AddMonths((int)Math.Max(1, value));
            else if (unit.StartsWith('m')) exact = now.AddMinutes(value);
            else if (unit.StartsWith('h')) exact = now.AddMinutes(value * 60);
            else if (unit.StartsWith('d')) date = today.AddDays((int)Math.Max(1, value));
            else if (unit.StartsWith('s') || unit.StartsWith('w')) date = today.AddDays(7 * (int)Math.Max(1, value));
            else date = today.AddYears((int)Math.Max(1, value));
        }

        // Clock ranges first, so "de 5 a 7" is never read as a lone time and a duration.
        int? duration = null;
        if (exact is null && (line.TakeWhere(TimeRange, ValidRange) ?? line.TakeWhere(TimeBetween, ValidRange)
            ?? line.TakeWhere(TimeDashClock, ValidRange) ?? line.TakeWhere(TimeDash, ValidRange)) is { } clockRange)
        {
            var (from, to) = ReadRange(clockRange)!.Value;
            time = from;
            int span = (int)(to.ToTimeSpan() - from.ToTimeSpan()).TotalMinutes;
            duration = span > 0 ? span : span + 1440;
        }

        if (duration is null)
        {
            if (line.Take(DurationHours) is { } hours) duration = HoursOf(hours);
            else if (line.Take(DurationCompact) is { } compact) duration = Int(compact, "h") * 60 + Int(compact, "m");
            else if (line.Take(DurationHourAndHalf) is not null) duration = 90;
            else if (line.Take(DurationHalfHour) is not null) duration = 30;
            else if (line.Take(DurationOneHour) is not null) duration = 60;
            else if (line.Take(DurationMinutes) is { } minutes) duration = Int(minutes, "m");
            else if (line.Take(DurationHoursWord) is { } hoursWord) duration = HoursOf(hoursWord);
            else if (line.Take(DurationShortHours) is { } shortHours) duration = Int(shortHours, "h") * 60;
        }

        bool midnight = false;
        if (time is null && exact is null)
        {
            if (line.TakeWhere(TimeSpoken, match => ReadClock(match, "t") is not null) is { } spoken) time = ReadClock(spoken, "t")!.Value.Time;
            else if (line.Take(Noon) is not null) time = new TimeOnly(12, 0);
            else if (line.Take(Midnight) is not null) { time = TimeOnly.MinValue; midnight = true; }
            else if (line.TakeWhere(TimeColon, match => ReadClock(match, "t") is not null) is { } colon) time = ReadClock(colon, "t")!.Value.Time;
            else if (line.TakeWhere(TimeMeridiem, match => ReadClock(match, "t") is not null) is { } meridiem) time = ReadClock(meridiem, "t")!.Value.Time;
            else if (line.TakeWhere(TimeHourMark, match => ReadClock(match, "t") is not null) is { } mark) time = ReadClock(mark, "t")!.Value.Time;
        }

        // Parts of the day: "esta noche" names a day too, "por la tarde" only an hour.
        if (line.Take(ThisPeriod) is { } thisPeriod)
        {
            date ??= today;
            time = ApplyPeriod(time, thisPeriod.Groups["p"].Value);
        }
        if (line.Take(Period) is { } period) time = ApplyPeriod(time, period.Groups["p"].Value);

        if (date is null && exact is null && spanStart is null)
        {
            var context = new Context(today, now, time);
            foreach (var form in DateForms)
            {
                var match = line.Peek(form.Find);
                if (match is null || form.Resolve(match, context) is not { } found) continue;
                date = found;
                line.Cut(match.Index, match.Length);
                break;
            }
        }

        if (exact is { } moment)
        {
            date = DateOnly.FromDateTime(moment);
            time = TimeOnly.FromDateTime(moment);
        }

        // A place written as "en …" is copied, never cut: the title keeps its own words.
        if (location.Length == 0) location = PlaceIn(line);

        string title = Title(line.Text, untouched: line.Text == text);
        if (title.Length == 0) return null;

        bool allDay = allDayWords || time is null;
        DateOnly day;
        bool dated = true;
        if (spanStart is { } spanFirst) day = spanFirst;
        else if (date is { } resolved) day = resolved;
        else if (repeat == RepeatKind.Weekly && days.Count > 0) day = FirstOfDays(days, today, now, allDay ? null : time);
        else { day = today; dated = false; }
        if (midnight && dated) day = day.AddDays(1);

        var start = allDay ? day.ToDateTime(TimeOnly.MinValue) : day.ToDateTime(time!.Value);
        // A bare time that already passed belongs to tomorrow, never to a moment behind us.
        if (!allDay && !dated && days.Count == 0 && start <= now) start = start.AddDays(1);
        var first = DateOnly.FromDateTime(start);

        DateOnly? until = null;
        int spanDays = spanStart is { } a && spanEnd is { } b ? Math.Max(1, b.DayNumber - a.DayNumber + 1) : 1;
        if (lasting is { } stay)
        {
            if (repeat != RepeatKind.None) until = Advance(first, stay.Amount, stay.Unit).AddDays(-1);
            else if (!allDay && stay.Unit.StartsWith('d'))
            {
                // "a las 8 durante 10 días": a daily routine with an end, not a ten-day meeting.
                repeat = RepeatKind.Daily;
                until = first.AddDays(Math.Max(1, stay.Amount) - 1);
            }
            else if (allDay) spanDays = Math.Max(spanDays, Advance(first, stay.Amount, stay.Unit).DayNumber - first.DayNumber);
        }
        if (untilMonth is { } monthNumber)
        {
            int year = monthNumber < first.Month ? first.Year + 1 : first.Year;
            until = new DateOnly(year, monthNumber, DateTime.DaysInMonth(year, monthNumber));
        }
        else if (untilPhrase is not null && ResolveDate(untilPhrase, first, first.ToDateTime(TimeOnly.MinValue)) is { } end) until = end;

        var draft = new AgendaEvent
        {
            Title = title,
            Location = location,
            Color = color,
            AllDay = allDay,
            Start = start,
            Minutes = allDay ? spanDays * 1440 : Math.Clamp(duration ?? 60, 5, 1440) + (spanDays - 1) * 1440,
            Repeat = repeat,
            Interval = Math.Clamp(interval, 1, 99),
            Days = repeat == RepeatKind.Weekly ? (days.Count > 0 ? days : [start.DayOfWeek]) : [],
            Until = repeat == RepeatKind.None ? null : until,
            Count = repeat == RepeatKind.None ? 0 : count,
            Reminders = silent ? [] : [reminder ?? (allDay ? 0 : 10)]
        };
        draft.Normalize();
        return new QuickAddReading(draft, spanStart is not null || date is not null || exact is not null, !allDay,
            repeat != RepeatKind.None, exact is not null, silent || reminder is not null);
    }

    // ------------------------------------------------------------------ the line

    /// <summary>The typed line and its folded twin, cut in step.</summary>
    private sealed class Line
    {
        private readonly char[] original;
        private readonly char[] folded;

        public Line(string text)
        {
            original = text.ToCharArray();
            folded = new char[original.Length];
            for (int i = 0; i < original.Length; i++) folded[i] = Fold(original[i]);
        }

        public string Folded => new(folded);
        public string Text => new(original);

        public Match? Peek(Regex pattern)
        {
            var match = pattern.Match(Folded);
            return match.Success && match.Length > 0 ? match : null;
        }

        public Match? Take(Regex pattern) => TakeWhere(pattern, _ => true);

        /// <summary>Cuts the first match the reader can actually use, and nothing it cannot.</summary>
        public Match? TakeWhere(Regex pattern, Func<Match, bool> accept)
        {
            for (var match = pattern.Match(Folded); match.Success; match = match.NextMatch())
            {
                if (match.Length == 0 || !accept(match)) continue;
                Cut(match.Index, match.Length);
                return match;
            }
            return null;
        }

        public string Slice(int index, int length) => new(original, index, length);

        public void Cut(int index, int length)
        {
            for (int i = index; i < index + length && i < original.Length; i++)
            {
                original[i] = ' ';
                folded[i] = ' ';
            }
        }

        private static char Fold(char character) => char.ToLowerInvariant(character) switch
        {
            'á' or 'à' or 'ä' or 'â' => 'a',
            'é' or 'è' or 'ë' or 'ê' => 'e',
            'í' or 'ì' or 'ï' or 'î' => 'i',
            'ó' or 'ò' or 'ö' or 'ô' => 'o',
            'ú' or 'ù' or 'ü' or 'û' => 'u',
            'ñ' => 'n',
            var other => other
        };
    }

    // ------------------------------------------------------------------ readers

    private static int Int(Match match, string group) =>
        match.Groups[group].Success && int.TryParse(match.Groups[group].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;

    private static int ToNumber(string text)
    {
        var word = Regex.Replace(text.Trim(), @"\s+", " ");
        if (word.Length == 0) return 1;
        if (word == "un par de") return 2;
        if (int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) return value;
        return Numbers.TryGetValue(word, out int named) ? named : 1;
    }

    private static int ReminderMinutes(Match alert)
    {
        string unit = alert.Groups["u"].Value;
        int per = unit.StartsWith('s') ? 10080 : unit.StartsWith('d') ? 1440 : unit.StartsWith('h') ? 60 : 1;
        string amount = alert.Groups["n"].Value;
        return amount == "media" ? per / 2 : ToNumber(amount) * per;
    }

    private static int HoursOf(Match match)
    {
        int whole = ToNumber(match.Groups["h"].Value);
        int tenths = Int(match, "t");
        int minutes = whole * 60 + tenths * 6 + Int(match, "m");
        if (match.Groups["half"].Success) minutes += 30;
        return Math.Max(5, minutes);
    }

    /// <summary>
    /// Reads a clock. Marked says whether the half of the day was stated — am/pm, "de la tarde",
    /// a 24-hour figure — which lets a range borrow the other end's half. Without any of it an
    /// early hour means the afternoon: "a las 4" is 16:00, as the words usually mean in Spanish.
    /// </summary>
    private static (TimeOnly Time, bool Marked)? ReadClock(Match match, string p)
    {
        var hourGroup = match.Groups[p + "h"];
        if (!hourGroup.Success) return null;
        int hour = ToNumber(hourGroup.Value);
        int minute = Int(match, p + "m");
        if (match.Groups[p + "f"].Success)
        {
            string fraction = match.Groups[p + "f"].Value;
            minute = fraction == "media" ? 30 : fraction == "cuarto" ? 15 : ToNumber(fraction);
        }
        if (match.Groups[p + "l"].Success)
        {
            string less = match.Groups[p + "l"].Value;
            hour -= 1;
            minute = 60 - (less == "cuarto" ? 15 : ToNumber(less));
        }
        if (hour is < 0 or > 23 || minute is < 0 or > 59) return null;

        string meridiem = match.Groups[p + "a"].Value.Contains('p') ? "pm" : match.Groups[p + "a"].Success ? "am" : "";
        string part = match.Groups[p + "p"].Value;
        bool leadingZero = hourGroup.Value.Length == 2 && hourGroup.Value[0] == '0';
        bool twentyFour = match.Groups[p + "x"].Success || leadingZero || hour >= 13 || hour == 0;
        bool marked = meridiem.Length > 0 || part.Length > 0 || twentyFour;

        if (meridiem == "pm" && hour < 12) hour += 12;
        else if (meridiem == "am" && hour == 12) hour = 0;
        else if (part is "tarde" or "noche")
        {
            if (hour < 12) hour += 12;
            else if (part == "noche" && hour == 12) hour = 0;
        }
        else if (part == "madrugada" && hour == 12) hour = 0;
        else if (part == "mediodia" && hour is >= 1 and <= 4) hour += 12;
        else if (!marked && hour is >= 1 and <= 6) hour += 12;
        return (new TimeOnly(hour % 24, minute), marked);
    }

    private static bool ValidRange(Match match) => ReadRange(match) is not null;

    private static (TimeOnly From, TimeOnly To)? ReadRange(Match match)
    {
        if (ReadClock(match, "a") is not { } from || ReadClock(match, "b") is not { } to) return null;
        var start = from.Time;
        var end = to.Time;
        // "de 5 a 7 de la tarde": the stated half of one end also belongs to the other.
        if (!from.Marked && to.Marked && end.Hour >= 12 && start.Hour < 12 && start.Hour + 12 <= end.Hour) start = start.AddHours(12);
        if (end <= start && !to.Marked && end.Hour < 12 && end.AddHours(12) > start) end = end.AddHours(12);
        return end == start ? null : (start, end);
    }

    private static TimeOnly? ApplyPeriod(TimeOnly? time, string part)
    {
        if (time is { } set)
            return part is "tarde" or "noche" && set.Hour < 12 ? set.AddHours(12) : set;
        return part switch
        {
            "manana" => new TimeOnly(9, 0),
            "tarde" => new TimeOnly(16, 0),
            "noche" => new TimeOnly(20, 0),
            _ => new TimeOnly(6, 0)
        };
    }

    // ------------------------------------------------------------------ dates

    private static DateOnly? ResolveDate(string phrase, DateOnly today, DateTime now)
    {
        string text = Regex.Replace(phrase.Trim(), @"\s+", " ");
        if (text.StartsWith("el ", StringComparison.Ordinal)) text = text[3..];
        var context = new Context(today, now, null);
        foreach (var form in DateForms)
        {
            var match = form.Find.Match(text);
            if (match.Success && match.Index == 0 && match.Length == text.Length && form.Resolve(match, context) is { } day) return day;
        }
        return null;
    }

    /// <summary>
    /// Both ends of "del … al …". A bare first number takes its month from the second end, so
    /// "del 20 al 25 de octubre" stays in October; the second end never falls before the first.
    /// </summary>
    private static (DateOnly? First, DateOnly? Last) ResolveRange(string startPhrase, string endPhrase, DateOnly today, DateTime now)
    {
        var last = ResolveDate(endPhrase, today, now);
        var bare = Regex.Match(startPhrase.Trim(), @"^(?:el\s+)?(?:dia\s+)?(?<d>" + DayNumber + @")$", RegexOptions.ExplicitCapture);
        DateOnly? first;
        if (bare.Success && last is { } endDay && Regex.IsMatch(endPhrase, Month))
        {
            int number = Int(bare, "d");
            var month = number <= endDay.Day ? new DateOnly(endDay.Year, endDay.Month, 1) : new DateOnly(endDay.Year, endDay.Month, 1).AddMonths(-1);
            first = number <= DateTime.DaysInMonth(month.Year, month.Month) ? new DateOnly(month.Year, month.Month, number) : null;
        }
        else
        {
            first = ResolveDate(startPhrase, today, now);
            if (first is { } from) last = ResolveDate(endPhrase, from, from.ToDateTime(TimeOnly.MinValue));
        }
        if (first is null || last is null || last < first) return (null, null);
        return (first, last);
    }

    private static DateOnly Advance(DateOnly from, int amount, string unit)
    {
        amount = Math.Max(1, amount);
        return unit.StartsWith('s') ? from.AddDays(7 * amount)
            : unit.StartsWith("mes", StringComparison.Ordinal) ? from.AddMonths(amount)
            : unit.StartsWith('a') ? from.AddYears(amount)
            : from.AddDays(amount);
    }

    private static RepeatKind KindOf(string unit) =>
        unit.StartsWith('d') ? RepeatKind.Daily
        : unit.StartsWith('s') ? RepeatKind.Weekly
        : unit.StartsWith("mes", StringComparison.Ordinal) ? RepeatKind.Monthly
        : RepeatKind.Yearly;

    private static int MonthOf(string word)
    {
        string name = word.TrimEnd('.');
        if (name.Length < 3) return 0;
        return name[..3] switch
        {
            "ene" => 1, "feb" => 2, "mar" => 3, "abr" => 4, "may" => 5, "jun" => 6, "jul" => 7,
            "ago" => 8, "sep" or "set" => 9, "oct" => 10, "nov" => 11, "dic" => 12, _ => 0
        };
    }

    private static DayOfWeek DayOf(string word) => word.TrimEnd('s') switch
    {
        "lune" or "lunes" => DayOfWeek.Monday,
        "marte" or "martes" => DayOfWeek.Tuesday,
        "miercole" or "miercoles" => DayOfWeek.Wednesday,
        "jueve" or "jueves" => DayOfWeek.Thursday,
        "vierne" or "viernes" => DayOfWeek.Friday,
        "sabado" => DayOfWeek.Saturday,
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

    /// <summary>
    /// "El viernes 18": the nearest date that is both a Friday and the 18th. When no month in the
    /// next half year pairs them, the weekday was a slip and the number wins — nobody typing
    /// "el sábado 18" means a Saturday twelve months away.
    /// </summary>
    private static DateOnly? WeekdayWithNumber(DayOfWeek target, int day, DateOnly today)
    {
        var month = new DateOnly(today.Year, today.Month, 1);
        for (int i = 0; i < 6; i++)
        {
            var candidateMonth = month.AddMonths(i);
            if (day > DateTime.DaysInMonth(candidateMonth.Year, candidateMonth.Month)) continue;
            var candidate = new DateOnly(candidateMonth.Year, candidateMonth.Month, day);
            if (candidate >= today && candidate.DayOfWeek == target) return candidate;
        }
        return NextDayOfMonth(day, today);
    }

    private static DateOnly? NextDayOfMonth(int day, DateOnly from)
    {
        var month = new DateOnly(from.Year, from.Month, 1);
        for (int i = 0; i < 24; i++)
        {
            var candidateMonth = month.AddMonths(i);
            if (day > DateTime.DaysInMonth(candidateMonth.Year, candidateMonth.Month)) continue;
            var candidate = new DateOnly(candidateMonth.Year, candidateMonth.Month, day);
            if (candidate >= from) return candidate;
        }
        return null;
    }

    private static DateOnly FirstOfDays(List<DayOfWeek> days, DateOnly today, DateTime now, TimeOnly? time)
    {
        for (int offset = 0; offset < 8; offset++)
        {
            var day = today.AddDays(offset);
            if (!days.Contains(day.DayOfWeek)) continue;
            if (offset == 0 && time is { } at && day.ToDateTime(at) <= now) continue;
            return day;
        }
        return today;
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

    private static string? ColorFor(string word) => word switch
    {
        "rojo" or "roja" or "red" => "red",
        "azul" or "blue" => "blue",
        "verde" or "green" => "green",
        "ambar" or "amarillo" or "naranja" or "amber" => "amber",
        "violeta" or "morado" or "lila" or "violet" => "violet",
        "turquesa" or "cian" or "teal" => "teal",
        "negro" or "tinta" or "ink" => "ink",
        _ => null
    };

    private static string PlaceIn(Line line)
    {
        var match = PlaceAfterEn.Match(line.Folded);
        if (!match.Success) return "";
        var group = match.Groups["p"];
        string folded = Regex.Replace(group.Value, @"\s+", " ").Trim();
        string place = Regex.Replace(line.Slice(group.Index, group.Length), @"\s+", " ").Trim().TrimEnd(',', '.', ';');
        if (place.Length is 0 or > 60) return "";
        bool named = char.IsUpper(place[0]);
        bool known = PlaceWords.Any(word => folded == word || folded.StartsWith(word + " ", StringComparison.Ordinal));
        return named || known ? place : "";
    }

    private static string Title(string rest, bool untouched = false)
    {
        var words = Regex.Split(rest.Trim(), @"\s+").Where(word => word.Length > 0 && !Regex.IsMatch(word, @"^[\-–—:,.;·|]+$")).ToList();
        static string Plain(string word) => new Line(word.Trim(',', '.', ';', ':')).Folded;
        var kept = new List<string>(words);
        while (kept.Count > 0 && Fillers.Contains(Plain(kept[0]))) kept.RemoveAt(0);
        while (kept.Count > 0 && Fillers.Contains(Plain(kept[^1]))) kept.RemoveAt(kept.Count - 1);
        // Connectors are only trimmed around something the reader took out. A line it recognised
        // nothing in is a title made of small words — "Del día" — and stays whole.
        if (kept.Count == 0 && untouched) kept = words;
        string text = string.Join(' ', kept).Trim(' ', '-', '–', '—', ':', ',', '.', ';');
        return text.Length == 0 ? "" : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
    }
}
