using System.Text.Json;

namespace PomoDock.Core;

public sealed class AgentChatMessage
{
    public string Role { get; set; } = "agent";
    public string Text { get; set; } = "";
    public DateTime At { get; set; } = DateTime.Now;
    public List<AgentActionReceipt> Receipts { get; set; } = [];
    public AgentPlan? Plan { get; set; }
}

public sealed class AgentChat
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public List<AgentChatMessage> Messages { get; set; } = [];

    public void Add(string role, string text, IEnumerable<AgentActionReceipt>? receipts = null, AgentPlan? plan = null)
    {
        Messages.Add(new AgentChatMessage
        {
            Role = role,
            Text = text,
            At = DateTime.Now,
            Receipts = receipts?.ToList() ?? [],
            Plan = plan
        });
        UpdatedUtc = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(Title) && role == "you" && text.Trim().Length > 0)
            Title = text.Trim().Length <= 42 ? text.Trim() : text.Trim()[..42].Trim() + "…";
    }
}

public sealed class AgentChatLog
{
    public int Version { get; set; } = 1;
    public Guid? CurrentId { get; set; }
    public List<AgentChat> Chats { get; set; } = [];
}

public sealed class AgentConversationStore
{
    public const string Key = "agent.conversations";
    public const int Keep = 20;
    private readonly Store store;
    public AgentChatLog Log { get; }

    public AgentConversationStore(Store store)
    {
        this.store = store;
        AgentChatLog? log = null;
        try { log = store.Read<AgentChatLog>(Key); }
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
        Trim();
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
        Trim();
        store.Write(Key, Log);
    }

    private void Trim()
    {
        var keep = Log.Chats
            .OrderBy(chat => chat.Id == Log.CurrentId ? 0 : 1)
            .ThenByDescending(chat => chat.UpdatedUtc)
            .Take(Keep)
            .ToList();
        Log.Chats = keep;
    }
}
