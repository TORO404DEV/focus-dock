using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FocusDock.Core;
using FocusDock.App.Native;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace FocusDock.App;

public sealed class WidgetCard : Border
{
    public WidgetConfig Config { get; }
    private readonly MainWindow owner;
    private readonly Grid body = new();
    private readonly Grid shell = new();
    private readonly TextBlock title;
    private ExternalWindowHost? host;
    private WebView2? web;
    private bool released;
    private bool dragging, resizing;
    private Point pointerStart;
    private double elementStartX, elementStartY, elementStartWidth, elementStartHeight;
    private static Task<CoreWebView2Environment>? browserEnvironment;
    public WidgetCard(MainWindow owner, WidgetConfig config)
    {
        this.owner = owner; Config = config;
        BorderThickness = new Thickness(1.5); SetResourceReference(BorderBrushProperty, "Line"); SetResourceReference(BackgroundProperty, "Surface");
        shell.RowDefinitions.Add(new() { Height = new GridLength(38) }); shell.RowDefinitions.Add(new()); Child = shell;
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(9, 0, 2, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, tip, action) in new (string, string, Action)[] {
            ("⋯", "Opciones del widget", Options),
            ("−", "Contraer / expandir", ToggleCollapsed),
            ("×", "Quitar widget y liberar ventana", () => owner.RemoveCard(this)) })
        {
            var button = new Button { Content = label, ToolTip = tip, Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(0), BorderThickness = new Thickness(0), FontSize = 13 };
            button.Click += (_, _) => action(); actions.Children.Add(button);
        }
        DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions);
        title = new TextBlock { Text = $"{KindLabel()} / {config.Title}", FontWeight = FontWeights.Bold, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        header.Children.Add(title); header.MouseLeftButtonDown += HeaderDown; header.MouseMove += HeaderMove; header.MouseLeftButtonUp += HeaderUp; shell.Children.Add(header);
        Grid.SetRow(body, 1); shell.Children.Add(body);
        var grip = new Thumb { Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.SizeNWSE, Opacity = .65, Background = Brushes.Transparent };
        grip.DragStarted += (_, _) => { resizing = true; elementStartWidth = ActualWidth; elementStartHeight = ActualHeight; pointerStart = Mouse.GetPosition(owner.WidgetCanvas); CaptureMouse(); };
        grip.DragDelta += (_, e) => { if (!resizing) return; Width = Math.Max(220, elementStartWidth + e.HorizontalChange); Height = Math.Max(42, elementStartHeight + e.VerticalChange); Config.Width = Width; Config.Height = Height; };
        grip.DragCompleted += (_, _) => { resizing = false; ReleaseMouseCapture(); owner.PersistWidget(this); };
        Grid.SetRow(grip, 1); Panel.SetZIndex(grip, 20); shell.Children.Add(grip);
        body.Visibility = config.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (config.Kind == "window") BuildWindow();
        else if (config.Kind == "web") Loaded += async (_, _) => await BuildWeb();
        else if (config.Kind == "notes")
        {
            var notes = new TextBox { Text = config.Value, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Margin = new Thickness(0), FontFamily = new FontFamily("Consolas"), FontSize = 14 };
            notes.TextChanged += (_, _) => config.Value = notes.Text;
            body.Children.Add(notes);
        }
        else Refresh();
    }
    private void HeaderDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button) return;
        dragging = true; pointerStart = e.GetPosition(owner.WidgetCanvas); elementStartX = Canvas.GetLeft(this); elementStartY = Canvas.GetTop(this); CaptureMouse(); e.Handled = true;
    }
    private void HeaderMove(object sender, MouseEventArgs e)
    {
        if (!dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(owner.WidgetCanvas); Canvas.SetLeft(this, Math.Max(0, elementStartX + now.X - pointerStart.X)); Canvas.SetTop(this, Math.Max(0, elementStartY + now.Y - pointerStart.Y));
        owner.PersistWidget(this, false);
    }
    private void HeaderUp(object sender, MouseButtonEventArgs e)
    {
        if (!dragging) return; dragging = false; ReleaseMouseCapture(); owner.PersistWidget(this); e.Handled = true;
    }
    private string KindLabel() => Config.Kind == "window" ? "APP" : Config.Kind == "web" ? "WEB" : Config.Kind == "stats" ? "STATS" : "TXT";
    private void BuildWindow()
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(20) };
        stack.Children.Add(new TextBlock { Text = "TU APP. DENTRO DE TU ESPACIO.", FontSize = 14, FontWeight = FontWeights.Bold });
        stack.Children.Add(new TextBlock { Text = "Selecciona una ventana que ya tengas abierta.\nSu sesión permanecerá en la aplicación original.", FontSize = 11, Margin = new Thickness(0, 8, 0, 12) });
        var button = new Button { Content = "CONECTAR VENTANA", HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => { var candidate = Dialogs.PickWindow(owner); if (candidate is not null) Attach(candidate.Handle); };
        stack.Children.Add(button); body.Children.Add(stack);
    }
    public void Attach(nint hwnd)
    {
        try
        {
            if (host is not null) { host.Detach(); host.Dispose(); }
            body.Children.Clear();
            host = new ExternalWindowHost(); body.Children.Add(host); body.UpdateLayout();
            host.Attach(hwnd, owner.Journal);
            owner.Status("VENTANA CONECTADA · Arrastra el separador para cambiar su tamaño.");
        }
        catch (Exception ex)
        {
            host?.Dispose(); host = null; body.Children.Clear(); BuildWindow();
            owner.Status("No se pudo conectar: " + ex.Message);
            Dialogs.Alert(owner, "COMPATIBILIDAD DE LA VENTANA", ex.Message);
        }
    }
    private async Task BuildWeb()
    {
        if (web is not null || released || Config.Collapsed) return;
        if (!Uri.TryCreate(Config.Value, UriKind.Absolute, out var uri) || uri.Scheme != "https") { owner.Status("El widget web requiere una dirección HTTPS."); return; }
        try
        {
            browserEnvironment ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(owner.Store.DirectoryPath, "browser"));
            var env = await browserEnvironment;
            if (released) return;
            web = new WebView2(); body.Children.Add(web);
            await web.EnsureCoreWebView2Async(env);
            if (released) return;
            web.CoreWebView2.Settings.AreHostObjectsAllowed = false;
            web.CoreWebView2.Settings.IsWebMessageEnabled = false;
            web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            web.CoreWebView2.NavigationStarting += (_, e) => { if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) || target.Scheme is not ("https" or "about")) { e.Cancel = true; owner.Status("Se bloqueó una navegación fuera de HTTPS."); } };
            web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) && target.Scheme == "https")
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
            };
            web.CoreWebView2.NavigationCompleted += (_, e) => { if (!e.IsSuccess) owner.Status("La web no pudo cargar: " + e.WebErrorStatus); };
            web.CoreWebView2.ProcessFailed += (_, _) => owner.Status("El panel web se interrumpió. Puedes recargarlo desde ⋯.");
            web.Source = uri;
        }
        catch (Exception ex) { owner.Status("WebView2 no está disponible: " + ex.Message); body.Children.Add(new TextBlock { Text = "No se pudo abrir el navegador. Instala Microsoft Edge WebView2 Runtime y vuelve a abrir este widget.", Margin = new Thickness(18) }); }
    }
    private async void ToggleCollapsed()
    {
        Config.Collapsed = !Config.Collapsed; body.Visibility = Config.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        owner.ArrangeCards(); owner.SaveState();
        if (Config.Kind != "web") return;
        if (Config.Collapsed && !Config.KeepAlive && web?.CoreWebView2 is { } core) { try { await core.TrySuspendAsync(); } catch { } }
        else if (!Config.Collapsed) { if (web is null) await BuildWeb(); else web.CoreWebView2?.Resume(); }
    }
    private void Options()
    {
        var options = Config.Kind == "window" ? new[] { "Renombrar", "Recortar barras superior / inferior", "Liberar ventana", "Conectar otra ventana" }
            : Config.Kind == "web" ? ["Renombrar", "Cambiar URL", "Recargar", Config.KeepAlive ? "Permitir suspensión" : "Mantener activo (música / dashboard)"] : ["Renombrar"];
        var choice = Dialogs.Choose(owner, "OPCIONES DEL WIDGET", options);
        if (choice == 0)
        {
            var name = Dialogs.Prompt(owner, "NOMBRE DEL WIDGET", "Nombre", Config.Title);
            if (!string.IsNullOrWhiteSpace(name)) { Config.Title = name; title.Text = $"{KindLabel()} / {name}"; }
        }
        else if (Config.Kind == "window")
        {
            if (choice == 1 && host is not null)
            {
                var top = Dialogs.Prompt(owner, "RECORTAR VENTANA", "Píxeles de la barra superior (0–200)", host.CropTop.ToString());
                if (top is not null && double.TryParse(top, out double t)) host.CropTop = Math.Clamp(t, 0, 200);
                var bottom = Dialogs.Prompt(owner, "RECORTAR VENTANA", "Píxeles de la barra inferior (0–200)", host.CropBottom.ToString());
                if (bottom is not null && double.TryParse(bottom, out double b)) host.CropBottom = Math.Clamp(b, 0, 200);
                host.Resize();
            }
            else if (choice == 2) { host?.Detach(); host?.Dispose(); host = null; body.Children.Clear(); BuildWindow(); }
            else if (choice == 3) { var candidate = Dialogs.PickWindow(owner); if (candidate is not null) Attach(candidate.Handle); }
        }
        else if (Config.Kind == "web")
        {
            if (choice == 1)
            {
                var url = Dialogs.Prompt(owner, "URL DEL WIDGET", "URL HTTPS", Config.Value);
                if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https") { Config.Value = uri.AbsoluteUri; if (web is not null) web.Source = uri; }
            }
            else if (choice == 2) web?.Reload();
            else if (choice == 3) { Config.KeepAlive = !Config.KeepAlive; if (Config.KeepAlive) web?.CoreWebView2?.Resume(); }
        }
        owner.SaveState();
    }
    public void Refresh()
    {
        if (host is not null && !host.Alive) { host.Dispose(); host = null; body.Children.Clear(); BuildWindow(); owner.Status("La ventana externa se cerró. Puedes conectar otra."); }
        if (Config.Kind != "stats") return;
        var sessions = owner.Store.Sessions(); var daily = Reports.Daily(sessions, TimeZoneInfo.Local);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var stack = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
        int streak = Reports.Streak(daily, today); double todayMinutes = daily.GetValueOrDefault(today); double weekly = daily.Where(x => x.Key >= today.AddDays(-6) && x.Key <= today).Sum(x => x.Value); int level = Math.Max(1, (int)(weekly / Math.Max(1, owner.Settings.DailyGoalMinutes) * 10));
        var heading = new Grid(); heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock { Text = $"{todayMinutes:0} MIN", FontSize = 30, FontWeight = FontWeights.Black, FontFamily = new FontFamily("Consolas") });
        var streakText = new TextBlock { Text = $"🔥 {streak} DÍAS\nNIVEL {level:00}", FontSize = 12, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Right }; Grid.SetColumn(streakText, 1); heading.Children.Add(streakText); stack.Children.Add(heading);
        stack.Children.Add(new TextBlock { Text = $"HOY / META {owner.Settings.DailyGoalMinutes} MIN     ·     {weekly:0} MIN ESTA SEMANA", FontSize = 9, Foreground = (Brush)Application.Current.Resources["Muted"] });
        var weekBars = new UniformGrid { Columns = 7, Height = 118, Margin = new Thickness(0, 14, 0, 0) };
        double peak = Math.Max(owner.Settings.FocusMinutes, Enumerable.Range(0, 7).Select(i => daily.GetValueOrDefault(today.AddDays(-6 + i))).DefaultIfEmpty(0).Max());
        for (int i = 0; i < 7; i++)
        {
            var day = today.AddDays(-6 + i); double value = daily.GetValueOrDefault(day); var cell = new Grid { Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Stretch };
            var track = new Border { Background = new SolidColorBrush(Color.FromArgb(28, 0, 0, 0)), VerticalAlignment = VerticalAlignment.Bottom, Height = 94 };
            var fill = new Border { Background = (Brush)Application.Current.Resources["Ink"], Height = Math.Max(value > 0 ? 5 : 0, 94 * value / peak), VerticalAlignment = VerticalAlignment.Bottom };
            cell.Children.Add(track); cell.Children.Add(fill); cell.Children.Add(new TextBlock { Text = day.ToString("dd"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, -18), FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center }); weekBars.Children.Add(cell);
        }
        stack.Children.Add(weekBars);
        var progress = new ProgressBar { Minimum = 0, Maximum = Math.Max(1, owner.Settings.DailyGoalMinutes), Value = Math.Min(todayMinutes, owner.Settings.DailyGoalMinutes), Height = 8, Margin = new Thickness(0, 20, 0, 5) }; stack.Children.Add(progress);
        stack.Children.Add(new TextBlock { Text = todayMinutes >= owner.Settings.DailyGoalMinutes ? "META SUPERADA · SIGUE CON INTENCIÓN." : $"FALTAN {Math.Max(0, owner.Settings.DailyGoalMinutes - todayMinutes):0} MIN PARA TU META", FontSize = 10, FontWeight = FontWeights.Bold });
        body.Children.Clear(); body.Children.Add(stack);
    }
    public void Release()
    {
        host?.Detach(); host?.Dispose(); host = null;
        web?.Dispose(); web = null; released = true;
    }
}
