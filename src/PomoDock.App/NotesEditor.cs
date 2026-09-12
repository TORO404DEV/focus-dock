using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>A compact rich-text editor that keeps old plain-text notes readable.</summary>
internal sealed class NotesEditor : Grid
{
    /// <summary>
    /// A checklist box drawn like the rest of PomoDock: a square in the note's ink that fills when
    /// done. It is a real button, so it takes clicks inside the text and never starts a page swipe.
    /// </summary>
    private sealed class ChecklistMark : Button
    {
        public ChecklistMark(Paragraph paragraph) => Paragraph = paragraph;
        public Paragraph Paragraph { get; }
        public bool IsChecked { get; set; }
    }

    private static readonly ControlTemplate MarkTemplate = BuildMarkTemplate();

    private readonly MainWindow owner;
    private readonly WidgetConfig config;
    private readonly WidgetCard? card;
    private readonly NoteArchiveStore archive;
    private readonly List<Button> toolButtons = [];
    private readonly List<Button> swatches = [];
    private Border? toolbar;
    private StackPanel? colorRow;
    private readonly NotesWidgetData data;
    private readonly RichTextBox editor;
    private readonly Border paper;
    private readonly TextBlock status;
    private readonly DispatcherTimer saveTimer;
    private bool loading;

    public NotesEditor(MainWindow owner, WidgetConfig config, WidgetCard? card = null)
    {
        this.owner = owner;
        this.config = config;
        this.card = card;
        archive = NoteArchiveStore.For(owner.Store);
        data = ReadData(config.Value, out var legacyText);

        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new());
        RowDefinitions.Add(new() { Height = GridLength.Auto });

        editor = new RichTextBox
        {
            BorderThickness = new Thickness(0),
            Padding = new Thickness(14, 12, 14, 12),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            AcceptsTab = true,
            // Without this, every control inside the text is disabled: the checklist boxes
            // showed up greyed out and ignored clicks.
            IsDocumentEnabled = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        editor.Document.PagePadding = new Thickness(0);
        editor.Document.LineHeight = double.NaN;

        paper = new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["Line"],
            BorderThickness = new Thickness(1),
            Child = editor
        };
        Grid.SetRow(paper, 1);

        toolbar = BuildToolbar();
        Grid.SetRow(toolbar, 0);

