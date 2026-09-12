using System.Globalization;
using System.Text.RegularExpressions;

namespace PomoDock.Core;

public sealed record AgentNoteAsk(string Title, string Color, string Text, bool HabitsToday);
public sealed record AgentSoundAsk(string Slot, string Query);
public sealed record AgentRemoveAsk(string Target, int? Page);

/// <summary>Deterministic intent for memory vs reminders vs workspace chores, before the model speaks.</summary>
public static class AgentIntent
{
    public static bool IsPlanAffirmation(string text) =>
        Regex.IsMatch((text ?? "").Trim(),
            @"^(s[ií]|ok|okay|dale|va|hazlo|ejecuta|confirmo|s[ií]\s+lo\s+confirmo|s[ií]\s*,?\s*confirmo|yes|yep|go|do\s+it|confirm)[\s!.]*$",
            RegexOptions.IgnoreCase);

    public static bool IsPlanRejection(string text) =>
        Regex.IsMatch((text ?? "").Trim(),
            @"^(no|nop|cancel[ae]r?|mejor\s+no|olvida|stop|never\s?mind)[\s!.]*$",
            RegexOptions.IgnoreCase);

    public static bool IsWhatDoYouKnow(string text) =>
        Regex.IsMatch(text ?? "", @"qu[eé]\s+sabes\s+de\s+m[ií]|what\s+do\s+you\s+know\s+about\s+me", RegexOptions.IgnoreCase);

    public static bool IsIntroOrHelp(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string value = text.Trim();
        if (value.Length > 90) return false;
        if (IsRemindTask(value) || TryRemember(value, out _) || TryForget(value, out _)) return false;
        if (IsDeleteEmptyPages(value) || TryCreateNote(value, out _) || TryListSounds(value)
            || TrySetSound(value, out _) || TryRemoveTimer(value, out _) || TryRemoveWidget(value, out _)
            || TryAddTodo(value, out _) || TryCompleteTodo(value, out _) || TryAddHabit(value, out _)
            || TryMarkHabit(value, out _) || TryAddEvent(value, out _) || TryAddWidget(value, out _)
            || TryTimer(value, out _) || TryGotoPage(value, out _) || TryGotoHome(value) || TryRenamePage(value, out _, out _) || TryAddPage(value)
            || TrySetFocusDuration(value, out _) || TryDeleteTodo(value, out _) || TryArchiveHabit(value, out _)
            || TryCompleteEvent(value, out _) || TryDeleteEvent(value, out _) || TryAddFinance(value, out _)
            || TryMarkFinancePaid(value, out _) || TryAddFocusTask(value, out _))
            return false;
        return Regex.IsMatch(value, """
            ^(hola|hey|buenas|hello|hi|buenos\s+d[ií]as|good\s+(morning|afternoon|evening))([\s!?.¿¡]|$)
            |qui[eé]n\s+eres
            |qu[eé]\s+eres
            |who\s+are\s+you
            |what\s+are\s+you
            |qu[eé]\s+puedes(\s+hacer)?
            |what\s+can\s+you\s+do
            |c[oó]mo\s+(te\s+uso|funcionas|trabajas)
            |pres[eé]ntate
            |tell\s+me\s+about\s+yourself
            |^(ayuda|help)[\s!?¿¡.]*$
            """, RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace);
    }

    public static string IntroReply(string text, bool spanish)
    {
        string[] spanishLines =
        [
            "Soy el agente de PomoDock. Razono con DeepSeek y opero tus datos en este PC: notas, páginas, tareas, hábitos, agenda, finanzas, widgets y temporizador. Dime qué quieres hacer.",
            "Agente PomoDock listo. Puedo crear notas, mover páginas, registrar gastos, hábitos, agenda y el timer. Pídeme algo concreto.",
            "Estoy dentro de PomoDock. Controlo el tablero: notas, páginas, tareas, hábitos, agenda, dinero, widgets y pomodoro. ¿Qué hacemos?",
            "PomoDock a tu lado. Dime la orden —una nota, un gasto, borrar páginas vacías, arrancar el timer— y la ejecuto."
        ];
        string[] englishLines =
        [
            "I'm the PomoDock agent. I reason with DeepSeek and operate your data on this PC: notes, pages, tasks, habits, calendar, finance, widgets, and the timer. What should I do?",
            "PomoDock agent ready. I can create notes, move pages, log expenses, habits, calendar, and the timer. Ask for something concrete.",
            "I live inside PomoDock. I can inspect and change the whole board. Tell me a concrete order.",
            "PomoDock here. Give me an order — a note, an expense, empty pages, the timer — and I'll do it."
        ];
        var lines = spanish ? spanishLines : englishLines;
        int pick = Math.Abs((text ?? "").Length + Environment.TickCount) % lines.Length;
        return lines[pick];
    }

    public static bool IsRemindTask(string text) =>
        Regex.IsMatch(text ?? "", @"recu[eé]rdame|remind\s+me", RegexOptions.IgnoreCase);

