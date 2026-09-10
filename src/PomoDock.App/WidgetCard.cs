using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PomoDock.Core;
using PomoDock.App.Native;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace PomoDock.App;

public sealed class WidgetCard : Border
{
    public WidgetConfig Config { get; }
    private readonly MainWindow owner;
    private readonly Grid body = new();
    private readonly Grid shell = new();
    private readonly Grid moveSurface = new();
    private readonly TextBlock title;
    private readonly List<Button> headerButtons = [];
    private ExternalWindowHost? host;
    private WebView2? web;
    private bool released;
    private bool resizing;
    private readonly List<Thumb> gestureHandles = [];
    private string resizeEdge = "";
    private Point pointerStart;
    private double elementStartX, elementStartY, elementStartWidth, elementStartHeight;
    private static Task<CoreWebView2Environment>? browserEnvironment;
    public WidgetCard(MainWindow owner, WidgetConfig config)
    {
        this.owner = owner; Config = config;
        BorderThickness = new Thickness(1.5); SetResourceReference(BorderBrushProperty, "Edge"); SetResourceReference(BackgroundProperty, "Surface");
        shell.RowDefinitions.Add(new() { Height = new GridLength(38) }); shell.RowDefinitions.Add(new()); Child = shell;
        body.Margin = new Thickness(8, 0, 8, 8);
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(9, 0, 2, 0), Cursor = Cursors.SizeAll };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, tip, action) in new (string, string, Action)[] {
            ("⋯", config.Kind == "notes" ? "Historial de notas" : "Opciones del widget", Options),
            ("−", "Contraer / expandir", ToggleCollapsed),
            ("×", L.T("widgets.remove"), () => owner.RemoveCard(this)) })
        {
            var button = new Button { Content = label, ToolTip = tip, Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(0), BorderThickness = new Thickness(0), FontSize = 13 };
            button.Click += (_, _) => action(); actions.Children.Add(button); headerButtons.Add(button);
        }
        DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions);
        title = new TextBlock { Text = $"{KindLabel()} / {config.Title}", FontWeight = FontWeights.Bold, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        var titleArea = moveSurface;
        titleArea.Background = Brushes.Transparent; titleArea.Cursor = Cursors.SizeAll;
        titleArea.PreviewMouseLeftButtonDown += (_, e) =>
        {
            BeginResize("MOVE");
            if (titleArea.CaptureMouse()) e.Handled = true;
        };
        titleArea.PreviewMouseMove += (_, e) =>
        {
            if (resizing && resizeEdge == "MOVE" && titleArea.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) UpdateGesture();
        };
        titleArea.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!resizing || resizeEdge != "MOVE") return;
            titleArea.ReleaseMouseCapture(); EndResize(); e.Handled = true;
        };
        title.IsHitTestVisible = false;
        titleArea.Children.Add(title);
        header.Children.Add(titleArea); shell.Children.Add(header);
        owner.Deactivated += CancelGesture;
        PreviewMouseDown += (_, e) => { if (!IsGestureSource(e.OriginalSource)) owner.BringCardToFront(this); };
        Unloaded += (_, _) => CancelGesture(this, EventArgs.Empty);
        Grid.SetRow(body, 1); shell.Children.Add(body);
        var handles = new Grid(); Panel.SetZIndex(handles, 30);
        handles.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) }); handles.RowDefinitions.Add(new RowDefinition()); handles.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        handles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) }); handles.ColumnDefinitions.Add(new ColumnDefinition()); handles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        AddResizeHandle(handles, "NW", 0, 0, Cursors.SizeNWSE); AddResizeHandle(handles, "N", 0, 1, Cursors.SizeNS); AddResizeHandle(handles, "NE", 0, 2, Cursors.SizeNESW);
        AddResizeHandle(handles, "W", 1, 0, Cursors.SizeWE); AddResizeHandle(handles, "E", 1, 2, Cursors.SizeWE);
        AddResizeHandle(handles, "SW", 2, 0, Cursors.SizeNESW); AddResizeHandle(handles, "S", 2, 1, Cursors.SizeNS); AddResizeHandle(handles, "SE", 2, 2, Cursors.SizeNWSE);
        Grid.SetRowSpan(handles, 2); shell.Children.Add(handles);
        body.Visibility = config.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (config.Kind == "window") BuildWindow();
        else if (config.Kind == "web") Loaded += async (_, _) => await BuildWeb();
        else if (config.Kind == "notes") BuildNotes();
        else if (config.Kind == "todo") BuildTodo();
        else if (config.Kind == "habits") BuildHabits();
        else if (config.Kind == "calendar") BuildCalendar();
        else
        {
            // Width changes the type scale just as much as height. Rebuild in small buckets so
            // the card follows both resize axes without repainting on every pointer pixel.
            if (config.Kind == "stats") body.SizeChanged += (_, _) =>
            {
                if (FocusCard.LayoutKey(body.ActualWidth, body.ActualHeight) != statsLayoutKey) Refresh();
            };
            Refresh();
        }
    }
    internal bool IsExternalAttached => host?.Alive == true;
    internal bool IsGestureActive => resizing || moveSurface.IsMouseCaptured || gestureHandles.Any(handle => handle.IsDragging);
    internal int StatsLayoutKeyForDiagnostics => statsLayoutKey;
    internal void BringExternalToFront() => host?.BringToFront();
    internal void SetCarouselTransition(bool active)
    {
        if (host is not null) host.Visibility = active ? Visibility.Hidden : Visibility.Visible;
        if (web is not null) web.Visibility = active ? Visibility.Hidden : Visibility.Visible;
    }
    private static bool IsGestureSource(object source)
    {
        return Ancestors.Find<Thumb>(source as DependencyObject) is not null;
    }
    private Thumb CreateHandle(string label, Cursor cursor)
    {
        // Thumb owns the capture and releases it on mouse-up or cancellation.
        // Capturing the card while listening on its header stranded the mouse.
        var visual = new FrameworkElementFactory(typeof(Border));
        visual.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var thumb = new Thumb { Cursor = cursor, ToolTip = label,
            Template = new ControlTemplate(typeof(Thumb)) { VisualTree = visual } };
        System.Windows.Automation.AutomationProperties.SetName(thumb, label);
        gestureHandles.Add(thumb);
        thumb.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { thumb.CancelDrag(); e.Handled = true; } };
        return thumb;
    }
    private void CancelGesture(object? sender, EventArgs e)
    {
        if (moveSurface.IsMouseCaptured) moveSurface.ReleaseMouseCapture();
        foreach (var handle in gestureHandles) if (handle.IsDragging) handle.CancelDrag();
    }
    private void AddResizeHandle(Grid grid, string edge, int row, int column, Cursor cursor)
    {
        var thumb = CreateHandle("Redimensionar " + edge, cursor);
        thumb.DragStarted += (_, _) => BeginResize(edge);
        thumb.DragDelta += (_, _) => UpdateGesture();
        thumb.DragCompleted += (_, _) => EndResize();
        Grid.SetRow(thumb, row); Grid.SetColumn(thumb, column); grid.Children.Add(thumb);
    }
    private void BeginResize(string edge)
    {
        resizing = true; resizeEdge = edge; elementStartX = Canvas.GetLeft(this); elementStartY = Canvas.GetTop(this); elementStartWidth = ActualWidth; elementStartHeight = ActualHeight;
        pointerStart = CurrentCanvasPointer();
        owner.BringCardToFront(this);
    }
    private Point CurrentCanvasPointer()
    {
        if (Win32.GetCursorPos(out var screen))
        {
            try { return owner.WidgetCanvas.PointFromScreen(new Point(screen.X, screen.Y)); }
            catch (InvalidOperationException) { }
        }
        return Mouse.GetPosition(owner.WidgetCanvas);
    }
    private void UpdateGesture()
    {
        if (!resizing) return;
        var pointer = CurrentCanvasPointer();
        double dx = pointer.X - pointerStart.X, dy = pointer.Y - pointerStart.Y;
        var edge = resizeEdge;
        double x = elementStartX, y = elementStartY, width = elementStartWidth, height = elementStartHeight;
        const double minWidth = 220, minHeight = 90;
        var maxX = owner.WidgetCanvas.ActualWidth; var maxY = owner.WidgetCanvas.ActualHeight;
        if (edge == "MOVE")
        {
            x = Math.Clamp(x + dx, 0, Math.Max(0, maxX - width));
            y = Math.Clamp(y + dy, 0, Math.Max(0, maxY - height));
        }
        else
        {
        dx = Math.Clamp(dx, -elementStartX, Math.Max(0, maxX - elementStartX));
        dy = Math.Clamp(dy, -elementStartY, Math.Max(0, maxY - elementStartY));
        if (edge.Contains('W')) { width = Math.Max(minWidth, elementStartWidth - dx); x = elementStartX + elementStartWidth - width; }
        if (edge.Contains('E')) width = Math.Max(minWidth, elementStartWidth + dx);
        if (edge.Contains('N')) { height = Math.Max(minHeight, elementStartHeight - dy); y = elementStartY + elementStartHeight - height; }
        if (edge.Contains('S')) height = Math.Max(minHeight, elementStartHeight + dy);
        width = Math.Min(width, Math.Max(minWidth, maxX - x));
        height = Math.Min(height, Math.Max(minHeight, maxY - y));
        }
        x = Math.Max(0, x); y = Math.Max(0, y);
        Width = width; Height = height; Canvas.SetLeft(this, x); Canvas.SetTop(this, y); Config.X = x; Config.Y = y; Config.Width = width; Config.Height = height;
        owner.UpdateCardOverlayPosition(this);
        owner.UpdateCardInteractionOverlayPosition(this);
    }
    private void EndResize()
    {
        if (!resizing) return;
        resizing = false; resizeEdge = ""; owner.PersistWidget(this);
    }
    internal void BeginOverlayResize(string edge) => BeginResize(edge);
    internal void UpdateOverlayResize() => UpdateGesture();
    internal void EndOverlayResize() => EndResize();
    private string KindLabel() => Config.Kind switch
    {
        "window" => L.T("widgets.kindApp"),
        "web" => L.T("widgets.kindWeb"),
        "stats" => L.T("widgets.kindStats"),
        "todo" => L.T("widgets.kindTodo"),
        "habits" => L.T("widgets.kindHabits"),
        "calendar" => L.T("widgets.kindCalendar"),
        "notes" => L.T("widgets.kindNote"),
        _ => L.T("widgets.kindText")
    };
    private void BuildNotes()
    {
        body.Children.Clear();
        body.Children.Add(new NotesEditor(owner, Config, this));
    }
    /// <summary>
    /// Paints the whole card in a widget's own colours. A post-it is its colour, so the
    /// frame, the header and its buttons follow it instead of the app theme. Null restores
    /// the theme, and every brush stays a dynamic reference until one is given.
    /// </summary>
    internal void ApplySkin(Brush? surface, Brush? ink, Brush? line)
    {
        if (surface is null || ink is null || line is null)
        {
            SetResourceReference(BackgroundProperty, "Surface");
            SetResourceReference(BorderBrushProperty, "Edge");
            title.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            foreach (var button in headerButtons)
            {
                button.ClearValue(Control.ForegroundProperty);
                button.ClearValue(Control.BackgroundProperty);
            }
            return;
        }
        Background = surface;
        BorderBrush = line;
        title.Foreground = ink;
        foreach (var button in headerButtons)
        {
            button.Foreground = ink;
            button.Background = Brushes.Transparent;
        }
    }
    private void BuildCalendar()
    {
        body.Children.Clear();
        body.Children.Add(new CalendarWidget(owner, Config));
    }
    private void BuildTodo()
    {
        // Tasks stay inside this card: two To Do widgets are two independent lists.
        body.Children.Clear();
        body.Children.Add(new TodoBoard(owner, Config));
    }
    private void BuildHabits()
    {
        // Habits live in the shared book, not in this card: the widget is only a lens.
        body.Children.Clear();
        body.Children.Add(new HabitsBoard(owner, Config));
    }
    private void BuildWindow()
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(20) };
        stack.Children.Add(new TextBlock { Text = "TU APP. DENTRO DE TU ESPACIO.", FontSize = 14, FontWeight = FontWeights.Bold });
        stack.Children.Add(new TextBlock { Text = L.T("widgets.chooseWindow"), FontSize = 11, Margin = new Thickness(0, 8, 0, 12) });
        var button = new Button { Content = "CONECTAR VENTANA", HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += async (_, _) => { var candidate = Dialogs.PickWindow(owner); if (candidate is not null) await Attach(candidate.Handle); };
        stack.Children.Add(button); body.Children.Add(stack);
    }
    public async Task Attach(nint hwnd)
    {
        if (released || host?.IsConnecting == true) return;
        ExternalWindowHost? connectingHost = null;
        try
        {
            if (host is not null) { host.Detach(); host.Dispose(); }
            body.Children.Clear();
            host = connectingHost = new ExternalWindowHost(); body.Children.Add(host); body.UpdateLayout();
            owner.Status(L.T("widgets.connecting"));
            await host.AttachAsync(hwnd, owner.Journal);
            if (released || host != connectingHost) return;
            owner.BringCardToFront(this);
            owner.Status(L.T("widgets.connected"));
        }
        catch (Exception ex)
        {
            if (released || host != connectingHost) return;
            host?.Dispose(); host = null; body.Children.Clear(); BuildWindow();
            owner.Status("No se pudo conectar: " + ex.Message);
            Dialogs.Alert(owner, "NO SE PUDO INCRUSTAR", ex.Message);
        }
    }
    private async Task BuildWeb()
    {
        if (web is not null || released || Config.Collapsed) return;
        if (!Uri.TryCreate(Config.Value, UriKind.Absolute, out var uri) || uri.Scheme != "https") { owner.Status(L.T("widgets.webNeedsHttps")); return; }
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
            web.CoreWebView2.NavigationStarting += (_, e) => { if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) || target.Scheme is not ("https" or "about")) { e.Cancel = true; owner.Status(L.T("widgets.webBlocked")); } };
            web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) && target.Scheme == "https")
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
            };
            web.CoreWebView2.NavigationCompleted += (_, e) => { if (!e.IsSuccess) owner.Status("La web no pudo cargar: " + e.WebErrorStatus); };
            web.CoreWebView2.ProcessFailed += (_, _) => owner.Status(L.T("widgets.webCrashed"));
            web.Source = uri;
        }
        catch (Exception ex) { owner.Status(L.T("widgets.webUnavailable", ex.Message)); body.Children.Add(new TextBlock { Text = L.T("widgets.webInstall"), Margin = new Thickness(18) }); }
    }
    private async void ToggleCollapsed()
    {
        Config.Collapsed = !Config.Collapsed; body.Visibility = Config.Collapsed ? Visibility.Collapsed : Visibility.Visible;
        owner.ArrangeCards(); owner.SaveState();
        if (Config.Kind != "web") return;
        if (Config.Collapsed && !Config.KeepAlive && web?.CoreWebView2 is { } core) { try { await core.TrySuspendAsync(); } catch { } }
        else if (!Config.Collapsed) { if (web is null) await BuildWeb(); else web.CoreWebView2?.Resume(); }
    }
    private async void Options()
    {
        // A note has nothing to configure; its ⋯ opens every note ever written, closed ones included.
        if (Config.Kind == "notes") { NotesHistory.Show(owner, Config.Id); return; }
        var options = Config.Kind == "window" ? new[] { "Renombrar", "Recortar barras superior / inferior", "Liberar ventana", "Conectar otra ventana" }
            : Config.Kind == "web" ? [L.T("widgets.menuRename"), L.T("widgets.menuChangeUrl"), L.T("widgets.menuReload"), Config.KeepAlive ? L.T("widgets.menuAllowSleep") : L.T("widgets.menuKeepAlive")]
            : Config.Kind == "stats" ? [L.T("widgets.menuRename"), L.T("widgets.menuGoal", owner.Settings.DailyGoalMinutes)] : [L.T("widgets.menuRename")];
        var choice = Dialogs.Choose(owner, "OPCIONES DEL WIDGET", options);
        if (choice == 0)
        {
            var name = Dialogs.Prompt(owner, "NOMBRE DEL WIDGET", "Nombre", Config.Title);
            if (!string.IsNullOrWhiteSpace(name)) { Config.Title = name; title.Text = $"{KindLabel()} / {name}"; }
        }
        else if (Config.Kind == "stats")
        {
            if (choice != 1) { owner.SaveState(); return; }
            var minutes = Dialogs.Prompt(owner, L.T("widgets.goalTitle"), L.T("widgets.goalLabel"), owner.Settings.DailyGoalMinutes.ToString());
            if (int.TryParse(minutes, out int value))
            {
                owner.Settings.DailyGoalMinutes = Math.Clamp(value, 15, 960);
                Refresh();
                owner.Status(L.T("widgets.goalSet", owner.Settings.DailyGoalMinutes));
            }
        }
        else if (Config.Kind == "window")
        {
            if (choice == 1 && host is not null)
            {
                var top = Dialogs.Prompt(owner, L.T("widgets.cropTitle"), L.T("widgets.cropTop"), host.CropTop.ToString());
                if (top is not null && double.TryParse(top, out double t)) host.CropTop = Math.Clamp(t, 0, 200);
                var bottom = Dialogs.Prompt(owner, L.T("widgets.cropTitle"), L.T("widgets.cropBottom"), host.CropBottom.ToString());
                if (bottom is not null && double.TryParse(bottom, out double b)) host.CropBottom = Math.Clamp(b, 0, 200);
                host.Resize();
            }
            else if (choice == 2) { host?.Detach(); host?.Dispose(); host = null; body.Children.Clear(); BuildWindow(); }
            else if (choice == 3) { var candidate = Dialogs.PickWindow(owner); if (candidate is not null) await Attach(candidate.Handle); }
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
    /// <summary>Puts the card in the skin that is on now, board and all.</summary>
    internal void Reskin()
    {
        foreach (var board in body.Children.OfType<IReskinnable>().ToArray()) board.Reskin();
        Refresh();
    }
    public void Refresh()
    {
        if (host is not null && !host.IsConnecting && !host.Alive) { host.Dispose(); host = null; body.Children.Clear(); BuildWindow(); owner.Status(L.T("widgets.windowClosed")); }
        if (Config.Kind != "stats") return;
        double statsWidth = body.ActualWidth > 4 ? body.ActualWidth : Math.Max(120, Config.Width - 22);
        double statsHeight = body.ActualHeight > 4 ? body.ActualHeight : Math.Max(60, Config.Height - 46);
        statsLayoutKey = FocusCard.LayoutKey(statsWidth, statsHeight);
        body.Children.Clear();
        body.Children.Add(FocusCard.Build(owner, statsWidth, statsHeight));
    }
    private int statsLayoutKey = -1;
    public void Release()
    {
        CancelGesture(this, EventArgs.Empty); owner.Deactivated -= CancelGesture;
        host?.Detach(); host?.Dispose(); host = null;
        web?.Dispose(); web = null; released = true;
    }
}
