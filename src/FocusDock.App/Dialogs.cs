using System.Windows;
using System.Windows.Controls;
using FocusDock.App.Native;

namespace FocusDock.App;

internal static class Dialogs
{
    public static Window Window(Window owner, string title, double width = 500, double height = 420) => new()
    { Owner = owner, Title = "FOCUS DOCK / " + title, Width = width, Height = height, MinWidth = 360, MinHeight = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
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
        return window.ShowDialog() == true ? input.Text.Trim() : null;
    }
    public static int Choose(Window owner, string title, string[] options)
    {
        var window = Window(owner, title, 480, 190 + options.Length * 46);
        var stack = new StackPanel { Margin = new Thickness(22) }; window.Content = stack; stack.Children.Add(Heading(title));
        int selected = -1;
        for (int i = 0; i < options.Length; i++)
        { int index = i; var b = Button(options[i], () => { selected = index; window.DialogResult = true; }); b.Margin = new Thickness(0, 0, 0, 8); stack.Children.Add(b); }
        window.ShowDialog(); return selected;
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
        window.ShowDialog(); return selected;
    }
}
