using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// A post-it for one To Do row: the task title is the note, and the colour chosen here
/// paints that row in the list.
/// </summary>
internal sealed class TodoNoteWindow : Window
{
    private static readonly Dictionary<Guid, TodoNoteWindow> Open = [];

    private readonly MainWindow owner;
    private readonly TodoStore store;
    private readonly TodoTask task;
    private readonly RichTextBox editor;
    private readonly Border paper;
    private readonly Border chrome;
    private readonly DockPanel header;
    private readonly TextBlock heading;
    private readonly Button close;
    private readonly StackPanel colorRow;
    private readonly List<Button> swatches = [];
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly IDisposable overlayPause;
    private bool loading = true;

    public static void OpenFor(MainWindow owner, TodoStore store, TodoTask task)
    {
        if (Open.TryGetValue(task.Id, out var existing))
        {
            existing.BringNoteForward();
            return;
        }
        var window = new TodoNoteWindow(owner, store, task);
        Open[task.Id] = window;
        window.PlaceNearOwner();
        window.Show();
        window.BringNoteForward();
    }

    private TodoNoteWindow(MainWindow owner, TodoStore store, TodoTask task)
    {
        this.owner = owner;
        this.store = store;
        this.task = task;

        Owner = owner;
        Title = "POMODOCK / TO DO";
        Width = 380;
        Height = 460;
        MinWidth = 280;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowActivated = true;
        Dialogs.KeepAboveChrome(this);
        overlayPause = owner.PauseOverlayLift();

        heading = new TextBlock
        {
            Text = HeadingText(),
            FontWeight = FontWeights.Bold,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        close = new Button
        {
            Content = "×", Padding = new Thickness(14, 2, 14, 2), Margin = new Thickness(0),
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontSize = 18
        };
        close.Click += (_, _) => Close();
        header = new DockPanel { LastChildFill = true, Cursor = Cursors.SizeAll, Height = 42 };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(heading);
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not Button) DragMove();
        };

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
        paper = new Border { BorderThickness = new Thickness(1), Child = editor };

        colorRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 8, 12, 10),
            ToolTip = "Color del post-it"
        };

        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.Children.Add(header);
        Grid.SetRow(paper, 1);
        body.Children.Add(paper);
        Grid.SetRow(colorRow, 2);
        body.Children.Add(colorRow);

        chrome = new Border { BorderThickness = new Thickness(1), Child = body };
        Content = chrome;

        LoadDocument();
        BuildSwatches();
        ApplySkin();
        loading = false;

        editor.TextChanged += (_, _) => QueueSave();
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Flush(); };
        store.Changed += OnStoreChanged;
        Closed += (_, _) =>
        {
            store.Changed -= OnStoreChanged;
            saveTimer.Stop();
            Flush();
            overlayPause.Dispose();
            Open.Remove(task.Id);
        };
        Loaded += (_, _) =>
        {
            BringNoteForward();
            editor.Focus();
        };
    }

    /// <summary>Cascade from the top of the dock so a new note is not born under the To Do card.</summary>
    private void PlaceNearOwner()
    {
        if (Owner is not Window host) return;
        int stack = Math.Max(0, Open.Count - 1);
        Left = host.Left + 28 + stack * 22;
        Top = host.Top + 28 + stack * 22;
    }

    /// <summary>
    /// Widget cards live in HWND_TOPMOST popups. PauseOverlayLift stops them from
    /// climbing over this note; LiftAboveChrome then parks the note at the top of that band.
    /// </summary>
    private void BringNoteForward()
    {
        Topmost = true;
        Activate();
        LiftZ();
        Dispatcher.BeginInvoke(LiftZ, DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(LiftZ, DispatcherPriority.Input);
        Dispatcher.BeginInvoke(LiftZ, DispatcherPriority.ContextIdle);
    }

    private void LiftZ()
    {
        Topmost = true;
        Dialogs.LiftAboveChrome(this);
        Activate();
    }

    private void OnStoreChanged()
    {
        if (!store.Book.Items.Any(item => item.Id == task.Id))
            Dispatcher.BeginInvoke(Close);
    }

    private string HeadingText()
    {
        string label = task.ListLabel();
        return label.Length == 0 ? "TO DO" : label.ToUpper(Strings.Culture);
    }

    private void LoadDocument()
    {
        if (!string.IsNullOrWhiteSpace(task.DocumentXaml))
        {
            try
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(task.DocumentXaml));
                new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Load(stream, DataFormats.Xaml);
                return;
            }
            catch (Exception) { /* damaged rich text falls back to the title */ }
        }
        editor.Document.Blocks.Clear();
        editor.Document.Blocks.Add(new Paragraph(new Run(task.Title ?? "")));
    }

    private void QueueSave()
    {
        if (loading) return;
        heading.Text = HeadingTextFromEditor();
        saveTimer.Stop();
        saveTimer.Start();
    }

    private void Flush()
    {
        string text = new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text.TrimEnd('\r', '\n').Trim();
        if (text.Length > 0) task.Title = text;
        using var stream = new MemoryStream();
        new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Save(stream, DataFormats.Xaml);
        task.DocumentXaml = Encoding.UTF8.GetString(stream.ToArray());
        heading.Text = HeadingText();
        store.Save();
    }

    private string HeadingTextFromEditor()
    {
        string text = new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text.Replace('\r', '\n');
        int cut = text.IndexOf('\n');
        string line = (cut < 0 ? text : text[..cut]).Trim();
        return line.Length == 0 ? "TO DO" : line.ToUpper(Strings.Culture);
    }

    private void BuildSwatches()
    {
        colorRow.Children.Clear();
        swatches.Clear();
        string current = string.IsNullOrWhiteSpace(task.Color) ? "paper" : task.Color;
        var chosen = NoteSkin.For(current);
        foreach (var preset in NoteSkin.Palette)
        {
            var swatch = Swatch(NoteSkin.Resolve(preset.Key), preset.Name, chosen.Hex);
            swatch.Click += (_, _) => SetColor(preset.Key);
            colorRow.Children.Add(swatch);
            swatches.Add(swatch);
        }
        bool custom = NoteSkin.Palette.All(preset =>
            !string.Equals(NoteSkin.ToHex(NoteSkin.Resolve(preset.Key)), chosen.Hex, StringComparison.OrdinalIgnoreCase));
        var picker = Swatch(custom ? chosen.Paper : Colors.Transparent, "Color a medida", custom ? chosen.Hex : "");
        picker.Content = custom ? "" : "+";
        picker.FontSize = 12;
        picker.FontWeight = FontWeights.Bold;
        picker.Click += (_, _) => PickColor();
        colorRow.Children.Add(picker);
        swatches.Add(picker);
    }

    private static Button Swatch(System.Windows.Media.Color color, string tip, string selectedHex)
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
        string current = string.IsNullOrWhiteSpace(task.Color) ? "paper" : task.Color;
        string? picked = NoteSkin.Pick(this, current);
        if (picked is null) return;
        SetColor(picked);
    }

    private void SetColor(string value)
    {
        task.Color = value;
        ApplySkin();
        BuildSwatches();
        store.Save();
    }

    private void ApplySkin()
    {
        var skin = NoteSkin.For(string.IsNullOrWhiteSpace(task.Color) ? "paper" : task.Color);
        chrome.Background = skin.PaperBrush;
        chrome.BorderBrush = skin.LineBrush;
        header.Background = skin.ChromeBrush;
        heading.Foreground = skin.InkBrush;
        close.Foreground = skin.InkBrush;
        paper.Background = skin.PaperBrush;
        paper.BorderBrush = skin.LineBrush;
        editor.Background = skin.PaperBrush;
        editor.Foreground = skin.InkBrush;
        editor.CaretBrush = skin.InkBrush;
        editor.SelectionBrush = skin.SelectionBrush;
        foreach (var swatch in swatches) swatch.BorderBrush = skin.LineBrush;
    }
}
