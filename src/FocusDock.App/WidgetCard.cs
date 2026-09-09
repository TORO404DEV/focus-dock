using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private readonly TextBlock title;
    private ExternalWindowHost? host;
    private WebView2? web;
    private bool released;
    private static Task<CoreWebView2Environment>? browserEnvironment;
    public WidgetCard(MainWindow owner, WidgetConfig config)
    {
        this.owner = owner; Config = config;
        BorderThickness = new Thickness(1.5); SetResourceReference(BorderBrushProperty, "Line"); SetResourceReference(BackgroundProperty, "Surface");
        var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = new GridLength(38) }); grid.RowDefinitions.Add(new()); Child = grid;
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(9, 0, 2, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, tip, action) in new (string, string, Action)[] {
            ("↑", "Mover arriba", () => owner.MoveCard(this, -1)),
            ("↓", "Mover abajo", () => owner.MoveCard(this, 1)),
            ("⋯", "Opciones del widget", Options),
            ("−", "Contraer / expandir", ToggleCollapsed),
            ("×", "Quitar widget y liberar ventana", () => owner.RemoveCard(this)) })
        {
            var button = new Button { Content = label, ToolTip = tip, Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(0), BorderThickness = new Thickness(0), FontSize = 13 };
            button.Click += (_, _) => action(); actions.Children.Add(button);
        }
        DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions);
        title = new TextBlock { Text = $"{KindLabel()} / {config.Title}", FontWeight = FontWeights.Bold, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        header.Children.Add(title); grid.Children.Add(header);
        Grid.SetRow(body, 1); grid.Children.Add(body);
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
            MessageBox.Show(owner, ex.Message, "Compatibilidad de la ventana", MessageBoxButton.OK, MessageBoxImage.Information);
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
        var stack = new StackPanel { Margin = new Thickness(18, 12, 18, 12), VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = $"{daily.GetValueOrDefault(today):0} MIN", FontSize = 34, FontWeight = FontWeights.Black, FontFamily = new FontFamily("Consolas") });
        stack.Children.Add(new TextBlock { Text = $"HOY / META {owner.Settings.DailyGoalMinutes} MIN     ·     RACHA {Reports.Streak(daily, today)} DÍAS", FontSize = 10 });
        body.Children.Clear(); body.Children.Add(stack);
    }
    public void Release()
    {
        host?.Detach(); host?.Dispose(); host = null;
        web?.Dispose(); web = null; released = true;
    }
}
