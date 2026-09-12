using System.Globalization;
using System.Text.RegularExpressions;

namespace PomoDock.Core;

/// <summary>Chat can keep digits and labels; the voice gets a sentence a person would say.</summary>
public static class AgentSpeech
{
    public static string Speakable(string? text, bool spanish)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string value = text.Trim();
        int cut = IndexOf(value, "ACCIONES REALIZADAS");
        if (cut < 0) cut = IndexOf(value, "COMPLETED ACTIONS");
        if (cut >= 0) value = value[..cut].Trim();

        value = Regex.Replace(value, @"```[\s\S]*?```", " ");
        value = Regex.Replace(value, @"\{[^{}]*\}", " ");
        value = Regex.Replace(value, @"https?://\S+", spanish ? " un enlace " : " a link ", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"(?i)Cifra calculada desde las sesiones guardadas\.?", " ");
        value = Regex.Replace(value, @"(?i)Figure calculated from stored sessions\.?", " ");
        value = Regex.Replace(value, @"\b(?:workspace|focus|todo|calendar|habits|notes|timer|memory|finance|settings|layouts|notifications|sounds|get_state|add_todo|add_note|add_event|add_habit|add_widget|add_focus_task|app)(?:[._][a-z0-9]+)*\b", " ", RegexOptions.IgnoreCase);
        value = value.Replace("✓", " ").Replace("✔", " ").Replace("→", " ").Replace("↑", " ").Replace("↓", " ").Replace("←", " ").Replace("→", " ")
            .Replace("•", ", ").Replace("·", " ").Replace("—", ", ").Replace("–", ", ").Replace("…", ". ")
            .Replace("«", " ").Replace("»", " ").Replace("“", " ").Replace("”", " ").Replace("\"", " ");

        value = Regex.Replace(value, @"(\d+(?:[.,]\d+)?)\s*min(?:utos?)?\s*\(\s*(\d+(?:[.,]\d+)?)\s*h(?:oras?)?\s*\)",
            match => Duration(Parse(match.Groups[1].Value), spanish), RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"(\d+(?:[.,]\d+)?)\s*h(?:oras?)?\b",
            match => Duration(Parse(match.Groups[1].Value) * 60, spanish), RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"(\d+(?:[.,]\d+)?)\s*min(?:utos?)?\b",
            match => Duration(Parse(match.Groups[1].Value), spanish), RegexOptions.IgnoreCase);

