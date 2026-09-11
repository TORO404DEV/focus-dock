using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

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
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new
            {
                success = true, model.Backend, loadMilliseconds = load.ElapsedMilliseconds,
                inferenceMilliseconds = inference.ElapsedMilliseconds, mappedFocusHistory, answer
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(outputPath, JsonSerializer.Serialize(new { success = false, error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
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
