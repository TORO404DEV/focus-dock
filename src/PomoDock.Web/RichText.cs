using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using PomoDock.Core;

namespace PomoDock.Web;

/// <summary>A stretch of text with one look, or a line break when <see cref="Break"/> is set.</summary>
public sealed class RichRun
{
    public string Text { get; set; } = "";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Strike { get; set; }
    public bool Break { get; set; }
}

/// <summary>
/// A paragraph, or a list of items that each hold paragraphs. A paragraph with a
/// <see cref="Check"/> value is a checklist line: false pending, true done.
/// </summary>
public sealed class RichBlock
{
    public bool List { get; set; }
    public bool Ordered { get; set; }
    public bool? Check { get; set; }
    public List<RichRun> Runs { get; set; } = [];
    public List<List<RichBlock>> Items { get; set; } = [];
}

/// <summary>
/// The desktop stores notes as the XAML a WPF RichTextBox writes: a Section of Paragraphs, Runs
/// and Lists, with checklist boxes kept beside it by paragraph index. The browser edits HTML.
/// This reads that XAML into a small block model and writes it back the same way, so a note
/// written here opens on the desktop and the other way round.
/// </summary>
public static class RichText
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    // ------------------------------------------------------------------ XAML in

    public static List<RichBlock> FromXaml(string? xaml, IEnumerable<NotesChecklistState>? checks, string? fallback = null)
    {
        var blocks = new List<RichBlock>();
        if (!string.IsNullOrWhiteSpace(xaml))
        {
            try
            {
                var root = XElement.Parse(xaml, LoadOptions.PreserveWhitespace);
                ReadBlocks(root, Look.Plain, blocks);
            }
            catch (XmlException) { blocks.Clear(); }
        }
        if (blocks.Count == 0)
            blocks.AddRange((fallback ?? "").Replace("\r", "").Split('\n').Select(line => Paragraph(line)));
        var marks = (checks ?? []).Where(item => item.ParagraphIndex >= 0)
            .GroupBy(item => item.ParagraphIndex).ToDictionary(group => group.Key, group => group.Last().IsChecked);
        int index = 0;
        foreach (var paragraph in Paragraphs(blocks))
        {
            if (marks.TryGetValue(index++, out bool done))
            {
                paragraph.Check = done;
                // The box was saved as a blank run, and the desktop strikes a finished line itself.
                TrimLeading(paragraph.Runs);
                foreach (var run in paragraph.Runs) run.Strike = false;
            }
        }
        return blocks;
    }

    private readonly record struct Look(bool Bold, bool Italic, bool Underline, bool Strike)
    {
        public static readonly Look Plain = new(false, false, false, false);
    }

    private static void ReadBlocks(XElement parent, Look look, List<RichBlock> into)
    {
        foreach (var element in parent.Elements())
        {
            var inner = Apply(element, look);
            switch (element.Name.LocalName)
            {
                case "Section":
                case "FlowDocument":
                case "BlockUIContainer":
                    ReadBlocks(element, inner, into);
                    break;
                case "Paragraph":
                    var paragraph = new RichBlock();
                    ReadInlines(element, inner, paragraph.Runs);
                    into.Add(paragraph);
                    break;
                case "List":
                    var list = new RichBlock { List = true, Ordered = IsOrdered(element.Attribute("MarkerStyle")?.Value) };
                    foreach (var item in element.Elements().Where(child => child.Name.LocalName == "ListItem"))
                    {
                        var blocks = new List<RichBlock>();
                        ReadBlocks(item, Apply(item, inner), blocks);
                        if (blocks.Count == 0) blocks.Add(new RichBlock());
                        list.Items.Add(blocks);
                    }
                    into.Add(list);
                    break;
                case "Table":
                    // Tables are not written by PomoDock; their words survive as paragraphs.
                    foreach (var cell in element.Descendants().Where(child => child.Name.LocalName == "Paragraph"))
                    {
                        var row = new RichBlock();
                        ReadInlines(cell, inner, row.Runs);
                        into.Add(row);
                    }
                    break;
            }
        }
    }

    private static void ReadInlines(XElement parent, Look look, List<RichRun> into)
    {
        foreach (var node in parent.Nodes())
        {
            if (node is XText text)
            {
                Add(into, text.Value, look);
                continue;
            }
            if (node is not XElement element) continue;
            var inner = Apply(element, look);
            switch (element.Name.LocalName)
            {
                case "Run":
                    Add(into, element.Attribute("Text")?.Value ?? element.Value, inner);
                    break;
                case "LineBreak":
                    into.Add(new RichRun { Break = true });
                    break;
                case "Span":
                case "Hyperlink":
                case "Bold":
                case "Italic":
                case "Underline":
                    ReadInlines(element, inner, into);
                    break;
                case "InlineUIContainer":
                    // A checklist box the desktop serialized in place: a blank that the index restores.
                    break;
                default:
                    if (!element.Name.LocalName.Contains('.')) ReadInlines(element, inner, into);
                    break;
            }
        }
    }

    private static void Add(List<RichRun> runs, string text, Look look)
    {
        if (text.Length == 0) return;
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) runs.Add(new RichRun { Break = true });
            if (lines[i].Length > 0)
                runs.Add(new RichRun { Text = lines[i], Bold = look.Bold, Italic = look.Italic, Underline = look.Underline, Strike = look.Strike });
        }
    }

    /// <summary>The look an element passes on: its tag (Bold, Italic…) and its own attributes.</summary>
    private static Look Apply(XElement element, Look look)
    {
        bool bold = look.Bold, italic = look.Italic, underline = look.Underline, strike = look.Strike;
        switch (element.Name.LocalName)
        {
            case "Bold": bold = true; break;
            case "Italic": italic = true; break;
            case "Underline": underline = true; break;
        }
        if (element.Attribute("FontWeight")?.Value is { } weight)
            bold = weight.Trim().ToLowerInvariant() is "bold" or "black" or "heavy" or "extrabold" or "ultrabold" or "semibold" or "demibold" or "extrablack" or "ultrablack"
                || (int.TryParse(weight, out int numeric) && numeric >= 600);
        if (element.Attribute("FontStyle")?.Value is { } style)
            italic = style.Trim().ToLowerInvariant() is "italic" or "oblique";
        var decorations = element.Attribute("TextDecorations")?.Value;
        var child = element.Elements().FirstOrDefault(item => item.Name.LocalName.EndsWith(".TextDecorations", StringComparison.Ordinal));
        if (child is not null)
            decorations = string.Join(",", child.Descendants().Select(item => item.Attribute("Location")?.Value ?? ""));
        if (decorations is not null)
        {
            var lower = decorations.ToLowerInvariant();
            underline = lower.Contains("underline");
            strike = lower.Contains("strikethrough");
        }
        return new Look(bold, italic, underline, strike);
    }

    private static bool IsOrdered(string? marker) =>
        (marker ?? "").Trim().ToLowerInvariant() is "decimal" or "lowerlatin" or "upperlatin" or "lowerroman" or "upperroman";

    private static void TrimLeading(List<RichRun> runs)
    {
        while (runs.Count > 0 && !runs[0].Break)
        {
            var trimmed = runs[0].Text.TrimStart();
            if (trimmed.Length > 0) { runs[0].Text = trimmed; return; }
            runs.RemoveAt(0);
        }
    }

    // ------------------------------------------------------------------ XAML out

    /// <summary>The note as the desktop saves it. Checklist boxes are written as the blank run WPF leaves.</summary>
    public static string ToXaml(IReadOnlyList<RichBlock> blocks)
    {
        var section = new XElement(Presentation + "Section",
            new XAttribute(XNamespace.Xml + "space", "preserve"),
            new XAttribute("FontFamily", "Segoe UI"),
            new XAttribute("FontSize", "14"));
        foreach (var block in blocks) section.Add(WriteBlock(block));
        return section.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement WriteBlock(RichBlock block)
    {
        if (block.List)
        {
            var list = new XElement(Presentation + "List", new XAttribute("MarkerStyle", block.Ordered ? "Decimal" : "Disc"));
            foreach (var item in block.Items)
                list.Add(new XElement(Presentation + "ListItem", item.Select(WriteBlock)));
            return list;
        }
        var paragraph = new XElement(Presentation + "Paragraph");
        if (block.Check is not null) paragraph.Add(new XElement(Presentation + "Run", " "));
        foreach (var run in block.Runs)
        {
            if (run.Break) { paragraph.Add(new XElement(Presentation + "LineBreak")); continue; }
            if (run.Text.Length == 0) continue;
            var element = new XElement(Presentation + "Run", run.Text);
            if (run.Bold) element.Add(new XAttribute("FontWeight", "Bold"));
            if (run.Italic) element.Add(new XAttribute("FontStyle", "Italic"));
            var decorations = new List<string>();
            if (run.Underline) decorations.Add("Underline");
            if (run.Strike && block.Check is null) decorations.Add("Strikethrough");
            if (decorations.Count > 0) element.Add(new XAttribute("TextDecorations", string.Join(", ", decorations)));
            paragraph.Add(element);
        }
        return paragraph;
    }

    /// <summary>Checklist boxes by paragraph index, counted the way the desktop editor counts them.</summary>
    public static List<NotesChecklistState> Checklists(IReadOnlyList<RichBlock> blocks) =>
        Paragraphs(blocks)
            .Select((paragraph, index) => (paragraph, index))
            .Where(item => item.paragraph.Check is not null)
            .Select(item => new NotesChecklistState { ParagraphIndex = item.index, IsChecked = item.paragraph.Check == true })
            .ToList();

    /// <summary>Every paragraph in document order: top level, then inside each list item.</summary>
    public static IEnumerable<RichBlock> Paragraphs(IEnumerable<RichBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (!block.List) { yield return block; continue; }
            foreach (var item in block.Items)
                foreach (var child in Paragraphs(item)) yield return child;
        }
    }

    // ------------------------------------------------------------------ HTML out

    /// <summary>The note for the browser's editor: paragraphs, lists, and checklist lines.</summary>
    public static string ToHtml(IEnumerable<RichBlock> blocks)
    {
        var html = new StringBuilder();
        foreach (var block in blocks) WriteHtml(block, html);
        if (html.Length == 0) html.Append("<p><br></p>");
        return html.ToString();
    }

    private static void WriteHtml(RichBlock block, StringBuilder html)
    {
        if (block.List)
        {
            string tag = block.Ordered ? "ol" : "ul";
            html.Append('<').Append(tag).Append('>');
            foreach (var item in block.Items)
            {
                html.Append("<li>");
                foreach (var child in item) WriteHtml(child, html);
                html.Append("</li>");
            }
            html.Append("</").Append(tag).Append('>');
            return;
        }
        html.Append(block.Check is { } done ? $"<p class=\"check\" data-checked=\"{(done ? "true" : "false")}\">" : "<p>");
        int written = 0;
        foreach (var run in block.Runs)
        {
            if (run.Break) { html.Append("<br>"); written++; continue; }
            if (run.Text.Length == 0) continue;
            if (run.Bold) html.Append("<b>");
            if (run.Italic) html.Append("<i>");
            if (run.Underline) html.Append("<u>");
            if (run.Strike) html.Append("<s>");
            html.Append(WebUtility.HtmlEncode(run.Text));
            if (run.Strike) html.Append("</s>");
            if (run.Underline) html.Append("</u>");
            if (run.Italic) html.Append("</i>");
            if (run.Bold) html.Append("</b>");
            written++;
        }
        // A trailing break or an empty line needs one more so the browser gives it height.
        if (written == 0 || block.Runs.LastOrDefault()?.Break == true) html.Append("<br>");
        html.Append("</p>");
    }

    // ------------------------------------------------------------------ text

    /// <summary>One line per paragraph, with its checklist mark: what the history and counters read.</summary>
    public static string PlainText(IEnumerable<RichBlock> blocks)
    {
        var lines = new List<string>();
        foreach (var paragraph in Paragraphs(blocks))
        {
            var line = string.Concat(paragraph.Runs.Select(run => run.Break ? "\n" : run.Text)).Trim();
            if (paragraph.Check is { } done) line = (done ? "☑ " : "☐ ") + line;
            lines.Add(line);
        }
        return string.Join("\n", lines).Trim();
    }

    /// <summary>The words alone, as the desktop's TextRange.Text reads them.</summary>
    public static string Text(IEnumerable<RichBlock> blocks) =>
        string.Join("\n", Paragraphs(blocks).Select(paragraph => string.Concat(paragraph.Runs.Select(run => run.Break ? "\n" : run.Text))));

    public static RichBlock Paragraph(string text) => new() { Runs = text.Length == 0 ? [] : [new RichRun { Text = text }] };
}
