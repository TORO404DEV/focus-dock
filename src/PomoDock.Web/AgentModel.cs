using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PomoDock.Core;

namespace PomoDock.Web;

internal interface IAgentConversation : IDisposable
{
    Task<string> AskAsync(string text, IProgress<string>? tokens, CancellationToken cancellationToken);
}

/// <summary>
/// DeepSeek chat API as the agent brain, the desktop's <c>DeepSeekAgentModel</c> called straight from
/// the page: DeepSeek answers CORS, so the person's own key never passes through a server of ours.
/// </summary>
internal static class DeepSeekAgentModel
{
    private const string Endpoint = "https://api.deepseek.com/chat/completions";
    /// <summary>
    /// V4 Pro + thinking disabled: stronger instruction/tool following than Flash
    /// (what legacy deepseek-chat currently routes to).
    /// </summary>
    private const string Model = "deepseek-v4-pro";
    /// <summary>
    /// The key lives under its own name, never in Settings: the backup exports Settings, and on the
    /// web there is no DPAPI to seal it with as the desktop does.
    /// </summary>
    private const string KeyName = "agent.deepseek-key";
    // No User-Agent: the browser owns that header, and asking for it would fail DeepSeek's preflight.
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = null };

    public static bool IsConfigured(WebStore store) => RevealKey(store).Length >= 8;

    public static string RevealKey(WebStore store)
    {
        string stored = (store.Read<string>(KeyName) ?? "").Trim();
        if (stored.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            stored = stored["Bearer ".Length..].Trim();
        return stored.Trim('"', '\'', ' ');
    }

    public static void SaveKey(WebStore store, string raw)
    {
        string trimmed = (raw ?? "").Trim().Trim('"', '\'');
        if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["Bearer ".Length..].Trim();
        if (trimmed.Length == 0) store.Remove(KeyName);
        else store.Write(KeyName, trimmed);
    }

    public static IAgentConversation Begin(WebStore store, string systemPrompt)
    {
        string key = RevealKey(store);
        if (!IsConfigured(store))
            throw new InvalidOperationException("Falta la API key de DeepSeek. Pégala en el agente.");
        // DeepSeek json_object mode requires the word "json" in the conversation.
        string system = systemPrompt.Contains("json", StringComparison.OrdinalIgnoreCase)
            ? systemPrompt
            : systemPrompt + "\n\nAlways reply with a single JSON object.";
        return new Conversation(key, system);
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
                    if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? responseText;
                }
            }
            catch (JsonException) { }
            return responseText;
        }

        private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";
    }
}

/// <summary>The desktop's <c>AgentMemoryStore</c> over the browser's storage, under the same key and shape.</summary>
internal sealed class AgentMemory
{
    private readonly WebStore store;
    public AgentMemoryBook Book { get; }

    public AgentMemory(WebStore store)
    {
        this.store = store;
        AgentMemoryBook? book = null;
        try { book = store.Read<AgentMemoryBook>(AgentMemoryStore.Key); }
        catch (JsonException) { }
        Book = book ?? new AgentMemoryBook();
        Book.Items ??= [];
    }

    public void Save() => store.Write(AgentMemoryStore.Key, Book);

    public string ExportJson()
    {
        var visible = Book.Visible.Select(item => new
        {
            item.Id, item.Kind, item.Content, item.CreatedUtc, item.Origin, item.Status, item.Tags, item.Sensitive
        });
        return JsonSerializer.Serialize(visible, Store.JsonOptions);
    }
}

/// <summary>The desktop's <c>AgentConversationStore</c>: the last twenty chats, the open one first.</summary>
internal sealed class AgentConversations
{
    private readonly WebStore store;
    public AgentChatLog Log { get; }

    public AgentConversations(WebStore store)
    {
        this.store = store;
        AgentChatLog? log = null;
        try { log = store.Read<AgentChatLog>(AgentConversationStore.Key); }
        catch (JsonException) { }
        Log = log ?? new AgentChatLog();
        Log.Chats ??= [];
    }

    public IReadOnlyList<AgentChat> Listed =>
        Log.Chats.OrderByDescending(chat => chat.UpdatedUtc).ToList();

    public AgentChat LatestOrNew()
    {
        if (Log.CurrentId is { } id && Log.Chats.FirstOrDefault(chat => chat.Id == id) is { } open)
            return open;
        var latest = Log.Chats.Where(chat => chat.Messages.Count > 0).OrderByDescending(chat => chat.UpdatedUtc).FirstOrDefault()
            ?? Log.Chats.FirstOrDefault();
        if (latest is not null)
        {
            Log.CurrentId = latest.Id;
            return latest;
        }
        return StartNew();
    }

    public AgentChat StartNew()
    {
        if (Log.Chats.FirstOrDefault(chat => chat.Id == Log.CurrentId) is { Messages.Count: 0 } blank)
            return blank;
        var created = new AgentChat();
        Log.Chats.Insert(0, created);
        Log.CurrentId = created.Id;
        Save();
        return created;
    }

    public AgentChat Open(Guid id)
    {
        var chat = Log.Chats.FirstOrDefault(item => item.Id == id) ?? LatestOrNew();
        Log.CurrentId = chat.Id;
        Save();
        return chat;
    }

    public void Delete(Guid id)
    {
        Log.Chats.RemoveAll(chat => chat.Id == id);
        if (Log.CurrentId == id) Log.CurrentId = null;
        Save();
    }

    public void ForgetIfEmpty(AgentChat chat)
    {
        if (chat.Messages.Count > 0) return;
        Log.Chats.RemoveAll(item => item.Id == chat.Id);
        if (Log.CurrentId == chat.Id) Log.CurrentId = null;
    }

    public void Touch(AgentChat chat)
    {
        Log.CurrentId = chat.Id;
        chat.UpdatedUtc = DateTime.UtcNow;
        Save();
    }

    public void Save()
    {
        Log.Chats = Log.Chats
            .OrderBy(chat => chat.Id == Log.CurrentId ? 0 : 1)
            .ThenByDescending(chat => chat.UpdatedUtc)
            .Take(AgentConversationStore.Keep)
            .ToList();
        store.Write(AgentConversationStore.Key, Log);
    }
}
