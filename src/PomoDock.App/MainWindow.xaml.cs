using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PomoDock.Core;
using PomoDock.App.Native;
using Microsoft.Win32;

namespace PomoDock.App;

public partial class MainWindow : Window
{
    private static string ProductDataPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PomoDock");
    private static string LegacyDataPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusDock");
    public static string DataPath => ResolveDataPath();
    public Store Store { get; }
    public Settings Settings { get; }
    public TimerEngine Timer { get; }
    public SoundEngine Sounds { get; }
    private readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromSeconds(1) };
    private List<WidgetCard> cards = [];
    private readonly Dictionary<WidgetCard, Popup> overlayCards = [];
    private readonly Dictionary<WidgetCard, Popup> interactionOverlays = [];
    private readonly List<Thumb> timerGestureHandles = [];
    private WorkTask? selectedTask;
    private int ticks;
    private bool fullscreen, exiting;
    private Rect normalRect;
    private WindowState previousState;
    private nint hwnd, focusHook;
    private readonly Win32.WinEventProc foregroundCallback;
    private bool hotkeyRegistered;
    private bool timerResizing;
    private string timerResizeEdge = "";
    private Point timerPointerStart;
    private double timerStartX, timerStartY, timerStartWidth, timerStartHeight;
    private bool appliedTimerAtBottom;
    private Popup? timerOverlay;
    private Popup? reportPopup;
    private readonly List<Popup> reportBackdrops = [];
    private ReportWindow? reportContent;
    private bool timerPointerPressed, timerWantsFront;
    public bool DiagnosticMode { get; }
    public static double Monotonic => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    private static string ResolveDataPath()
    {
        if (!Directory.Exists(ProductDataPath) && Directory.Exists(LegacyDataPath))
        {
            try { Directory.Move(LegacyDataPath, ProductDataPath); }
            catch { return LegacyDataPath; }
        }
        return ProductDataPath;
    }
    public string Journal => Path.Combine(Store.DirectoryPath, "windows.json");
    internal Canvas WidgetCanvas => WidgetArea;
    public MainWindow(string? data = null, bool diagnostic = false)
    {
        DiagnosticMode = diagnostic;
        Store = new(data ?? DataPath);
        Settings = Store.Read<Settings>("settings") ?? new(); Settings.Validate();
        Timer = new(Settings);
        Sounds = new(Settings);
        InitializeComponent();
        appliedTimerAtBottom = Settings.TimerAtBottom;
        BuildTimerHandles();
        TimerMoveHeader.PreviewMouseLeftButtonDown += TimerMoveHeaderDown;
        TimerMoveHeader.PreviewMouseMove += TimerMoveHeaderMove;
        TimerMoveHeader.PreviewMouseLeftButtonUp += TimerMoveHeaderUp;
        TimerFrame.PreviewMouseDown += TimerFrameMouseDown;
        TimerFrame.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(TimerFrameMouseUp), true);
        Deactivated += (_, _) => CancelTimerGesture();
        ApplyTimerPosition(); ApplyTheme(); UpdateHeaderClock();
        Width = Math.Max(MinWidth, Settings.WindowWidth); Height = Math.Max(MinHeight, Settings.WindowHeight);
        Left = Settings.WindowLeft; Top = Settings.WindowTop;
        Topmost = Settings.AlwaysOnTop;
        foregroundCallback = (_, _, _, _, _, _, _) => Dispatcher.BeginInvoke(UpdateHotkey);
        Loaded += OnLoaded;
        LocationChanged += (_, _) => UpdateOverlayPositions();
        SizeChanged += (_, _) => UpdateOverlayPositions();
        StateChanged += (_, _) => UpdateOverlayPositions();
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
        Timer.GapDetected += () => Status("PAUSADO · Se detectó suspensión o una interrupción del reloj.");
        Timer.Finished += OnFinished;
        if (Store.Read<Session>("checkpoint") is { } checkpoint && !Store.Sessions().Any(s => s.Id == checkpoint.Id))
        { Timer.Restore(checkpoint); Status("SESIÓN RECUPERADA · Reanuda cuando estés listo."); }
        ticker.Tick += (_, _) =>
        {
            Timer.Tick(DateTimeOffset.UtcNow, Monotonic); UpdateTimer();
            if (++ticks % 15 == 0) SaveState();
            if (ticks % 5 == 0) foreach (var card in cards.ToArray()) card.Refresh();
        };
        statusReset.Tick += (_, _) => { statusReset.Stop(); UpdatePageMeta(); };
        PageDock.PreviewMouseWheel += (_, e) =>
        {
            if (e.Delta < 0) NavigateWorkspace(1);
            else if (e.Delta > 0) NavigateWorkspace(-1);
            e.Handled = true;
        };
        SystemEvents.PowerModeChanged += PowerChanged;
        UpdateTimer();
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        hwnd = new WindowInteropHelper(this).Handle;
        // Keep the restored window reachable after a monitor is disconnected.
        var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
        if (!System.Windows.Forms.Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new System.Drawing.Rectangle((int)Left, (int)Top, (int)Width, (int)Height))))
        { Left = screen.WorkingArea.Left + 30; Top = screen.WorkingArea.Top + 30; }
        var source = HwndSource.FromHwnd(hwnd); source?.AddHook(WindowMessages);
        focusHook = Win32.SetWinEventHook(3, 3, 0, foregroundCallback, 0, 0, 0);
        InitializeWorkspacePages(); ticker.Start(); UpdateHotkey();
        // Calendar reminders ring on their own clock: they never depend on a widget being visible.
        AgendaReminders.For(Store).Attach(this);
        if (Settings.Fullscreen) ToggleFullscreen();
        if (!DiagnosticMode)
        {
            try
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                start.ArgumentList.Add("--guardian"); start.ArgumentList.Add(Environment.ProcessId.ToString()); start.ArgumentList.Add(Journal);
                Process.Start(start)?.Dispose();
            }
            catch (Exception ex) { Status("No se inició la recuperación auxiliar: " + ex.Message); }
        }
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) Dispatcher.Invoke(() => { Timer.Pause(DateTimeOffset.UtcNow, Monotonic); SaveState(); Status("PAUSADO · Equipo suspendido."); });
    }
    public void SaveState()
    {
        SaveCurrentWorkspacePage();
        if (!fullscreen && WindowState == WindowState.Normal) { Settings.WindowLeft = Left; Settings.WindowTop = Top; Settings.WindowWidth = Width; Settings.WindowHeight = Height; }
        Settings.Fullscreen = fullscreen;
        Store.Write("settings", Settings); Store.Write("checkpoint", Timer.Active);
    }
    private void OnFinished(Session session)
    {
        Store.Complete(session);
        if (session.Outcome != Outcome.Completed) return;
        Sounds.StopNoise();
        Sounds.Completed(session.Phase);
        Status(session.Phase == Phase.Focus ? "SESIÓN COMPLETA · Es momento de descansar." : "DESCANSO COMPLETO · Puedes volver a enfocarte.");
        var next = Timer.NextPhase();
        Dispatcher.BeginInvoke(() =>
        {
            Timer.Select(next, DateTimeOffset.UtcNow, Monotonic);
            if ((next == Phase.Focus && Settings.AutoFocus) || (next != Phase.Focus && Settings.AutoBreak))
            {
                Timer.Start(DateTimeOffset.UtcNow, Monotonic, selectedTask);
                if (next == Phase.Focus) Sounds.StartNoise();
            }
            AnimateTimer(); UpdateTimer(); SaveState();
        });
    }
    private Brush PhaseBrush() => new SolidColorBrush(ParseColor(Timer.Phase == Phase.Focus ? Settings.FocusColor : Timer.Phase == Phase.ShortBreak ? Settings.ShortBreakColor : Settings.LongBreakColor, Colors.Transparent));
    private void UpdateTimer()
    {
        UpdateHeaderClock();
        var remaining = TimeSpan.FromSeconds(Math.Ceiling(Timer.Remaining));
        ClockText.Text = $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
        StartButton.Content = Timer.Running ? "PAUSE Ⅱ" : Timer.Active is null ? "START →" : "RESUME →";
        var label = Timer.Phase == Phase.Focus ? "TIEMPO DE ENFOQUE" : Timer.Phase == Phase.ShortBreak ? "DESCANSO CORTO" : "DESCANSO LARGO";
        PhaseLabel.Text = $"{Timer.CompletedFocus + 1:00} / {label}";
        CycleText.Text = $"CICLO {Timer.CompletedFocus % Settings.LongInterval + 1} / {Settings.LongInterval}";
        foreach (var button in new[] { FocusButton, ShortButton, LongButton })
        {
            bool active = button.Tag.ToString() == Timer.Phase.ToString();
            button.SetResourceReference(BackgroundProperty, active ? "Ink" : "Surface");
            button.SetResourceReference(ForegroundProperty, active ? "Paper" : "Ink");
        }
        var phaseBrush = PhaseBrush(); TimerFrame.Background = phaseBrush; TimerSurface.Background = phaseBrush;
        ContextButton.Content = Timer.Active is { } s ? $"●  {s.Project} / {s.Task}" : selectedTask is null ? "○  ENFOQUE LIBRE   /   Elegir tarea" : $"○  {selectedTask.Project} / {selectedTask.Name}";
    }
    private void StartClick(object sender, RoutedEventArgs e) { Sounds.Button(Timer.Running ? "pause" : "start"); ToggleTimer(); }
    internal void ToggleTimer()
    {
        if (Timer.Running) { Timer.Pause(DateTimeOffset.UtcNow, Monotonic); Sounds.StopNoise(); Status("PAUSADO · Tu progreso está guardado."); }
        else { Timer.Start(DateTimeOffset.UtcNow, Monotonic, selectedTask); if (Timer.Phase == Phase.Focus) Sounds.StartNoise(); Status("EN CURSO · Una cosa a la vez."); }
        UpdateTimer(); SaveState();
    }
    private void PhaseClick(object sender, RoutedEventArgs e)
    {
        var phase = Enum.Parse<Phase>(((Button)sender).Tag.ToString()!);
        if (phase == Timer.Phase) return;
        Sounds.Button("phase"); Sounds.StopNoise(); Timer.Select(phase, DateTimeOffset.UtcNow, Monotonic); UpdateTimer(); AnimateTimer(); SaveState();
    }
    private void ResetClick(object sender, RoutedEventArgs e) { Sounds.Button("reset"); Sounds.StopNoise(); Timer.Select(Timer.Phase, DateTimeOffset.UtcNow, Monotonic); UpdateTimer(); SaveState(); Status("REINICIADO · El tiempo trabajado se guardó como parcial."); }
    private void SkipClick(object sender, RoutedEventArgs e) { Sounds.Button("skip"); Sounds.StopNoise(); var next = Timer.NextPhase(); Timer.Select(next, DateTimeOffset.UtcNow, Monotonic); UpdateTimer(); AnimateTimer(); SaveState(); }
    private void AnimateTimer()
    {
        if (Settings.ReduceMotion) return;
        TimerSurface.BeginAnimation(OpacityProperty, new DoubleAnimation(0.65, 1, TimeSpan.FromMilliseconds(180)));
    }
    public void Status(string text)
    {
        if (!workspacePagesLoaded) return;
        PageMetaText.Text = text;
        statusReset.Stop();
        statusReset.Start();
    }
    private void UpdateHeaderClock()
    {
        var now = DateTime.Now;
        var culture = CultureInfo.GetCultureInfo("es-MX");
        HeaderDateText.Text = now.ToString("ddd dd MMM yyyy", culture).ToUpper(culture);
        HeaderTimeText.Text = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }
    private void TasksClick(object sender, RoutedEventArgs e)
    {
        var dialog = new TasksWindow(this); dialog.ShowDialog();
        if (dialog.SelectionChanged)
        {
            Sounds.StopNoise();
            Timer.Pause(DateTimeOffset.UtcNow, Monotonic); Timer.Finish(Outcome.Partial, DateTimeOffset.UtcNow);
            selectedTask = dialog.SelectedTask; Status("CONTEXTO ACTUALIZADO · Inicia la siguiente sesión.");
        }
        UpdateTimer(); SaveState();
    }
    private void ReportClick(object sender, RoutedEventArgs e) => ShowReportModal();

    private void ShowReportModal()
    {
        if (reportPopup is { IsOpen: true } existing) { BringPopupToFront(existing); return; }
        CancelTimerGesture();
        var report = new ReportWindow(this);
        reportContent = report;
        var panel = report.TakeModalContent(HideReportModal, 1, 1);
        panel.Focusable = true;
        KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Cycle);
        // WPF restricts a Popup's screen coverage. Four small backdrop surfaces
        // cover the owner without putting a fullscreen Grid in a single Popup.
        // They also intercept input above hosted HWNDs and floating widgets.
        for (int i = 0; i < 4; i++)
        {
            var shade = new Border { Background = new SolidColorBrush(Color.FromArgb(180, 23, 25, 22)) };
            shade.MouseLeftButtonDown += (_, e) => { HideReportModal(); e.Handled = true; };
            reportBackdrops.Add(new Popup
            {
                Child = shade, Placement = PlacementMode.Absolute, PlacementTarget = Root,
                AllowsTransparency = true, StaysOpen = true, PopupAnimation = PopupAnimation.None
            });
        }
        var popup = new Popup
        {
            Placement = PlacementMode.Absolute,
            PlacementTarget = Root,
            HorizontalOffset = 0,
            VerticalOffset = 0,
            AllowsTransparency = true,
            StaysOpen = true,
            Focusable = true,
            Child = panel
        };
        reportPopup = popup;
        UpdateReportModalPosition();
        foreach (var shade in reportBackdrops) { shade.IsOpen = true; BringPopupToFront(shade); }
        popup.Opened += (_, _) => { UpdateReportModalPosition(); BringPopupToFront(popup); Keyboard.Focus(panel); };
        popup.IsOpen = true;
    }

    private void HideReportModal()
    {
        if (reportPopup is null) return;
        reportPopup.IsOpen = false;
        reportPopup.Child = null;
        reportPopup = null;
        foreach (var shade in reportBackdrops) { shade.IsOpen = false; shade.Child = null; }
        reportBackdrops.Clear();
        reportContent = null;
        if (!exiting) Focus();
    }

    private void UpdateReportModalPosition()
    {
        if (reportPopup?.Child is not FrameworkElement panel) return;
        if (WindowState == WindowState.Minimized) { HideReportModal(); return; }
        var dpi = VisualTreeHelper.GetDpi(Root);
        var origin = Root.PointToScreen(new Point());
        var screen = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
        var viewport = Rect.Intersect(new Rect(origin.X, origin.Y,
            Root.ActualWidth * dpi.DpiScaleX, Root.ActualHeight * dpi.DpiScaleY),
            new Rect(screen.Left, screen.Top, screen.Width, screen.Height));
        if (viewport.IsEmpty) return;
        for (int i = 0; i < reportBackdrops.Count; i++)
        {
            var shade = reportBackdrops[i];
            var left = viewport.Left + Math.Floor(viewport.Width / 2) * (i % 2);
            var top = viewport.Top + Math.Floor(viewport.Height / 2) * (i / 2);
            shade.HorizontalOffset = left / dpi.DpiScaleX;
            shade.VerticalOffset = top / dpi.DpiScaleY;
            var child = (FrameworkElement)shade.Child;
            child.Width = (i % 2 == 0 ? Math.Floor(viewport.Width / 2) : Math.Ceiling(viewport.Width / 2)) / dpi.DpiScaleX;
            child.Height = (i / 2 == 0 ? Math.Floor(viewport.Height / 2) : Math.Ceiling(viewport.Height / 2)) / dpi.DpiScaleY;
        }
        panel.Width = Math.Max(1, Math.Min(820, viewport.Width / dpi.DpiScaleX - 32));
        panel.Height = Math.Max(1, Math.Min(880, Math.Min(viewport.Height - 40 * dpi.DpiScaleY, screen.Height * .70) / dpi.DpiScaleY));
        reportPopup.HorizontalOffset = (viewport.Left + (viewport.Width - panel.Width * dpi.DpiScaleX) / 2) / dpi.DpiScaleX;
        reportPopup.VerticalOffset = (viewport.Top + (viewport.Height - panel.Height * dpi.DpiScaleY) / 2) / dpi.DpiScaleY;
    }

    internal bool IsReportModalOpen => reportPopup is { IsOpen: true };
    internal bool ReportModalFitsVisibleScreen
    {
        get
        {
            if (reportPopup?.Child is not FrameworkElement panel ||
                PresentationSource.FromVisual(panel) is not HwndSource source ||
                !Win32.GetWindowRect(source.Handle, out var rect)) return false;
            var dpi = VisualTreeHelper.GetDpi(panel);
            var screen = System.Windows.Forms.Screen.FromHandle(source.Handle).Bounds;
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            return rect.Left >= screen.Left && rect.Top >= screen.Top && rect.Right <= screen.Right && rect.Bottom <= screen.Bottom &&
                width >= panel.ActualWidth * dpi.DpiScaleX - 2 && height >= panel.ActualHeight * dpi.DpiScaleY - 2;
        }
    }
    internal string HeaderClockText => $"{HeaderDateText.Text} {HeaderTimeText.Text}";
    internal int ReportVisibleSessionCount => reportContent?.VisibleSessionCount ?? 0;
    internal FrameworkElement? ReportModalSurface => reportPopup?.Child as FrameworkElement;
    internal void ShowReportForDiagnostics() => ShowReportModal();
    internal bool ShowReportChartHoverForDiagnostics() => reportContent?.ShowChartHoverForDiagnostics() == true;
    internal void ShowReportDetailForDiagnostics() => reportContent?.ShowDetailForDiagnostics();
    internal void HideReportForDiagnostics() => HideReportModal();
    private void SettingsClick(object sender, RoutedEventArgs e) { new SettingsWindow(this).ShowDialog(); ApplyLiveSettings(); SaveState(); }
    /// <summary>Puts a settings change on screen at once, so the panel shows its effect while it is open.</summary>
    public void ApplyLiveSettings() { Settings.Validate(); ApplyTimerPosition(); ApplyTheme(); Topmost = Settings.AlwaysOnTop; UpdateTimer(); }
    private void LayoutsClick(object sender, RoutedEventArgs e) { new LayoutsWindow(this).ShowDialog(); }
    private static Color ParseColor(string value, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(value); } catch { return fallback; }
    }
    public void ApplyTheme()
    {
        var resources = Application.Current.Resources;
        string[] keys = ["Paper", "Ink", "Muted", "Surface", "Line", "Accent"];
        string[] colors = Settings.Dark ? ["#191B18", "#EEEEE5", "#AFB3A4", "#252822", "#C2C6B8", Settings.AccentColor] : ["#F1F0E9", "#171916", "#66695E", "#FAF9F3", "#171916", Settings.AccentColor];
        for (int i = 0; i < keys.Length; i++) resources[keys[i]] = new SolidColorBrush(ParseColor(colors[i], Colors.Transparent));
        var phaseBrush = PhaseBrush(); TimerFrame.Background = phaseBrush; TimerSurface.Background = phaseBrush;
        UpdatePageNavigation();
    }
    private void ApplyTimerPosition()
    {
        // TimerAtBottom remains the quick placement preference from Settings.
        // Once the user moves the timer manually, its canvas coordinates win
        // until that preference is changed again.
        if (Settings.TimerAtBottom != appliedTimerAtBottom)
        {
            CurrentPage.TimerPositionCustomized = false;
            appliedTimerAtBottom = Settings.TimerAtBottom;
        }
        ArrangeTimerWidget();
    }

    private void BuildTimerHandles()
    {
        TimerHandles.RowDefinitions.Add(new RowDefinition { Height = new GridLength(9) });
        TimerHandles.RowDefinitions.Add(new RowDefinition());
        TimerHandles.RowDefinitions.Add(new RowDefinition { Height = new GridLength(9) });
        TimerHandles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9) });
        TimerHandles.ColumnDefinitions.Add(new ColumnDefinition());
        TimerHandles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9) });
        AddTimerHandle("NW", 0, 0, Cursors.SizeNWSE);
        AddTimerHandle("N", 0, 1, Cursors.SizeNS);
        AddTimerHandle("NE", 0, 2, Cursors.SizeNESW);
        AddTimerHandle("W", 1, 0, Cursors.SizeWE);
        AddTimerHandle("E", 1, 2, Cursors.SizeWE);
        AddTimerHandle("SW", 2, 0, Cursors.SizeNESW);
        AddTimerHandle("S", 2, 1, Cursors.SizeNS);
        AddTimerHandle("SE", 2, 2, Cursors.SizeNWSE);
    }

    private void AddTimerHandle(string edge, int row, int column, Cursor cursor)
    {
        var visual = new FrameworkElementFactory(typeof(Border));
        visual.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var thumb = new Thumb
        {
            Cursor = cursor,
            IsHitTestVisible = true,
            Template = new ControlTemplate(typeof(Thumb)) { VisualTree = visual }
        };
        System.Windows.Automation.AutomationProperties.SetName(thumb, "Redimensionar temporizador " + edge);
        timerGestureHandles.Add(thumb);
        thumb.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { thumb.CancelDrag(); e.Handled = true; } };
        thumb.DragStarted += (_, _) => BeginTimerGesture(edge);
        thumb.DragDelta += (_, _) => UpdateTimerGesture();
        thumb.DragCompleted += (_, _) => EndTimerGesture();
        Grid.SetRow(thumb, row); Grid.SetColumn(thumb, column); TimerHandles.Children.Add(thumb);
    }

    private void TimerFrameMouseDown(object sender, MouseButtonEventArgs e)
    {
        timerPointerPressed = true;
        BringTimerToFront();
    }

    private void TimerFrameMouseUp(object sender, MouseButtonEventArgs e)
    {
        timerPointerPressed = false;
        QueueTimerPromotion();
    }

    private void QueueTimerPromotion()
    {
        // A dispatcher callback queued on mouse-down is NOT after the click:
        // it runs while the button/Thumb still owns capture. Wait for mouse-up
        // and the Click/DragCompleted handlers (which may open a modal).
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!exiting && IsEnabled && timerWantsFront && !timerPointerPressed && !timerResizing)
                BringTimerToFront();
        }));
    }

    private void TimerMoveHeaderDown(object sender, MouseButtonEventArgs e)
    {
        for (var current = e.OriginalSource as DependencyObject; current is not null && current != TimerMoveHeader; current = VisualTreeHelper.GetParent(current))
            if (current is ButtonBase) return;
        BeginTimerGesture("MOVE");
        if (TimerMoveHeader.CaptureMouse()) e.Handled = true;
    }

    private void TimerMoveHeaderMove(object sender, MouseEventArgs e)
    {
        if (timerResizing && timerResizeEdge == "MOVE" && TimerMoveHeader.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
            UpdateTimerGesture();
    }

    private void TimerMoveHeaderUp(object sender, MouseButtonEventArgs e)
    {
        if (!timerResizing || timerResizeEdge != "MOVE") return;
        TimerMoveHeader.ReleaseMouseCapture(); EndTimerGesture(); e.Handled = true;
    }

    private void CancelTimerGesture()
    {
        if (TimerMoveHeader.IsMouseCaptured) TimerMoveHeader.ReleaseMouseCapture();
        foreach (var handle in timerGestureHandles) if (handle.IsDragging) handle.CancelDrag();
        timerResizing = false; timerResizeEdge = ""; timerPointerPressed = false;
    }

    private Point CurrentTimerPointer()
    {
        if (Win32.GetCursorPos(out var screen))
        {
            try { return WidgetArea.PointFromScreen(new Point(screen.X, screen.Y)); }
            catch (InvalidOperationException) { }
        }
        return Mouse.GetPosition(WidgetArea);
    }

    private void BeginTimerGesture(string edge)
    {
        timerResizing = true; timerResizeEdge = edge;
        timerStartX = Canvas.GetLeft(TimerFrame); timerStartY = Canvas.GetTop(TimerFrame);
        timerStartWidth = TimerFrame.ActualWidth > 0 ? TimerFrame.ActualWidth : TimerFrame.Width;
        timerStartHeight = TimerFrame.ActualHeight > 0 ? TimerFrame.ActualHeight : TimerFrame.Height;
        timerPointerStart = CurrentTimerPointer();
        BringTimerToFront();
    }

    private void UpdateTimerGesture()
    {
        if (!timerResizing) return;
        UpdateTimerGesture(CurrentTimerPointer());
    }

    private void UpdateTimerGesture(Point pointer)
    {
        if (!timerResizing) return;
        double dx = pointer.X - timerPointerStart.X, dy = pointer.Y - timerPointerStart.Y;
        double x = timerStartX, y = timerStartY, width = timerStartWidth, height = timerStartHeight;
        const double minWidth = 360, minHeight = 300;
        double maxX = Math.Max(minWidth, WidgetArea.ActualWidth), maxY = Math.Max(minHeight, WidgetArea.ActualHeight);
        if (timerResizeEdge == "MOVE")
        {
            x = Math.Clamp(x + dx, 0, Math.Max(0, maxX - width));
            y = Math.Clamp(y + dy, 0, Math.Max(0, maxY - height));
        }
        else
        {
            dx = Math.Clamp(dx, -timerStartX, Math.Max(0, maxX - timerStartX));
            dy = Math.Clamp(dy, -timerStartY, Math.Max(0, maxY - timerStartY));
            if (timerResizeEdge.Contains('W')) { width = Math.Max(minWidth, timerStartWidth - dx); x = timerStartX + timerStartWidth - width; }
            if (timerResizeEdge.Contains('E')) width = Math.Max(minWidth, timerStartWidth + dx);
            if (timerResizeEdge.Contains('N')) { height = Math.Max(minHeight, timerStartHeight - dy); y = timerStartY + timerStartHeight - height; }
            if (timerResizeEdge.Contains('S')) height = Math.Max(minHeight, timerStartHeight + dy);
            width = Math.Min(width, Math.Max(minWidth, maxX - x));
            height = Math.Min(height, Math.Max(minHeight, maxY - y));
        }
        x = Math.Max(0, x); y = Math.Max(0, y);
        TimerFrame.Width = width; TimerFrame.Height = height;
        Canvas.SetLeft(TimerFrame, x); Canvas.SetTop(TimerFrame, y);
        if (CurrentTimerWidget is { } config)
        {
            config.X = x; config.Y = y; config.Width = width; config.Height = height;
            CurrentPage.TimerPositionCustomized = true;
        }
        UpdateTimerOverlayPosition();
    }

    private void EndTimerGesture()
    {
        if (!timerResizing) return;
        timerResizing = false; timerResizeEdge = ""; CurrentPage.TimerPositionCustomized = true; SaveState();
        QueueTimerPromotion();
    }

    private void ArrangeTimerWidget()
    {
        if (TimerFrame is null) return;
        var config = CurrentTimerWidget;
        if (config is null)
        {
            HideTimerOverlay();
            TimerFrame.Visibility = Visibility.Collapsed;
            return;
        }
        TimerFrame.Visibility = Visibility.Visible;
        config.Kind = "timer"; if (string.IsNullOrWhiteSpace(config.Title)) config.Title = "POMODORO";
        double canvasWidth = WidgetArea.ActualWidth > 0 ? WidgetArea.ActualWidth : 720;
        double canvasHeight = WidgetArea.ActualHeight > 0 ? WidgetArea.ActualHeight : 720;
        double maxWidth = Math.Max(360, canvasWidth), maxHeight = Math.Max(300, canvasHeight);
        if (config.Width <= 0) config.Width = Math.Min(560, maxWidth);
        if (config.Height <= 0) config.Height = Math.Min(360, maxHeight);
        if (!CurrentPage.TimerPositionCustomized)
        {
            config.X = 12;
            config.Y = Settings.TimerAtBottom ? Math.Max(12, canvasHeight - config.Height - 12) : 12;
        }
        config.Width = Math.Clamp(config.Width, 360, maxWidth);
        config.Height = Math.Clamp(config.Height, 300, maxHeight);
        config.X = Math.Clamp(config.X, 0, Math.Max(0, canvasWidth - config.Width));
        config.Y = Math.Clamp(config.Y, 0, Math.Max(0, canvasHeight - config.Height));
        TimerFrame.Width = config.Width; TimerFrame.Height = config.Height;
        Canvas.SetLeft(TimerFrame, config.X); Canvas.SetTop(TimerFrame, config.Y);
        UpdateTimerOverlayPosition();
    }

    private void UpdateTimerOverlayPosition()
    {
        if (timerOverlay is null || !timerOverlay.IsOpen) return;
        var screen = WidgetArea.PointToScreen(new Point(Canvas.GetLeft(TimerFrame), Canvas.GetTop(TimerFrame)));
        var dpi = VisualTreeHelper.GetDpi(this);
        timerOverlay.HorizontalOffset = screen.X / dpi.DpiScaleX;
        timerOverlay.VerticalOffset = screen.Y / dpi.DpiScaleY;
        timerOverlay.Width = TimerFrame.Width; timerOverlay.Height = TimerFrame.Height;
    }
    private void AddWidgetClick(object sender, RoutedEventArgs e)
    {
        var choice = Dialogs.ChooseWidget(this);
        if (choice == 0) AddWindowClick(sender, e);
        else if (choice == 1)
        {
            var url = Dialogs.Prompt(this, "PÁGINA WEB", "URL HTTPS", "https://www.youtube.com");
            if (url is null) return;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") { Status("Introduce una URL HTTPS válida."); return; }
            AddCard(new() { Kind = "web", Title = uri.Host, Value = uri.AbsoluteUri }, true);
        }
        else if (choice == 2) AddCard(new() { Kind = "notes", Title = "NOTAS" }, true);
        else if (choice == 3) AddCard(new() { Kind = "stats", Title = "MI ENFOQUE" }, true);
        else if (choice == 4) AddCard(new() { Kind = "todo", Title = "TO DO" }, true);
        else if (choice == 5) AddCard(new() { Kind = "habits", Title = "HÁBITOS" }, true);
        else if (choice == 6) AddCard(new() { Kind = "calendar", Title = "AGENDA" }, true);
        else if (choice == 7) AddTimerWidget();
    }
    private void AddWindowClick(object sender, RoutedEventArgs e)
    {
        var candidate = Dialogs.PickWindow(this);
        if (candidate is null) return;
        var card = AddCard(new() { Kind = "window", Title = candidate.Title, Value = candidate.ProcessName }, true);
        Dispatcher.BeginInvoke(new Action(async () => await card.Attach(candidate.Handle)), DispatcherPriority.Loaded);
    }
    public WidgetCard AddCard(WidgetConfig config, bool save)
    {
        var newPlacement = config.Width <= 0 && config.Height <= 0 && config.X == 0 && config.Y == 0;
        var canvasWidth = WidgetArea.ActualWidth > 0 ? WidgetArea.ActualWidth : 720;
        if (config.Width <= 0)
        {
            var twoColumnWidth = (canvasWidth - 40) / 2;
            config.Width = canvasWidth >= 560 ? Math.Max(240, twoColumnWidth) : Math.Max(280, canvasWidth - 24);
        }
        if (config.Height <= 0) config.Height = config.Kind switch
        {
            "stats" => 260,
            "notes" => 360,
            "todo" => 400,
            "habits" => 390,
            "calendar" => 520,
            _ => 300
        };
        if (newPlacement)
        {
            int index = cards.Count;
            int columns = canvasWidth >= 560 ? 2 : 1;
            int column = index % columns;
            int row = index / columns;
            config.X = 12 + column * (config.Width + 16);
            config.Y = 12 + row * (config.Height + 16);
        }
        var card = new WidgetCard(this, config); cards.Add(card); WidgetArea.Children.Add(card);
        ArrangeCards();
        if (IsLoaded && !pageTransitioning) ShowInteractionOverlay(card);
        if (save)
        {
            SaveState();
            UpdatePageNavigation();
            AnimateWidgetArrival(card);
        }
        return card;
    }
    public void RemoveCard(WidgetCard card)
    {
        HideInteractionOverlay(card); HideOverlay(card);
        card.Release(); WidgetArea.Children.Remove(card); cards.Remove(card); ArrangeCards(); SaveState(); UpdatePageNavigation();
    }
    public void MoveCard(WidgetCard card, int direction)
    {
        var index = cards.IndexOf(card); var next = index + direction;
        if (next < 0 || next >= cards.Count) return;
        cards.RemoveAt(index); cards.Insert(next, card); ArrangeCards(); SaveState();
    }
    public void ArrangeCards()
    {
        Welcome.Visibility = cards.Count == 0 && CurrentTimerWidget is null ? Visibility.Visible : Visibility.Collapsed;
        Welcome.Width = Math.Max(1, WidgetArea.ActualWidth); Welcome.Height = Math.Max(1, WidgetArea.ActualHeight);
        foreach (var card in cards)
        {
            card.Width = Math.Clamp(card.Config.Width, 220, Math.Max(220, WidgetArea.ActualWidth));
            card.Height = Math.Clamp(card.Config.Collapsed ? 42 : card.Config.Height, 42, Math.Max(42, WidgetArea.ActualHeight));
            Canvas.SetLeft(card, Math.Clamp(card.Config.X, 0, Math.Max(0, WidgetArea.ActualWidth - card.Width)));
            Canvas.SetTop(card, Math.Clamp(card.Config.Y, 0, Math.Max(0, WidgetArea.ActualHeight - card.Height)));
        }
        ArrangeTimerWidget();
        UpdateOverlayPositions();
    }
    private void WidgetAreaSizeChanged(object sender, SizeChangedEventArgs e) => ArrangeCards();
    public void PersistWidget(WidgetCard card, bool save = true)
    {
        card.Config.X = Canvas.GetLeft(card); card.Config.Y = Canvas.GetTop(card); card.Config.Width = card.Width;
        if (!card.Config.Collapsed) card.Config.Height = card.Height;
        if (save) SaveState();
    }
    internal void BringCardToFront(WidgetCard card)
    {
        if (IsReportModalOpen) return;
        timerWantsFront = false;
        HideTimerOverlay();
        Panel.SetZIndex(TimerFrame, 0);
        foreach (var other in cards) Panel.SetZIndex(other, 0);
        Panel.SetZIndex(card, 1);
        if (cards.Any(c => c.IsExternalAttached))
        {
            if (card.IsExternalAttached)
            {
                foreach (var other in cards.Where(c => c != card)) HideOverlay(other);
                card.BringExternalToFront();
            }
            else ShowOverlay(card);
        }
        if (interactionOverlays.ContainsKey(card))
        {
            UpdateInteractionOverlayPosition(card);
            BringInteractionOverlayToFront(card);
        }
        else ShowInteractionOverlay(card);
    }

    private void BringTimerToFront()
    {
        if (exiting || !IsEnabled || IsReportModalOpen) return;
        timerWantsFront = true;
        foreach (var card in interactionOverlays.Keys.ToArray()) HideInteractionOverlay(card);
        foreach (var card in overlayCards.Keys.ToArray()) HideOverlay(card);
        foreach (var other in cards) Panel.SetZIndex(other, 0);
        Panel.SetZIndex(TimerFrame, 20);
        if (cards.Any(c => c.IsExternalAttached)) ShowTimerOverlay();
    }

    private void ShowTimerOverlay()
    {
        if (timerOverlay is not null)
        {
            // Keep the same native surface and mouse capture throughout a
            // press/drag. Changing IsOpen loses Button and Thumb input state.
            UpdateTimerOverlayPosition(); BringPopupToFront(timerOverlay); return;
        }
        if (timerPointerPressed || timerResizing || TimerFrame.IsMouseCaptureWithin) return;
        if (TimerFrame.Parent == WidgetArea) WidgetArea.Children.Remove(TimerFrame);
        var popup = new Popup
        {
            Child = TimerFrame, AllowsTransparency = true, StaysOpen = true,
            Placement = PlacementMode.Absolute, PlacementTarget = WidgetArea, PopupAnimation = PopupAnimation.None,
            Focusable = false
        };
        timerOverlay = popup;
        popup.Opened += (_, _) => BringPopupToFront(popup);
        popup.IsOpen = true;
        UpdateTimerOverlayPosition(); BringPopupToFront(timerOverlay);
    }

    private void HideTimerOverlay()
    {
        if (timerOverlay is null) return;
        timerOverlay.IsOpen = false; timerOverlay.Child = null; timerOverlay = null;
        if (!WidgetArea.Children.Contains(TimerFrame)) WidgetArea.Children.Add(TimerFrame);
        Panel.SetZIndex(TimerFrame, 20);
        ArrangeTimerWidget();
    }
    private void ShowOverlay(WidgetCard card)
    {
        if (overlayCards.TryGetValue(card, out var existing))
        {
            // Re-opening the popup recreates its native surface after an
            // embedded HWND may have changed the desktop z-order. Bring the
            // complete PomoDock card back above that native child as one unit.
            existing.IsOpen = false; existing.IsOpen = true; UpdateOverlayPosition(card); BringPopupToFront(existing); return;
        }
        if (card.Parent == WidgetArea) WidgetArea.Children.Remove(card);
        var popup = new Popup
        {
            Child = card, AllowsTransparency = true, StaysOpen = true, Placement = PlacementMode.Absolute,
            PopupAnimation = PopupAnimation.None, Focusable = false, IsOpen = true
        };
        popup.Opened += (_, _) => BringPopupToFront(popup);
        overlayCards[card] = popup;
        UpdateOverlayPosition(card);
        // Popup is a separate WPF HWND. It must be explicitly lifted above
        // hosted application windows; WPF Panel.ZIndex alone cannot cross the
        // native HWND boundary.
        BringPopupToFront(popup);
    }
    private void HideOverlay(WidgetCard card)
    {
        if (!overlayCards.Remove(card, out var popup)) return;
        popup.IsOpen = false; popup.Child = null;
        if (!WidgetArea.Children.Contains(card)) WidgetArea.Children.Add(card);
        Panel.SetZIndex(card, 0);
    }
    private void UpdateOverlayPositions()
    {
        foreach (var card in overlayCards.Keys.ToArray()) UpdateOverlayPosition(card);
        foreach (var card in interactionOverlays.Keys.ToArray()) UpdateInteractionOverlayPosition(card);
        UpdateTimerOverlayPosition();
        UpdateReportModalPosition();
    }
    private void UpdateOverlayPosition(WidgetCard card)
    {
        if (!overlayCards.TryGetValue(card, out var popup) || !popup.IsOpen) return;
        var screen = WidgetArea.PointToScreen(new Point(Canvas.GetLeft(card), Canvas.GetTop(card)));
        var dpi = VisualTreeHelper.GetDpi(this);
        popup.HorizontalOffset = screen.X / dpi.DpiScaleX;
        popup.VerticalOffset = screen.Y / dpi.DpiScaleY;
        popup.Width = card.Width; popup.Height = card.Height;
    }
    internal void UpdateCardOverlayPosition(WidgetCard card) => UpdateOverlayPosition(card);
    internal void UpdateCardInteractionOverlayPosition(WidgetCard card) => UpdateInteractionOverlayPosition(card);
    private void ShowInteractionOverlay(WidgetCard card)
    {
        if (interactionOverlays.TryGetValue(card, out var existing))
        {
            // Reopening tears down and recreates the popup window under the cursor, which
            // swallows the rest of the click: a note never received the caret that way.
            if (!existing.IsOpen) existing.IsOpen = true;
            UpdateInteractionOverlayPosition(card); BringPopupToFront(existing); return;
        }
        var root = new Grid { Width = card.Width, Height = card.Height, IsHitTestVisible = true };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) }); root.ColumnDefinitions.Add(new ColumnDefinition()); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        AddInteractionThumb(root, card, "NW", 0, 0, Cursors.SizeNWSE); AddInteractionThumb(root, card, "N", 0, 1, Cursors.SizeNS); AddInteractionThumb(root, card, "NE", 0, 2, Cursors.SizeNESW);
        AddInteractionThumb(root, card, "W", 1, 0, Cursors.SizeWE); AddInteractionThumb(root, card, "E", 1, 2, Cursors.SizeWE);
        AddInteractionThumb(root, card, "SW", 2, 0, Cursors.SizeNESW); AddInteractionThumb(root, card, "S", 2, 1, Cursors.SizeNS); AddInteractionThumb(root, card, "SE", 2, 2, Cursors.SizeNWSE);
        var popup = new Popup { Child = root, AllowsTransparency = true, StaysOpen = true, Placement = PlacementMode.Absolute, PopupAnimation = PopupAnimation.None, Focusable = false };
        popup.Opened += (_, _) => BringPopupToFront(popup);
        interactionOverlays[card] = popup; UpdateInteractionOverlayPosition(card);
        popup.IsOpen = true; BringPopupToFront(popup);
    }
    private static Thumb CreateInteractionThumb(Cursor cursor)
    {
        var visual = new FrameworkElementFactory(typeof(Border)); visual.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(2, 23, 25, 22)));
        return new Thumb { Cursor = cursor, IsHitTestVisible = true, Template = new ControlTemplate(typeof(Thumb)) { VisualTree = visual } };
    }
    private static void AddInteractionThumb(Grid root, WidgetCard card, string edge, int row, int column, Cursor cursor)
    {
        var thumb = CreateInteractionThumb(cursor); System.Windows.Automation.AutomationProperties.SetName(thumb, "Redimensionar " + edge);
        thumb.DragStarted += (_, _) => card.BeginOverlayResize(edge); thumb.DragDelta += (_, _) => card.UpdateOverlayResize(); thumb.DragCompleted += (_, _) => card.EndOverlayResize();
        Grid.SetRow(thumb, row); Grid.SetColumn(thumb, column); root.Children.Add(thumb);
    }
    private void HideInteractionOverlay(WidgetCard card)
    {
        if (!interactionOverlays.Remove(card, out var popup)) return;
        popup.IsOpen = false; popup.Child = null;
    }
    private void BringInteractionOverlayToFront(WidgetCard card)
    {
        if (interactionOverlays.TryGetValue(card, out var popup)) BringPopupToFront(popup);
    }
    private void UpdateInteractionOverlayPosition(WidgetCard card)
    {
        if (!interactionOverlays.TryGetValue(card, out var popup) || !popup.IsOpen) return;
        if (popup.Child is FrameworkElement root) { root.Width = card.Width; root.Height = card.Height; }
        var screen = WidgetArea.PointToScreen(new Point(Canvas.GetLeft(card), Canvas.GetTop(card))); var dpi = VisualTreeHelper.GetDpi(this);
        popup.HorizontalOffset = screen.X / dpi.DpiScaleX; popup.VerticalOffset = screen.Y / dpi.DpiScaleY; popup.Width = card.Width; popup.Height = card.Height;
    }
    private static void BringPopupToFront(Popup popup)
    {
        if (popup.Child is Visual visual && PresentationSource.FromVisual(visual) is HwndSource source)
            Win32.SetWindowPos(source.Handle, Win32.HWND_TOP, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }
    public void LoadLayout(List<WidgetConfig> widgets)
    {
        foreach (var card in cards.ToArray()) RemoveCard(card);
        var copy = JsonSerializer.Deserialize<List<WidgetConfig>>(JsonSerializer.Serialize(widgets))!;
        foreach (var config in copy) { config.Id = Guid.NewGuid(); AddCard(config, false); }
        SaveState(); UpdatePageNavigation(); Status("DISTRIBUCIÓN CARGADA · Reconecta las ventanas que quieras usar.");
    }
    public void ToggleFullscreen()
    {
        if (!fullscreen)
        {
            normalRect = new(Left, Top, Width, Height); previousState = WindowState;
            var bounds = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
            WindowState = WindowState.Normal; ResizeMode = ResizeMode.NoResize;
            ChromeRow.Height = new GridLength(0); TitleBar.Visibility = Visibility.Collapsed;
            var scale = VisualTreeHelper.GetDpi(this);
            Left = bounds.Left / scale.DpiScaleX; Top = bounds.Top / scale.DpiScaleY; Width = bounds.Width / scale.DpiScaleX; Height = bounds.Height / scale.DpiScaleY;
            Win32.SetWindowPos(hwnd, 0, bounds.Left, bounds.Top, bounds.Width, bounds.Height, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
            fullscreen = true;
        }
        else
        {
            fullscreen = false; ResizeMode = ResizeMode.CanResize;
            ChromeRow.Height = new GridLength(42); TitleBar.Visibility = Visibility.Visible;
            Left = normalRect.Left; Top = normalRect.Top; Width = normalRect.Width; Height = normalRect.Height; WindowState = previousState;
        }
        SaveState();
    }
    private void UpdateHotkey()
    {
        if (hwnd == 0 || exiting) return;
        bool belongs = Win32.GetAncestor(Win32.GetForegroundWindow(), 2) == hwnd;
        if (belongs && !hotkeyRegistered) hotkeyRegistered = Win32.RegisterHotKey(hwnd, 11, 0x4000, 0x7A);
        else if (!belongs && hotkeyRegistered) { Win32.UnregisterHotKey(hwnd, 11); hotkeyRegistered = false; }
    }
    private nint WindowMessages(nint h, int message, nint wp, nint lp, ref bool handled)
    {
        if (message == 0x0312 && wp == 11) { ToggleFullscreen(); handled = true; }
        return 0;
    }
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && reportPopup is { IsOpen: true }) { HideReportModal(); e.Handled = true; return; }
        if (e.Key == Key.F11 && !hotkeyRegistered) { ToggleFullscreen(); e.Handled = true; }
        if (e.Key == Key.Escape && fullscreen) { ToggleFullscreen(); e.Handled = true; }
        // While a note, a task or any field holds the caret the keyboard belongs to it:
        // a rich note is a RichTextBox, not a TextBox, so space used to start the timer
        // instead of being typed, and Ctrl+arrows changed page instead of moving a word.
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Left) { NavigateWorkspace(-1); e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Right) { NavigateWorkspace(1); e.Handled = true; return; }
        if (e.Key == Key.Space && CurrentTimerWidget is not null && Keyboard.FocusedElement is not ButtonBase) { ToggleTimer(); e.Handled = true; }
    }
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        pageSwipePoll.Stop();
        CancelTimerGesture(); HideReportModal(); HideTimerOverlay(); AgendaToast.CloseAll();
        foreach (var card in interactionOverlays.Keys.ToArray()) HideInteractionOverlay(card);
        foreach (var card in overlayCards.Keys.ToArray()) HideOverlay(card);
        try
        {
            foreach (var card in pageCards.Values.SelectMany(value => value).Concat(cards).Distinct()) card.Release();
        }
        catch (Exception ex) { e.Cancel = true; Status(ex.Message); return; }
        Sounds.StopNoise(); Timer.Pause(DateTimeOffset.UtcNow, Monotonic); SaveState();
        exiting = true; ticker.Stop(); statusReset.Stop(); SystemEvents.PowerModeChanged -= PowerChanged;
        Win32.UnregisterHotKey(hwnd, 11); if (focusHook != 0) Win32.UnhookWinEvent(focusHook);
        Sounds.Dispose(); Store.Dispose();
    }
    private void FullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void DragTitle(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) MaximizeClick(sender, e); else if (e.OriginalSource is TextBlock || e.OriginalSource == TitleBar) DragMove(); }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
