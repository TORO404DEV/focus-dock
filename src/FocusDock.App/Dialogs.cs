using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    public static WindowCandidate? PickWindow(Window owner)
    {
        var window = Window(owner, "INCRUSTAR VENTANA", 620, 520);
        var grid = new Grid { Margin = new Thickness(22) }; window.Content = grid;
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) grid.RowDefinitions.Add(new() { Height = h });
        grid.Children.Add(Heading("ELIGE UNA VENTANA"));
        var filter = new TextBox { ToolTip = "Filtrar por nombre de ventana o aplicación" }; Grid.SetRow(filter, 1); grid.Children.Add(filter);
        var list = new ListBox(); Grid.SetRow(list, 2); grid.Children.Add(list);
        var candidates = WindowLease.Candidates();
        void Refresh() { list.ItemsSource = candidates.Where(w => w.ToString().Contains(filter.Text, StringComparison.OrdinalIgnoreCase)).ToList(); }
        filter.TextChanged += (_, _) => Refresh(); Refresh();
        var hint = new TextBlock { Text = "La ventana seguirá perteneciendo a su app. Al liberarla volverá al escritorio. Compatibilidad según aplicación y escalado.", FontSize = 11, Margin = new Thickness(0, 12, 0, 12) }; Grid.SetRow(hint, 3); grid.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        WindowCandidate? selected = null;
        buttons.Children.Add(Button("INCRUSTAR", () => { if (list.SelectedItem is WindowCandidate c) { selected = c; window.DialogResult = true; } }));
        buttons.Children.Add(Button("ACTUALIZAR", () => { candidates = WindowLease.Candidates(); Refresh(); }));
        buttons.Children.Add(Button("CANCELAR", () => window.Close())); Grid.SetRow(buttons, 4); grid.Children.Add(buttons);
        window.Loaded += (_, _) => Modalize(window); window.ShowDialog(); return selected;
    }
    private sealed class ModalSurface : Border { }
}