        var footer = new Grid { Margin = new Thickness(1, 6, 1, 0) };
        footer.ColumnDefinitions.Add(new());
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        status = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 9,
            Foreground = (Brush)Application.Current.Resources["Muted"],
            VerticalAlignment = VerticalAlignment.Center
        };
        footer.Children.Add(status);
        colorRow = new StackPanel { Orientation = Orientation.Horizontal, ToolTip = "Color del post-it" };
        BuildSwatches();
        Grid.SetColumn(colorRow, 1);
        footer.Children.Add(colorRow);
        Grid.SetRow(footer, 2);

        Children.Add(toolbar);
        Children.Add(paper);
        Children.Add(footer);

        saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        saveTimer.Tick += (_, _) =>
        {
            saveTimer.Stop();
            owner.SaveState();
            archive.Track(config);
            UpdateStatus("GUARDADO");
        };

        loading = true;
        LoadDocument(legacyText);
        ApplySkin();
        RestoreChecklists();
        WireLegacyBoxes();
        ApplySkin();
        loading = false;
        editor.TextChanged += (_, _) => QueueSave();
        editor.SelectionChanged += (_, _) => UpdateStatus();
        editor.PreviewKeyDown += EditorKeyDown;
        Unloaded += (_, _) =>
        {
            if (!saveTimer.IsEnabled) return;
            saveTimer.Stop();
            owner.SaveState();
            archive.Track(config);
        };
        UpdateStatus();
        // A note is in the history from the moment it exists, not only once it is edited.
        archive.Track(config);
    }

    private Border BuildToolbar()
    {
        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        tools.Children.Add(CommandButton("B", L.T("notes.bold"), EditingCommands.ToggleBold, FontWeights.Black));
        tools.Children.Add(CommandButton("I", L.T("notes.italic"), EditingCommands.ToggleItalic, fontStyle: FontStyles.Italic));
        tools.Children.Add(Decorated(CommandButton("U", L.T("notes.underline"), EditingCommands.ToggleUnderline), TextDecorations.Underline));
        tools.Children.Add(Decorated(ActionButton("S", L.T("notes.strike"), ToggleStrikethrough), TextDecorations.Strikethrough));
        tools.Children.Add(ActionButton("☐", L.T("notes.checkbox"), ToggleChecklist));
        tools.Children.Add(CommandButton("•", L.T("notes.bullets"), EditingCommands.ToggleBullets));
        tools.Children.Add(CommandButton("1.", "Lista numerada", EditingCommands.ToggleNumbering));
        tools.Children.Add(ActionButton("Aa", "Quitar formato", () => editor.Selection.ClearAllProperties(), 32));
        tools.Children.Add(ActionButton("＋", "Insertar fecha y hora", InsertTimestamp));
        tools.Children.Add(CommandButton("↶", L.T("notes.undo"), ApplicationCommands.Undo));
        tools.Children.Add(CommandButton("↷", L.T("notes.redo"), ApplicationCommands.Redo));
        return new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["Line"],
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = tools
        };
    }

    /// <summary>The underline and strike buttons show the very format they apply.</summary>
    private static Button Decorated(Button button, TextDecorationCollection decorations)
    {
        button.Content = new TextBlock { Text = button.Content as string, TextDecorations = decorations };
        return button;
    }

    private Button CommandButton(string label, string tip, ICommand command, FontWeight? fontWeight = null, FontStyle? fontStyle = null)
    {
        var button = ToolButton(label, tip);
        button.Command = command;
        button.CommandTarget = editor;
        if (fontWeight is not null) button.FontWeight = fontWeight.Value;
        if (fontStyle is not null) button.FontStyle = fontStyle.Value;
        return button;
    }

    private Button ActionButton(string label, string tip, Action action, double width = 28)
    {
        var button = ToolButton(label, tip, width);
        button.Click += (_, _) => { action(); editor.Focus(); };
        return button;
    }

    private Button ToolButton(string label, string tip, double width = 28)
    {
        var button = new Button
        {
            Content = label,
            ToolTip = tip,
            Width = width,
            Height = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 4),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12
        };
        toolButtons.Add(button);
        return button;
    }

    private void EditorKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.C && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            ToggleChecklist();
            e.Handled = true;
        }
        else if (e.Key == Key.X && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            ToggleStrikethrough();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && modifiers == ModifierKeys.None && CaretChecklist() is { } item)
        {
            ContinueChecklist(item.Paragraph, item.Container);
            e.Handled = true;
        }
        else if (e.Key == Key.Back && modifiers == ModifierKeys.None && editor.Selection.IsEmpty &&
                 CaretChecklist() is { } line && CaretSitsRightAfter(line.Container))
        {
            // Backspace next to the box turns the item back into plain text, the way lists do.
            RemoveMark(line.Paragraph, line.Container);
            QueueSave();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Adds or removes the strike line and keeps any underline. For plain text WPF reports an
    /// empty collection rather than null, which the old toggle read as "already struck", so the
    /// button only ever removed the line and never drew it.
    /// </summary>
    private void ToggleStrikethrough()
    {
        var current = editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        bool struck = IsStruck(current);
        var next = new TextDecorationCollection();
        foreach (var decoration in current ?? [])
            if (decoration.Location != TextDecorationLocation.Strikethrough) next.Add(decoration);
        if (!struck)
            foreach (var decoration in TextDecorations.Strikethrough) next.Add(decoration);
        editor.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, next);
        QueueSave();
    }

    private static bool IsStruck(TextDecorationCollection? decorations) =>
        decorations is not null && decorations.Any(decoration => decoration.Location == TextDecorationLocation.Strikethrough);

    private void InsertTimestamp()
    {
        editor.CaretPosition.InsertTextInRun(DateTime.Now.ToString(L.T("notes.stampFormat"), Strings.Culture));
        QueueSave();
    }

    // ---------------------------------------------------------------- checklist

    private void ToggleChecklist()
    {
        var paragraphs = SelectedParagraphs().ToArray();
        if (paragraphs.Length == 0 && editor.CaretPosition.Paragraph is { } current) paragraphs = [current];
        // A mixed selection becomes a checklist; a selection that already is one stops being one.
        bool remove = paragraphs.Length > 0 && paragraphs.All(paragraph => ChecklistOf(paragraph) is not null);
        foreach (var paragraph in paragraphs)
        {
            var existing = ChecklistOf(paragraph);
            if (remove && existing is not null) RemoveMark(paragraph, existing);
            else if (!remove && existing is null) AddMark(paragraph, false);
        }
        QueueSave();
    }

    /// <summary>
    /// Enter on an item opens the next item, the way every checklist app behaves. Enter on an
    /// empty item ends the list instead of piling up empty boxes.
    /// </summary>
    private void ContinueChecklist(Paragraph paragraph, InlineUIContainer container)
    {
        if (new TextRange(container.ElementEnd, paragraph.ContentEnd).Text.Trim().Length == 0)
        {
            RemoveMark(paragraph, container);
            QueueSave();
            return;
        }
        EditingCommands.EnterParagraphBreak.Execute(null, editor);
        if (editor.CaretPosition.Paragraph is not { } next || next == paragraph) return;
        // Splitting copies the paragraph's look; the new item starts pending, not crossed out.
        next.TextDecorations = null;
        next.ClearValue(TextElement.ForegroundProperty);
        AddMark(next, false);
        editor.CaretPosition = next.ContentEnd;
        QueueSave();
    }

    private (Paragraph Paragraph, InlineUIContainer Container)? CaretChecklist() =>
        editor.CaretPosition.Paragraph is { } paragraph && ChecklistOf(paragraph) is { } container ? (paragraph, container) : null;

    private bool CaretSitsRightAfter(InlineUIContainer container) =>
        editor.CaretPosition.CompareTo(container.ElementEnd) >= 0 &&
        new TextRange(container.ElementEnd, editor.CaretPosition).Text.Trim().Length == 0;

    private static InlineUIContainer? ChecklistOf(Paragraph paragraph) =>
        paragraph.Inlines.OfType<InlineUIContainer>().FirstOrDefault(container => container.Child is ChecklistMark);

    private IEnumerable<ChecklistMark> DocumentMarks() => AllParagraphs(editor.Document.Blocks)
        .SelectMany(paragraph => paragraph.Inlines.OfType<InlineUIContainer>())
        .Select(container => container.Child)
        .OfType<ChecklistMark>();

    private void AddMark(Paragraph paragraph, bool isChecked)
    {
        var container = new InlineUIContainer(CreateMark(paragraph, isChecked)) { BaselineAlignment = BaselineAlignment.Center };
        var first = paragraph.Inlines.FirstInline;
        if (first is null)
        {
            paragraph.Inlines.Add(container);
            paragraph.Inlines.Add(new Run(" "));
        }
        else
        {
            paragraph.Inlines.InsertBefore(first, container);
            paragraph.Inlines.InsertAfter(container, new Run(" "));
        }
        // Painted again now that it sits in the line: text split off a finished item arrives
        // crossed out, and a new item must start clean.
        PaintMark((ChecklistMark)container.Child, NoteSkin.For(data.Color));
    }

    private static void RemoveMark(Paragraph paragraph, InlineUIContainer container)
    {
        StrikeLine(container, false);
        var next = container.NextInline;
        paragraph.Inlines.Remove(container);
        if (next is Run { Text: " " }) paragraph.Inlines.Remove(next);
        paragraph.TextDecorations = null;
        paragraph.ClearValue(TextElement.ForegroundProperty);
    }

    private ChecklistMark CreateMark(Paragraph paragraph, bool isChecked)
    {
        var mark = new ChecklistMark(paragraph)
        {
            IsChecked = isChecked,
            Template = MarkTemplate,
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 7, -2),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1.5),
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 11,
            FontWeight = FontWeights.Black,
            Cursor = Cursors.Hand,
            // Clicking the box must not pull the caret out of the line being written.
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        mark.Click += (_, _) =>
        {
            mark.IsChecked = !mark.IsChecked;
            PaintMark(mark, NoteSkin.For(data.Color));
            QueueSave();
        };
        PaintMark(mark, NoteSkin.For(data.Color));
        return mark;
    }

    /// <summary>A done item is filled, and its line is crossed out and faded so what is left stands out.</summary>
    private static void PaintMark(ChecklistMark mark, NoteSkin skin)
    {
        mark.BorderBrush = skin.InkBrush;
        mark.Background = mark.IsChecked ? skin.InkBrush : Brushes.Transparent;
        mark.Foreground = skin.PaperBrush;
        mark.Content = mark.IsChecked ? "✓" : null;
        mark.ToolTip = mark.IsChecked ? "Marcar como pendiente" : "Marcar como hecho";
        AutomationProperties.SetName(mark, mark.IsChecked ? "Casilla marcada" : "Casilla sin marcar");
        // The strike goes on the words only. Struck as a whole paragraph it also crossed the space
        // after the box, which drew a stray dash between the box and the text.
        mark.Paragraph.TextDecorations = null;
        if (mark.Parent is InlineUIContainer container) StrikeLine(container, mark.IsChecked);
        if (mark.IsChecked) mark.Paragraph.Foreground = skin.MutedBrush;
        else mark.Paragraph.ClearValue(TextElement.ForegroundProperty);
    }

    /// <summary>Crosses out, or clears, the words of an item: everything after the box and its spaces.</summary>
    private static void StrikeLine(InlineUIContainer container, bool struck)
    {
        if (container.Parent is not Paragraph paragraph) return;
        var start = SkipSpaces(container.ElementEnd, paragraph.ContentEnd);
        if (start.CompareTo(paragraph.ContentEnd) >= 0) return;
        new TextRange(start, paragraph.ContentEnd).ApplyPropertyValue(
            Inline.TextDecorationsProperty, struck ? TextDecorations.Strikethrough : new TextDecorationCollection());
    }

    /// <summary>The first position after any run of spaces, across run boundaries.</summary>
    private static TextPointer SkipSpaces(TextPointer from, TextPointer end)
    {
        var position = from;
        while (position.CompareTo(end) < 0)
        {
            if (position.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text)
            {
                position = position.GetNextContextPosition(LogicalDirection.Forward);
                continue;
            }
            var run = position.GetTextInRun(LogicalDirection.Forward);
            int spaces = run.Length - run.TrimStart().Length;
            if (spaces < run.Length) return position.GetPositionAtOffset(spaces)!;
            position = position.GetPositionAtOffset(run.Length)!;
        }
        return position;
    }

    private static ControlTemplate BuildMarkTemplate()
    {
        var frame = new FrameworkElementFactory(typeof(Border));
        frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        frame.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        frame.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        frame.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = frame };
        template.Seal();
        return template;
    }

    private IEnumerable<Paragraph> SelectedParagraphs()
    {
        foreach (var paragraph in AllParagraphs(editor.Document.Blocks))
        {
            if (editor.Selection.IsEmpty)
            {
                if (paragraph == editor.CaretPosition.Paragraph) yield return paragraph;
            }
            else if (editor.Selection.Start.CompareTo(paragraph.ContentEnd) < 0 &&
                     editor.Selection.End.CompareTo(paragraph.ContentStart) > 0)
                yield return paragraph;
        }
    }

    private static IEnumerable<Paragraph> AllParagraphs(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph paragraph) yield return paragraph;
            else if (block is Section section)
                foreach (var child in AllParagraphs(section.Blocks)) yield return child;
            else if (block is List list)
                foreach (var item in list.ListItems)
                    foreach (var child in AllParagraphs(item.Blocks)) yield return child;
        }
    }

    /// <summary>Checkboxes kept by an older version of the note become the new boxes.</summary>
    private void WireLegacyBoxes()
    {
        foreach (var paragraph in AllParagraphs(editor.Document.Blocks))
        {
            foreach (var container in paragraph.Inlines.OfType<InlineUIContainer>().ToArray())
            {
                if (container.Child is not CheckBox oldBox) continue;
                container.Child = CreateMark(paragraph, oldBox.IsChecked == true);
            }
        }
    }

    private void RestoreChecklists()
    {
        var states = data.Checklists
            .Where(item => item.ParagraphIndex >= 0)
            .GroupBy(item => item.ParagraphIndex)
            .ToDictionary(group => group.Key, group => group.Last());
        var paragraphs = AllParagraphs(editor.Document.Blocks).ToArray();
        for (var index = 0; index < paragraphs.Length; index++)
        {
            if (!states.TryGetValue(index, out var state)) continue;
            var paragraph = paragraphs[index];
            var first = paragraph.Inlines.FirstInline;
            // WPF serializes an InlineUIContainer as a single blank Run. Remove
            // that placeholder before putting the interactive checkbox back.
            if (first is Run { Text: { } placeholder } && string.IsNullOrWhiteSpace(placeholder))
                paragraph.Inlines.Remove(first);
            first = paragraph.Inlines.FirstInline;
            var container = new InlineUIContainer(CreateMark(paragraph, state.IsChecked))
            {
                BaselineAlignment = BaselineAlignment.Center
            };
            if (first is null)
            {
                paragraph.Inlines.Add(container);
                paragraph.Inlines.Add(new Run(" "));
            }
            else paragraph.Inlines.InsertBefore(first, container);
        }
    }

    private void CaptureChecklistState()
    {
        data.Checklists = AllParagraphs(editor.Document.Blocks)
            .Select((paragraph, index) => new { index, Mark = ChecklistOf(paragraph)?.Child as ChecklistMark })
            .Where(item => item.Mark is not null)
            .Select(item => new NotesChecklistState { ParagraphIndex = item.index, IsChecked = item.Mark!.IsChecked })
            .ToList();
    }

    // ---------------------------------------------------------------- colour

    private void BuildSwatches()
    {
        if (colorRow is null) return;
        colorRow.Children.Clear();
        swatches.Clear();
        var chosen = NoteSkin.For(data.Color);
        foreach (var preset in NoteSkin.Palette)
        {
            var swatch = Swatch(NoteSkin.Resolve(preset.Key), preset.Name, chosen.Hex);
            swatch.Click += (_, _) => SetColor(preset.Key);
            colorRow.Children.Add(swatch);
            swatches.Add(swatch);
        }
        // The current colour is shown too when it is not one of the presets.
        bool custom = NoteSkin.Palette.All(preset => !string.Equals(NoteSkin.ToHex(NoteSkin.Resolve(preset.Key)), chosen.Hex, StringComparison.OrdinalIgnoreCase));
        var picker = Swatch(custom ? chosen.Paper : Colors.Transparent, "Color a medida", custom ? chosen.Hex : "");
        picker.Content = custom ? "" : "+";
        picker.FontSize = 12;
        picker.FontWeight = FontWeights.Bold;
        picker.Click += (_, _) => PickColor();
        colorRow.Children.Add(picker);
        swatches.Add(picker);
    }

    private Button Swatch(Color color, string tip, string selectedHex)
    {
        bool selected = selectedHex.Length > 0 && string.Equals(NoteSkin.ToHex(color), selectedHex, StringComparison.OrdinalIgnoreCase);
        return new Button
        {
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            Margin = new Thickness(3, 0, 0, 0),
            Background = color == Colors.Transparent ? Brushes.Transparent : new SolidColorBrush(color),
            BorderThickness = new Thickness(selected ? 2.5 : 1),
            ToolTip = tip
        };
    }

    private void PickColor()
    {
        var picked = NoteSkin.Pick(owner, data.Color);
        if (picked is null) return;
        SetColor(picked);
    }

    private void SetColor(string value)
    {
        data.Color = value;
        ApplySkin();
        BuildSwatches();
        QueueSave();
    }

    /// <summary>Agent path: append plain text and flush so the open editor never overwrites it.</summary>
    internal bool AgentAppend(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        editor.Document.Blocks.Add(new Paragraph(new Run(text.Trim())));
        QueueSave();
        FlushNow();
        return true;
    }

    /// <summary>Agent path: change post-it colour (preset key or #hex) and flush.</summary>
    internal bool AgentSetColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string color = value.Trim();
        if (color.Equals("lavender", StringComparison.OrdinalIgnoreCase)) color = "lilac";
        SetColor(color);
        FlushNow();
        return true;
    }

    private void FlushNow()
    {
        saveTimer.Stop();
        data.DocumentXaml = SaveDocument();
        data.UpdatedUtc = DateTime.UtcNow;
        config.Value = JsonSerializer.Serialize(data);
        owner.SaveState();
        archive.Track(config);
        UpdateStatus();
    }

    /// <summary>One colour drives the note, its toolbar, its footer and the card around it.</summary>
    private void ApplySkin()
    {
        var skin = NoteSkin.For(data.Color);
        paper.Background = skin.PaperBrush;
        paper.BorderBrush = skin.LineBrush;
        editor.Background = skin.PaperBrush;
        editor.Foreground = skin.InkBrush;
        editor.CaretBrush = skin.InkBrush;
        editor.SelectionBrush = skin.SelectionBrush;
        if (toolbar is not null) toolbar.BorderBrush = skin.LineBrush;
        foreach (var button in toolButtons)
        {
            button.Background = skin.ChromeBrush;
            button.Foreground = skin.InkBrush;
            button.BorderBrush = skin.LineBrush;
        }
        foreach (var swatch in swatches) swatch.BorderBrush = skin.LineBrush;
        foreach (var mark in DocumentMarks()) PaintMark(mark, skin);
        status.Foreground = skin.MutedBrush;
        card?.ApplySkin(skin.ChromeBrush, skin.InkBrush, skin.LineBrush);
    }

    // ---------------------------------------------------------------- storage

    private void QueueSave()
    {
        if (loading) return;
        CaptureChecklistState();
        data.DocumentXaml = SaveDocument();
        data.UpdatedUtc = DateTime.UtcNow;
        config.Value = JsonSerializer.Serialize(data);
        UpdateStatus("EDITANDO");
        saveTimer.Stop();
        saveTimer.Start();
    }

    private string SaveDocument()
    {
        using var stream = new MemoryStream();
        new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Save(stream, DataFormats.Xaml);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void LoadDocument(string? legacyText)
    {
        if (!string.IsNullOrWhiteSpace(data.DocumentXaml))
        {
            try
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(data.DocumentXaml));
                new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Load(stream, DataFormats.Xaml);
                return;
            }
            catch (Exception) { /* A damaged rich note falls back to its readable source. */ }
        }
        editor.Document.Blocks.Clear();
        editor.Document.Blocks.Add(new Paragraph(new Run(legacyText ?? "")));
    }

    private void UpdateStatus(string state = "LISTO")
    {
        var text = new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text.TrimEnd('\r', '\n');
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        status.Text = L.T("notes.status", words.ToString("00", System.Globalization.CultureInfo.InvariantCulture), text.Length.ToString("000", System.Globalization.CultureInfo.InvariantCulture), state);
    }

    private static NotesWidgetData ReadData(string value, out string? legacyText)
    {
        legacyText = null;
        if (!string.IsNullOrWhiteSpace(value))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<NotesWidgetData>(value);
                if (parsed is not null && parsed.Version > 0)
                {
                    parsed.Checklists ??= [];
                    return parsed;
                }
            }
            catch (JsonException) { }
            legacyText = value;
        }
        return new NotesWidgetData();
    }

    // ---------------------------------------------------------------- self test

    internal void SelectAllForDiagnostics() => editor.SelectAll();
    internal void ToggleStrikeForDiagnostics() => ToggleStrikethrough();
    internal bool SelectionIsStruckForDiagnostics => IsStruck(editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection);
    internal void AddChecklistToFirstLineForDiagnostics()
    {
        var start = editor.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        editor.Selection.Select(start, start);
        ToggleChecklist();
    }
    internal Button? FirstChecklistBoxForDiagnostics => DocumentMarks().FirstOrDefault();
    internal void FlushForDiagnostics()
    {
        saveTimer.Stop();
        owner.SaveState();
        archive.Track(config);
    }
}
