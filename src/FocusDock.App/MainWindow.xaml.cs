using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
    private readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<WidgetCard> cards = [];
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
    public MainWindow(string? data = null, bool diagnostic = false)
    {
        DiagnosticMode = diagnostic;
        Store = new(data ?? DataPath);
        Settings = Store.Read<Settings>("settings") ?? new(); Settings.Validate();
        Timer = new(Settings);
        InitializeComponent(); ApplyTheme();
        Width = Math.Max(MinWidth, Settings.WindowWidth); Height = Math.Max(MinHeight, Settings.WindowHeight);
        Left = Settings.WindowLeft; Top = Settings.WindowTop;
        Topmost = Settings.AlwaysOnTop;
        foregroundCallback = (_, _, _, _, _, _, _) => Dispatcher.BeginInvoke(UpdateHotkey);
        Loaded += OnLoaded;
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
        if (Settings.Sound) System.Media.SystemSounds.Exclamation.Play();
        Status(session.Phase == Phase.Focus ? "SESIÓN COMPLETA · Es momento de descansar." : "DESCANSO COMPLETO · Puedes volver a enfocarte.");
        var next = Timer.NextPhase();
        Dispatcher.BeginInvoke(() =>
        {
            Timer.Select(next, DateTimeOffset.UtcNow, Monotonic);
            if ((next == Phase.Focus && Settings.AutoFocus) || (next != Phase.Focus && Settings.AutoBreak)) Timer.Start(DateTimeOffset.UtcNow, Monotonic, selectedTask);
            AnimateTimer(); UpdateTimer(); SaveState();
        });
    }
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
        ContextButton.Content = Timer.Active is { } s ? $"●  {s.Project} / {s.Task}" : selectedTask is null ? "○  ENFOQUE LIBRE   /   Elegir tarea" : $"○  {selectedTask.Project} / {selectedTask.Name}";
    }
    private void StartClick(object sender, RoutedEventArgs e) => ToggleTimer();
    internal void ToggleTimer()
    {
        if (Timer.Running) { Timer.Pause(DateTimeOffset.UtcNow, Monotonic); Status("PAUSADO · Tu progreso está guardado."); }
        else { Timer.Start(DateTimeOffset.UtcNow, Monotonic, selectedTask); Status("EN CURSO · Una cosa a la vez."); }
        UpdateTimer(); SaveState();
    }
    private void PhaseClick(object sender, RoutedEventArgs e)
    {
        var phase = Enum.Parse<Phase>(((Button)sender).Tag.ToString()!);
        if (phase == Timer.Phase) return;
        Timer.Select(phase, DateTimeOffset.UtcNow, Monotonic); UpdateTimer(); AnimateTimer(); SaveState();
    }
    private void ResetClick(object sender, RoutedEventArgs e) { Timer.Select(Timer.Phase, DateTimeOffset.UtcNow, Monotonic); UpdateTimer(); SaveState(); Status("REINICIADO · El tiempo trabajado se guardó como parcial."); }
    private void SkipClick(object sender, RoutedEventArgs e) { var next = Timer.NextPhase(); Timer.Select(next, DateTimeOffset.UtcNow, Monotonic); UpdateTimer(); AnimateTimer(); SaveState(); }
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
            Timer.Pause(DateTimeOffset.UtcNow, Monotonic); Timer.Finish(Outcome.Partial, DateTimeOffset.UtcNow);
            selectedTask = dialog.SelectedTask; Status("CONTEXTO ACTUALIZADO · Inicia la siguiente sesión.");
        }
        UpdateTimer(); SaveState();
    }
    private void ReportClick(object sender, RoutedEventArgs e) { new ReportWindow(this).ShowDialog(); }
    private void SettingsClick(object sender, RoutedEventArgs e) { new SettingsWindow(this).ShowDialog(); Settings.Validate(); ApplyTheme(); Topmost = Settings.AlwaysOnTop; UpdateTimer(); SaveState(); }
    private void LayoutsClick(object sender, RoutedEventArgs e) { new LayoutsWindow(this).ShowDialog(); }
    public void ApplyTheme()
    {
        var resources = Application.Current.Resources;
        string[] keys = ["Paper", "Ink", "Muted", "Surface", "Line"];
        string[] colors = Settings.Dark ? ["#191B18", "#EEEEE5", "#AFB3A4", "#252822", "#C2C6B8"] : ["#F1F0E9", "#171916", "#66695E", "#FAF9F3", "#171916"];
        for (int i = 0; i < keys.Length; i++) resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
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
        Dispatcher.BeginInvoke(() => card.Attach(candidate.Handle), DispatcherPriority.Loaded);
    }
    public WidgetCard AddCard(WidgetConfig config, bool save)
    {
        var card = new WidgetCard(this, config); cards.Add(card); WidgetArea.Children.Add(card);
        ArrangeCards(); if (save) SaveState(); return card;
    }
    public void RemoveCard(WidgetCard card) { card.Release(); WidgetArea.Children.Remove(card); cards.Remove(card); ArrangeCards(); SaveState(); }
    public void MoveCard(WidgetCard card, int direction)
    {
        var index = cards.IndexOf(card); var next = index + direction;
        if (next < 0 || next >= cards.Count) return;
        cards.RemoveAt(index); cards.Insert(next, card); ArrangeCards(); SaveState();
    }
    public void ArrangeCards()
    {
        foreach (var split in WidgetArea.Children.OfType<GridSplitter>().ToArray()) WidgetArea.Children.Remove(split);
        WidgetArea.RowDefinitions.Clear(); Welcome.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < cards.Count; i++)
        {
            var config = cards[i].Config;
            WidgetArea.RowDefinitions.Add(new RowDefinition { Height = config.Collapsed ? new GridLength(40) : new GridLength(Math.Max(0.1, config.Weight), GridUnitType.Star), MinHeight = config.Collapsed ? 40 : 90 });
            Grid.SetRow(cards[i], i * 2);
            if (i < cards.Count - 1)
            {
                WidgetArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
                var splitter = new GridSplitter { Height = 8, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
                Grid.SetRow(splitter, i * 2 + 1);
                splitter.DragCompleted += (_, _) => { for (int j = 0; j < cards.Count; j++) if (!cards[j].Config.Collapsed) cards[j].Config.Weight = WidgetArea.RowDefinitions[j * 2].ActualHeight; SaveState(); };
                WidgetArea.Children.Add(splitter);
            }
        }
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
        try { foreach (var card in cards) card.Release(); }
        catch (Exception ex) { e.Cancel = true; Status(ex.Message); return; }
        Timer.Pause(DateTimeOffset.UtcNow, Monotonic); SaveState();
        exiting = true; ticker.Stop(); SystemEvents.PowerModeChanged -= PowerChanged;
        Win32.UnregisterHotKey(hwnd, 11); if (focusHook != 0) Win32.UnhookWinEvent(focusHook);
        Store.Dispose();
    }
    private void FullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void DragTitle(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) MaximizeClick(sender, e); else if (e.OriginalSource is TextBlock || e.OriginalSource == TitleBar) DragMove(); }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
