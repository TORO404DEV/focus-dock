using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace PomoDock.Core.Agent;

/// <summary>Minimal OpenAI-compatible chat client (Ollama / local gateways). Streaming optional.</summary>
public sealed class AgentLlmClient : IDisposable
{
    private readonly HttpClient http;
    private readonly AgentSettings settings;
    private readonly bool ownsClient;

    public AgentLlmClient(AgentSettings settings, HttpClient? http = null)
    {
        this.settings = settings;
        settings.Validate();
        if (http is null)
        {
            this.http = new HttpClient { Timeout = TimeSpan.FromMinutes(4) };
            ownsClient = true;
        }
        else
        {
            this.http = http;
            ownsClient = false;
        }
    }

    public async Task<string> CompleteAsync(IEnumerable<(string Role, string Content)> messages, CancellationToken ct = default)
    {
        var body = BuildBody(messages, stream: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint + "/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"LLM HTTP {(int)response.StatusCode}: {Trim(json, 240)}");

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    public async IAsyncEnumerable<string> StreamAsync(IEnumerable<(string Role, string Content)> messages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = BuildBody(messages, stream: true);
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint + "/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"LLM HTTP {(int)response.StatusCode}: {Trim(err, 240)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]") yield break;
            string? delta = null;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("delta", out var deltaEl) && deltaEl.TryGetProperty("content", out var content))
                        delta = content.GetString();
                }
            }
            catch (JsonException) { /* ignore malformed chunks */ }
            if (!string.IsNullOrEmpty(delta)) yield return delta;
        }
    }

    private string BuildBody(IEnumerable<(string Role, string Content)> messages, bool stream)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = settings.Model,
            ["stream"] = stream,
            ["messages"] = messages.Select(m => new Dictionary<string, string>
            {
                ["role"] = m.Role,
                ["content"] = m.Content
            }).ToList()
        };
        return JsonSerializer.Serialize(payload);
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public void Dispose()
    {
        if (ownsClient) http.Dispose();
    }
}

/// <summary>System prompt: tools + rules. No chain-of-thought instructions beyond structured activity.</summary>
public static class AgentPrompt
{
    public static string System(AgentMemoryBook memory)
    {
        var tools = string.Join("\n", AgentToolCatalog.All.Select(t => $"- {t.Name} [{t.Kind}]: {t.Description} args={t.SchemaHint}"));
        var mem = memory.Facts.Count == 0 ? "(vacía)" : string.Join("; ", memory.Facts.Select(f => f.Text));
        return
            """
            Eres el agente local de PomoDock. Respondes en el idioma del usuario.
            Para cifras de enfoque DEBES usar focus.summarize; nunca inventes horas.
            Mutaciones requieren plan; no ejecutes escrituras sin que el runtime lo apruebe.
            No expongas razonamiento interno: solo hechos, plan y resultados.
            """.Trim() + "\n\nHerramientas:\n" + tools + "\n\nMemoria personal:\n" + mem;
    }
}
