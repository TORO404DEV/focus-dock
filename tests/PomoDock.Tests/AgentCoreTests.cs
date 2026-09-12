using PomoDock.Core;
using System.Text.Json;

internal static class AgentCoreTests
{
    public static void Run(Action<string, Action> test, Action<double, double> equal, Action<bool, string> assert)
    {
        test("a complete Qwen GGUF is reused and Whisper files are ignored", () =>
        {
            var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PomoDock-llm-" + Guid.NewGuid())).FullName;
            try
            {
                var models = Path.Combine(root, "models");
                Directory.CreateDirectory(models);
                File.WriteAllBytes(Path.Combine(models, "ggml-small-q5_1.bin"), new byte[190_085_487]);
                string gguf = Path.Combine(models, LocalLlmFile.FileName);
                using (var stream = new FileStream(gguf, FileMode.Create, FileAccess.Write, FileShare.None))
                    stream.SetLength(LocalLlmFile.ExpectedBytes);
                var status = LocalLlmFile.Inspect(root);
                assert(status.Ready && status.Path == gguf, "the exact Qwen GGUF is ready");
                assert(!LocalLlmFile.IsUsableGguf(Path.Combine(models, "ggml-small-q5_1.bin"), out _), "Whisper is not the LLM");
            }
            finally { TryDelete(root); }
        });

        test("GGUF magic distinguishes a real weights prefix from HTML or gzip junk", () =>
        {
            var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PomoDock-llm-" + Guid.NewGuid())).FullName;
            try
            {
                string partial = LocalLlmFile.PartialPath(root);
                Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
                File.WriteAllBytes(partial, "<!DOCTYPE html>"u8.ToArray());
                assert(!LocalLlmFile.HasGgufMagic(partial), "an HTML error page is not GGUF");
                File.WriteAllBytes(partial, [(byte)'G', (byte)'G', (byte)'U', (byte)'F', 0, 1, 2, 3]);
                assert(LocalLlmFile.HasGgufMagic(partial), "a GGUF prefix is accepted");
            }
            finally { TryDelete(root); }
        });

        test("reopening the store restores the current chat", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "PomoDock-chat-current-" + Guid.NewGuid());
            Directory.CreateDirectory(directory);
            try
            {
                using (var store = new Store(directory))
                {
                    var conversations = new AgentConversationStore(store);
                    var first = conversations.StartNew();
                    first.Add("you", "primer hilo");
                    conversations.Save();
                    var second = conversations.StartNew();
                    second.Add("you", "hilo actual");
                    conversations.Touch(second);
                }
                using var reopened = new Store(directory);
                var again = new AgentConversationStore(reopened);
                assert(again.LatestOrNew().Messages[0].Text == "hilo actual", "current chat survives reopen");
            }
            finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); TryDelete(directory); }
        });

        test("an incomplete official GGUF is demoted back to .partial instead of loaded", () =>
        {
            var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PomoDock-llm-" + Guid.NewGuid())).FullName;
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "models"));
                string ready = LocalLlmFile.DefaultPath(root);
                using (var stream = new FileStream(ready, FileMode.Create, FileAccess.Write, FileShare.None))
                    stream.SetLength(2_049_927_588);
                var status = LocalLlmFile.Inspect(root);
                assert(status.Partial && !status.Ready, "a 2 GB stub is not the finished model");
                assert(File.Exists(LocalLlmFile.PartialPath(root)) && !File.Exists(ready), "the stub goes back to .partial");
                assert(status.Bytes == 2_049_927_588, "resume keeps every downloaded byte");
            }
            finally { TryDelete(root); }
        });

        test("a finished .partial GGUF is promoted and an incomplete one can resume", () =>
        {
            var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PomoDock-llm-" + Guid.NewGuid())).FullName;
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "models"));
                string partial = LocalLlmFile.PartialPath(root);
                using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                    stream.SetLength(LocalLlmFile.ExpectedBytes);
                var promoted = LocalLlmFile.Inspect(root);
                assert(promoted.Ready && File.Exists(LocalLlmFile.DefaultPath(root)), "complete partial becomes the model");
                File.Delete(LocalLlmFile.DefaultPath(root));
                using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                    stream.SetLength(800_000_000);
                var resume = LocalLlmFile.Inspect(root);
                assert(resume.Partial && !resume.Ready && resume.Bytes == 800_000_000, "incomplete partial is resumed, not discarded");
            }
            finally { TryDelete(root); }
        });

        test("agent JSON turns and loop guard reject a repeated mutation", () =>
        {
            assert(AgentJson.TryReadTurn("{\"action\":\"focus.summarize\",\"arguments\":{\"period\":\"last_month\"},\"message\":\"\"}", out var turn, out _), "valid JSON is accepted");
            assert(turn.Action == "focus.summarize", "action is read");
            var args = JsonDocument.Parse("{\"title\":\"x\"}").RootElement;
            var guard = new AgentLoopGuard();
            assert(!guard.IsRepeat("add_todo", args, AgentRisk.Write, out _), "first call is new");
            guard.Remember("add_todo", args, AgentRisk.Write, "{\"success\":true}", true, true);
            assert(guard.IsRepeat("add_todo", args, AgentRisk.Write, out var prior) && prior.Contains("success"), "the same write is blocked");
            assert(guard.TooManyRepeats() == false, "one duplicate is a warning");
            guard.IsRepeat("add_todo", args, AgentRisk.Write, out _);
            assert(guard.TooManyRepeats(), "two duplicates stop the loop");
        });

        test("personal memory remembers, lists, forgets and exports only visible facts", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "PomoDock-mem-" + Guid.NewGuid());
            Directory.CreateDirectory(directory);
            try
            {
                using var store = new Store(directory);
                var memory = new AgentMemoryStore(store);
                memory.Book.Remember("Me llamo Cristopher", "identity");
                memory.Book.Propose("Prefiere sesiones de 50 minutos", "preference");
                memory.Save();
                using var reopened = new Store(directory);
                var again = new AgentMemoryStore(reopened);
                assert(again.Book.Search("Cristopher").Count == 1, "confirmed memory survives reopen");
                assert(again.Book.Forget("50 minutos"), "forget matches the preference");
                again.Save();
                var exported = again.ExportJson();
                assert(exported.Contains("Cristopher") && !exported.Contains("50 minutos"), "export hides forgotten facts");
                assert(again.Book.ContextBlock().Contains("Cristopher"), "context only includes confirmed memory");
            }
            finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); TryDelete(directory); }
        });

        test("conversations keep the latest twenty chats", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "PomoDock-chat-" + Guid.NewGuid());
            Directory.CreateDirectory(directory);
            try
            {
                using var store = new Store(directory);
                var conversations = new AgentConversationStore(store);
                for (int i = 0; i < 25; i++)
                {
                    var chat = conversations.StartNew();
                    chat.Add("you", "mensaje " + i);
                    conversations.Save();
                }
                equal(AgentConversationStore.Keep, conversations.Log.Chats.Count);
                using var reopened = new Store(directory);
                var again = new AgentConversationStore(reopened);
                equal(20, again.Log.Chats.Count);
                assert(again.LatestOrNew().Messages.Count > 0, "the latest chat is restored");
            }
            finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); TryDelete(directory); }
        });

        test("think blocks are stripped before JSON is read", () =>
        {
            assert(AgentJson.TryReadTurn("<think>hidden</think>{\"action\":\"finish\",\"arguments\":{},\"message\":\"2 h\"}", out var turn, out _), "JSON after think is accepted");
            assert(turn.Action == "finish" && turn.Message == "2 h", "message survives think stripping");
            assert(!AgentThink.Strip("<think>raw chain of thought {not json}").Contains("chain"), "unclosed think is dropped");
        });

        test("remember is memory, remind-me is a task, and focus questions stay standalone", () =>
        {
            assert(AgentIntent.TryRemember("Recuerda que prefiero sesiones de 50 minutos", out var fact) && fact.Contains("50"), "remember that is memory");
            assert(!AgentIntent.TryRemember("Recuérdame registrar materias antes del lunes", out _), "remind me is not memory");
            assert(AgentIntent.IsRemindTask("remind me to file taxes"), "english remind me");
            assert(AgentIntent.TrySchedule("recuerdame sacar la basura en un minuto", new DateTime(2026, 9, 11, 21, 44, 0), out var soon)
                && soon.Title.Contains("basura", StringComparison.OrdinalIgnoreCase)
                && soon.Start >= new DateTime(2026, 9, 11, 21, 44, 0)
                && soon.Start <= new DateTime(2026, 9, 11, 21, 46, 0), "remind in one minute lands ~now+1m");
            assert(AgendaQuickAdd.Read("Dentista mañana a las 17:30 durante 45m", new DateTime(2026, 9, 11, 12, 0, 0)) is { Scheduled: true } dentist
                && dentist.Event.Title.Contains("Dentista", StringComparison.OrdinalIgnoreCase), "agenda-style line schedules");
            assert(AgentIntent.IsUpcomingCalendarQuery("¿Cuáles son mis próximos pendientes en el calendario?"), "the reported calendar question is deterministic");
            assert(!AgentIntent.NeedsLocalModel("¿Cuáles son mis próximos pendientes en el calendario?"), "upcoming calendar lookup skips native inference");
            var reminderClock = new DateTime(2026, 9, 11, 18, 53, 0);
            assert(AgentIntent.TrySchedule("Recuérdame sacar la basura en tres horas", reminderClock, out var reminder), "the reported reminder is parsed directly");
            assert(reminder.Title.Equals("sacar la basura", StringComparison.OrdinalIgnoreCase), "reminder keeps the requested task");
            assert(reminder.Start == new DateTime(2026, 9, 11, 21, 53, 0), "relative reminder uses the user's current clock");
            assert(reminder.Reminders.SequenceEqual([0]), "relative remind-me rings at the requested instant");
            assert(!AgentIntent.NeedsLocalModel("Recuérdame sacar la basura en tres horas"), "relative reminders skip native inference");
            assert(AgentIntent.IsStandaloneFocusQuestion("¿Cuántas horas enfoqué el último mes?"), "hours last month is a focus question");
            assert(!AgentIntent.IsStandaloneFocusQuestion("Recuérdame registrar materias y dime las horas"), "a reminder is not a silent focus answer");
            assert(AgentIntent.IsWhatDoYouKnow("qué sabes de mí"), "self query");
            assert(AgentIntent.IsIntroOrHelp("hola quien eres y que puedes hacer"), "greeting plus who are you");
            assert(AgentIntent.IsIntroOrHelp("who are you"), "english identity");
            assert(!AgentIntent.IsIntroOrHelp("añade una tarea para mañana"), "a write is not an intro");
            assert(!AgentIntent.NeedsLocalModel("hola quien eres y que puedes hacer"), "who-are-you does not load the GPU model");
            assert(AgentIntent.IntroReply("quien eres??", true).Contains("PomoDock", StringComparison.OrdinalIgnoreCase), "intro names PomoDock");
            assert(!AgentIntent.NeedsLocalModel("añade una tarea para mañana"), "creating a task skips the LLM");
            assert(AgentThink.LiveText("{\"action\":\"finish\",\"arguments\":{},\"message\":\"Hola, soy PomoDock") == "Hola, soy PomoDock", "partial JSON message streams");
            assert(AgentThink.LiveText("{\"action\":\"get_state\"") == "", "tool calls do not fake a chat message");
            assert(AgentThink.LiveText("{\"action\":\"get_state\",\"message\":\"Estado actual") == "", "tool prose stays off the live bubble");
            assert(AgentIntent.IsDeleteEmptyPages("elimina todas las pantallas vacias porfavor"), "empty screens are empty pages");
            assert(AgentIntent.IsDeleteEmptyPages("elimina todas las pantallas sin widgets"), "pages without widgets");
            assert(AgentIntent.IsDeleteEmptyPages("Puedes borrar todas las páginas que no tengan widgets."), "the exact polite Spanish request");
            assert(AgentIntent.IsDeleteEmptyPages("podrías eliminar las páginas en blanco"), "blank pages");
            assert(AgentIntent.IsDeleteEmptyPages("delete all pages without widgets"), "english without widgets");
            assert(AgentIntent.IsDeleteEmptyPages("clear the blank pages"), "english blank pages");
            assert(!AgentIntent.IsDeleteEmptyPages("elimina la tarea vacía"), "a task is not a page");
            assert(!AgentIntent.NeedsLocalModel("borra las páginas vacías"), "empty-page cleanup skips the LLM");
            assert(!AgentIntent.NeedsLocalModel("Puedes borrar todas las páginas que no tengan widgets."), "the polite empty-page request skips the LLM");
            assert(AgentCatalog.Exists("workspace.delete_empty_pages") && AgentCatalog.Canonical("delete_widget") == "workspace.remove_widget", "delete_widget maps to remove_widget");
            assert(AgentCatalog.Exists("workspace.rename_widget") && AgentCatalog.Exists("layouts.save") && AgentCatalog.Exists("app.fullscreen"), "workspace chrome tools exist");
            assert(AgentCatalog.Exists("todo.clear_done") && AgentCatalog.Exists("finance.set_currency") && AgentCatalog.Exists("habits.set_target"), "coverage tools exist");
            assert(AgentIntent.TryCreateNote("es una nota color amarillo con los hábitos de hoy", out var noteAsk) && noteAsk.Color == "yellow" && noteAsk.HabitsToday, "yellow note with today's habits");
            assert(!AgentIntent.NeedsLocalModel("es una nota color amarillo con los hábitos de hoy"), "note chores skip the LLM");
            assert(AgentIntent.TryListSounds("muestrame los sonidos disponibles para el enfoque"), "list focus sounds");
            assert(AgentIntent.TrySetSound("usa lluvia para el enfoque", out var soundAsk) && soundAsk.Slot == "ambient" && soundAsk.Query.Contains("lluvia", StringComparison.OrdinalIgnoreCase), "set rain");
            assert(AgentIntent.TryRemoveTimer("borra el timer de la pagina 4", out var timerAsk) && timerAsk.Page == 4, "remove timer on page 4");
            assert(AgentCatalog.Exists("sounds.list") && AgentCatalog.Exists("workspace.remove_timer") && AgentCatalog.Exists("calendar.upcoming"), "new workspace and calendar tools exist");
            assert(AgentCatalog.Describe("add_note", "{\"title\":\"Pendientes\",\"color\":\"yellow\",\"text\":\"sacar la basura\"}").Contains("Pendientes"), "plans name the note");
            assert(AgentCatalog.Describe("workspace.delete_empty_pages", "{}").Contains("páginas"), "empty-page plan is explicit");
            assert(AgentIntent.TryCreateNote("Ahora crea una nota de color amarillo", out var yellowNow) && yellowNow.Color == "yellow", "ahora does not hide a yellow note");
            assert(AgentIntent.WantsNewPage("crea una página nueva y una nota") && AgentIntent.TryCreateNote("crea una página nueva y una nota", out _), "page plus note is one chore");
            assert(AgentIntent.TryCreateNote("Crea una página nueva con una nota amarilla y escribe lo siguiente. Mañana tengo que ir a barrer la calle.", out var sweep)
                && sweep.Color == "yellow"
                && sweep.Text.Contains("barrer la calle", StringComparison.OrdinalIgnoreCase)
                && !sweep.Text.Contains("escribe", StringComparison.OrdinalIgnoreCase)
                && sweep.Title.Contains("Mañana", StringComparison.OrdinalIgnoreCase),
                "the note keeps the dictated sentence");
            assert(AgentIntent.TryCreateNote("crea una nota que diga sacar la basura", out var say) && say.Text.Contains("sacar la basura"), "que diga is the body");
            assert(AgentIntent.ColorLabel("yellow", true) == "amarilla", "yellow is spoken as amarilla");
            var spoken = AgentReply.After(
                "Listo. Te dejé una nota amarilla en una página nueva con: Mañana tengo que ir a barrer la calle.",
                [
                    new AgentActionReceipt { Tool = "workspace.add_page", Summary = "Página 5 creada" },
                    new AgentActionReceipt { Tool = "add_note", Summary = "Nota amarilla: Mañana tengo que ir a barrer la calle." }
                ],
                spanish: true);
            assert(spoken.Contains("barrer la calle", StringComparison.OrdinalIgnoreCase), "the chat names the written sentence");
            assert(!spoken.Contains("Página 5", StringComparison.OrdinalIgnoreCase) && !spoken.Contains("yellow"), "receipt dumps stay out of the reply");
            var afterConfirm = AgentReply.After(
                "Listo, toro. Dejé preparado el borrado de todas las páginas sin widgets; solo falta que lo confirmes para ejecutarlo.",
                [new AgentActionReceipt { Tool = "workspace.delete_empty_pages", Summary = "Se borraron 2 páginas vacías. Te dejé en una página con contenido." }],
                spanish: true);
            assert(afterConfirm.Contains("borraron", StringComparison.OrdinalIgnoreCase), "executed plan speaks the receipt");
            assert(!afterConfirm.Contains("confirm", StringComparison.OrdinalIgnoreCase) && !afterConfirm.Contains("preparado", StringComparison.OrdinalIgnoreCase), "pending-confirm copy never survives execution");
            var pendingVoice = AgentReply.Pending(new AgentPlan
            {
                Understood = "Eliminar todos los gastos",
                Message = "Listo, eliminé todos tus gastos.",
                Steps =
                [
                    new AgentPlanStep { Tool = "finance.delete", Label = "Eliminar gasto ChatGPT", Risk = AgentRisk.Destructive },
                    new AgentPlanStep { Tool = "finance.delete", Label = "Eliminar gasto DeepSeek", Risk = AgentRisk.Destructive }
                ]
            }, spanish: true);
            assert(pendingVoice.Contains("todavía no", StringComparison.OrdinalIgnoreCase) || pendingVoice.Contains("confirmar", StringComparison.OrdinalIgnoreCase), "pending plan asks for confirmation");
            assert(!AgentReply.LooksHonestlyDone(pendingVoice), "pending voice must not sound finished");
            assert(AgentReply.Visible([
                new AgentActionReceipt { Tool = "workspace.add_page", Summary = "Página 5 creada" },
                new AgentActionReceipt { Tool = "add_note", Summary = "Nota amarilla: Mañana tengo que ir a barrer la calle." }
            ]).Count == 1, "creating a page is scaffolding next to the note");
            assert(AgentIntent.TryAddTodo("añade una tarea comprar pan", out _), "add task");
            assert(AgentIntent.TryCompleteTodo("completa la tarea comprar pan", out var doneTitle) && doneTitle.Contains("comprar", StringComparison.OrdinalIgnoreCase), "complete task");
            assert(AgentIntent.TryTimer("inicia el temporizador", out var timerCmd) && timerCmd == "start", "start timer");
            assert(AgentIntent.TryGotoPage("ve a la página 3", out var gotoPage) && gotoPage == 3, "go to page 3");
            assert(!AgentIntent.NeedsLocalModel("Ahora crea una nota de color amarillo"), "a yellow note skips the LLM");
            assert(AgentIntent.TryDeleteTodo("borra la tarea comprar pan", out var delTodo) && delTodo.Contains("comprar", StringComparison.OrdinalIgnoreCase), "delete task");
            assert(AgentIntent.TryArchiveHabit("archiva el hábito correr", out var archHabit) && archHabit.Contains("correr", StringComparison.OrdinalIgnoreCase), "archive habit");
            assert(AgentIntent.TryCompleteEvent("completa el evento dentist", out var doneEvt) && doneEvt.Contains("dentist", StringComparison.OrdinalIgnoreCase), "complete event");
            assert(AgentIntent.TryAddFinance("registra un gasto de 20 en chatgpt", out var fin) && fin.Contains("20", StringComparison.Ordinal) && fin.Contains("chatgpt", StringComparison.OrdinalIgnoreCase) && !fin.Contains("registra", StringComparison.OrdinalIgnoreCase), "finance expense");
            assert(AgentIntent.TryAddFinance("agrega 10 usd a gastos, concepto deepseek api", out var fin2) && fin2.Contains("deepseek", StringComparison.OrdinalIgnoreCase) && !fin2.Contains("agrega", StringComparison.OrdinalIgnoreCase), "finance concept extract");
            assert(AgentIntent.IsPlanAffirmation("si lo confirmo"), "spoken plan yes");
            assert(AgentIntent.TryMarkFinancePaid("pagué cursor", out var paid) && paid.Contains("cursor", StringComparison.OrdinalIgnoreCase), "mark subscription paid");
            assert(!AgentIntent.NeedsLocalModel("borra la tarea comprar pan"), "deleting a task skips the LLM");
            assert(AgentGrammar.JsonTurn.Contains("add_note", StringComparison.Ordinal) && AgentGrammar.JsonTurn.Contains("finish", StringComparison.Ordinal), "grammar lists real tools");
            assert(!AgentGrammar.JsonTurn.Contains("todo.create", StringComparison.Ordinal) && AgentGrammar.JsonTurn.Contains("add_todo", StringComparison.Ordinal), "the model sees one canonical name per tool");
            var plan = new AgentPlan { Steps = [new AgentPlanStep { Tool = "add_todo", Risk = AgentRisk.Write }] };
            assert(plan.HasMutations && plan.CanAutoRun, "a write runs without asking");
            var wipe = new AgentPlan { Steps = [new AgentPlanStep { Tool = "workspace.delete_empty_pages", Risk = AgentRisk.Destructive }] };
            assert(wipe.HasMutations && !wipe.CanAutoRun, "destructive work still needs a decision");
            assert(!AgentPlan.Reply("ok", "listo").HasMutations, "a reply has no mutations");
        });

        test("spoken replies strip symbols and say durations in natural language", () =>
        {
            string hours = AgentSpeech.Speakable(
                "Enfoque de esta semana (07/09/2026 – 11/09/2026): 871.4 min (14.52 h) en 12 sesión(es) y 5 día(s) activo(s). Cifra calculada desde las sesiones guardadas.",
                spanish: true);
            assert(hours.Contains("catorce", StringComparison.OrdinalIgnoreCase) && hours.Contains("hora", StringComparison.OrdinalIgnoreCase), "871 minutes becomes about fourteen hours");
            assert(!hours.Contains("871") && !hours.Contains("14.52") && !hours.Contains("min"), "raw minute dumps stay off speech");
            assert(!hours.Contains("07/09") && !hours.Contains("ACCIONES"), "dates and action headers are not narrated");
            string page = AgentSpeech.Speakable("✓ Estás en PÁGINA 04", spanish: true);
            assert(page.Contains("página", StringComparison.OrdinalIgnoreCase) && page.Contains("cuatro", StringComparison.OrdinalIgnoreCase), "page 04 is spoken as página cuatro");
            assert(!page.Contains('✓') && !page.Contains("04") && !page.Contains('▍'), "symbols and zero-padded codes are not spoken");
            string actions = AgentSpeech.Speakable("Listo.\nACCIONES REALIZADAS\n✓ workspace.goto", spanish: true);
            assert(!actions.Contains("ACCIONES", StringComparison.OrdinalIgnoreCase) && !actions.Contains("workspace", StringComparison.OrdinalIgnoreCase) && !actions.Contains('✓'), "receipts are not dumped into speech");
            string intro = AgentSpeech.Speakable(L.T("agent.intro"), spanish: true);
            assert(intro.Contains("PomoDock") && intro.Contains("equipo"), "the intro stays a speakable sentence");
            string english = AgentSpeech.Speakable("Focus this week: 90 min (1.5 h) across 2 session(s).", spanish: false);
            assert(english.Contains("hour") && english.Contains("half") && !english.Contains("1.5"), "English speech rounds to a half hour");
            string hash = AgentSpeech.Speakable("Voy a borrar página 8, página 11 y PÁGINA 13 (#13).", spanish: true);
            assert(hash.Contains("ocho", StringComparison.OrdinalIgnoreCase) && hash.Contains("once", StringComparison.OrdinalIgnoreCase) && hash.Contains("trece", StringComparison.OrdinalIgnoreCase), "page numbers become words");
            assert(!hash.Contains('#') && !hash.Contains("almohadilla", StringComparison.OrdinalIgnoreCase) && !hash.Contains("13"), "hash marks are never spoken");
            string zero = AgentSpeech.Speakable("Estás en la página 0", spanish: true);
            assert(zero.Contains("primera", StringComparison.OrdinalIgnoreCase) && !zero.Contains("cero"), "page 0 is the first page");
            string clock = AgentSpeech.Speakable("Te recuerdo sacar la basura a las 17:30.", spanish: true);
            assert(clock.Contains("media", StringComparison.OrdinalIgnoreCase) && clock.Contains("tarde", StringComparison.OrdinalIgnoreCase), "17:30 becomes five thirty in the afternoon");
            assert(!clock.Contains("17:30") && !clock.Contains(':'), "raw clock digits are not spoken");
            string iso = AgentSpeech.Speakable("Evento el 2026-09-11 a las 09:15.", spanish: true);
            assert(iso.Contains("septiembre", StringComparison.OrdinalIgnoreCase) && iso.Contains("cuarto", StringComparison.OrdinalIgnoreCase), "ISO date and time become spoken phrases");
            assert(!iso.Contains("2026-09-11") && !iso.Contains("09:15"), "ISO stamps stay out of speech");
            string cash = AgentSpeech.Speakable("Registré -10 USD en gastos.", spanish: true);
            assert(cash.Contains("dólar", StringComparison.OrdinalIgnoreCase) && !cash.Contains("USD", StringComparison.OrdinalIgnoreCase), "currency codes become words");
        });
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, true); } catch { }
    }
}
