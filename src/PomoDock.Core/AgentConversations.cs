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

    public AgentChat LatestOrNew()
    {
        var latest = Log.Chats.OrderByDescending(chat => chat.UpdatedUtc).FirstOrDefault();
        if (latest is not null) return latest;
        var created = new AgentChat();
        Log.Chats.Add(created);
        return created;
    }

    public AgentChat StartNew()
    {
        var created = new AgentChat();
        Log.Chats.Insert(0, created);
        Trim();
        Save();
        return created;
    }

    public void Save()
    {
        Trim();
        store.Write(Key, Log);
    }

    private void Trim()
    {
        Log.Chats = Log.Chats.OrderByDescending(chat => chat.UpdatedUtc).Take(Keep).ToList();
    }
}
