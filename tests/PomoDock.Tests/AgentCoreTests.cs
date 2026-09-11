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
            assert(AgentIntent.IsStandaloneFocusQuestion("¿Cuántas horas enfoqué el último mes?"), "hours last month is a focus question");
            assert(!AgentIntent.IsStandaloneFocusQuestion("Recuérdame registrar materias y dime las horas"), "a reminder is not a silent focus answer");
            assert(AgentIntent.IsWhatDoYouKnow("qué sabes de mí"), "self query");
            var plan = new AgentPlan { Steps = [new AgentPlanStep { Tool = "add_todo", Risk = AgentRisk.Write }] };
            assert(plan.HasMutations, "a write step requires approval");
            assert(!AgentPlan.Reply("ok", "listo").HasMutations, "a reply has no mutations");
        });
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, true); } catch { }
    }
}
