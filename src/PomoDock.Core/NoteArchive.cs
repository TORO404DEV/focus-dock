using System.Globalization;
using System.Text.Json.Serialization;

namespace PomoDock.Core;

/// <summary>
/// Every note the workspace has held, open or closed. Closing a card no longer throws away what
/// was written on it: the last version stays here until it is deleted from the history.
/// </summary>
public sealed class NoteArchive
{
    /// <summary>Closed notes kept at most, newest first. Notes still on a page are never trimmed.</summary>
    public const int ClosedLimit = 300;

    public int Version { get; set; } = 1;
    public List<ArchivedNote> Notes { get; set; } = [];

    public void Normalize()
    {
        Notes ??= [];
        foreach (var note in Notes) note.Normalize();
        Notes.RemoveAll(note => note.Text.Length == 0);
        // One entry per note: a repeated id keeps its most recent version.
        foreach (var duplicate in Notes.GroupBy(note => note.Id).SelectMany(group => group.OrderByDescending(note => note.UpdatedUtc).Skip(1)).ToList())
            Notes.Remove(duplicate);
        foreach (var stale in Notes.Where(note => !note.IsOpen).OrderByDescending(note => note.ClosedUtc).Skip(ClosedLimit).ToList())
            Notes.Remove(stale);
    }

    public ArchivedNote? Find(Guid id) => Notes.FirstOrDefault(note => note.Id == id);

    /// <summary>Records the latest version of a note that is on a page. An empty note leaves no trace.</summary>
    public void Track(Guid id, string text, string color, string payload, DateTime updatedUtc)
    {
        var clean = (text ?? "").Trim();
        var existing = Find(id);
        if (clean.Length == 0)
        {
            if (existing is not null) Notes.Remove(existing);
            return;
        }
        if (existing is null)
        {
            existing = new ArchivedNote { Id = id, CreatedUtc = updatedUtc };
            Notes.Add(existing);
        }
        existing.Text = clean;
        existing.Color = color ?? "";
        existing.Payload = payload ?? "";
        existing.UpdatedUtc = updatedUtc;
        existing.ClosedUtc = null;
    }

    /// <summary>Marks a note as closed. Its content stays, ready to be reopened.</summary>
    public bool Close(Guid id, DateTime closedUtc)
    {
        var note = Find(id);
        if (note is null) return false;
        note.ClosedUtc = closedUtc;
        return true;
    }

    public bool Forget(Guid id) => Notes.RemoveAll(note => note.Id == id) > 0;

    /// <summary>
    /// Notes the history believes are open but that no page holds any more — an app that closed
    /// abruptly, a card removed by an older version — are treated as closed, so they can be reopened.
    /// </summary>
    public int Reconcile(ISet<Guid> onPages, DateTime nowUtc)
    {
        int closed = 0;
        foreach (var note in Notes.Where(note => note.IsOpen && !onPages.Contains(note.Id)))
        {
            // When it really went away is unknown; the last time it was seen is the honest answer.
            note.ClosedUtc = note.UpdatedUtc == default ? nowUtc : note.UpdatedUtc;
            closed++;
        }
        return closed;
    }

    /// <summary>What is on a page first, then closed notes, most recent first.</summary>
    public List<ArchivedNote> Ordered() => Notes
        .OrderBy(note => note.IsOpen ? 0 : 1)
        .ThenByDescending(note => note.ClosedUtc ?? note.UpdatedUtc)
        .ToList();

    /// <summary>Every word has to appear, in any case and with or without accents.</summary>
    public List<ArchivedNote> Search(string? query)
    {
        var terms = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        return Ordered()
            .Where(note => terms.All(term => compare.IndexOf(note.Text, term, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0))
            .ToList();
    }

    /// <summary>Folds a backup in: notes not here yet are added, notes already here stay as they are.</summary>
    public int MergeFrom(NoteArchive? other)
    {
        if (other is null) return 0;
        other.Normalize();
        int added = 0;
        foreach (var note in other.Notes)
        {
            if (Find(note.Id) is not null) continue;
            // A restored note is a memory, not a card on this screen: it arrives closed.
            note.ClosedUtc ??= note.UpdatedUtc;
            Notes.Add(note);
            added++;
        }
        Normalize();
        return added;
    }
}

public sealed class ArchivedNote
{
    public Guid Id { get; set; }
    /// <summary>Plain text of the note, one line per paragraph, used for the list and for search.</summary>
    public string Text { get; set; } = "";
    public string Color { get; set; } = "";
    /// <summary>The card's payload exactly as it was stored, so reopening a note loses nothing.</summary>
    public string Payload { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? ClosedUtc { get; set; }

    [JsonIgnore] public bool IsOpen => ClosedUtc is null;

    /// <summary>The first line that says something.</summary>
    [JsonIgnore]
    public string Title
    {
        get
        {
            var first = Text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? "";
            return first.Length <= 70 ? first : first[..69].TrimEnd() + "…";
        }
    }

    /// <summary>Everything after the title on one line, for a two-line preview.</summary>
    [JsonIgnore]
    public string Body
    {
        get
        {
            var lines = Text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).Skip(1);
            return string.Join("  ·  ", lines);
        }
    }

    [JsonIgnore] public int Words => Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Count(word => word is not ("☐" or "☑"));

    public void Normalize()
    {
        Text = (Text ?? "").Trim();
        Color ??= "";
        Payload ??= "";
    }
}
