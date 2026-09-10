using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Documents;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// Keeps the note history in the database. Every note card reports its latest text here, and
/// closing a card only marks its note as closed, so nothing written is lost with the card.
/// </summary>
internal sealed class NoteArchiveStore
{
    private static readonly ConditionalWeakTable<Store, NoteArchiveStore> shared = new();
    private readonly Store store;
    public NoteArchive Archive { get; }
    /// <summary>Raised after every write so an open history window stays current.</summary>
    public event Action? Changed;

    public static NoteArchiveStore For(Store store) => shared.GetValue(store, key => new NoteArchiveStore(key));

    private NoteArchiveStore(Store store)
    {
        this.store = store;
        NoteArchive? archive = null;
        try { archive = store.Read<NoteArchive>(Store.NotesKey); }
        catch (JsonException) { }
        Archive = archive ?? new NoteArchive();
        Archive.Normalize();
    }

    private void Save()
    {
        Archive.Normalize();
        store.Write(Store.NotesKey, Archive);
        Changed?.Invoke();
    }

    /// <summary>Records what a note card holds right now. Nothing is written when nothing changed.</summary>
    public void Track(WidgetConfig config)
    {
        if (Absorb(config)) Save();
    }

    /// <summary>The card is going away: its last words stay in the history, marked as closed.</summary>
    public void Close(WidgetConfig config)
    {
        Absorb(config);
        Archive.Close(config.Id, DateTime.UtcNow);
        Save();
    }

    public void Forget(Guid id)
    {
        if (Archive.Forget(id)) Save();
    }

    public int Merge(NoteArchive? incoming)
    {
        if (incoming is null) return 0;
        int added = Archive.MergeFrom(incoming);
        Save();
        return added;
    }

    /// <summary>
    /// Brings the history up to date with every note on every page — pages not opened yet this
    /// session included — and closes the entries no page holds any more.
    /// </summary>
    public void Sync(Settings settings)
    {
        var notes = settings.WorkspacePages.SelectMany(page => page.Widgets).Where(widget => widget.Kind == "notes").ToList();
        bool changed = false;
        foreach (var note in notes) changed |= Absorb(note);
        changed |= Archive.Reconcile(notes.Select(note => note.Id).ToHashSet(), DateTime.UtcNow) > 0;
        if (changed) Save();
    }

    private bool Absorb(WidgetConfig config)
    {
        var existing = Archive.Find(config.Id);
        if (existing is { IsOpen: true } && existing.Payload == config.Value) return false;
        var (text, color, updated) = Read(config.Value);
        // A fresh, empty note has nothing to remember yet.
        if (existing is null && text.Trim().Length == 0) return false;
        Archive.Track(config.Id, text, color, config.Value, updated ?? existing?.UpdatedUtc ?? DateTime.UtcNow);
        return true;
    }

    /// <summary>A note card's plain text — one line per paragraph, with its checklist marks.</summary>
    public static (string Text, string Color, DateTime? Updated) Read(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return ("", "", null);
        NotesWidgetData? data = null;
        try { data = JsonSerializer.Deserialize<NotesWidgetData>(payload); }
        catch (JsonException) { }
        // Anything that is not a rich note is a plain-text note written by an older version.
        if (data is null || data.Version <= 0) return (payload.Trim(), "", null);
        return (PlainText(data), data.Color ?? "", data.UpdatedUtc);
    }

    private static string PlainText(NotesWidgetData data)
    {
        if (string.IsNullOrWhiteSpace(data.DocumentXaml)) return "";
        var document = new FlowDocument();
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(data.DocumentXaml));
            new TextRange(document.ContentStart, document.ContentEnd).Load(stream, DataFormats.Xaml);
        }
        catch (Exception) { return ""; }
        var marks = (data.Checklists ?? []).GroupBy(item => item.ParagraphIndex).ToDictionary(group => group.Key, group => group.Last().IsChecked);
        var lines = new List<string>();
        int index = 0;
        foreach (var paragraph in Paragraphs(document.Blocks))
        {
            var line = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Trim();
            if (marks.TryGetValue(index, out bool done)) line = (done ? "☑ " : "☐ ") + line;
            lines.Add(line);
            index++;
        }
        return string.Join("\n", lines).Trim();
    }

    /// <summary>The same walk the editor uses, so checklist indices mean the same paragraphs.</summary>
    private static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph paragraph) yield return paragraph;
            else if (block is Section section)
                foreach (var child in Paragraphs(section.Blocks)) yield return child;
            else if (block is List list)
                foreach (var item in list.ListItems)
                    foreach (var child in Paragraphs(item.Blocks)) yield return child;
        }
    }
}
