using System.Text.Json;

namespace PomoDock.Core.Agent;

public static class AgentMemory
{
    public const string StateKey = "agent-memory";

    public static AgentMemoryBook Load(Store store) =>
        store.Read<AgentMemoryBook>(StateKey) ?? new AgentMemoryBook();

    public static void Save(Store store, AgentMemoryBook book)
    {
        book.Facts ??= [];
        book.UpdatedUtc = DateTimeOffset.UtcNow;
        store.Write(StateKey, book);
    }

    public static AgentMemoryFact Remember(AgentMemoryBook book, string text, IEnumerable<string>? tags = null)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("La memoria necesita texto.");
        var existing = book.Facts.FirstOrDefault(f => string.Equals(f.Text, text, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.UpdatedUtc = DateTimeOffset.UtcNow;
            if (tags is not null) existing.Tags = tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return existing;
        }
        var fact = new AgentMemoryFact
        {
            Text = text,
            Tags = tags?.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? []
        };
        book.Facts.Add(fact);
        book.UpdatedUtc = DateTimeOffset.UtcNow;
        return fact;
    }

    public static bool Forget(AgentMemoryBook book, string? id = null, string? text = null)
    {
        AgentMemoryFact? match = null;
        if (!string.IsNullOrWhiteSpace(id))
            match = book.Facts.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
        if (match is null && !string.IsNullOrWhiteSpace(text))
            match = book.Facts.FirstOrDefault(f => string.Equals(f.Text, text.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null) return false;
        book.Facts.Remove(match);
        book.UpdatedUtc = DateTimeOffset.UtcNow;
        return true;
    }

    public static string ExportJson(AgentMemoryBook book) => JsonSerializer.Serialize(book, Store.JsonOptions);

    public static AgentMemoryBook ImportJson(string json)
    {
        var book = JsonSerializer.Deserialize<AgentMemoryBook>(json) ?? throw new InvalidDataException("Memoria inválida.");
        book.Facts ??= [];
        return book;
    }

    public static string FormatList(AgentMemoryBook book)
    {
        if (book.Facts.Count == 0) return "Memoria vacía.";
        return string.Join("\n", book.Facts.Select(f => $"• [{f.Id[..Math.Min(8, f.Id.Length)]}] {f.Text}"));
    }
}

public static class AgentConversations
{
    public const string StateKey = "agent-conversations";

    public static AgentConversationBook Load(Store store) =>
        store.Read<AgentConversationBook>(StateKey) ?? new AgentConversationBook();

    public static void Save(Store store, AgentConversationBook book)
    {
        book.Conversations ??= [];
        store.Write(StateKey, book);
    }

    public static AgentConversation EnsureActive(AgentConversationBook book)
    {
        var active = book.Conversations.FirstOrDefault(c => c.Id == book.ActiveId);
        if (active is not null) return active;
        active = new AgentConversation { Title = "Nueva conversación" };
        book.Conversations.Insert(0, active);
        book.ActiveId = active.Id;
        return active;
    }

    public static AgentConversation StartNew(AgentConversationBook book, string? title = null)
    {
        var conversation = new AgentConversation { Title = string.IsNullOrWhiteSpace(title) ? "Nueva conversación" : title.Trim() };
        book.Conversations.Insert(0, conversation);
        book.ActiveId = conversation.Id;
        return conversation;
    }

    public static bool Delete(AgentConversationBook book, string id)
    {
        var removed = book.Conversations.RemoveAll(c => c.Id == id) > 0;
        if (book.ActiveId == id) book.ActiveId = book.Conversations.FirstOrDefault()?.Id;
        return removed;
    }

    public static void Append(AgentConversation conversation, AgentMessage message, int maxMessages = 200)
    {
        conversation.Messages.Add(message);
        conversation.UpdatedUtc = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(conversation.Title) || conversation.Title == "Nueva conversación")
        {
            if (message.Role == "user" && message.Text.Length > 0)
                conversation.Title = message.Text.Length <= 48 ? message.Text : message.Text[..45] + "…";
        }
        while (conversation.Messages.Count > maxMessages)
            conversation.Messages.RemoveAt(0);
    }
}
