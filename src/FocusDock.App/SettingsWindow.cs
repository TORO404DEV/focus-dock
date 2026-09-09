using System.Windows;
using System.Windows.Controls;

namespace FocusDock.App;

public sealed class SettingsWindow : Window
{
    public SettingsWindow(MainWindow owner)
    {
        Owner = owner; Title = "FOCUS DOCK / Ajustes"; Width = 500; Height = 710; MinWidth = 400; MinHeight = 400; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var stack = new StackPanel { Margin = new Thickness(24) }; Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        stack.Children.Add(Dialogs.Heading("A TU RITMO."));
        var numbers = new List<(TextBox Box, Action<int> Set, int Max)>();
        void Number(string label, int value, int max, Action<int> setter) { stack.Children.Add(new TextBlock { Text = label }); var box = new TextBox { Text = value.ToString() }; stack.Children.Add(box); numbers.Add((box, setter, max)); }
        var settings = owner.Settings;
        Number("Pomodoro · minutos (1–180)", settings.FocusMinutes, 180, v => settings.FocusMinutes = v);
        Number("Descanso corto · minutos (1–60)", settings.ShortMinutes, 60, v => settings.ShortMinutes = v);
        Number("Descanso largo · minutos (1–120)", settings.LongMinutes, 120, v => settings.LongMinutes = v);
        Number("Sesiones antes del descanso largo (1–12)", settings.LongInterval, 12, v => settings.LongInterval = v);
        Number("Meta diaria · minutos (1–1440)", settings.DailyGoalMinutes, 1440, v => settings.DailyGoalMinutes = v);
        var toggles = new List<(CheckBox Box, Action<bool> Set)>();
        void Toggle(string label, bool value, Action<bool> setter) { var check = new CheckBox { Content = label, IsChecked = value }; stack.Children.Add(check); toggles.Add((check, setter)); }
        Toggle("Iniciar descansos automáticamente", settings.AutoBreak, v => settings.AutoBreak = v);
        Toggle("Iniciar enfoque automáticamente", settings.AutoFocus, v => settings.AutoFocus = v);
        Toggle("Alarma al terminar", settings.Sound, v => settings.Sound = v);
        Toggle("Modo oscuro", settings.Dark, v => settings.Dark = v);
        Toggle("Reducir movimiento", settings.ReduceMotion, v => settings.ReduceMotion = v);
        Toggle("Mantener la ventana encima", settings.AlwaysOnTop, v => settings.AlwaysOnTop = v);
        stack.Children.Add(new TextBlock { Text = "Los cambios de duración se aplican a la próxima sesión. Los datos se guardan en este PC.", FontSize = 11, Margin = new Thickness(0, 10, 0, 14) });
        stack.Children.Add(Dialogs.Button("GUARDAR AJUSTES", () =>
        {
            foreach (var (box, _, max) in numbers) if (!int.TryParse(box.Text, out int value) || value < 1 || value > max) { MessageBox.Show(this, $"Introduce un número entre 1 y {max}."); box.Focus(); return; }
            foreach (var (box, set, _) in numbers) set(int.Parse(box.Text));
            foreach (var (box, set) in toggles) set(box.IsChecked == true);
            DialogResult = true;
        }));
    }
}

public sealed class LayoutsWindow : Window
{
    public LayoutsWindow(MainWindow owner)
    {
        Owner = owner; Title = "FOCUS DOCK / Distribuciones"; Width = 500; Height = 460; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var stack = new StackPanel { Margin = new Thickness(22) }; Content = stack; stack.Children.Add(Dialogs.Heading("GUARDA TU ESPACIO."));
        stack.Children.Add(new TextBlock { Text = "Guarda widgets y proporciones. Al cargar otra distribución se liberan las ventanas actuales; podrás reconectarlas." });
        var input = new TextBox { Text = "Mi escritorio" }; stack.Children.Add(input);
        var list = new ListBox { Height = 150, Margin = new Thickness(0, 10, 0, 10), ItemsSource = owner.Settings.Layouts.Keys.ToList() };
        stack.Children.Add(Dialogs.Button("GUARDAR DISTRIBUCIÓN ACTUAL", () =>
        {
            if (string.IsNullOrWhiteSpace(input.Text)) return;
            owner.SaveState();
            owner.Settings.Layouts[input.Text.Trim()] = System.Text.Json.JsonSerializer.Deserialize<List<FocusDock.Core.WidgetConfig>>(System.Text.Json.JsonSerializer.Serialize(owner.Settings.Widgets))!;
            owner.SaveState(); list.ItemsSource = owner.Settings.Layouts.Keys.ToList();
        }));
        stack.Children.Add(list);
        stack.Children.Add(Dialogs.Button("CARGAR SELECCIONADA", () => { if (list.SelectedItem is string name) { owner.LoadLayout(owner.Settings.Layouts[name]); Close(); } }));
    }
}