        value = Regex.Replace(value, @"\b(\d{4})-(\d{2})-(\d{2})[ T](\d{1,2}):(\d{2})(?::\d{2})?\b",
            match => DateAndTime(
                int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture),
                spanish));
        value = Regex.Replace(value, @"\b(\d{4})-(\d{2})-(\d{2})\b",
            match => DatePhrase(
                int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                spanish));
        value = Regex.Replace(value, @"(\d{1,2})/(\d{1,2})/(\d{4})",
            match => DatePhrase(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture), spanish));

        value = Regex.Replace(value, @"\$\s*(\d+(?:[.,]\d+)?)\s*(USD|MXN|EUR)?\b",
            match => Money(Parse(match.Groups[1].Value), match.Groups[2].Value, spanish), RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\b(\d+(?:[.,]\d+)?)\s*(USD|MXN|EUR|dólares?|dolares?|pesos?)\b",
            match => Money(Parse(match.Groups[1].Value), match.Groups[2].Value, spanish), RegexOptions.IgnoreCase);

        value = Regex.Replace(value, @"\(\s*#\s*\d+\s*\)", " ");
        value = Regex.Replace(value, @"#\s*(\d+)",
            match => PageCardinal(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), spanish), RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bP[ÁA]GINA\s*0*(\d+)\b",
            match => PageCardinal(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), spanish), RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bPAGE\s*0*(\d+)\b",
            match => PageCardinal(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), spanish), RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bp[áa]gina\s+0*(\d+)\b",
            match => PageCardinal(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), spanish), RegexOptions.IgnoreCase);

        value = Regex.Replace(value, @"\b([01]?\d|2[0-3]):([0-5]\d)(?::[0-5]\d)?\b",
            match => TimePhrase(
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                spanish));

        value = Regex.Replace(value, @"sesión\(es\)", spanish ? "sesiones" : "sessions", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"día\(s\)", spanish ? "días" : "days", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"página\(s\)", spanish ? "páginas" : "pages", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"activo\(s\)", spanish ? "activos" : "active", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"session\(s\)", "sessions", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"day\(s\)", "days", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"[#*_`|\\<>\[\]{}@^=~]", " ");
        value = Regex.Replace(value, @"(?i)\balmohadilla\b", " ");
        value = Regex.Replace(value, @"(?i)\bhash\b", " ");
        value = Regex.Replace(value, @"\(\s*\)", " ");
        value = Regex.Replace(value, @"\s*,\s*,+", ", ");
        value = Regex.Replace(value, @"\s{2,}", " ").Trim(' ', ',', ';', ':');
        if (value.Length == 0) return "";
        if (value.Length <= 420) return value;
        int stop = value.LastIndexOfAny(['.', '!', '?', '…'], Math.Min(420, value.Length - 1));
        return (stop >= 80 ? value[..(stop + 1)] : value[..Math.Min(420, value.Length)]).Trim();
    }

    public static string Duration(double minutes, bool spanish)
    {
        if (minutes < 0) minutes = 0;
        int total = (int)Math.Round(minutes, MidpointRounding.AwayFromZero);
        int hours = total / 60;
        int mins = total % 60;
        if (mins >= 53) { hours++; mins = 0; }
        else if (mins >= 38) mins = 45;
        else if (mins >= 23) mins = 30;
        else if (mins >= 8) mins = 15;
        else mins = 0;
        if (hours == 0)
        {
            if (mins == 0) return spanish ? "menos de un cuarto de hora" : "less than fifteen minutes";
            return spanish ? Cardinal(mins, true, false) + " minutos" : Cardinal(mins, false, false) + " minutes";
        }
        string head = HoursPhrase(hours, spanish);
        if (mins == 0) return head;
        if (mins == 30) return spanish ? head + " y media" : head + " and a half";
        if (mins == 15) return spanish ? head + " y cuarto" : head + " and a quarter";
        if (mins == 45) return spanish ? head + " y tres cuartos" : head + " and three quarters";
        return spanish
            ? head + " y " + Cardinal(mins, true, false) + " minutos"
            : head + " and " + Cardinal(mins, false, false) + " minutes";
    }

    public static string CountPhrase(int count, bool spanish, string singularEs, string pluralEs, string singularEn, string pluralEn)
    {
        if (count == 1) return spanish ? "una " + singularEs : "one " + singularEn;
        return spanish
            ? Cardinal(count, true, false) + " " + pluralEs
            : Cardinal(count, false, false) + " " + pluralEn;
    }

    private static string PageCardinal(int number, bool spanish)
    {
        if (number <= 0) return spanish ? "la primera página" : "the first page";
        return (spanish ? "página " : "page ") + Cardinal(number, spanish, feminine: true);
    }

    private static string HoursPhrase(int hours, bool spanish)
    {
        if (hours == 1) return spanish ? "una hora" : "one hour";
        return Cardinal(hours, spanish, feminine: true) + (spanish ? " horas" : " hours");
    }

    private static string DateAndTime(int day, int month, int year, int hour, int minute, bool spanish) =>
        DatePhrase(day, month, year, spanish) + (spanish ? ", " : ", ") + TimePhrase(hour, minute, spanish);

    private static string DatePhrase(int day, int month, int year, bool spanish)
    {
        string[] monthsEs = ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre"];
        string[] monthsEn = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
        month = Math.Clamp(month, 1, 12);
        day = Math.Clamp(day, 1, 31);
        string monthName = spanish ? monthsEs[month - 1] : monthsEn[month - 1];
        string dayWord = Cardinal(day, spanish, feminine: false);
        return spanish ? $"el {dayWord} de {monthName}" : $"{monthName} {dayWord}";
    }

    private static string TimePhrase(int hour, int minute, bool spanish)
    {
        hour = ((hour % 24) + 24) % 24;
        minute = Math.Clamp(minute, 0, 59);
        if (!spanish)
        {
            int h12 = hour % 12;
            if (h12 == 0) h12 = 12;
            string ampm = hour < 12 ? "AM" : "PM";
            if (minute == 0) return $"{English(h12)} {ampm}";
            if (minute == 15) return $"{English(h12)} fifteen {ampm}";
            if (minute == 30) return $"{English(h12)} thirty {ampm}";
            if (minute == 45) return $"{English(h12)} forty-five {ampm}";
            return $"{English(h12)} {English(minute)} {ampm}";
        }

        int display = hour % 12;
        if (display == 0) display = 12;
        string clock = display == 1 ? "la una" : "las " + Cardinal(display, true, feminine: true);
        string period = hour switch
        {
            >= 0 and < 5 => "de la madrugada",
            >= 5 and < 12 => "de la mañana",
            12 when minute == 0 => "del mediodía",
            >= 12 and < 14 => "del mediodía",
            >= 14 and < 20 => "de la tarde",
            _ => "de la noche"
        };
        if (minute == 0) return $"{clock} {period}";
        if (minute == 15) return $"{clock} y cuarto {period}";
        if (minute == 30) return $"{clock} y media {period}";
        if (minute == 45)
        {
            int next = display == 12 ? 1 : display + 1;
            string nextClock = next == 1 ? "la una" : "las " + Cardinal(next, true, feminine: true);
            string nextPeriod = ((hour + 1) % 24) switch
            {
                >= 0 and < 5 => "de la madrugada",
                >= 5 and < 12 => "de la mañana",
                >= 12 and < 14 => "del mediodía",
                >= 14 and < 20 => "de la tarde",
                _ => "de la noche"
            };
            return $"{nextClock} menos cuarto {nextPeriod}";
        }
        return $"{clock} {Cardinal(minute, true, false)} {period}";
    }

    private static string Money(double amount, string unit, bool spanish)
    {
        int whole = (int)Math.Round(amount, MidpointRounding.AwayFromZero);
        string currency = unit.Trim().ToLowerInvariant() switch
        {
            "mxn" or "peso" or "pesos" => spanish ? (whole == 1 ? "peso" : "pesos") : (whole == 1 ? "peso" : "pesos"),
            "eur" => spanish ? (whole == 1 ? "euro" : "euros") : (whole == 1 ? "euro" : "euros"),
            _ => spanish ? (whole == 1 ? "dólar" : "dólares") : (whole == 1 ? "dollar" : "dollars")
        };
        return spanish
            ? Cardinal(whole, true, false) + " " + currency
            : Cardinal(whole, false, false) + " " + currency;
    }

    private static double Parse(string value) =>
        double.TryParse(value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out double number) ? number : 0;

    private static int IndexOf(string text, string token) =>
        text.IndexOf(token, StringComparison.OrdinalIgnoreCase);

    internal static string Cardinal(int value, bool spanish, bool feminine)
    {
        if (value < 0) value = Math.Abs(value);
        if (!spanish) return English(value);
        if (value == 0) return "cero";
        if (value == 1) return feminine ? "una" : "un";
        if (value < 30) return Teen(value, feminine);
        if (value < 100)
        {
            int ten = (value / 10) * 10;
            int unit = value % 10;
            return unit == 0 ? Tens(ten) : Tens(ten) + " y " + Cardinal(unit, true, feminine);
        }
        if (value == 100) return "cien";
        if (value < 200) return "ciento " + Cardinal(value - 100, true, feminine);
        if (value < 1000)
        {
            int hundreds = value / 100;
            int rest = value % 100;
            string head = (hundreds, feminine) switch
            {
                (2, false) => "doscientos",
                (2, true) => "doscientas",
                (3, false) => "trescientos",
                (3, true) => "trescientas",
                (4, false) => "cuatrocientos",
                (4, true) => "cuatrocientas",
                (5, false) => "quinientos",
                (5, true) => "quinientas",
                (6, false) => "seiscientos",
                (6, true) => "seiscientas",
                (7, false) => "setecientos",
                (7, true) => "setecientas",
                (8, false) => "ochocientos",
                (8, true) => "ochocientas",
                (9, false) => "novecientos",
                (9, true) => "novecientas",
                _ => Cardinal(hundreds, true, false) + (feminine ? "cientas" : "cientos")
            };
            return rest == 0 ? head : head + " " + Cardinal(rest, true, feminine);
        }
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string Teen(int value, bool feminine) => value switch
    {
        2 => "dos", 3 => "tres", 4 => "cuatro", 5 => "cinco", 6 => "seis", 7 => "siete",
        8 => "ocho", 9 => "nueve", 10 => "diez", 11 => "once", 12 => "doce", 13 => "trece",
        14 => "catorce", 15 => "quince", 16 => "dieciséis", 17 => "diecisiete", 18 => "dieciocho",
        19 => "diecinueve", 20 => "veinte", 21 => feminine ? "veintiuna" : "veintiún",
        22 => "veintidós", 23 => "veintitrés", 24 => "veinticuatro", 25 => "veinticinco",
        26 => "veintiséis", 27 => "veintisiete", 28 => "veintiocho", 29 => "veintinueve",
        _ => value.ToString(CultureInfo.InvariantCulture)
    };

    private static string Tens(int value) => value switch
    {
        30 => "treinta", 40 => "cuarenta", 50 => "cincuenta", 60 => "sesenta",
        70 => "setenta", 80 => "ochenta", 90 => "noventa",
        _ => value.ToString(CultureInfo.InvariantCulture)
    };

    private static string English(int value)
    {
        if (value == 0) return "zero";
        if (value < 20) return value switch
        {
            1 => "one", 2 => "two", 3 => "three", 4 => "four", 5 => "five",
            6 => "six", 7 => "seven", 8 => "eight", 9 => "nine", 10 => "ten",
            11 => "eleven", 12 => "twelve", 13 => "thirteen", 14 => "fourteen", 15 => "fifteen",
            16 => "sixteen", 17 => "seventeen", 18 => "eighteen", 19 => "nineteen",
            _ => value.ToString(CultureInfo.InvariantCulture)
        };
        if (value < 100)
        {
            string ten = (value / 10) switch
            {
                2 => "twenty", 3 => "thirty", 4 => "forty", 5 => "fifty",
                6 => "sixty", 7 => "seventy", 8 => "eighty", 9 => "ninety",
                _ => ""
            };
            int unit = value % 10;
            return unit == 0 ? ten : ten + "-" + English(unit);
        }
        if (value < 1000)
        {
            int hundreds = value / 100;
            int rest = value % 100;
            string head = English(hundreds) + " hundred";
            return rest == 0 ? head : head + " " + English(rest);
        }
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
