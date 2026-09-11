using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PomoDock.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentMemoryStatus { Proposed, Confirmed, Forgotten }

public sealed class AgentMemoryItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "preference";
    public string Content { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Origin { get; set; } = "user";
    public double Confidence { get; set; } = 1;
    public AgentMemoryStatus Status { get; set; } = AgentMemoryStatus.Confirmed;
    public List<string> Tags { get; set; } = [];
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
    public bool Sensitive { get; set; }
}

public sealed class AgentMemoryBook
{
    public int Version { get; set; } = 1;
    public List<AgentMemoryItem> Items { get; set; } = [];
    public bool Enabled { get; set; } = true;

    public IEnumerable<AgentMemoryItem> Visible =>
        Items.Where(item => item.Status != AgentMemoryStatus.Forgotten);

    public AgentMemoryItem Remember(string content, string kind = "preference", string origin = "user", bool sensitive = false)
    {
        content = (content ?? "").Trim();
        var existing = Visible.FirstOrDefault(item =>
            string.Equals(item.Content, content, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.LastUsedUtc = DateTime.UtcNow;
            existing.Status = AgentMemoryStatus.Confirmed;
            existing.Kind = string.IsNullOrWhiteSpace(kind) ? existing.Kind : kind;
            return existing;
        }
        var item = new AgentMemoryItem
        {
            Content = content,
            Kind = string.IsNullOrWhiteSpace(kind) ? "preference" : kind.Trim(),
            Origin = origin,
            Sensitive = sensitive,
            Status = AgentMemoryStatus.Confirmed
        };
        Items.Add(item);
        return item;
    }

    public AgentMemoryItem Propose(string content, string kind = "preference")
    {
        content = (content ?? "").Trim();
        var existing = Visible.FirstOrDefault(item =>
            string.Equals(item.Content, content, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var item = new AgentMemoryItem
        {
            Content = content,
            Kind = string.IsNullOrWhiteSpace(kind) ? "preference" : kind.Trim(),
            Origin = "agent",
            Status = AgentMemoryStatus.Proposed,
            Confidence = 0.6
        };
        Items.Add(item);
        return item;
    }

    public bool Forget(string query)
    {
        query = (query ?? "").Trim();
        bool last = query.Length == 0 || query.Equals("lo", StringComparison.OrdinalIgnoreCase)
            || query.Equals("eso", StringComparison.OrdinalIgnoreCase)
            || query.Equals("it", StringComparison.OrdinalIgnoreCase)
            || query.Equals("that", StringComparison.OrdinalIgnoreCase);
        if (last)
        {
            var recent = Visible.OrderByDescending(item => item.LastUsedUtc).FirstOrDefault();
            if (recent is null) return false;
            recent.Status = AgentMemoryStatus.Forgotten;
            recent.LastUsedUtc = DateTime.UtcNow;
            return true;
        }
        var matches = Visible.Where(item =>
            item.Id.ToString("N").StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
            item.Content.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1) return false;
        matches[0].Status = AgentMemoryStatus.Forgotten;
        matches[0].LastUsedUtc = DateTime.UtcNow;
        return true;
    }

    public IReadOnlyList<AgentMemoryItem> Search(string query, int limit = 8)
    {
        query = (query ?? "").Trim();
        return Visible
            .Where(item => query.Length == 0 || item.Content.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                           item.Kind.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                           item.Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(item => item.LastUsedUtc)
            .Take(Math.Clamp(limit, 1, 40))
            .ToList();
    }

    public string ContextBlock(int budget = 800)
    {
        var lines = new StringBuilder();
        foreach (var item in Visible.Where(item => item.Status == AgentMemoryStatus.Confirmed).OrderByDescending(item => item.LastUsedUtc))
        {
            string line = $"- [{item.Kind}] {item.Content}";
            if (lines.Length + line.Length + 1 > budget) break;
            if (lines.Length > 0) lines.AppendLine();
            lines.Append(line);
            item.LastUsedUtc = DateTime.UtcNow;
        }
        return lines.ToString();
    }
}

/// <summary>Personal memory lives beside the rest of PomoDock's SQLite state. Sensitive text is sealed with DPAPI.</summary>
public sealed class AgentMemoryStore
{
    public const string Key = "agent.memory";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PomoDock.agent.memory.v1");
    private readonly Store store;
    public AgentMemoryBook Book { get; }

    public AgentMemoryStore(Store store)
    {
        this.store = store;
        AgentMemoryBook? book = null;
        try { book = store.Read<AgentMemoryBook>(Key); }
        catch (JsonException) { }
        Book = book ?? new AgentMemoryBook();
        Book.Items ??= [];
        foreach (var item in Book.Items) item.Content = Reveal(item.Content);
    }

    public void Save()
    {
        var sealedBook = new AgentMemoryBook
        {
            Version = Book.Version,
            Enabled = Book.Enabled,
            Items = Book.Items.Select(item => new AgentMemoryItem
            {
                Id = item.Id,
                Kind = item.Kind,
                Content = Seal(item.Content, item.Sensitive),
                CreatedUtc = item.CreatedUtc,
                Origin = item.Origin,
                Confidence = item.Confidence,
                Status = item.Status,
                Tags = item.Tags,
                LastUsedUtc = item.LastUsedUtc,
                Sensitive = item.Sensitive
            }).ToList()
        };
        store.Write(Key, sealedBook);
    }

    public string ExportJson()
    {
        var visible = Book.Visible.Select(item => new
        {
            item.Id, item.Kind, item.Content, item.CreatedUtc, item.Origin, item.Status, item.Tags, item.Sensitive
        });
        return JsonSerializer.Serialize(visible, Store.JsonOptions);
    }

    internal static string Seal(string text, bool sensitive)
    {
        if (!sensitive || string.IsNullOrEmpty(text)) return text;
        try
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(text), Entropy, DataProtectionScope.CurrentUser);
            return "dpapi:" + Convert.ToBase64String(bytes);
        }
        catch (PlatformNotSupportedException) { return text; }
        catch (CryptographicException) { return text; }
    }

    internal static string Reveal(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.StartsWith("dpapi:", StringComparison.Ordinal)) return text;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(text[6..]), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception) { return text; }
    }
}
