using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// The note history behind a note's ⋯ button: every note the workspace has held, the closed ones
/// included. It is searchable, copies any note's text, and puts a closed note back on the page.
/// </summary>
internal static class NotesHistory
{
    public static void Show(MainWindow owner, Guid current) => Build(owner, current).ShowDialog();

    /// <summary>Builds the history window without showing it, so the self test can photograph it.</summary>
    internal static Window Build(MainWindow owner, Guid current)
    {
        var archive = NoteArchiveStore.For(owner.Store);
        archive.Sync(owner.Settings);

        var window = Dialogs.Window(owner, "HISTORIAL DE NOTAS", 580, 660);
        window.MinWidth = 440; window.MinHeight = 420;

        var root = new Grid { Margin = new Thickness(22, 18, 22, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        window.Content = root;

        var intro = new StackPanel();
        intro.Children.Add(Dialogs.Heading("TUS NOTAS"));
        intro.Children.Add(new TextBlock
        {
            Text = "Todo lo que escribiste, también en las notas que cerraste. Reabre una en esta página o copia su texto.",
            FontSize = 11, Foreground = Resource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -8, 0, 12)
        });
        root.Children.Add(intro);

        var search = new TextBox { Height = 32, FontSize = 12, Padding = new Thickness(9, 0, 9, 0), Margin = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center };
        search.SetValue(AutomationProperties.NameProperty, "Buscar en las notas");
        var searchField = AgendaVisuals.WithHint(search, "Buscar en todas las notas");
        searchField.Margin = new Thickness(0, 0, 0, 10);
        Grid.SetRow(searchField, 1);
        root.Children.Add(searchField);

        var list = new ListBox
        {
            BorderThickness = new Thickness(1), Padding = new Thickness(6), SelectionMode = SelectionMode.Single,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 0, 0, 6)));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        var selectedTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, Resource("Ink")));
        selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, Resource("Accent")));
        itemStyle.Triggers.Add(selectedTrigger);
        list.ItemContainerStyle = itemStyle;
        Grid.SetRow(list, 2);
        root.Children.Add(list);

        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var actions = new WrapPanel();
        var reopen = new Button { Content = "REABRIR AQUÍ", FontSize = 11, Padding = new Thickness(12, 8, 12, 8), Background = Resource("Ink"), Foreground = Resource("Paper"), ToolTip = "Poner la nota cerrada de nuevo en esta página" };
        var copy = new Button { Content = "COPIAR TEXTO", FontSize = 11, Padding = new Thickness(12, 8, 12, 8) };
        var forget = new Button { Content = "ELIMINAR", FontSize = 11, Padding = new Thickness(12, 8, 12, 8), ToolTip = "Borrar la nota cerrada del historial para siempre" };
        actions.Children.Add(reopen); actions.Children.Add(copy); actions.Children.Add(forget);
        footer.Children.Add(actions);
        var close = new Button { Content = "CERRAR", FontSize = 11, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0), IsCancel = true };
        close.Click += (_, _) => window.Close();
        Grid.SetColumn(close, 1);
        footer.Children.Add(close);
        var status = new TextBlock { FontSize = 10, Foreground = Resource("Muted"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        var bottom = new StackPanel();
        bottom.Children.Add(footer);
        bottom.Children.Add(status);
        Grid.SetRow(bottom, 3);
        root.Children.Add(bottom);

        ArchivedNote? Selected() => (list.SelectedItem as FrameworkElement)?.Tag as ArchivedNote;

        void Refresh(Guid? keep)
        {
            var pages = PageNames(owner.Settings);
            var notes = archive.Archive.Search(search.Text);
            list.Items.Clear();
            foreach (var note in notes)
            {
                var row = Row(note, note.Id == current, pages.TryGetValue(note.Id, out var page) ? page : null);
                list.Items.Add(row);
                if (note.Id == keep) list.SelectedItem = row;
            }
            if (notes.Count == 0)
                status.Text = search.Text.Trim().Length > 0 ? "Ninguna nota contiene eso." : "Todavía no hay notas en el historial.";
            else
                status.Text = $"{notes.Count(note => note.IsOpen):00} EN PÁGINAS · {notes.Count(note => !note.IsOpen):00} CERRADAS";
            if (list.SelectedItem is null && list.Items.Count > 0) list.SelectedIndex = 0;
            UpdateActions();
        }

        void UpdateActions()
        {
            var note = Selected();
            reopen.IsEnabled = note is { IsOpen: false };
            forget.IsEnabled = note is { IsOpen: false };
            copy.IsEnabled = note is not null;
        }

        void Reopen()
        {
            if (Selected() is not { IsOpen: false } note) return;
            if (NotesHistory.Reopen(owner, note) is null) { Refresh(note.Id); return; }
            window.Close();
        }

        list.SelectionChanged += (_, _) => UpdateActions();
        list.MouseDoubleClick += (_, _) => Reopen();
        list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Reopen(); e.Handled = true; } };
        search.TextChanged += (_, _) => Refresh(Selected()?.Id);
        reopen.Click += (_, _) => Reopen();
        copy.Click += (_, _) =>
        {
            if (Selected() is not { } note) return;
            try { Clipboard.SetText(note.Text); status.Text = "TEXTO COPIADO AL PORTAPAPELES."; }
            catch (Exception) { status.Text = "El portapapeles está ocupado por otra aplicación. Inténtalo de nuevo."; }
        };
        forget.Click += (_, _) =>
        {
            if (Selected() is not { IsOpen: false } note) return;
            if (Dialogs.Choose(window, "ELIMINAR DEL HISTORIAL", ["Eliminar para siempre", "Cancelar"]) != 0) return;
            archive.Forget(note.Id);
            Refresh(null);
        };

        Refresh(current);
        window.Loaded += (_, _) => { Dialogs.Modalize(window); search.Focus(); };
        return window;
    }

    /// <summary>
    /// Puts a closed note back on the current page, under its own identity, so its history entry
    /// simply becomes open again. A note is only ever on one page at a time.
    /// </summary>
    internal static WidgetCard? Reopen(MainWindow owner, ArchivedNote note)
    {
        if (note.IsOpen || owner.Settings.WorkspacePages.SelectMany(page => page.Widgets).Any(widget => widget.Id == note.Id)) return null;
        var card = owner.AddCard(new WidgetConfig { Id = note.Id, Kind = "notes", Title = "NOTAS", Value = note.Payload }, true);
        owner.Status($"NOTA REABIERTA · {note.Title}");
        return card;
    }

    /// <summary>Which page holds each open note, by its widget id.</summary>
    private static Dictionary<Guid, string> PageNames(Settings settings)
    {
        var names = new Dictionary<Guid, string>();
        foreach (var page in settings.WorkspacePages)
            foreach (var widget in page.Widgets.Where(widget => widget.Kind == "notes"))
                names[widget.Id] = page.Name;
        return names;
    }

    private static Border Row(ArchivedNote note, bool isCurrent, string? page)
    {
        var skin = NoteSkin.For(note.Color);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new Border
        {
            Width = 12, Margin = new Thickness(0, 0, 11, 0), Background = skin.PaperBrush,
            BorderBrush = Resource("Line"), BorderThickness = new Thickness(1)
        });

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = note.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        if (note.Body.Length > 0)
            text.Children.Add(new TextBlock { Text = note.Body, FontSize = 11, Foreground = Resource("Muted"), TextWrapping = TextWrapping.Wrap, MaxHeight = 32, TextTrimming = TextTrimming.WordEllipsis, Margin = new Thickness(0, 2, 0, 0) });

        string when = (note.ClosedUtc ?? note.UpdatedUtc).ToLocalTime().ToString("dd MMM · HH:mm", AgendaVisuals.Spanish).ToUpper(AgendaVisuals.Spanish).Replace(".", "");
        string where = note.IsOpen
            ? (isCurrent ? "ESTA NOTA" : page is null ? "EN UNA PÁGINA" : $"EN {page}") + $"  ·  EDITADA {when}"
            : $"CERRADA {when}";
        text.Children.Add(new TextBlock
        {
            Text = $"{where}  ·  {note.Words} PALABRA{(note.Words == 1 ? "" : "S")}",
            FontFamily = new FontFamily("Consolas"), FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = note.IsOpen ? Resource("Ink") : Resource("Muted"), Margin = new Thickness(0, 5, 0, 0)
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        return new Border
        {
            Child = grid, Tag = note, Padding = new Thickness(10, 9, 10, 9),
            BorderBrush = AgendaVisuals.Fade("Line", 70), BorderThickness = new Thickness(1),
            Background = Resource("Surface"), Opacity = note.IsOpen ? 1 : 0.85
        };
    }

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
}