    /// <summary>
    /// Calendar lookups that are common enough to answer from the agenda directly. Keeping these
    /// off the LLM makes them instant and, more importantly, guarantees that the answer is made
    /// from real occurrences (including repeating events) rather than generated prose.
    /// </summary>
    public static bool IsUpcomingCalendarQuery(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsRemindTask(text)) return false;
        bool calendar = Regex.IsMatch(text, @"calendario|agenda|calendar|eventos?|citas?|reuniones|appointments?|meetings?", RegexOptions.IgnoreCase);
        bool upcoming = Regex.IsMatch(text, @"pr[oó]xim[oa]s?|pendientes?|siguiente(?:s)?|upcoming|next|pending|qu[eé]\s+(?:tengo|hay)", RegexOptions.IgnoreCase);
        bool question = Regex.IsMatch(text, @"cu[aá]les|qu[eé]|dime|muestra|lista|hay|what|show|list|do\s+i\s+have", RegexOptions.IgnoreCase);
        return calendar && upcoming && question && !LooksLikeWrite(text);
    }

    /// <summary>
    /// Uses the same mature natural-language reader as Calendar's quick-add field. A scheduled
    /// reminder must contain a real date/time/rhythm; an underspecified "remind me" is left to
    /// the conversational planner instead of silently choosing a time.
    /// </summary>
    public static bool TrySchedule(string text, DateTime now, out AgendaEvent scheduled)
    {
        scheduled = new AgendaEvent();
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool reminder = IsRemindTask(text);
        bool calendarCommand = Regex.IsMatch(text, @"\b(agenda|agendar|programa|programar|crea|crear|añade|agrega|add|create|schedule)\b", RegexOptions.IgnoreCase)
            && Regex.IsMatch(text, @"\b(evento|event|cita|reuni[oó]n|appointment|meeting|recordatorio|reminder)\b", RegexOptions.IgnoreCase);
        if (!reminder && !calendarCommand) return false;
        var reading = AgendaQuickAdd.Read(text, now);
        if (reading is null || !reading.Scheduled || string.IsNullOrWhiteSpace(reading.Event.Title)) return false;
        scheduled = reading.Event;
        // "Recuérdame X en tres horas" means alert at that instant, not ten minutes earlier.
        if (reminder && reading.Relative && !reading.ExplicitReminder)
            scheduled.Reminders = [0];
        return true;
    }

    public static bool TryRemember(string text, out string fact)
    {
        fact = "";
        if (string.IsNullOrWhiteSpace(text) || IsRemindTask(text)) return false;
        var match = Regex.Match(text.Trim(), @"^(recuerda(?:\s+que)?|remember(?:\s+that)?)\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        fact = match.Success ? match.Groups[2].Value.Trim().TrimEnd('.') : "";
        return fact.Length > 0;
    }

    public static bool TryForget(string text, out string query)
    {
        query = "";
        if (string.IsNullOrWhiteSpace(text) || IsRemindTask(text)) return false;
        var match = Regex.Match(text.Trim(), @"^(olv[ií]da(?:lo|r)?(?:\s+que)?|forget(?:\s+that| it)?)\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success) return false;
        query = match.Groups[2].Value.Trim().TrimEnd('.');
        return true;
    }

    public static bool NeedsLocalModel(string text)
    {
        if (IsIntroOrHelp(text) || IsWhatDoYouKnow(text) || IsDeleteEmptyPages(text) || IsUpcomingCalendarQuery(text)) return false;
        if (TrySchedule(text, DateTime.Now, out _)) return false;
        if (TryCreateNote(text, out _) || TryListSounds(text) || TrySetSound(text, out _)
            || TryRemoveTimer(text, out _) || TryRemoveWidget(text, out _))
            return false;
        if (TryAddTodo(text, out _) || TryCompleteTodo(text, out _) || TryAddHabit(text, out _)
            || TryMarkHabit(text, out _) || TryAddEvent(text, out _) || TryAddWidget(text, out _)
            || TryTimer(text, out _) || TryGotoPage(text, out _) || TryGotoHome(text) || TryRenamePage(text, out _, out _) || TryAddPage(text)
            || TrySetFocusDuration(text, out _) || TryDeleteTodo(text, out _) || TryArchiveHabit(text, out _)
            || TryCompleteEvent(text, out _) || TryDeleteEvent(text, out _) || TryAddFinance(text, out _)
            || TryMarkFinancePaid(text, out _) || TryAddFocusTask(text, out _))
            return false;
        if (TryRemember(text, out _) || TryForget(text, out _)) return false;
        return !(IsStandaloneFocusQuestion(text) && FocusHistory.TryGuessPeriod(text, out _));
    }

    public static bool IsDeleteEmptyPages(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string value = text.Trim();
        if (!Regex.IsMatch(value, @"p[aá]ginas?|pantallas?|pages?", RegexOptions.IgnoreCase)) return false;
        bool empty = Regex.IsMatch(value, """
            vac[ií]as?
            |en\s+blanco
            |blank
            |empty
            |sin\s+(ning[uú]n\s+)?widgets?
            |sin\s+nada
            |que\s+no\s+tengan?\s+(ning[uú]n\s+)?widgets?
            |without\s+(any\s+)?widgets?
            |with\s+no\s+widgets?
            |don'?t\s+have\s+(any\s+)?widgets?
            |do\s+not\s+have\s+(any\s+)?widgets?
            """, RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace);
        bool del = Regex.IsMatch(value, @"elimina(?:r)?|borrar?|quita(?:r)?|limpia(?:r)?|delete|remove|clear", RegexOptions.IgnoreCase);
        return empty && del;
    }

    public static bool TryCreateNote(string text, out AgentNoteAsk note)
    {
        note = new("NOTAS", "paper", "", false);
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!Regex.IsMatch(text, @"\b(nota|note)\b", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(text, @"\b(borra|elimina|quita|delete|remove)\b", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(text, @"\b(crea|crear|haz|añade|agrega|add|create|es una)\b", RegexOptions.IgnoreCase))
            return false;
        bool asked = Regex.IsMatch(text, """
            (crea|crear|haz|añade|agrega|add|create|es una|una nota|nota de color|nota color|nota amarilla|yellow note)
            """, RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace);
        bool habits = Regex.IsMatch(text, @"h[aá]bitos?\s+(de\s+)?hoy|today'?s\s+habits", RegexOptions.IgnoreCase);
        string color = ColorOf(text);
        if (!asked && color == "paper" && !habits) return false;
        string body = habits ? "" : NoteBody(text);
        string title = NoteTitle(body, habits, LooksEnglish(text));
        note = new(title, color, body, habits);
        return true;
    }

    public static string ColorLabel(string color, bool spanish) => (color, spanish) switch
    {
        ("yellow", true) => "amarilla",
        ("yellow", false) => "yellow",
        ("mint", true) => "menta",
        ("mint", false) => "mint",
        ("blue", true) => "azul",
        ("blue", false) => "blue",
        ("rose", true) => "rosa",
        ("rose", false) => "rose",
        ("lavender", true) or ("lilac", true) => "lila",
        ("lavender", false) or ("lilac", false) => "lilac",
        (_, true) => "blanca",
        _ => "plain"
    };

    public static string NoteBody(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var quoted = Regex.Match(text, @"[""«“](.+?)[""»”]", RegexOptions.Singleline);
        if (quoted.Success)
        {
            string inside = CleanNotePayload(quoted.Groups[1].Value);
            if (LooksLikePayload(inside)) return inside;
        }

        var marked = Regex.Match(text, """
            (?:\bescribe\b(?:\s+lo\s+siguiente)?
            |\bwrite\b(?:\s+(?:the\s+)?following)?
            |\bpon(?:le)?\b(?:\s+(?:esto|el\s+texto))?
            |que\s+diga
            |con\s+el\s+texto
            |que\s+ponga)
            \s*[:.\-]?\s*(.+)$
            """, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.IgnorePatternWhitespace);
        if (marked.Success)
        {
            string body = CleanNotePayload(marked.Groups[1].Value);
            if (LooksLikePayload(body)) return body;
        }

        var colon = Regex.Match(text, @"\b(?:nota|note)\b[^\n:]{0,60}:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (colon.Success)
        {
            string body = CleanNotePayload(colon.Groups[1].Value);
            if (LooksLikePayload(body)) return body;
        }

        var split = Regex.Match(text, @"\b(?:nota|note)\b[^.!?\n]{0,90}[.!?]\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (split.Success)
        {
            string body = CleanNotePayload(split.Groups[1].Value);
            if (LooksLikePayload(body)) return body;
        }
        return "";
    }

    public static string NoteTitle(string body, bool habits, bool english)
    {
        if (habits) return english ? "Today's habits" : "Hábitos de hoy";
        string line = (body ?? "").Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        line = line.TrimEnd('.', '!', '?');
        if (line.Length < 3) return "NOTAS";
        if (line.Length <= 42) return line;
        int cut = line.LastIndexOf(' ', 42);
        return (cut > 12 ? line[..cut] : line[..42]).TrimEnd(',', ';', ':') + "…";
    }

    private static string CleanNotePayload(string value)
    {
        string body = (value ?? "").Trim().TrimStart('.', ':', '-', '–', '—', ' ').Trim();
        body = Regex.Replace(body, @"^lo\s+siguiente\s*[:.\-]?\s*", "", RegexOptions.IgnoreCase);
        return body.Trim().TrimEnd();
    }

    private static bool LooksLikePayload(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length < 2) return false;
        return !Regex.IsMatch(body, """
            ^(con\s+una\s+nota|y\s+una\s+nota|amarilla|yellow|p[aá]gina\s+nueva
            |los?\s+h[aá]bitos|today'?s\s+habits)
            """, RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace);
    }

    public static bool TryListSounds(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return Regex.IsMatch(text, """
            (sonidos?|sounds?)
            .{0,48}
            (disponibles?|enfoque|focus|ambiente|ambient|alarma|alarm|lista|list)
            |
            (muestra|ens[eé]ña|lista|cu[aá]les|show|list)
            .{0,24}
            (sonidos?|sounds?)
            """, RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace);
    }

    public static bool TrySetSound(string text, out AgentSoundAsk ask)
    {
        ask = new("ambient", "");
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!Regex.IsMatch(text, @"\b(usa|pon|cambia|usar|use|set|play|ponle)\b", RegexOptions.IgnoreCase)) return false;
        if (!Regex.IsMatch(text, @"sonido|sound|ruido|lluvia|rain|olas|waves|chimenea|fire|ambiente|enfoque", RegexOptions.IgnoreCase))
            return false;
        string slot = "ambient";
        if (Regex.IsMatch(text, @"alarma|fin\s+de\s+(foco|enfoque)|focus\s+end", RegexOptions.IgnoreCase)) slot = "focus_end";
        else if (Regex.IsMatch(text, @"descanso|break", RegexOptions.IgnoreCase)) slot = "break_end";
        else if (Regex.IsMatch(text, @"recordatorio|reminder", RegexOptions.IgnoreCase)) slot = "reminder";
        else if (Regex.IsMatch(text, @"clic|click|bot[oó]n", RegexOptions.IgnoreCase)) slot = "click";
        var match = Regex.Match(text.Trim(),
            @"(?:usa|pon|cambia|usar|use|set|play|ponle)\s+(?:el\s+)?(?:sonido\s+(?:de\s+)?)?(.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        string query = match.Success ? match.Groups[1].Value : text;
        query = Regex.Replace(query, @"\b(para\s+(el|la)\s+)?(enfoque|focus|ambiente|ambient|alarma|alarm)\b", "", RegexOptions.IgnoreCase);
        query = Regex.Replace(query, @"\bpara\s+(el|la)\b", "", RegexOptions.IgnoreCase).Trim(' ', '.', ',', ':');
        if (query.Length < 2) return false;
        ask = new(slot, query);
        return true;
    }

    public static bool TryRemoveTimer(string text, out AgentRemoveAsk ask)
    {
        ask = new("timer", null);
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!Regex.IsMatch(text, @"(borra|quita|elimina|delete|remove).{0,36}(timer|temporizador)", RegexOptions.IgnoreCase))
            return false;
        ask = new("timer", PageNumber(text));
        return true;
    }

    public static bool TryRemoveWidget(string text, out AgentRemoveAsk ask)
    {
        ask = new("", null);
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (IsDeleteEmptyPages(text)) return false;
        if (TryRemoveTimer(text, out _)) return false;
        if (!Regex.IsMatch(text, @"(borra|quita|elimina|delete|remove).{0,36}(widget|nota|note|tareas|todo|h[aá]bitos|agenda|calendar|finanzas|stats)", RegexOptions.IgnoreCase))
            return false;
        var match = Regex.Match(text, @"(widget|nota|note|tareas|todo|h[aá]bitos|agenda|calendar|finanzas|stats)", RegexOptions.IgnoreCase);
        ask = new(match.Success ? match.Value : "widget", PageNumber(text));
        return match.Success;
    }

    public static bool IsStandaloneFocusQuestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (IsRemindTask(text) || TryRemember(text, out _) || TryForget(text, out _)) return false;
        if (!FocusHistory.TryGuessPeriod(text, out _)) return false;
        string lower = text.ToLowerInvariant();
        string[] writes = ["añade", "agrega", "crea", "borra", "elimina", "completa", "marca", "add ", "create ", "delete ", "complete ", "mark "];
        return !writes.Any(verb => lower.Contains(verb));
    }

    public static bool WantsNewPage(string text) =>
        Regex.IsMatch(text ?? "", @"p[aá]gina\s+nueva|nueva\s+p[aá]gina|otra\s+p[aá]gina|add(?:\s+a)?\s+page|new\s+page", RegexOptions.IgnoreCase);

    public static bool TryAddPage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (TryCreateNote(text, out _) || TryAddWidget(text, out _)) return false;
        return WantsNewPage(text) && Regex.IsMatch(text, @"\b(crea|crear|abre|abrir|añade|agrega|haz|add|create|open)\b", RegexOptions.IgnoreCase);
    }

    public static bool TryAddTodo(string text, out string task)
    {
        task = "";
        if (string.IsNullOrWhiteSpace(text) || TryCreateNote(text, out _) || IsDeleteEmptyPages(text)) return false;
        if (!Regex.IsMatch(text, @"\b(tarea|tareas|todo|task)\b", RegexOptions.IgnoreCase)) return false;
        if (!Regex.IsMatch(text, @"\b(crea|crear|añade|agrega|haz|apunta|add|create)\b", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(text, @"\b(completa|termina|borra|elimina|quita)\b", RegexOptions.IgnoreCase)) return false;
        task = text.Trim();
        return true;
    }

    public static bool TryCompleteTodo(string text, out string title)
    {
        title = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = Regex.Match(text.Trim(),
            @"(?:completa|completar|marca(?:\s+como)?(?:\s+hecha)?|termina|done|complete)\s+(?:la\s+)?(?:tarea|todo|task)\s+(.+)$",
            RegexOptions.IgnoreCase);
        title = match.Success ? match.Groups[1].Value.Trim().TrimEnd('.') : "";
        return title.Length > 0;
    }

    public static bool TryAddHabit(string text, out string name)
    {
        name = "";
        if (string.IsNullOrWhiteSpace(text) || TryCreateNote(text, out _)) return false;
        var match = Regex.Match(text.Trim(),
            @"(?:crea|crear|añade|agrega|haz|add|create)\s+(?:un\s+)?h[aá]bito\s+(?:de\s+|llamado\s+|que\s+se\s+llame\s+)?(.+)$",
            RegexOptions.IgnoreCase);
        if (!match.Success)
            match = Regex.Match(text.Trim(), @"(?:crea|add|create)\s+(?:a\s+)?habit\s+(?:called\s+)?(.+)$", RegexOptions.IgnoreCase);
        name = match.Success ? match.Groups[1].Value.Trim().TrimEnd('.') : "";
        return name.Length > 0 && !Regex.IsMatch(name, @"^hoy\b", RegexOptions.IgnoreCase);
    }

    public static bool TryMarkHabit(string text, out string name)
    {
        name = "";
        if (string.IsNullOrWhiteSpace(text) || TryCreateNote(text, out _)) return false;
        var match = Regex.Match(text.Trim(),
            @"(?:marca|marqu[eé]|completa|tacha|check)\s+(?:el\s+)?h[aá]bito\s+(.+)$",
            RegexOptions.IgnoreCase);
        name = match.Success ? match.Groups[1].Value.Trim().TrimEnd('.') : "";
        return name.Length > 0;
    }

    public static bool TryAddEvent(string text, out (string Title, string Start) evt)
    {
        evt = ("", "");
        if (string.IsNullOrWhiteSpace(text) || TryCreateNote(text, out _) || TryAddTodo(text, out _)) return false;
        if (!Regex.IsMatch(text, @"\b(evento|event|cita|reuni[oó]n)\b", RegexOptions.IgnoreCase)) return false;
        if (!Regex.IsMatch(text, @"\b(crea|crear|añade|agrega|agenda|add|create|schedule)\b", RegexOptions.IgnoreCase)) return false;
        var title = Regex.Match(text.Trim(),
            @"(?:evento|event|cita|reuni[oó]n)\s+(?:de\s+|llamad[oa]\s+|para\s+)?(.+)$",
            RegexOptions.IgnoreCase);
        string name = title.Success ? title.Groups[1].Value.Trim().TrimEnd('.') : text.Trim();
        if (name.Length == 0) return false;
        DateTime when = DateTime.Now.AddHours(1);
        when = new DateTime(when.Year, when.Month, when.Day, when.Hour, 0, 0);
        if (Regex.IsMatch(text, @"ma[ñn]ana|tomorrow", RegexOptions.IgnoreCase))
            when = DateTime.Today.AddDays(1).AddHours(9);
        var hour = Regex.Match(text, @"\b([01]?\d|2[0-3])(?::([0-5]\d))?\s*(h|hrs|am|pm)?\b", RegexOptions.IgnoreCase);
        if (hour.Success && int.TryParse(hour.Groups[1].Value, out int h))
        {
            int m = hour.Groups[2].Success ? int.Parse(hour.Groups[2].Value) : 0;
            if (hour.Groups[3].Value.Equals("pm", StringComparison.OrdinalIgnoreCase) && h < 12) h += 12;
            when = new DateTime(when.Year, when.Month, when.Day, Math.Clamp(h, 0, 23), m, 0);
        }
        evt = (name, when.ToString("yyyy-MM-dd HH:mm"));
        return true;
    }

    public static bool TryAddWidget(string text, out string kind)
    {
        kind = "";
        if (string.IsNullOrWhiteSpace(text) || TryCreateNote(text, out _)) return false;
        if (!Regex.IsMatch(text, @"\b(crea|crear|añade|agrega|abre|haz|add|create|open)\b", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(text, @"\b(nota|note)\b", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(text, @"tareas?|todo", RegexOptions.IgnoreCase) && Regex.IsMatch(text, @"widget|tablero|lista", RegexOptions.IgnoreCase))
            kind = "todo";
        else if (Regex.IsMatch(text, @"h[aá]bitos?|habits?", RegexOptions.IgnoreCase) && Regex.IsMatch(text, @"widget|tablero", RegexOptions.IgnoreCase))
            kind = "habits";
        else if (Regex.IsMatch(text, @"\b(agenda|calendario|calendar)\b", RegexOptions.IgnoreCase))
            kind = "calendar";
        else if (Regex.IsMatch(text, @"\b(finanzas|finance)\b", RegexOptions.IgnoreCase))
            kind = "finance";
        else if (Regex.IsMatch(text, @"\b(stats|estad[ií]sticas|enfoque)\b", RegexOptions.IgnoreCase) && Regex.IsMatch(text, @"widget|tablero", RegexOptions.IgnoreCase))
            kind = "stats";
        else if (Regex.IsMatch(text, @"\b(temporizador|timer)\b", RegexOptions.IgnoreCase) && Regex.IsMatch(text, @"widget|a[ñn]ade|crea", RegexOptions.IgnoreCase))
            kind = "timer";
        return kind.Length > 0;
    }

    public static bool TryTimer(string text, out string command)
    {
        command = "";
        if (string.IsNullOrWhiteSpace(text) || IsDeleteEmptyPages(text)) return false;
        if (TryRemoveTimer(text, out _)) return false;
        if (!Regex.IsMatch(text, @"timer|temporizador|pomodoro|enfoque|foco|sesi[oó]n", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(text, @"\b(pausa|pause|det[eé]n|para)\b", RegexOptions.IgnoreCase)) command = "pause";
        else if (Regex.IsMatch(text, @"\b(reinicia|reset|reiniciar)\b", RegexOptions.IgnoreCase)) command = "reset";
        else if (Regex.IsMatch(text, @"\b(salta|skip|siguiente fase)\b", RegexOptions.IgnoreCase)) command = "skip";
        else if (Regex.IsMatch(text, @"\b(inicia|iniciar|arranca|empieza|start|pon)\b", RegexOptions.IgnoreCase)) command = "start";
        return command.Length > 0;
    }

    public static bool TryGotoPage(string text, out int page)
    {
        page = 0;
        if (string.IsNullOrWhiteSpace(text) || WantsNewPage(text) || IsDeleteEmptyPages(text)) return false;
        if (!Regex.IsMatch(text, @"\b(ve|ir|vete|abre|mu[eé]strame|goto|go to|open|vuelve|regresa|back)\b", RegexOptions.IgnoreCase)) return false;
        var match = Regex.Match(text, @"(?:p[aá]gina|page|pagina)\s*(\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out page) && page > 0;
    }

    /// <summary>Go to home / casa / principal (resolved later by workspace.goto with home:true).</summary>
    public static bool TryGotoHome(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || WantsNewPage(text) || IsDeleteEmptyPages(text)) return false;
        if (!Regex.IsMatch(text, @"\b(ve|ir|vete|abre|mu[eé]strame|goto|go to|open|vuelve|regresa|back)\b", RegexOptions.IgnoreCase))
            return false;
        return Regex.IsMatch(text,
            @"\b(casa|home|principal|inicio|main)\b|p[aá]gina\s+principal",
            RegexOptions.IgnoreCase);
    }

    /// <summary>Rename a workspace page (not a widget). pageNumber may be 0 = current.</summary>
    public static bool TryRenamePage(string text, out int pageNumber, out string name)
    {
        pageNumber = 0;
        name = "";
        if (string.IsNullOrWhiteSpace(text) || IsDeleteEmptyPages(text)) return false;
        // Avoid widget rename: "renombra el widget..."
        if (Regex.IsMatch(text, @"\bwidget\b", RegexOptions.IgnoreCase)) return false;
        if (!Regex.IsMatch(text, @"\b(renombra|renombrar|llama|llamar|nombra|nombrar|name|rename|pon(?:le)?\s+nombre)\b", RegexOptions.IgnoreCase))
            return false;
        if (!Regex.IsMatch(text, @"\bp[aá]gina\b|\bpage\b|\bcasa\b|\bprincipal\b|\bhome\b", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(text, @"\b(esta|esta\s+p[aá]gina|esta\s+pantalla)\b", RegexOptions.IgnoreCase))
            return false;

        var numbered = Regex.Match(text,
            @"(?:renombra|renombrar|llama|llamar|nombra|nombrar|name|rename)\s+(?:la\s+)?(?:p[aá]gina|page)\s*(\d+)\s+(?:a|como|as|to)?\s*[«""']?(.+?)[»""']?\s*$",
            RegexOptions.IgnoreCase);
        if (numbered.Success)
        {
            int.TryParse(numbered.Groups[1].Value, out pageNumber);
            name = CleanPageName(numbered.Groups[2].Value);
            return pageNumber > 0 && name.Length > 0;
        }

        var current = Regex.Match(text,
            @"(?:renombra|renombrar|llama|llamar|nombra|nombrar|name|rename)\s+(?:esta\s+)?(?:p[aá]gina|page|pantalla)?\s*(?:a|como|as|to)?\s*[«""']?(.+?)[»""']?\s*$",
            RegexOptions.IgnoreCase);
        if (current.Success)
        {
            name = CleanPageName(current.Groups[1].Value);
            // Reject if we accidentally captured "la página 3 a casa" without number parse above
            if (Regex.IsMatch(name, @"^(?:la\s+)?(?:p[aá]gina|page)\s*\d+", RegexOptions.IgnoreCase)) return false;
            return name.Length > 0;
        }
        return false;
    }

    private static string CleanPageName(string raw)
    {
        string name = raw.Trim().TrimEnd('.', ',', ';');
        name = Regex.Replace(name, @"^(?:a|como|as|to)\s+", "", RegexOptions.IgnoreCase).Trim();
        name = Regex.Replace(name, @"^(?:casa|home|principal)\s*/\s*", "", RegexOptions.IgnoreCase).Trim();
        // "casa/principal" or "casa / principal" → prefer first token as display name, keep "casa"
        if (name.Contains('/') || name.Contains('\\'))
        {
            var parts = name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0) name = parts[0];
        }
        return name.Trim('«', '»', '"', '\'', ' ');
    }

    public static bool TrySetFocusDuration(string text, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!Regex.IsMatch(text, @"enfoque|foco|focus|pomodoro|sesi[oó]n", RegexOptions.IgnoreCase)) return false;
        if (!Regex.IsMatch(text, @"\b(pon|deja|cambia|ajusta|set|use)\b", RegexOptions.IgnoreCase)) return false;
        var match = Regex.Match(text, @"(\d{1,3})\s*(?:min(?:utos?)?|m)\b", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out minutes) && minutes is >= 1 and <= 180;
    }

    public static bool LooksLikeWrite(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (IsRemindTask(text)) return true;
        if (IsDeleteEmptyPages(text) || TryCreateNote(text, out _) || TryAddPage(text) || WantsNewPage(text)) return true;
        if (TryAddTodo(text, out _) || TryCompleteTodo(text, out _) || TryAddHabit(text, out _) || TryMarkHabit(text, out _)) return true;
        if (TryAddEvent(text, out _) || TryAddWidget(text, out _) || TryTimer(text, out _) || TrySetSound(text, out _)
            || TryRemoveTimer(text, out _) || TryRemoveWidget(text, out _) || TrySetFocusDuration(text, out _)
            || TryRemember(text, out _) || TryForget(text, out _) || TryGotoPage(text, out _) || TryGotoHome(text)
            || TryRenamePage(text, out _, out _)
            || TryDeleteTodo(text, out _) || TryArchiveHabit(text, out _) || TryCompleteEvent(text, out _)
            || TryDeleteEvent(text, out _) || TryAddFinance(text, out _) || TryMarkFinancePaid(text, out _)
            || TryAddFocusTask(text, out _))
            return true;
        return Regex.IsMatch(text, @"\b(crea|crear|añade|agrega|borra|elimina|quita|marca|completa|inicia|abre|registra|apunta|archiva|pagu[eé])\b", RegexOptions.IgnoreCase)
            && Regex.IsMatch(text, @"nota|p[aá]gina|tarea|h[aá]bito|widget|timer|temporizador|evento|agenda|sonido|gasto|ingreso|suscrip", RegexOptions.IgnoreCase);
    }

    public static bool TryDeleteTodo(string text, out string title)
    {
        title = "";
        if (string.IsNullOrWhiteSpace(text) || IsDeleteEmptyPages(text)) return false;
        var match = Regex.Match(text.Trim(),
            @"(?:borra|elimina|quita|delete|remove)\s+(?:la\s+)?(?:tarea|todo|task)\s+(.+)$",
            RegexOptions.IgnoreCase);
        title = match.Success ? match.Groups[1].Value.Trim().TrimEnd('.') : "";
        return title.Length > 0;
    }

    public static bool TryArchiveHabit(string text, out string name)
    {
        name = "";
        if (string.IsNullOrWhiteSpace(text) || TryCreateNote(text, out _) || TryMarkHabit(text, out _)) return false;
        var match = Regex.Match(text.Trim(),
            @"(?:archiva|archive|borra|elimina|quita)\s+(?:el\s+)?h[aá]bito\s+(.+)$",
            RegexOptions.IgnoreCase);
        if (!match.Success)
            match = Regex.Match(text.Trim(), @"(?:archive|delete|remove)\s+(?:the\s+)?habit\s+(.+)$", RegexOptions.IgnoreCase);
        name = match.Success ? match.Groups[1].Value.Trim().TrimEnd('.') : "";
        return name.Length > 0;
    }

    public static bool TryCompleteEvent(string text, out string title)
    {
        title = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = Regex.Match(text.Trim(),
            @"(?:completa|completar|marca(?:\s+como)?(?:\s+hecha)?|done|complete)\s+(?:el\s+)?(?:evento|event|cita|reuni[oó]n)\s+(.+)$",
            RegexOptions.IgnoreCase);
        title = match.Success ? match.Groups[1].Value.Trim().TrimEnd('.') : "";
        return title.Length > 0;
    }

    public static bool TryDeleteEvent(string text, out string title)
    {
        title = "";
        if (string.IsNullOrWhiteSpace(text) || IsDeleteEmptyPages(text) || TryDeleteTodo(text, out _)) return false;
        var match = Regex.Match(text.Trim(),
            @"(?:borra|elimina|quita|delete|remove|cancela)\s+(?:el\s+|la\s+)?(?:evento|event|cita|reuni[oó]n)\s+(.+)$",
            RegexOptions.IgnoreCase);
        title = match.Success ? match.Groups[1].Value.Trim().TrimEnd('.') : "";
        return title.Length > 0;
    }

    public static bool TryAddFinance(string text, out string line)
    {
        line = "";
        if (string.IsNullOrWhiteSpace(text) || TryAddTodo(text, out _) || TryCreateNote(text, out _)) return false;
        if (!Regex.IsMatch(text, @"gasto|ingreso|sueldo|salario|suscrip|subscription|\bfijo\b|renta|finance|finanzas|[+-]\s*\d|usd|mxn|concepto", RegexOptions.IgnoreCase))
            return false;
        if (!Regex.IsMatch(text, @"\d")) return false;
        // Always rebuild a compact quick-add line — never pass the raw utterance (it becomes the title).
        object? parsed = FinanceQuickAdd.Parse(text.Trim(), DateOnly.FromDateTime(DateTime.Today));
        if (parsed is MoneyEntry entry)
        {
            string sign = entry.Flow == MoneyFlow.Income ? "+" : "-";
            line = $"{sign}{entry.Amount.ToString(CultureInfo.InvariantCulture)} {entry.Title}";
            return true;
        }
        if (parsed is RecurringMoney recurring)
        {
            string kind = recurring.Subscription ? "sub" : "fijo";
            line = $"{kind} {recurring.Title} {recurring.Amount.ToString(CultureInfo.InvariantCulture)}";
            return FinanceQuickAdd.Parse(line, DateOnly.FromDateTime(DateTime.Today)) is not null;
        }
        var amount = Regex.Match(text, @"(\d+(?:[.,]\d{1,2})?)");
        if (!amount.Success) return false;
        bool income = Regex.IsMatch(text, @"ingreso|sueldo|salario|\+", RegexOptions.IgnoreCase);
        bool sub = Regex.IsMatch(text, @"suscrip|subscription|\bsub\b", RegexOptions.IgnoreCase);
        bool fixedCost = Regex.IsMatch(text, @"\bfijo\b|renta|alquiler", RegexOptions.IgnoreCase);
        string title = text;
        var concept = Regex.Match(text, @"\bconcepto\s*[:=]?\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (concept.Success) title = concept.Groups[1].Value;
        title = Regex.Replace(title, @"(registra|apunta|añade|agrega|crea|add|un|una|de|en|el|la|a|al|del|gasto|gastos|ingreso|ingresos|suscripci[oó]n|subscription|fijo|renta|usd|mxn|eur|d[oó]lares|dollars|por\s+favor|concepto|concept)", " ", RegexOptions.IgnoreCase);
        title = Regex.Replace(title, amount.Value, " ");
        title = Regex.Replace(title, @"\s+", " ").Trim(' ', '.', ',', ':');
        if (title.Length == 0) title = income ? "Ingreso" : "Gasto";
        if (sub || fixedCost)
            line = $"{(sub ? "sub" : "fijo")} {title} {amount.Value.Replace(',', '.')}";
        else
            line = $"{(income ? "+" : "-")}{amount.Value.Replace(',', '.')} {title}";
        return FinanceQuickAdd.Parse(line, DateOnly.FromDateTime(DateTime.Today)) is not null;
    }

    public static bool TryMarkFinancePaid(string text, out string title)
    {
        title = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = Regex.Match(text.Trim(),
            @"(?:pagu[eé]|paga(?:do)?|mar[ck]a(?:\s+como)?\s+pagad[oa]|paid)\s+(?:la\s+)?(?:suscripci[oó]n\s+|gasto\s+fijo\s+)?(.+)$",
            RegexOptions.IgnoreCase);
        title = match.Success ? match.Groups[1].Value.Trim().TrimEnd('.') : "";
        return title.Length > 1 && !Regex.IsMatch(title, @"^(el|la|un|una)$", RegexOptions.IgnoreCase);
    }

    public static bool TryAddFocusTask(string text, out string name)
    {
        name = "";
        if (string.IsNullOrWhiteSpace(text) || TryAddTodo(text, out _) || TryCreateNote(text, out _)) return false;
        var match = Regex.Match(text.Trim(),
            @"(?:añade|agrega|crea|add|create)\s+(?:una?\s+)?(?:tarea\s+de\s+enfoque|focus\s+task|pomodoro)\s+(?:para\s+|de\s+)?(.+)$",
            RegexOptions.IgnoreCase);
        name = match.Success ? match.Groups[1].Value.Trim().TrimEnd('.') : "";
        return name.Length > 0;
    }

    public static string ColorOf(string text)
    {
        if (Regex.IsMatch(text, @"amarill|yellow", RegexOptions.IgnoreCase)) return "yellow";
        if (Regex.IsMatch(text, @"menta|mint|verde", RegexOptions.IgnoreCase)) return "mint";
        if (Regex.IsMatch(text, @"azul|blue", RegexOptions.IgnoreCase)) return "blue";
        if (Regex.IsMatch(text, @"rosa|rose|pink", RegexOptions.IgnoreCase)) return "rose";
        if (Regex.IsMatch(text, @"lila|lavanda|lavender", RegexOptions.IgnoreCase)) return "lavender";
        return "paper";
    }

    private static int? PageNumber(string text)
    {
        var match = Regex.Match(text, @"(?:p[aá]gina|page|pagina)\s*(\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out int page) ? page : null;
    }

    private static bool LooksEnglish(string text) => Regex.IsMatch(text, @"\b(the|note|with|today|habits|yellow)\b", RegexOptions.IgnoreCase);
}
