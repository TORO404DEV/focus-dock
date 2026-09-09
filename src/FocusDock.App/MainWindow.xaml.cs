using System.Diagnostics;
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
using FocusDock.Core;
using FocusDock.App.Native;
using Microsoft.Win32;

namespace FocusDock.App;

public partial class MainWindow : Window
{
    public static string DataPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusDock");
    public Store Store { get; }
    public Settings Settings { get; }
    public TimerEngine Timer { get; }
    public SoundEngine Sounds { get; }
    private readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<WidgetCard> cards = [];
    private readonly Dictionary<WidgetCard, Popup> overlayCards = [];
    private WorkTask? selectedTask;
    private int ticks;
    private bool fullscreen, exiting;
    private Rect normalRect;
    private WindowState previousState;
    private nint hwnd, focusHook;
    private readonly Win32.WinEventProc foregroundCallback;
    private bool hotkeyRegistered;
    public bool DiagnosticMode { get; }
    public static double Monotonic => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public string Journal => Path.Combine(Store.DirectoryPath, "windows.json");
    internal Canvas WidgetCanvas => WidgetArea;
    public MainWindow(string? data = null, bool diagnostic = false)
    {
        DiagnosticMode = diagnostic;
        Store = new(data ?? DataPath);
        Settings = Store.Read<Settings>("settings") ?? new(); Settings.Validate();
        Timer = new(Settings);
        Sounds = new(Settings);
        InitializeComponent(); ApplyTimerPosition(); ApplyTheme();
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
        foreach (var config in Settings.Widgets) AddCard(config, false);
        ArrangeCards(); ticker.Start(); UpdateHotkey();
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
        Settings.Widgets = cards.Select(c => c.Config).ToList();
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
    public void Status(string text) => StatusText.Text = text;
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
    private void ReportClick(object sender, RoutedEventArgs e) { new ReportWindow(this).ShowDialog(); }
    private void SettingsClick(object sender, RoutedEventArgs e) { new SettingsWindow(this).ShowDialog(); Settings.Validate(); ApplyTimerPosition(); ApplyTheme(); Topmost = Settings.AlwaysOnTop; UpdateTimer(); SaveState(); }
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
    }
    private void ApplyTimerPosition()
    {
        bool bottom = Settings.TimerAtBottom;
        Grid.SetRow(Workspace, bottom ? 2 : 3); Grid.SetRow(TimerFrame, bottom ? 3 : 2);
        Root.RowDefinitions[2].Height = bottom ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        Root.RowDefinitions[3].Height = bottom ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
    }
    private void AddWidgetClick(object sender, RoutedEventArgs e)
    {
        var choice = Dialogs.Choose(this, "AÑADIR WIDGET", ["Ventana de otra app", "Página web / YouTube", "Notas", "Métricas de enfoque"]);
        if (choice == 0) AddWindowClick(sender, e);
        else if (choice == 1)
        {
            var url = Dialogs.Prompt(this, "PÁGINA WEB", "URL HTTPS", "https://www.youtube.com");
            if (url is null) return;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") { Status("Introduce una URL HTTPS válida."); return; }
            AddCard(new() { Kind = "web", Title = uri.Host, Value = uri.AbsoluteUri }, true);
        }
        else if (choice >= 2) AddCard(new() { Kind = choice == 2 ? "notes" : "stats", Title = choice == 2 ? "NOTAS" : "MI ENFOQUE" }, true);
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
        if (config.Height <= 0) config.Height = config.Kind == "stats" ? 260 : 300;
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
        ArrangeCards(); if (save) SaveState(); return card;
    }
    public void RemoveCard(WidgetCard card)
    {
        HideOverlay(card);
        card.Release(); WidgetArea.Children.Remove(card); cards.Remove(card); ArrangeCards(); SaveState();
    }
    public void MoveCard(WidgetCard card, int direction)
    {
        var index = cards.IndexOf(card); var next = index + direction;
        if (next < 0 || next >= cards.Count) return;
        cards.RemoveAt(index); cards.Insert(next, card); ArrangeCards(); SaveState();
    }
    public void ArrangeCards()
    {
        Welcome.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Welcome.Width = Math.Max(1, WidgetArea.ActualWidth); Welcome.Height = Math.Max(1, WidgetArea.ActualHeight);
        foreach (var card in cards)
        {
            card.Width = Math.Clamp(card.Config.Width, 220, Math.Max(220, WidgetArea.ActualWidth));
            card.Height = Math.Clamp(card.Config.Collapsed ? 42 : card.Config.Height, 42, Math.Max(42, WidgetArea.ActualHeight));
            Canvas.SetLeft(card, Math.Clamp(card.Config.X, 0, Math.Max(0, WidgetArea.ActualWidth - card.Width)));
            Canvas.SetTop(card, Math.Clamp(card.Config.Y, 0, Math.Max(0, WidgetArea.ActualHeight - card.Height)));
        }
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
    }
    private void ShowOverlay(WidgetCard card)
    {
        if (overlayCards.TryGetValue(card, out var existing))
        {
            existing.IsOpen = false; existing.IsOpen = true; UpdateOverlayPosition(card); return;
        }
        if (card.Parent == WidgetArea) WidgetArea.Children.Remove(card);
        var popup = new Popup
        {
            Child = card, AllowsTransparency = true, StaysOpen = true, Placement = PlacementMode.Absolute,
            PopupAnimation = PopupAnimation.None, Focusable = false, IsOpen = true
        };
        overlayCards[card] = popup;
        UpdateOverlayPosition(card);
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
    public void LoadLayout(List<WidgetConfig> widgets)
    {
        foreach (var card in cards.ToArray()) RemoveCard(card);
        var copy = JsonSerializer.Deserialize<List<WidgetConfig>>(JsonSerializer.Serialize(widgets))!;
        foreach (var config in copy) { config.Id = Guid.NewGuid(); AddCard(config, false); }
        SaveState(); Status("DISTRIBUCIÓN CARGADA · Reconecta las ventanas que quieras usar.");
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
        if (e.Key == Key.F11 && !hotkeyRegistered) { ToggleFullscreen(); e.Handled = true; }
        if (e.Key == Key.Escape && fullscreen) { ToggleFullscreen(); e.Handled = true; }
        if (e.Key == Key.Space && e.OriginalSource is not TextBox && e.OriginalSource is not Button) { ToggleTimer(); e.Handled = true; }
    }
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        foreach (var card in overlayCards.Keys.ToArray()) HideOverlay(card);
        try { foreach (var card in cards) card.Release(); }
        catch (Exception ex) { e.Cancel = true; Status(ex.Message); return; }
        Sounds.StopNoise(); Timer.Pause(DateTimeOffset.UtcNow, Monotonic); SaveState();
        exiting = true; ticker.Stop(); SystemEvents.PowerModeChanged -= PowerChanged;
        Win32.UnregisterHotKey(hwnd, 11); if (focusHook != 0) Win32.UnhookWinEvent(focusHook);
        Sounds.Dispose(); Store.Dispose();
    }
    private void FullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void DragTitle(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) MaximizeClick(sender, e); else if (e.OriginalSource is TextBlock || e.OriginalSource == TitleBar) DragMove(); }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
