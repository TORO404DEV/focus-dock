using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Windows.Controls.Primitives;
using FocusDock.App.Native;

namespace FocusDock.App;

internal static class Dialogs
{
    public static Window Window(Window? owner, string title, double width = 500, double height = 420) => new()
    { Owner = owner, Title = "FOCUS DOCK / " + title, Width = width, Height = height, MinWidth = 360, MinHeight = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.CanResizeWithGrip };
    public static void Modalize(Window window)
    {
        if (window.Content is not FrameworkElement content || content is ModalSurface) return;
        window.Content = null;
        var frame = new ModalSurface(); frame.SetResourceReference(Border.BackgroundProperty, "Surface"); frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        var root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) }); root.RowDefinitions.Add(new RowDefinition());
        var header = new DockPanel { Background = (Brush)Application.Current.Resources["Ink"], LastChildFill = true, Cursor = Cursors.SizeAll };
        var close = new Button { Content = "×", Padding = new Thickness(14, 2, 14, 2), Margin = new Thickness(0), BorderThickness = new Thickness(0), Foreground = (Brush)Application.Current.Resources["Paper"], Background = Brushes.Transparent, FontSize = 18 }; DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        var heading = new TextBlock { Text = window.Title, Foreground = (Brush)Application.Current.Resources["Paper"], FontWeight = FontWeights.Bold, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) }; header.Children.Add(heading);
        header.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not System.Windows.Controls.Button) window.DragMove(); }; close.Click += (_, _) => window.Close();
        root.Children.Add(header); Grid.SetRow(content, 1); root.Children.Add(content); frame.Child = root; window.Content = frame;
    }
    public static void Alert(Window? owner, string title, string message)
    {
        var window = Window(owner ?? Application.Current.MainWindow!, title, 520, 290);
        var stack = new StackPanel { Margin = new Thickness(24) }; stack.Children.Add(Heading(title)); stack.Children.Add(new TextBlock { Text = message, FontSize = 13, Margin = new Thickness(0, 0, 0, 22) }); stack.Children.Add(Button("OK", () => window.DialogResult = true)); window.Content = stack;
        window.Loaded += (_, _) => Modalize(window); window.ShowDialog();
    }
    public static TextBlock Heading(string text) => new() { Text = text, FontSize = 24, FontWeight = FontWeights.Black, Margin = new Thickness(0, 0, 0, 16) };
    public static Button Button(string text, Action action)
    { var button = new Button { Content = text }; button.Click += (_, _) => action(); return button; }
    public static string? Prompt(Window owner, string title, string label, string value = "")
    {
        var window = Window(owner, title, 470, 260);
        var stack = new StackPanel { Margin = new Thickness(22) }; window.Content = stack;
        stack.Children.Add(Heading(title)); stack.Children.Add(new TextBlock { Text = label });
        var input = new TextBox { Text = value }; stack.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var save = Button("GUARDAR", () => window.DialogResult = true); save.IsDefault = true;
        buttons.Children.Add(save); var cancel = Button("CANCELAR", () => window.DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel); stack.Children.Add(buttons);
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        window.Loaded += (_, _) => Modalize(window); return window.ShowDialog() == true ? input.Text.Trim() : null;
    }
    public static int Choose(Window owner, string title, string[] options)
    {
        var window = Window(owner, title, 480, 190 + options.Length * 46);
        var stack = new StackPanel { Margin = new Thickness(22) }; window.Content = stack; stack.Children.Add(Heading(title));
        int selected = -1;
        for (int i = 0; i < options.Length; i++)
        { int index = i; var b = Button(options[i], () => { selected = index; window.DialogResult = true; }); b.Margin = new Thickness(0, 0, 0, 8); stack.Children.Add(b); }
        window.Loaded += (_, _) => Modalize(window); window.ShowDialog(); return selected;
    }
    public static int ChooseWidget(Window owner)
    {
        var window = Window(owner, "AÑADIR WIDGET", 700, 540); window.MinWidth = 600; window.MinHeight = 460;
        var root = new Grid { Margin = new Thickness(28, 22, 28, 24) }; root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); window.Content = root;
        var intro = new StackPanel(); intro.Children.Add(Heading("AÑADIR WIDGET")); intro.Children.Add(new TextBlock { Text = "Elige qué quieres tener a la vista en tu monitor.", FontSize = 12, Foreground = (Brush)Application.Current.Resources["Muted"], Margin = new Thickness(0, -8, 0, 20) }); Grid.SetRow(intro, 0); root.Children.Add(intro);
        var guide = new TextBlock { Text = "LANZADOR DE ESPACIO   ·   4 OPCIONES", FontFamily = new FontFamily("Consolas"), FontSize = 10, Foreground = (Brush)Application.Current.Resources["Muted"], Margin = new Thickness(0, 0, 0, 8) }; Grid.SetRow(guide, 1); root.Children.Add(guide);
        var launcher = new UniformGrid { Columns = 2, Rows = 2 };
        var entries = new[]
        {
            ("▣", "VENTANA DE OTRA APP", "Telegram, Brave, Spotify y cualquier ventana abierta.", "1"),
            ("◎", "PÁGINA WEB / YOUTUBE", "Un panel web para música, dashboards o referencias.", "2"),
            ("✎", "NOTAS", "Ideas, checklist y texto rápido junto al temporizador.", "3"),
            ("◒", "MÉTRICAS DE ENFOQUE", "Minutos, racha diaria, nivel y progreso semanal.", "4")
        };
        int selected = -1;
        for (int i = 0; i < entries.Length; i++)
        {
            var entry = entries[i]; int index = i;
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
            content.Children.Add(new TextBlock { Text = entry.Item1, FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 34, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
            content.Children.Add(new TextBlock { Text = entry.Item2, FontSize = 13, FontWeight = FontWeights.Black, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = entry.Item3, FontSize = 10, Foreground = (Brush)Application.Current.Resources["Muted"], Margin = new Thickness(0, 7, 0, 0), TextWrapping = TextWrapping.Wrap });
            var launch = new Button { Content = content, Tag = entry.Item4, MinHeight = 142, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(16), HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top, Background = (Brush)Application.Current.Resources["Surface"] };
            launch.SetValue(AutomationProperties.NameProperty, entry.Item2); launch.Click += (_, _) => { selected = index; window.DialogResult = true; }; launcher.Children.Add(launch);
        }
        Grid.SetRow(launcher, 2); root.Children.Add(launcher);
        window.PreviewKeyDown += (_, e) => { if (e.Key is >= Key.D1 and <= Key.D4) { selected = (int)e.Key - (int)Key.D1; window.DialogResult = true; e.Handled = true; } };
        window.Loaded += (_, _) => { Modalize(window); Keyboard.Focus(launcher.Children[0]); }; window.ShowDialog(); return selected;
    }
    public static WindowCandidate? PickWindow(Window owner)
    {
        var window = Window(owner, "INCRUSTAR VENTANA", 760, 640);
        window.MinWidth = 600; window.MinHeight = 500;
        TextBox filter = null!; ComboBox category = null!; Button refreshButton = null!; TextBlock summary = null!; ListBox list = null!; Button embed = null!;
        var candidates = new List<WindowCandidate>();
        var grid = new Grid { Margin = new Thickness(24, 20, 24, 20) }; window.Content = grid;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var intro = new StackPanel();
        intro.Children.Add(Heading("ELIGE UNA VENTANA"));
        intro.Children.Add(new TextBlock { Text = "Convierte cualquier app abierta en un widget de tu espacio.", Foreground = (Brush)Application.Current.Resources["Muted"], FontSize = 12, Margin = new Thickness(0, -8, 0, 14) });
        Grid.SetRow(intro, 0); grid.Children.Add(intro);

        var toolbar = new Grid(); toolbar.ColumnDefinitions.Add(new ColumnDefinition()); toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filter = new TextBox { Text = "", ToolTip = "Buscar por título, aplicación o proceso", Margin = new Thickness(0, 0, 8, 8), MinHeight = 38 };
        filter.SetValue(AutomationProperties.NameProperty, "Buscar ventanas");
        filter.TextChanged += (_, _) => Refresh(); Grid.SetColumn(filter, 0); toolbar.Children.Add(filter);
        category = new ComboBox { ItemsSource = new[] { "Todas las apps", "Navegadores", "Mensajería", "Desarrollo", "Otras" }, SelectedIndex = 0, Width = 145, Margin = new Thickness(0, 4, 8, 10) };
        category.SelectionChanged += (_, _) => Refresh(); Grid.SetColumn(category, 1); toolbar.Children.Add(category);
        refreshButton = Button("↻ ACTUALIZAR", () => _ = LoadCandidates()); refreshButton.Padding = new Thickness(10, 8, 10, 8); Grid.SetColumn(refreshButton, 2); toolbar.Children.Add(refreshButton);
        Grid.SetRow(toolbar, 1); grid.Children.Add(toolbar);

        summary = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 10, Foreground = (Brush)Application.Current.Resources["Muted"], Margin = new Thickness(0, 0, 0, 7) }; Grid.SetRow(summary, 2); grid.Children.Add(summary);
        list = new ListBox { BorderThickness = new Thickness(1), Padding = new Thickness(6), SelectionMode = SelectionMode.Single, ItemTemplate = CandidateTemplate() };
        var selectedStyle = new Style(typeof(ListBoxItem)); selectedStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); selectedStyle.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 0, 0, 6))); selectedStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        var selectedTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true }; selectedTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, (Brush)Application.Current.Resources["Ink"])); selectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, (Brush)Application.Current.Resources["Accent"])); selectedStyle.Triggers.Add(selectedTrigger); list.ItemContainerStyle = selectedStyle;
        Grid.SetRow(list, 3); grid.Children.Add(list);

        var hint = new TextBlock { Text = "La ventana seguirá perteneciendo a su aplicación. Al liberarla volverá al escritorio. Para mejores resultados, usa apps con permisos de administrador iguales a Focus Dock.", FontSize = 10, Foreground = (Brush)Application.Current.Resources["Muted"], Margin = new Thickness(0, 12, 0, 12), TextWrapping = TextWrapping.Wrap }; Grid.SetRow(hint, 4); grid.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        WindowCandidate? selected = null;
        embed = Button("INCRUSTAR VENTANA", () => { if (list.SelectedItem is WindowCandidate c) { selected = c; window.DialogResult = true; } }); embed.IsDefault = true; embed.Padding = new Thickness(14, 9, 14, 9); buttons.Children.Add(embed);
        var cancel = Button("CANCELAR", () => window.Close()); cancel.IsCancel = true; buttons.Children.Add(cancel); Grid.SetRow(buttons, 5); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.Children.Add(buttons);

        static bool IsCategory(WindowCandidate candidate, string value)
        {
            var process = candidate.ProcessName.ToLowerInvariant();
            return value switch
            {
                "Navegadores" => process is "brave" or "chrome" or "msedge" or "firefox" or "opera",
                "Mensajería" => process.Contains("telegram") || process.Contains("whatsapp") || process.Contains("discord") || process.Contains("slack"),
                "Desarrollo" => process is "code" or "devenv" or "githubdesktop" or "notepad++" || process.Contains("studio"),
                "Otras" => !(process is "brave" or "chrome" or "msedge" or "firefox" or "opera") && !process.Contains("telegram") && !process.Contains("whatsapp") && !process.Contains("discord") && !process.Contains("slack") && process is not ("code" or "devenv" or "githubdesktop"),
                _ => true
            };
        }
        void Refresh()
        {
            var query = filter.Text.Trim(); var group = category.SelectedItem?.ToString() ?? "Todas las apps";
            var visible = candidates.Where(w => (string.IsNullOrWhiteSpace(query) || w.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)) && IsCategory(w, group)).ToList();
            list.ItemsSource = visible; summary.Text = $"{visible.Count:00} VENTANAS DISPONIBLES   ·   Selecciona una tarjeta para continuar"; embed.IsEnabled = visible.Count > 0;
        }
        async Task LoadCandidates()
        {
            refreshButton.IsEnabled = false; summary.Text = "BUSCANDO VENTANAS ABIERTAS…";
            try { candidates = await Task.Run(WindowLease.Candidates); Refresh(); }
            finally { refreshButton.IsEnabled = true; }
        }
        filter.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && list.Items.Count > 0) { list.SelectedIndex = 0; list.Focus(); e.Handled = true; } };
        Refresh(); window.Loaded += (_, _) => { Modalize(window); filter.Focus(); _ = LoadCandidates(); }; window.ShowDialog(); return selected;
    }
    private static DataTemplate CandidateTemplate()
    {
        var template = new DataTemplate(typeof(WindowCandidate));
        var card = new FrameworkElementFactory(typeof(Border)); card.SetValue(Border.BorderBrushProperty, (Brush)Application.Current.Resources["Line"]); card.SetValue(Border.BorderThicknessProperty, new Thickness(1)); card.SetValue(Border.BackgroundProperty, (Brush)Application.Current.Resources["Surface"]); card.SetValue(Border.PaddingProperty, new Thickness(10, 8, 10, 8));
        var row = new FrameworkElementFactory(typeof(StackPanel)); row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal); row.SetValue(StackPanel.MinHeightProperty, 52d);
        var icon = new FrameworkElementFactory(typeof(Border)); icon.SetValue(Border.WidthProperty, 36d); icon.SetValue(Border.HeightProperty, 36d); icon.SetValue(Border.BackgroundProperty, (Brush)Application.Current.Resources["Accent"]); icon.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Left); icon.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
        var image = new FrameworkElementFactory(typeof(Image)); image.SetValue(Image.WidthProperty, 26d); image.SetValue(Image.HeightProperty, 26d); image.SetValue(Image.StretchProperty, Stretch.Uniform); image.SetBinding(Image.SourceProperty, new Binding(nameof(WindowCandidate.Icon))); icon.AppendChild(image); row.AppendChild(icon);
        var text = new FrameworkElementFactory(typeof(StackPanel)); text.SetValue(StackPanel.MarginProperty, new Thickness(10, 0, 0, 0)); text.SetValue(FrameworkElement.WidthProperty, 590d);
        var title = new FrameworkElementFactory(typeof(TextBlock)); title.SetBinding(TextBlock.TextProperty, new Binding(nameof(WindowCandidate.Title))); title.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold); title.SetValue(TextBlock.FontSizeProperty, 13d); title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); text.AppendChild(title);
        var process = new FrameworkElementFactory(typeof(TextBlock)); process.SetBinding(TextBlock.TextProperty, new Binding(nameof(WindowCandidate.ProcessLabel))); process.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Consolas")); process.SetValue(TextBlock.FontSizeProperty, 10d); process.SetValue(TextBlock.ForegroundProperty, (Brush)Application.Current.Resources["Muted"]); text.AppendChild(process);
        row.AppendChild(text); card.AppendChild(row); template.VisualTree = card; return template;
    }
    private sealed class ModalSurface : Border { }
}
