using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// DeepSeek chat API as the agent brain. Same JSON-turn protocol as the local model,
/// without native llama.cpp (which crashed or timed out on this hardware).
/// </summary>
internal static class DeepSeekAgentModel
{
    private const string Endpoint = "https://api.deepseek.com/chat/completions";
    /// <summary>
    /// V4 Pro + thinking disabled: stronger instruction/tool following than Flash
    /// (what legacy deepseek-chat currently routes to).
    /// </summary>
    private const string Model = "deepseek-v4-pro";
    private static readonly HttpClient http = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = null };

    public static bool IsConfigured(Settings settings)
    {
        string key = RevealKey(settings);
        return key.Length >= 8 && !key.StartsWith("dpapi:", StringComparison.Ordinal);
    }

    public static string RevealKey(Settings settings)
    {
        string stored = (settings.AgentDeepSeekApiKey ?? "").Trim();
        if (stored.Length == 0) return "";
        string revealed = AgentMemoryStore.Reveal(stored).Trim();
        if (revealed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            revealed = revealed["Bearer ".Length..].Trim();
        return revealed.Trim('"', '\'', ' ');
    }

    public static void SaveKey(Settings settings, string raw)
    {
        string trimmed = (raw ?? "").Trim().Trim('"', '\'');
        if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["Bearer ".Length..].Trim();
        settings.AgentDeepSeekApiKey = trimmed.Length == 0 ? "" : AgentMemoryStore.Seal(trimmed, sensitive: true);
    }

    public static IAgentConversation Begin(Settings settings, string systemPrompt)
    {
        string key = RevealKey(settings);
        if (!IsConfigured(settings))
            throw new InvalidOperationException("Falta la API key de DeepSeek o no se pudo descifrar. Vuelve a pegarla en el agente o en Ajustes.");
        // DeepSeek json_object mode requires the word "json" in the conversation.
        string system = systemPrompt.Contains("json", StringComparison.OrdinalIgnoreCase)
            ? systemPrompt
            : systemPrompt + "\n\nAlways reply with a single JSON object.";
        return new Conversation(key, system);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PomoDock/0.2");
        return client;
    }

    private sealed class Conversation : IAgentConversation
    {
        private readonly string apiKey;
        private readonly List<Dictionary<string, string>> messages = [];
        private bool disposed;

        public Conversation(string apiKey, string systemPrompt)
        {
            this.apiKey = apiKey;
            messages.Add(new Dictionary<string, string> { ["role"] = "system", ["content"] = systemPrompt });
        }

        public Task<string> AskAsync(string text, CancellationToken cancellationToken) =>
            AskAsync(text, null, cancellationToken);

        public async Task<string> AskAsync(string text, IProgress<string>? tokens, CancellationToken cancellationToken)
        {
            if (disposed) throw new ObjectDisposedException(nameof(Conversation));
            string user = EnsureJsonHint(text);
            messages.Add(new Dictionary<string, string> { ["role"] = "user", ["content"] = user });
            tokens?.Report("\u0001");

            // Non-streaming JSON is more reliable for the tool protocol than SSE deltas.
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var body = new Dictionary<string, object?>
            {
                ["model"] = Model,
                ["temperature"] = 0.15,
                ["max_tokens"] = 1600,
                ["stream"] = false,
                ["response_format"] = new Dictionary<string, string> { ["type"] = "json_object" },
                // Flash/Pro default thinking hurts our tight JSON tool loop; force non-thinking.
                ["thinking"] = new Dictionary<string, string> { ["type"] = "disabled" },
                ["messages"] = messages
            };
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(request, cancellationToken);
            string responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string detail = Clip(ExtractError(responseText), 280);
                throw new HttpRequestException($"DeepSeek {(int)response.StatusCode}: {detail}");
            }

            string answer = AgentThink.Strip(ExtractContent(responseText));
            if (string.IsNullOrWhiteSpace(answer))
                throw new InvalidOperationException("DeepSeek devolvió una respuesta vacía.");

            tokens?.Report(AgentThink.LiveText(answer).Length > 0 ? AgentThink.LiveText(answer) : "\u0001");
            messages.Add(new Dictionary<string, string> { ["role"] = "assistant", ["content"] = answer });
            return answer;
        }

        public void Dispose() => disposed = true;

        private static string EnsureJsonHint(string text) =>
            text.Contains("json", StringComparison.OrdinalIgnoreCase)
                ? text
                : text.TrimEnd() + "\n\nResponde únicamente con un objeto JSON válido (campos action, arguments, message, understood).";

        private static string ExtractContent(string responseText)
        {
            using var doc = JsonDocument.Parse(responseText);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return "";
            var message = choices[0].GetProperty("message");
            return message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                ? content.GetString() ?? ""
                : "";
        }

        private static string ExtractError(string responseText)
        {
            try
            {
                using var doc = JsonDocument.Parse(responseText);
                if (doc.RootElement.TryGetProperty("error", out var error))
                {
                    if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message))
                        return message.GetString() ?? responseText;
                    return error.ToString();
                }
            }
            catch (JsonException) { }
            return responseText;
        }

        private static string Clip(string text, int max) =>
            string.IsNullOrWhiteSpace(text) ? "" : text.Length <= max ? text : text[..max] + "…";
    }
}
