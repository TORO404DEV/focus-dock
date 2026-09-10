using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
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
    private readonly MainWindow owner;
    private readonly WidgetConfig config;
    private readonly WidgetCard? card;
    private readonly List<Button> toolButtons = [];
    private readonly List<Button> swatches = [];
    private readonly List<CheckBox> checklistBoxes = [];
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
            UpdateStatus("GUARDADO");
        };

        loading = true;
        LoadDocument(legacyText);
        ApplySkin();
        RestoreChecklists();
        WireChecklistBoxes();
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
        };
        UpdateStatus();
    }

    private Border BuildToolbar()
    {
        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        tools.Children.Add(CommandButton("B", "Negrita · Ctrl+B", EditingCommands.ToggleBold, FontWeights.Black));
        tools.Children.Add(CommandButton("I", "Cursiva · Ctrl+I", EditingCommands.ToggleItalic, fontStyle: FontStyles.Italic));
        tools.Children.Add(CommandButton("U", "Subrayado · Ctrl+U", EditingCommands.ToggleUnderline));
        tools.Children.Add(ActionButton("S", "Tachado", ToggleStrikethrough));
        tools.Children.Add(ActionButton("☐", "Añadir o quitar checklist · Ctrl+Shift+C", ToggleChecklist));
        tools.Children.Add(CommandButton("•", "Lista con viñetas", EditingCommands.ToggleBullets));
        tools.Children.Add(CommandButton("1.", "Lista numerada", EditingCommands.ToggleNumbering));
        tools.Children.Add(ActionButton("Aa", "Quitar formato", () => editor.Selection.ClearAllProperties(), 32));
        tools.Children.Add(ActionButton("＋", "Insertar fecha y hora", InsertTimestamp));
        tools.Children.Add(CommandButton("↶", "Deshacer · Ctrl+Z", ApplicationCommands.Undo));
        tools.Children.Add(CommandButton("↷", "Rehacer · Ctrl+Y", ApplicationCommands.Redo));
        return new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["Line"],
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = tools
        };
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
        if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            ToggleChecklist();
            e.Handled = true;
        }
    }

    private void ToggleStrikethrough()
    {
        var current = editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
        editor.Selection.ApplyPropertyValue(
            Inline.TextDecorationsProperty,
            current == DependencyProperty.UnsetValue || current is null ? TextDecorations.Strikethrough : null);
    }

    private void InsertTimestamp()
    {
        editor.CaretPosition.InsertTextInRun(DateTime.Now.ToString("dd MMM yyyy · HH:mm"));
        QueueSave();
    }

    private void ToggleChecklist()
    {
        var paragraphs = SelectedParagraphs().ToArray();
        if (paragraphs.Length == 0 && editor.CaretPosition.Paragraph is { } current) paragraphs = [current];
        foreach (var paragraph in paragraphs)
        {
            var existing = paragraph.Inlines.OfType<InlineUIContainer>()
                .FirstOrDefault(container => container.Child is CheckBox);
            if (existing is not null)
            {
                var next = existing.NextInline;
                paragraph.Inlines.Remove(existing);
                if (next is Run { Text: " " }) paragraph.Inlines.Remove(next);
                paragraph.TextDecorations = null;
                continue;
            }

            var box = CreateChecklistBox(paragraph, false);
            var container = new InlineUIContainer(box) { BaselineAlignment = BaselineAlignment.Center };
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
        }
        QueueSave();
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

    private CheckBox CreateChecklistBox(Paragraph paragraph, bool isChecked)
    {
        var box = new CheckBox
        {
            IsChecked = isChecked,
            Margin = new Thickness(0, 0, 2, -1),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = isChecked ? "Marcar pendiente" : "Marcar completado",
            Foreground = NoteSkin.For(data.Color).InkBrush
        };
        checklistBoxes.Add(box);
        box.Checked += (_, _) => SetChecklistState(paragraph, box, true);
        box.Unchecked += (_, _) => SetChecklistState(paragraph, box, false);
        return box;
    }

    private void SetChecklistState(Paragraph paragraph, CheckBox box, bool isChecked)
    {
        paragraph.TextDecorations = isChecked ? TextDecorations.Strikethrough : null;
        box.ToolTip = isChecked ? "Marcar pendiente" : "Marcar completado";
        QueueSave();
    }

    private void WireChecklistBoxes()
    {
        foreach (var paragraph in AllParagraphs(editor.Document.Blocks))
        {
            foreach (var container in paragraph.Inlines.OfType<InlineUIContainer>().ToArray())
            {
                if (container.Child is not CheckBox oldBox) continue;
                var replacement = CreateChecklistBox(paragraph, oldBox.IsChecked == true);
                container.Child = replacement;
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
            var container = new InlineUIContainer(CreateChecklistBox(paragraph, state.IsChecked))
            {
                BaselineAlignment = BaselineAlignment.Center
            };
            if (first is null)
            {
                paragraph.Inlines.Add(container);
                paragraph.Inlines.Add(new Run(" "));
            }
            else paragraph.Inlines.InsertBefore(first, container);
            paragraph.TextDecorations = state.IsChecked ? TextDecorations.Strikethrough : null;
        }
    }

    private void CaptureChecklistState()
    {
        data.Checklists = AllParagraphs(editor.Document.Blocks)
            .Select((paragraph, index) => new { paragraph, index })
            .Select(item => new
            {
                item.index,
                Box = item.paragraph.Inlines.OfType<InlineUIContainer>()
                    .Select(container => container.Child)
                    .OfType<CheckBox>()
                    .FirstOrDefault()
            })
            .Where(item => item.Box is not null)
            .Select(item => new NotesChecklistState
            {
                ParagraphIndex = item.index,
                IsChecked = item.Box!.IsChecked == true
            })
            .ToList();
    }

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
        foreach (var box in checklistBoxes) box.Foreground = skin.InkBrush;
        status.Foreground = skin.MutedBrush;
        card?.ApplySkin(skin.ChromeBrush, skin.InkBrush, skin.LineBrush);
    }

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
        status.Text = $"{words:00} PAL · {text.Length:000} CAR · {state}";
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

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }
}
