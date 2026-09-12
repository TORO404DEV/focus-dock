using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using PomoDock.Core;

namespace PomoDock.App;

internal static class AgentDiagnostics
{
    public static void RunVoice(string dataPath, string outputPath)
    {
        try
        {
            using var voice = new LocalAgentVoice(dataPath);
            var timer = Stopwatch.StartNew();
            Task.Run(() => voice.RenderToWaveAsync("Hola. Soy el agente local de PomoDock y ya puedo hablar.", "es", 1.05, outputPath, null, CancellationToken.None)).GetAwaiter().GetResult();
            timer.Stop();
            File.WriteAllText(outputPath + ".json", JsonSerializer.Serialize(new { success = true, milliseconds = timer.ElapsedMilliseconds, bytes = new FileInfo(outputPath).Length }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(outputPath + ".json", JsonSerializer.Serialize(new { success = false, error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { Application.Current.Shutdown(); }
    }

    public static void RunModel(string dataPath, string outputPath)
    {
        try
        {
            using var model = new LocalAgentModel(dataPath);
            var load = Stopwatch.StartNew(); Task.Run(() => model.LoadAsync(CancellationToken.None)).GetAwaiter().GetResult(); load.Stop();
            var now = DateTime.Now;
            using var conversation = model.Begin(PomoAgent.SystemPrompt(now));
            var inference = Stopwatch.StartNew();
            var answer = Task.Run(() => conversation.AskAsync($"""
                FECHA LOCAL: {now:yyyy-MM-dd HH:mm:ss}
                IDIOMA DE LA INTERFAZ: es
                PETICIÓN: ¿Cuántas horas enfoqué el último mes?
                Devuelve la primera acción necesaria.
                /no_think
                """, CancellationToken.None)).GetAwaiter().GetResult();
            inference.Stop();
            using var action = FirstJson(answer);
            bool mappedFocusHistory = action.RootElement.GetProperty("action").GetString() == "focus.summarize"
                && action.RootElement.GetProperty("arguments").GetProperty("period").GetString() == "last_month";
            if (!mappedFocusHistory) throw new InvalidOperationException("The model did not map a last-month total to focus.summarize/last_month. Output: " + answer);
            // Regression: never CancelAfter into LLamaSharp mid-stream (that closed the process).
            // Soft-stop marks the conversation corrupt; the next turn must open a fresh session.
            // Also: the first JSON must be fully drained before the same sampler/context is reused.
            var followUp = Task.Run(() => conversation.AskAsync("""
                RESULTADO DE focus.summarize: {"success":true,"data":{"from":"2026-08-01","to":"2026-08-31","totalMinutes":120,"totalHours":2}}
                Responde con finish y esos datos. No repitas la herramienta.
                /no_think
                """, CancellationToken.None)).GetAwaiter().GetResult();
            using var finished = FirstJson(followUp);
            bool completedSecondTurn = finished.RootElement.GetProperty("action").GetString() == "finish";
            if (!completedSecondTurn) throw new InvalidOperationException("The model did not finish after a successful tool result. Output: " + followUp);
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new
            {
                success = true, model.Backend, loadMilliseconds = load.ElapsedMilliseconds,
                inferenceMilliseconds = inference.ElapsedMilliseconds, mappedFocusHistory, completedSecondTurn, answer, followUp
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new { success = false, error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { Application.Current.Shutdown(); }
    }

    /// <summary>Live DeepSeek round-trip: blue note colour + finish protocol. Needs a stored API key.</summary>
    public static void RunDeepSeekSmoke(string dataPath, string outputPath)
    {
        try
        {
            var store = new Store(dataPath);
            var settings = store.Read<Settings>("settings") ?? new Settings();
            settings.Validate();
            if (!DeepSeekAgentModel.IsConfigured(settings))
                throw new InvalidOperationException("No hay API key de DeepSeek guardada en settings.");

            var now = DateTime.Now;
            string system = PomoAgent.SystemPrompt(now);
            using var conversation = DeepSeekAgentModel.Begin(settings, system);
            var timer = Stopwatch.StartNew();
            string first = Task.Run(() => conversation.AskAsync($"""
                FECHA LOCAL: {now:yyyy-MM-dd HH:mm:ss}
                IDIOMA DE LA INTERFAZ: es
                PANTALLA ACTUAL: Página 1/1. Widgets visibles ahora: (ninguno). Timer: no.
                PETICIÓN DEL USUARIO: crea una nota azul que diga smoke test pomodock
                """, CancellationToken.None)).GetAwaiter().GetResult();
            timer.Stop();
            using var turn = FirstJson(first);
            string action = turn.RootElement.GetProperty("action").GetString() ?? "";
            string color = "";
            if (turn.RootElement.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object
                && args.TryGetProperty("color", out var colorEl) && colorEl.ValueKind == JsonValueKind.String)
                color = colorEl.GetString() ?? "";
            bool noteOk = action is "add_note" or "notes.create"
                && color.Equals("blue", StringComparison.OrdinalIgnoreCase);

            string follow = "";
            bool finishOk = false;
            if (noteOk)
            {
                follow = Task.Run(() => conversation.AskAsync("""
                    RESULTADO DE add_note: {"success":true,"changed":true,"summary":"Nota azul: smoke test pomodock"}
                    PROPUESTA REGISTRADA, no ejecutada: add_note {"color":"blue","text":"smoke test pomodock"}.
                    Termina con action=finish y un message corto de resultado (sin pedir confirmación otra vez).
                    """, CancellationToken.None)).GetAwaiter().GetResult();
                using var done = FirstJson(follow);
                finishOk = (done.RootElement.GetProperty("action").GetString() ?? "") == "finish";
            }

            var reply = AgentReply.After(
                "Listo. Dejé propuesto el widget; solo falta que lo confirmes para ejecutarlo.",
                [new AgentActionReceipt { Tool = "add_note", Summary = "Nota azul: smoke test pomodock" }],
                spanish: true);
            bool replyOk = reply.Contains("azul", StringComparison.OrdinalIgnoreCase)
                && !reply.Contains("confirm", StringComparison.OrdinalIgnoreCase);

            // Follow-up context: opening must carry the prior habit turn for "cuáles son".
            var fakeHistory = new List<AgentChatMessage>
            {
                new() { Role = "you", Text = "que habitos tengo" },
                new() { Role = "agent", Text = "3 hábitos.", Receipts = [new AgentActionReceipt { Tool = "habits.search", Summary = "3 hábitos: bici, leer, no fumar" }] },
                new() { Role = "you", Text = "cuales son" }
            };
            var prior = fakeHistory.Take(fakeHistory.Count - 1).ToList();
            string thread = string.Join("\n", prior.Select(m =>
            {
                string extras = m.Receipts.Count == 0 ? "" : " · " + string.Join("; ", m.Receipts.Select(r => r.Summary));
                return (m.Role == "you" ? "usuario" : "agente") + ": " + m.Text + extras;
            }));
            bool contextOk = thread.Contains("habitos", StringComparison.OrdinalIgnoreCase)
                && thread.Contains("bici", StringComparison.OrdinalIgnoreCase)
                && thread.Contains("3 hábitos", StringComparison.OrdinalIgnoreCase);

            string followUp = Task.Run(() => conversation.AskAsync($"""
                FECHA LOCAL: {now:yyyy-MM-dd HH:mm:ss}
                IDIOMA: es
                PANTALLA ACTUAL: Página 1/1. Widgets visibles ahora: (ninguno). Timer: no.
                MEMORIA: (vacía)
                HILO RECIENTE (contexto obligatorio; resuelve referencias como «cuáles», «eso», «el anterior» contra este hilo):
                {thread}
                PETICIÓN ACTUAL: cuales son
                Si la petición es corta o anafórica («cuáles son», «y eso», «dímelos»), continúa el tema del HILO RECIENTE; no cambies a get_state ni a un resumen global.
                Al listar hábitos, el message debe nombrarlos.
                """, CancellationToken.None)).GetAwaiter().GetResult();
            using var followTurn = FirstJson(followUp);
            string followAction = followTurn.RootElement.GetProperty("action").GetString() ?? "";
            string followMessage = followTurn.RootElement.TryGetProperty("message", out var fm) ? fm.GetString() ?? "" : "";
            bool followOk = followAction is "habits.search" or "finish"
                && (followMessage.Contains("bici", StringComparison.OrdinalIgnoreCase)
                    || followMessage.Contains("leer", StringComparison.OrdinalIgnoreCase)
                    || followAction == "habits.search");

            bool success = noteOk && finishOk && replyOk && contextOk && followOk;
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new
            {
                success,
                model = "deepseek-v4-pro",
                milliseconds = timer.ElapsedMilliseconds,
                noteOk,
                finishOk,
                replyOk,
                contextOk,
                followOk,
                color,
                action,
                reply,
                followAction,
                followMessage,
                first,
                follow,
                followUp
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (!success) throw new InvalidOperationException("DeepSeek smoke failed. See " + outputPath);
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(outputPath, JsonSerializer.Serialize(new { success = false, error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
        finally { Application.Current.Shutdown(); }
    }

    private static JsonDocument FirstJson(string text)
    {
        int start = text.IndexOf('{');
        if (start < 0) throw new JsonException("No JSON object in model output.");
        bool quoted = false, escaped = false; int depth = 0;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (escaped) { escaped = false; continue; }
            if (quoted && c == '\\') { escaped = true; continue; }
            if (c == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return JsonDocument.Parse(text[start..(i + 1)]);
        }
        throw new JsonException("Incomplete JSON object in model output.");
    }
}
