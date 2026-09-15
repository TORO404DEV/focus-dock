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
    /// <summary>Set while an approved agent plan runs so UI dialogs are not shown again per step.</summary>
    internal bool AgentPlanApproved { get; set; }
    public TimerEngine Timer { get; }
    public SoundEngine Sounds { get; }
    private readonly AppUsageWatch appUsage = new();
    private readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer overlayZ = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private int overlayLiftPause;
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
    private AgentWindow? agentWindow;
    private WidgetCard? agentCard;
    private AgentWakeListen? wakeListen;
    private int agentListenPause;
    private readonly DispatcherTimer listenPulse = new() { Interval = TimeSpan.FromMilliseconds(80) };
    internal Guid? LastCreatedWidgetId { get; private set; }
    internal bool AgentCreatedWidgetThisTurn { get; set; }
    private readonly List<Popup> reportBackdrops = [];
    private ReportWindow? reportContent;
    private bool reportHidTimerOverlay;
    private WidgetCard[] reportHiddenOverlays = [];
    private WidgetCard[] reportHiddenInteractionOverlays = [];
    private bool timerPointerPressed, timerWantsFront;
    private readonly VoiceDictation voiceDictation;
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
        // Remember the last focus task across restarts until the user clears it on purpose.
        selectedTask = Settings.SelectedTaskId is { } remembered
            ? Settings.Tasks.FirstOrDefault(task => task.Id == remembered && !task.Template)
            : null;
        // The language has to be on before anything draws itself, or the first paint speaks Spanish.
        Strings.Use(Settings.Language);
        Timer = new(Settings);
        Sounds = new(Settings);
        InitializeComponent();
        ReportButton.Content = ChromeIcon.Report();
        TasksButton.Content = ChromeIcon.Tasks();
        PageMapButton.Content = ChromeIcon.PageMap();
        voiceDictation = VoiceDictation.Attach(this);
        InitializeNotifications();
        ApplyLanguage();
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
        foregroundCallback = (_, _, _, _, _, _, _) => Dispatcher.BeginInvoke(() =>
        {
            UpdateHotkey();
            appUsage.Observe(Timer.Running && Timer.Phase == Phase.Focus, DateTimeOffset.UtcNow);
        });
        Loaded += OnLoaded;
        LocationChanged += (_, _) => UpdateOverlayPositions();
        SizeChanged += (_, _) => UpdateOverlayPositions();
        StateChanged += (_, _) => UpdateOverlayPositions();
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
        listenPulse.Tick += (_, _) =>
        {
            AgentButton.Opacity = 0.48 + 0.52 * (0.5 + 0.5 * Math.Sin(Monotonic * 8));
        };
        Timer.GapDetected += () => Status(L.T("status.gapDetected"));
        Timer.Finished += OnFinished;
        if (Store.Read<Session>("checkpoint") is { } checkpoint && !Store.Sessions().Any(s => s.Id == checkpoint.Id))
        { Timer.Restore(checkpoint); Status(L.T("status.recovered")); }
        ticker.Tick += (_, _) =>
        {
            Timer.Tick(DateTimeOffset.UtcNow, Monotonic);
            appUsage.Observe(Timer.Running && Timer.Phase == Phase.Focus, DateTimeOffset.UtcNow);
            UpdateTimer();
            if (++ticks % 15 == 0) SaveState();
            if (ticks % 5 == 0 && !IsReportModalOpen && !pageMapOpen)
                foreach (var card in cards.ToArray()) card.Refresh();
            if (overlayLiftPause == 0) LiftOverlaysAboveNatives();
        };
        overlayZ.Tick += (_, _) => { if (overlayLiftPause == 0) LiftOverlaysAboveNatives(); };
        statusReset.Tick += (_, _) => { statusReset.Stop(); UpdatePageMeta(); };
        PageDock.PreviewMouseWheel += (_, e) =>
        {
            bool vertical = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (e.Delta < 0) NavigateWorkspace(vertical ? 0 : 1, vertical ? 1 : 0);
            else if (e.Delta > 0) NavigateWorkspace(vertical ? 0 : -1, vertical ? -1 : 0);
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
        InitializeWorkspacePages(); ticker.Start(); overlayZ.Start(); UpdateHotkey();
        if (Settings.AgentWidgetVisible) ShowAgentWidget();
        SyncAgentListen();
        // Calendar reminders ring on their own clock: they never depend on a widget being visible.
        AgendaReminders.For(Store).Attach(this);
        if (Settings.Fullscreen) ToggleFullscreen();
        else if (Settings.WindowMaximized) WindowState = WindowState.Maximized;
        ResumeTimerIfNeeded();
        // The guardian is this same program started again. A host that merely embeds the window
        // (a preview harness, a test runner) would be relaunched with arguments it does not know,
        // and could not recover anyone's windows anyway.
        bool runningAsPomoDock = string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "PomoDock", StringComparison.OrdinalIgnoreCase);
        if (!DiagnosticMode && runningAsPomoDock)
        {
            try
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                start.ArgumentList.Add("--guardian"); start.ArgumentList.Add(Environment.ProcessId.ToString()); start.ArgumentList.Add(Journal);
                Process.Start(start)?.Dispose();
            }
            catch (Exception ex) { Status(L.T("status.recoveryFailed", ex.Message)); }
            WindowsStartup.Sync(Settings.StartWithWindows);
        }
    }

    /// <summary>Picks up a pomodoro that was running when the app last saved state.</summary>
    private void ResumeTimerIfNeeded()
    {
        if (DiagnosticMode || !Settings.TimerWasRunning || Timer.Active is null || Timer.Running) return;
        Timer.Start(DateTimeOffset.UtcNow, Monotonic, selectedTask);
        if (Timer.Phase == Phase.Focus) Sounds.StartNoise();
        appUsage.Observe(Timer.Phase == Phase.Focus, DateTimeOffset.UtcNow);
        UpdateTimer();
        Status(L.T("status.running"));
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) Dispatcher.Invoke(() => { Timer.Pause(DateTimeOffset.UtcNow, Monotonic); SaveState(); Status(L.T("status.suspended")); });
    }
    public void SaveState()
    {
        SaveCurrentWorkspacePage();
        if (!fullscreen && WindowState == WindowState.Normal) { Settings.WindowLeft = Left; Settings.WindowTop = Top; Settings.WindowWidth = Width; Settings.WindowHeight = Height; }
        if (!fullscreen) Settings.WindowMaximized = WindowState == WindowState.Maximized;
        Settings.Fullscreen = fullscreen;
        Settings.SelectedTaskId = selectedTask?.Id;
        if (!exiting) Settings.TimerWasRunning = Timer.Running;
        Store.Write("settings", Settings); Store.Write("checkpoint", Timer.Active);
    }

    /// <summary>Sets the focus task. Only free-focus (null) or picking another task clears the previous one.</summary>
    private void SetSelectedTask(WorkTask? task)
    {
        selectedTask = task;
        task?.Touch();
        Settings.SelectedTaskId = task?.Id;
    }
    private void OnFinished(Session session)
    {
        if (session.Phase == Phase.Focus)
        {
            session.Apps = appUsage.Take(DateTimeOffset.UtcNow);
            if (session.TaskId is { } id)
                Settings.Tasks.FirstOrDefault(task => task.Id == id)?.Touch(session.Ended);
        }
        Store.Complete(session);
        if (session.Outcome != Outcome.Completed) return;
        Sounds.StopNoise();
        Sounds.Completed(session.Phase);
        Status(session.Phase == Phase.Focus ? L.T("status.focusComplete") : L.T("status.breakComplete"));
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
            AnnounceTimerComplete(session.Phase, next);
        });
    }
    /// <summary>
    /// The colour of the phase the timer is in. The three colours are pastels picked for paper, so
    /// in the dark they are laid over the card instead of painted onto it: the phase still reads as
    /// a colour, and the clock stays light on dark instead of light on a pale slab.
    /// </summary>
    private Brush PhaseBrush()
    {
        var phase = Theme.Parse(Timer.Phase == Phase.Focus ? Settings.FocusColor : Timer.Phase == Phase.ShortBreak ? Settings.ShortBreakColor : Settings.LongBreakColor, Colors.Transparent);
        return new SolidColorBrush(Settings.Dark ? Theme.Mix(phase, Theme.Of("Surface"), .13) : phase);
    }
    private void UpdateTimer()
    {
        UpdateHeaderClock();
        var remaining = TimeSpan.FromSeconds(Math.Ceiling(Timer.Remaining));
        ClockText.Text = $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
        StartButton.Content = Timer.Running ? L.T("timer.pause") : Timer.Active is null ? L.T("timer.start") : L.T("timer.resume");
        var label = Timer.Phase == Phase.Focus ? L.T("timer.phaseFocusName") : Timer.Phase == Phase.ShortBreak ? L.T("timer.phaseShort") : L.T("timer.phaseLong");
        PhaseLabel.Text = $"{Timer.CompletedFocus + 1:00} / {label}";
        CycleText.Text = L.T("timer.cycle", Timer.CompletedFocus % Settings.LongInterval + 1, Settings.LongInterval);
        foreach (var button in new[] { FocusButton, ShortButton, LongButton })
        {
            bool active = button.Tag.ToString() == Timer.Phase.ToString();
            button.SetResourceReference(BackgroundProperty, active ? "Ink" : "Surface");
            button.SetResourceReference(ForegroundProperty, active ? "Paper" : "Ink");
        }
        var phaseBrush = PhaseBrush(); TimerFrame.Background = phaseBrush; TimerSurface.Background = phaseBrush;
        ContextButton.Content = Timer.Active is { } s ? L.T("timer.contextActive", s.Project, s.Task) : selectedTask is null ? L.T("timer.freeFocusButton") : L.T("timer.contextChosen", selectedTask.Project, selectedTask.Name);
    }
    private void StartClick(object sender, RoutedEventArgs e) { Sounds.Button(Timer.Running ? "pause" : "start"); ToggleTimer(); }
    internal void ToggleTimer()
    {
        if (Timer.Running)
        {
            Timer.Pause(DateTimeOffset.UtcNow, Monotonic);
            appUsage.Observe(false, DateTimeOffset.UtcNow);
            Sounds.StopNoise();
            Status(L.T("status.paused"));
        }
        else
        {
            // Starting the next phase acknowledges the alarm that announced the previous one.
            AcknowledgeTimerAlerts();
            Timer.Start(DateTimeOffset.UtcNow, Monotonic, selectedTask);
            if (Timer.Phase == Phase.Focus) Sounds.StartNoise();
            appUsage.Observe(Timer.Phase == Phase.Focus, DateTimeOffset.UtcNow);
            Status(L.T("status.running"));
        }
        UpdateTimer(); SaveState();
    }
    private void PhaseClick(object sender, RoutedEventArgs e)
    {
        var phase = Enum.Parse<Phase>(((Button)sender).Tag.ToString()!);
        if (phase == Timer.Phase) return;
        Sounds.Button("phase"); Sounds.StopNoise(); Timer.Select(phase, DateTimeOffset.UtcNow, Monotonic); UpdateTimer(); AnimateTimer(); SaveState();
    }
    private void ResetClick(object sender, RoutedEventArgs e) { Sounds.Button("reset"); Sounds.StopNoise(); Timer.Select(Timer.Phase, DateTimeOffset.UtcNow, Monotonic); UpdateTimer(); SaveState(); Status(L.T("status.reset")); }
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
        var culture = Strings.Culture;
        HeaderDateText.Text = now.ToString("ddd d MMM", culture).ToUpper(culture);
        HeaderTimeText.Text = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        // Always show how much of the active pomodoro remains — not only wall-clock time.
        if (Timer.Running || Timer.Active is not null)
        {
            var remaining = TimeSpan.FromSeconds(Math.Ceiling(Timer.Remaining));
            string mark = Timer.Running ? "❚❚" : "▶";
            HeaderTimerButton.Content = $"{mark} {(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
            HeaderTimerButton.Visibility = Visibility.Visible;
            HeaderTimerButton.SetResourceReference(Control.ForegroundProperty, Timer.Running ? "Ink" : "Muted");
            HeaderTimerButton.ToolTip = Timer.Running ? L.T("chrome.headerTimerPause") : L.T("chrome.headerTimerResume");
        }
        else
        {
            HeaderTimerButton.Content = "";
            HeaderTimerButton.ToolTip = null;
            HeaderTimerButton.Visibility = Visibility.Collapsed;
        }
    }
    private void HeaderTimerClick(object sender, RoutedEventArgs e)
    {
        Sounds.Button(Timer.Running ? "pause" : "start");
        ToggleTimer();
        e.Handled = true;
    }
    private void TasksClick(object sender, RoutedEventArgs e)
    {
        var dialog = new TasksWindow(this, selectedTask); ShowWorkspaceDialog(dialog);
        if (dialog.SelectionChanged)
        {
            string? status = null;
            bool applied = false;
            RunModal(() => applied = TryApplyTaskSwitch(dialog.SelectedTask, out status));
            if (applied && status is not null) Status(status);
        }
        // Deleted while selected → drop it; otherwise keep the remembered task.
        if (selectedTask is not null && !Settings.Tasks.Any(task => task.Id == selectedTask.Id))
            SetSelectedTask(null);
        UpdateTimer(); SaveState();
    }

    /// <summary>
    /// Applies a task/project change. If a pomodoro is in progress, asks whether to close it
    /// under the previous task or keep the clock and move it to the new one.
    /// </summary>
    private bool TryApplyTaskSwitch(WorkTask? next, out string status)
    {
        status = L.T("status.contextUpdated");
        if (selectedTask?.Id == next?.Id)
        {
            SetSelectedTask(next);
            return true;
        }

        if (Timer.Active is null)
        {
            SetSelectedTask(next);
            return true;
        }

        int choice = Dialogs.Choose(this, L.T("tasks.switchTimerTitle"),
        [
            L.T("tasks.switchTimerSavePrevious"),
            L.T("tasks.switchTimerKeepMoving"),
            L.T("common.cancel")
        ]);
        if (choice is < 0 or 2) return false;

        if (choice == 0)
        {
            Sounds.StopNoise();
            Timer.Pause(DateTimeOffset.UtcNow, Monotonic);
            Timer.Finish(Outcome.Partial, DateTimeOffset.UtcNow);
            SetSelectedTask(next);
            status = L.T("status.contextUpdated");
            return true;
        }

        Timer.Rebind(next);
        SetSelectedTask(next);
        status = L.T("status.contextMoved");
        return true;
    }
    private void ReportClick(object sender, RoutedEventArgs e) => ShowReportModal();

    private void AgentClick(object sender, RoutedEventArgs e)
    {
        Sounds.Button("click");
        if (agentCard is { Visibility: Visibility.Visible }) HideAgentWidget();
        else ShowAgentWidget();
    }

    internal void ShowAgentWidget()
    {
        EnsureAgentHost();
        if (agentCard is null)
        {
            var config = Settings.AgentWidget ?? new WidgetConfig { Kind = "agent", Title = L.T("agent.name"), Width = 360, Height = 520 };
            config.Kind = "agent";
            if (config.Width < 280) config.Width = 360;
            if (config.Height < 280) config.Height = 520;
            if (config.X == 0 && config.Y == 0 && WidgetArea.ActualHeight > 0)
            {
                config.X = 16;
                config.Y = Math.Max(16, WidgetArea.ActualHeight - config.Height - 24);
            }
            agentCard = new WidgetCard(this, config);
            WidgetArea.Children.Add(agentCard);
            AnimateWidgetArrival(agentCard);
        }
        agentCard.Visibility = Visibility.Visible;
        Settings.AgentWidgetVisible = true;
        ArrangeCards();
        Panel.SetZIndex(agentCard, 20);
        if (IsLoaded && !pageTransitioning) BringCardToFront(agentCard);
        SaveState();
    }

    internal void HideAgentWidget()
    {
        if (agentCard is null) return;
        PersistWidget(agentCard, false);
        Settings.AgentWidget = agentCard.Config;
        Settings.AgentWidgetVisible = false;
        // Collapse first: HideOverlay can run layout, and SyncAgentAirspace
        // would otherwise float the chat again while it is still Visible.
        agentCard.Visibility = Visibility.Collapsed;
        HideInteractionOverlay(agentCard);
        HideOverlay(agentCard);
        SaveState();
    }

    internal void AttachAgentBody(Grid body)
    {
        EnsureAgentHost();
        if (agentWindow!.Surface.Parent is Panel parent) parent.Children.Remove(agentWindow.Surface);
        body.ClipToBounds = true; body.MinWidth = 0; body.MinHeight = 0;
        agentWindow.Surface.HorizontalAlignment = HorizontalAlignment.Stretch;
        agentWindow.Surface.VerticalAlignment = VerticalAlignment.Stretch;
        agentWindow.Surface.MinWidth = 0; agentWindow.Surface.MinHeight = 0;
        body.Children.Clear();
        body.Children.Add(agentWindow.Surface);
    }

    private void EnsureAgentHost()
    {
        agentWindow ??= new AgentWindow(this, HideAgentWidget);
    }

    internal void SetAgentListenEnabled(bool on)
    {
        Settings.AgentListenEnabled = on;
        SaveState();
        SyncAgentListen();
    }

    internal void SyncAgentListen()
    {
        try
        {
            if (Settings.AgentListenEnabled)
            {
                wakeListen ??= new AgentWakeListen(this);
                wakeListen.Changed -= OnAgentListenChanged;
                wakeListen.Changed += OnAgentListenChanged;
                wakeListen.Logged -= OnAgentListenLog;
                wakeListen.Logged += OnAgentListenLog;
                wakeListen.SetEnabled(true);
            }
            else wakeListen?.SetEnabled(false);
        }
        catch (Exception ex)
        {
            Settings.AgentListenEnabled = false;
            try { wakeListen?.SetEnabled(false); } catch { }
            Status(ex.Message);
        }
        PaintAgentListen();
    }

    internal void PauseAgentListen()
    {
        agentListenPause++;
        if (agentListenPause == 1) wakeListen?.Pause();
    }

    internal void ResumeAgentListen()
    {
        agentListenPause = Math.Max(0, agentListenPause - 1);
        if (agentListenPause == 0) wakeListen?.Resume();
    }

    internal void HandleWakeCommand(string command)
    {
        if (command.Length == 0)
        {
            Status(L.T("agent.listenHearing"));
            return;
        }
        Status(L.T("agent.listenHeard", command));
        Sounds.Button("click");
        ShowAgentWidget();
        EnsureAgentHost();
        agentWindow!.AcceptSpoken(command);
    }

    internal void PaintAgentListen()
    {
        bool on = Settings.AgentListenEnabled;
        var state = wakeListen?.State ?? AgentListenState.Off;
        bool live = on && state is AgentListenState.Armed or AgentListenState.Working;
        if (on)
        {
            AgentButton.Background = (Brush)FindResource("Ink");
            AgentButton.Foreground = (Brush)FindResource("Paper");
            AgentButton.ToolTip = L.T("agent.openListening");
        }
        else
        {
            AgentButton.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
            AgentButton.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
            AgentButton.ToolTip = L.T("agent.open");
        }
        if (live && !Settings.ReduceMotion)
        {
            if (!listenPulse.IsEnabled) listenPulse.Start();
        }
        else
        {
            listenPulse.Stop();
            AgentButton.Opacity = 1;
        }
        agentWindow?.PaintListen();
    }

    private void OnAgentListenChanged() => PaintAgentListen();

    internal IReadOnlyList<AgentListenLogEntry> ListenLog => wakeListen?.History ?? [];

    internal void OnAgentListenLog(AgentListenLogEntry entry)
    {
        string line = FormatListenLog(entry);
        Status(line);
        agentWindow?.AppendListenLog(entry);
    }

    internal static string FormatListenLog(AgentListenLogEntry entry)
    {
        string body = entry.Kind switch
        {
            AgentListenLogKind.Music => L.T("agent.listenLogMusic"),
            AgentListenLogKind.Noise => string.IsNullOrWhiteSpace(entry.Detail) ? L.T("agent.listenLogNoise") : L.T("agent.listenLogNoiseDetail", entry.Detail),
            AgentListenLogKind.Transcript => L.T("agent.listenLogHeard", entry.Detail),
            AgentListenLogKind.Wake => L.T("agent.listenLogWake", entry.Detail),
            AgentListenLogKind.Command => L.T("agent.listenLogCommand", entry.Detail),
            AgentListenLogKind.Armed => L.T("agent.listenLogArmed"),
            AgentListenLogKind.Timeout => L.T("agent.listenLogTimeout"),
            AgentListenLogKind.Error => L.T("agent.listenLogError", entry.Detail),
            AgentListenLogKind.Miss => L.T("agent.listenLogMiss", entry.Detail),
            _ => entry.Detail
        };
        return $"{entry.At:HH:mm:ss}  {body}";
    }

    internal void ShowCreatedWork(Guid? widgetId)
    {
        Status(L.T("agent.showingWork"));
        if (widgetId is { } id)
        {
            var card = cards.FirstOrDefault(item => item.Config.Id == id);
            if (card is not null)
            {
                BringCardToFront(card);
                Pulse(card);
            }
        }
        else PulsePageChrome();
        SyncAgentAirspace();
    }

    private void ShowReportModal()
    {
        if (reportPopup is { IsOpen: true } existing)
        {
            BringReportLayerToFront();
            return;
        }
        CancelTimerGesture();
        // Timer/widget overlays are independent HWNDs; without parking them the
        // floating pomodoro (and Brave chrome) sit above the report popup.
        reportHidTimerOverlay = timerOverlay is not null;
        reportHiddenOverlays = overlayCards.Keys.ToArray();
        reportHiddenInteractionOverlays = interactionOverlays.Keys.ToArray();
        HideTimerOverlay();
        foreach (var card in reportHiddenInteractionOverlays) HideInteractionOverlay(card);
        foreach (var card in reportHiddenOverlays) HideOverlay(card);

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
        popup.Opened += (_, _) => { UpdateReportModalPosition(); BringReportLayerToFront(); Keyboard.Focus(panel); };
        popup.IsOpen = true;
        BringReportLayerToFront();
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
        if (!exiting)
        {
            foreach (var card in reportHiddenOverlays.Where(card => cards.Contains(card) || card == agentCard))
                ShowOverlay(card);
            foreach (var card in reportHiddenInteractionOverlays.Where(card => cards.Contains(card) || card == agentCard))
                ShowInteractionOverlay(card);
            if (reportHidTimerOverlay && CurrentTimerWidget is not null) ShowTimerOverlay();
            SyncAgentAirspace();
            Focus();
        }
        reportHidTimerOverlay = false;
        reportHiddenOverlays = [];
        reportHiddenInteractionOverlays = [];
    }

    private void BringReportLayerToFront()
    {
        foreach (var shade in reportBackdrops) BringPopupToFront(shade);
        if (reportPopup is not null) BringPopupToFront(reportPopup);
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
            double horizontal = left / dpi.DpiScaleX;
            double vertical = top / dpi.DpiScaleY;
            var child = (FrameworkElement)shade.Child;
            double width = (i % 2 == 0 ? Math.Floor(viewport.Width / 2) : Math.Ceiling(viewport.Width / 2)) / dpi.DpiScaleX;
            double height = (i / 2 == 0 ? Math.Floor(viewport.Height / 2) : Math.Ceiling(viewport.Height / 2)) / dpi.DpiScaleY;
            // Assigning identical offsets still forces a Popup HWND move and flashes the modal.
            if (Math.Abs(shade.HorizontalOffset - horizontal) > .5) shade.HorizontalOffset = horizontal;
            if (Math.Abs(shade.VerticalOffset - vertical) > .5) shade.VerticalOffset = vertical;
            if (Math.Abs(child.Width - width) > .5) child.Width = width;
            if (Math.Abs(child.Height - height) > .5) child.Height = height;
        }
        double panelWidth = Math.Max(1, Math.Min(820, viewport.Width / dpi.DpiScaleX - 32));
        double panelHeight = Math.Max(1, Math.Min(880, Math.Min(viewport.Height - 40 * dpi.DpiScaleY, screen.Height * .70) / dpi.DpiScaleY));
        double panelLeft = (viewport.Left + (viewport.Width - panelWidth * dpi.DpiScaleX) / 2) / dpi.DpiScaleX;
        double panelTop = (viewport.Top + (viewport.Height - panelHeight * dpi.DpiScaleY) / 2) / dpi.DpiScaleY;
        if (Math.Abs(panel.Width - panelWidth) > .5) panel.Width = panelWidth;
        if (Math.Abs(panel.Height - panelHeight) > .5) panel.Height = panelHeight;
        if (Math.Abs(reportPopup.HorizontalOffset - panelLeft) > .5) reportPopup.HorizontalOffset = panelLeft;
        if (Math.Abs(reportPopup.VerticalOffset - panelTop) > .5) reportPopup.VerticalOffset = panelTop;
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
    internal string HeaderClockText
    {
        get
        {
            string timer = HeaderTimerButton.Content as string ?? "";
            return string.IsNullOrWhiteSpace(timer)
                ? $"{HeaderDateText.Text} {HeaderTimeText.Text}"
                : $"{HeaderDateText.Text} {HeaderTimeText.Text} {timer}";
        }
    }
    internal int ReportVisibleSessionCount => reportContent?.VisibleSessionCount ?? 0;
    internal FrameworkElement? ReportModalSurface => reportPopup?.Child as FrameworkElement;
    internal void ShowReportForDiagnostics() => ShowReportModal();
    internal bool ShowReportChartHoverForDiagnostics() => reportContent?.ShowChartHoverForDiagnostics() == true;
    internal void ShowReportDetailForDiagnostics() => reportContent?.ShowDetailForDiagnostics();
    internal void HideReportForDiagnostics() => HideReportModal();
    private void SettingsClick(object sender, RoutedEventArgs e) { ShowWorkspaceDialog(new SettingsWindow(this)); ApplyLiveSettings(); SaveState(); }
    /// <summary>Puts a settings change on screen at once, so the panel shows its effect while it is open.</summary>
    public void ApplyLiveSettings()
    {
        Settings.Validate(); Strings.Use(Settings.Language); ApplyLanguage(); ApplyTimerPosition(); ApplyTheme();
        Topmost = Settings.AlwaysOnTop; UpdateTimer(); Sounds.Refresh(); SyncAgentListen();
        WindowsStartup.Sync(Settings.StartWithWindows);
    }

    /// <summary>
    /// The shell's own words. Everything else redraws itself from <see cref="L"/> when it is built,
    /// but the title bar, the toolbar and the timer frame come from XAML and are only written once,
    /// so a language change has to reach them here.
    /// </summary>
    public void ApplyLanguage()
    {
        TitleBarText.Text = L.T("chrome.subtitle");
        MinimizeButton.ToolTip = L.T("chrome.minimize");
        MaximizeButton.ToolTip = L.T("chrome.maximize");
        CloseButton.ToolTip = L.T("chrome.closeRelease");
        ReportButton.ToolTip = L.T("chrome.report");
        AgentButton.ToolTip = L.T("agent.open");
        PaintAgentListen();
        TasksButton.ToolTip = L.T("chrome.tasks");
        RefreshNotificationChrome();
        SettingsButton.ToolTip = L.T("chrome.settings");
        FullscreenButton.ToolTip = L.T("chrome.fullscreen");
        PageMapButton.ToolTip = L.T("chrome.pageMap");
        SyncPageMapButton();
        AddWidgetButton.ToolTip = L.T("chrome.addWidget");
        WelcomeTitle.Text = L.T("pages.emptyTitle");
        WelcomeAddButton.Content = L.T("pages.addFirst");
        WelcomeHint.Text = L.T("pages.swipeHint");
        RemoveTimerButton.ToolTip = L.T("timer.removeFromPage");
        SyncTimerFillChrome();
        TimerHeaderTitle.Text = L.T("timer.pomodoro");
        TimerHeaderHint.Text = L.T("timer.dragToMove");
        FocusButton.Content = L.T("timer.pomodoro");
        ShortButton.Content = L.T("timer.shortBreak");
        LongButton.Content = L.T("timer.longBreak");
        ResetButton.ToolTip = L.T("timer.reset");
        SkipButton.ToolTip = L.T("timer.skip");
        voiceDictation.RefreshLanguage();
        UpdateHeaderClock();
    }
    private void LayoutsClick(object sender, RoutedEventArgs e) { ShowWorkspaceDialog(new LayoutsWindow(this)); }

    /// <summary>
    /// Workspace cards sometimes live in independent popup HWNDs so they can overlap embedded
    /// applications. Those surfaces must leave while one of our own dialogs is open or they can
    /// sit above Tasks, Settings, Layouts or the post-it colour picker. Their exact visible set
    /// is restored afterwards.
    /// </summary>
    private bool WorkspaceDialogOpen =>
        OwnedWindows.Cast<Window>().Any(window => window.IsVisible);

    /// <summary>Parks airspace popups for the duration of any modal (dialog ShowDialog, etc.).</summary>
    internal void RunModal(Action modal)
    {
        bool timerWasFloating = timerOverlay is not null;
        var floatingCards = overlayCards.Keys.ToArray();
        var floatingHandles = interactionOverlays.Keys.ToArray();
        HideTimerOverlay();
        // Always-on-top main chrome can still paint the embedded timer above a non-topmost dialog.
        TimerFrame.Visibility = Visibility.Collapsed;
        foreach (var card in floatingHandles) HideInteractionOverlay(card);
        foreach (var card in floatingCards) HideOverlay(card);
        try { modal(); }
        finally
        {
            TimerFrame.Visibility = Visibility.Visible;
            if (!exiting)
            {
                foreach (var card in floatingCards.Where(card => cards.Contains(card) || card == agentCard)) ShowOverlay(card);
                foreach (var card in floatingHandles.Where(card => cards.Contains(card) || card == agentCard)) ShowInteractionOverlay(card);
                if (timerWasFloating && CurrentTimerWidget is not null) ShowTimerOverlay();
                SyncAgentAirspace();
            }
        }
    }

    private void ShowWorkspaceDialog(Window dialog) => RunModal(() => dialog.ShowDialog());
    private static Color ParseColor(string value, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(value); } catch { return fallback; }
    }
    public void ApplyTheme()
    {
        Theme.Apply(Application.Current.Resources, Settings.Dark, Settings.AccentColor);
        // A window's own foreground is not covered by the Window style, so without this the date,
        // the card titles and everything else that inherits its colour stay black on a dark page.
        SetResourceReference(ForegroundProperty, "Ink");
        var phaseBrush = PhaseBrush(); TimerFrame.Background = phaseBrush; TimerSurface.Background = phaseBrush;
        // Widgets take a copy of the brush when they draw, so they only wear the new skin once
        // they are drawn again. Changing theme has to reach them now, not at the next tick.
        foreach (var card in cards.ToArray()) card.Reskin();
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
        for (var current = e.OriginalSource as DependencyObject; current is not null && current != TimerMoveHeader; current = Ancestors.Up(current))
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
            // Only the edge that owns the canvas origin needs its delta floored so it cannot push
            // past 0 — the opposite edge shrinks freely, limited only by minWidth/minHeight below.
            // Flooring both from a single shared clamp used to choke the far edge whenever the
            // widget sat close to the left or top, letting it shrink only a few pixels from there.
            if (timerResizeEdge.Contains('W')) dx = Math.Max(dx, -timerStartX);
            if (timerResizeEdge.Contains('N')) dy = Math.Max(dy, -timerStartY);
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
            config.DropCanvasFillIfBoundsMoved(WidgetArea.ActualWidth, WidgetArea.ActualHeight);
            CurrentPage.TimerPositionCustomized = true;
            SyncTimerFillChrome();
        }
        UpdateTimerOverlayPosition();
    }

    private void EndTimerGesture()
    {
        if (!timerResizing) return;
        timerResizing = false; timerResizeEdge = ""; CurrentPage.TimerPositionCustomized = true; SaveState();
        QueueTimerPromotion();
    }

    private void TimerFillClick(object sender, RoutedEventArgs e)
    {
        ToggleTimerCanvasFill();
        e.Handled = true;
    }

    internal void ToggleTimerCanvasFill()
    {
        if (CurrentTimerWidget is null) return;
        SetTimerCanvasFill(!CurrentTimerWidget.CanvasFill);
    }

    internal void SetTimerCanvasFill(bool fill)
    {
        if (CurrentTimerWidget is null) return;
        if (fill)
        {
            ClearCanvasFillsExcept(CurrentTimerWidget);
            CurrentTimerWidget.FillCanvas(WidgetArea.ActualWidth, WidgetArea.ActualHeight);
            CurrentPage.TimerPositionCustomized = true;
        }
        else if (CurrentTimerWidget.CanvasFill) CurrentTimerWidget.RestoreFromCanvasFill();
        ArrangeCards();
        BringTimerToFront();
        Dispatcher.BeginInvoke(() =>
        {
            if (CurrentTimerWidget?.CanvasFill == true) BringTimerToFront();
        }, DispatcherPriority.Input);
        AgentCreatedWidgetThisTurn = false;
        SaveState();
    }

    private void SyncTimerFillChrome()
    {
        if (TimerFillButton is null) return;
        bool filled = CurrentTimerWidget?.CanvasFill == true;
        TimerFillButton.Content = filled ? ChromeIcon.Restore() : ChromeIcon.Expand();
        TimerFillButton.ToolTip = L.T(filled ? "widgets.canvasRestore" : "widgets.canvasFill");
        TimerFillButton.Visibility = CurrentTimerWidget is null ? Visibility.Collapsed : Visibility.Visible;
        if (RemoveTimerButton is not null && RemoveTimerButton.Content is not Viewbox)
            RemoveTimerButton.Content = ChromeIcon.Close();
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
        if (cards.Concat(agentCard is not null ? [agentCard] : Array.Empty<WidgetCard>()).Any(card => card.Config.CanvasFill && !card.Config.Collapsed))
        {
            TimerFrame.Visibility = Visibility.Hidden;
            HideTimerOverlay();
            SyncTimerFillChrome();
            return;
        }
        double canvasWidth = WidgetArea.ActualWidth > 0 ? WidgetArea.ActualWidth : 720;
        double canvasHeight = WidgetArea.ActualHeight > 0 ? WidgetArea.ActualHeight : 720;
        double maxWidth = Math.Max(360, canvasWidth), maxHeight = Math.Max(300, canvasHeight);
        if (config.CanvasFill)
        {
            config.ApplyCanvasFillIfNeeded(canvasWidth, canvasHeight);
        }
        else
        {
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
        }
        TimerFrame.Width = config.Width; TimerFrame.Height = config.Height;
        Canvas.SetLeft(TimerFrame, config.X); Canvas.SetTop(TimerFrame, config.Y);
        SyncTimerFillChrome();
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
            var url = Dialogs.Prompt(this, L.T("widgets.webUrlTitle"), L.T("widgets.webUrlLabel"), "https://www.youtube.com");
            if (url is null) return;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") { Status(L.T("status.needHttps")); return; }
            AddCard(new() { Kind = "web", Title = uri.Host, Value = uri.AbsoluteUri }, true);
        }
        else if (choice == 2) AddCard(new() { Kind = "notes", Title = "NOTAS" }, true);
        else if (choice == 3) AddCard(new() { Kind = "stats", Title = "MI ENFOQUE" }, true);
        else if (choice == 4) AddCard(new() { Kind = "todo", Title = "TO DO" }, true);
        else if (choice == 5) AddCard(new() { Kind = "habits", Title = L.T("widgets.kindHabits") }, true);
        else if (choice == 6) AddCard(new() { Kind = "calendar", Title = "AGENDA" }, true);
        else if (choice == 7) AddCard(new() { Kind = "finance", Title = L.T("widgets.kindFinance") }, true);
        else if (choice == 8)
        {
            var card = AddCard(new() { Kind = "memento", Title = L.T("widgets.kindMemento") }, true);
            card.PromptMementoBirth();
        }
        else if (choice == 9) AddTimerWidget();
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
            config.Width = config.Kind == "memento"
                ? Math.Max(320, Math.Min(420, canvasWidth - 24))
                : canvasWidth >= 560 ? Math.Max(240, twoColumnWidth) : Math.Max(280, canvasWidth - 24);
        }
        if (config.Height <= 0) config.Height = config.Kind switch
        {
            "stats" => 260,
            "memento" => 380,
            "notes" => 360,
            "todo" => 400,
            "habits" => 390,
            "calendar" => 520,
            "finance" => 580,
            "agent" => 520,
            "window" => 420,
            "web" => 360,
            _ => 300
        };
        if (newPlacement)
        {
            // A new widget lands where the eye already is, not tucked into a corner behind
            // whatever is already there — dead centre of the visible canvas, every time.
            double viewportWidth = WidgetArea.ActualWidth > 0 ? WidgetArea.ActualWidth : canvasWidth;
            double viewportHeight = WidgetArea.ActualHeight > 0 ? WidgetArea.ActualHeight : 720;
            config.X = Math.Max(0, (viewportWidth - config.Width) / 2);
            config.Y = Math.Max(0, (viewportHeight - config.Height) / 2);
        }
        var card = new WidgetCard(this, config); cards.Add(card); WidgetArea.Children.Add(card);
        LastCreatedWidgetId = config.Id;
        AgentCreatedWidgetThisTurn = true;
        ArrangeCards();
        // On top of everything else — the timer included — so it is never born hidden under one.
        if (IsLoaded && !pageTransitioning) BringCardToFront(card);
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
        if (card.Config.Kind == "agent") { HideAgentWidget(); return; }
        // Closing a note keeps its words in the note history instead of throwing them away.
        if (card.Config.Kind == "notes") NoteArchiveStore.For(Store).Close(card.Config);
        HideInteractionOverlay(card); HideOverlay(card);
        card.Release(); WidgetArea.Children.Remove(card); cards.Remove(card); ArrangeCards(); SaveState(); UpdatePageNavigation();
    }
    public void MoveCard(WidgetCard card, int direction)
    {
        var index = cards.IndexOf(card); var next = index + direction;
        if (next < 0 || next >= cards.Count) return;
        cards.RemoveAt(index); cards.Insert(next, card); ArrangeCards(); SaveState();
    }
    internal void ClearCanvasFillsExcept(WidgetConfig? keep)
    {
        if (CurrentTimerWidget is { } timer && timer != keep && timer.CanvasFill)
            timer.RestoreFromCanvasFill();
        foreach (var card in cards.Concat(agentCard is not null ? [agentCard] : Array.Empty<WidgetCard>()))
        {
            if (card.Config != keep && card.Config.CanvasFill)
            {
                card.Config.RestoreFromCanvasFill();
                card.SyncFillChrome();
            }
        }
    }

    public void ArrangeCards()
    {
        Welcome.Visibility = cards.Count == 0 && CurrentTimerWidget is null ? Visibility.Visible : Visibility.Collapsed;
        Welcome.Width = Math.Max(1, WidgetArea.ActualWidth); Welcome.Height = Math.Max(1, WidgetArea.ActualHeight);
        var filledCard = cards.Concat(agentCard is { Visibility: not Visibility.Collapsed } ? [agentCard] : Array.Empty<WidgetCard>())
            .FirstOrDefault(card => card.Config.CanvasFill && !card.Config.Collapsed);
        bool timerFilled = CurrentTimerWidget is { CanvasFill: true };
        bool isolating = filledCard is not null || timerFilled;
        foreach (var card in cards.Concat(agentCard is not null ? [agentCard] : Array.Empty<WidgetCard>()))
        {
            bool hide = isolating && card != filledCard && card != agentCard;
            card.SetFillPeerHidden(hide);
            if (hide)
            {
                HideOverlay(card);
                HideInteractionOverlay(card);
            }
            if (hide) continue;
            card.Config.ApplyCanvasFillIfNeeded(WidgetArea.ActualWidth, WidgetArea.ActualHeight);
            double minW = card.Config.Kind == "agent" ? 260 : 220;
            double minH = card.Config.Collapsed ? 50 : card.Config.Kind == "agent" ? 280 : 50;
            card.Width = Math.Clamp(card.Config.Width, minW, Math.Max(minW, WidgetArea.ActualWidth));
            card.Height = card.Config.Collapsed
                ? 50
                : Math.Clamp(card.Config.Height, minH, Math.Max(minH, WidgetArea.ActualHeight));
            Canvas.SetLeft(card, Math.Clamp(card.Config.X, 0, Math.Max(0, WidgetArea.ActualWidth - card.Width)));
            Canvas.SetTop(card, Math.Clamp(card.Config.Y, 0, Math.Max(0, WidgetArea.ActualHeight - card.Height)));
        }
        if (agentCard is { Visibility: Visibility.Visible }) Panel.SetZIndex(agentCard, 20);
        ArrangeTimerWidget();
        SyncAgentAirspace();
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
        Panel.SetZIndex(card, card.Config.Kind == "agent" ? 20 : 1);
        if (agentCard is { Visibility: Visibility.Visible } && card != agentCard) Panel.SetZIndex(agentCard, 20);
        bool natives = AnyExposedNative;
        if (natives)
        {
            if (card.HasExposedNativeSurface)
            {
                foreach (var other in cards.Where(c => c != card)) HideOverlay(other);
                card.BringExternalToFront();
            }
            else
            {
                ShowOverlay(card);
                ClaimOverlayKeyboard(card);
            }
        }
        if (interactionOverlays.ContainsKey(card))
        {
            UpdateInteractionOverlayPosition(card);
            BringInteractionOverlayToFront(card);
        }
        else ShowInteractionOverlay(card);
        SyncAgentAirspace(claimKeyboard: card == agentCard);
        LiftOverlaysAboveNatives();
    }

    private void BringTimerToFront()
    {
        if (exiting || !IsEnabled || IsReportModalOpen) return;
        timerWantsFront = true;
        foreach (var card in interactionOverlays.Keys.ToArray())
            if (card != agentCard) HideInteractionOverlay(card);
        foreach (var card in overlayCards.Keys.ToArray())
            if (card != agentCard) HideOverlay(card);
        foreach (var other in cards) Panel.SetZIndex(other, 0);
        Panel.SetZIndex(TimerFrame, 20);
        if (AnyExposedNative) ShowTimerOverlay();
        else HideTimerOverlay();
        SyncAgentAirspace();
    }

    private void ShowTimerOverlay()
    {
        if (IsReportModalOpen || pageMapOpen || WorkspaceDialogOpen) return;
        if (timerOverlay is not null)
        {
            // Keep the same native surface and mouse capture throughout a
            // press/drag. Changing IsOpen loses Button and Thumb input state.
            UpdateTimerOverlayPosition(); BringPopupToFront(timerOverlay); return;
        }
        if (timerPointerPressed || timerResizing || TimerFrame.IsMouseCaptureWithin) return;
        var popup = new Popup
        {
            AllowsTransparency = true, StaysOpen = true,
            Placement = PlacementMode.Absolute, PlacementTarget = WidgetArea, PopupAnimation = PopupAnimation.None,
            Focusable = false
        };
        timerOverlay = popup;
        popup.Opened += (_, _) => BringPopupToFront(popup);
        DetachFromLogicalParent(TimerFrame);
        popup.Child = TimerFrame;
        popup.IsOpen = true;
        UpdateTimerOverlayPosition(); BringPopupToFront(timerOverlay);
    }

    private void HideTimerOverlay()
    {
        if (timerOverlay is null) return;
        var popup = timerOverlay;
        popup.IsOpen = false;
        popup.Child = null;
        timerOverlay = null;
        DetachFromLogicalParent(TimerFrame);
        if (!WidgetArea.Children.Contains(TimerFrame)) WidgetArea.Children.Add(TimerFrame);
        Panel.SetZIndex(TimerFrame, 20);
        ArrangeTimerWidget();
    }
    private void ShowOverlay(WidgetCard card)
    {
        if (IsReportModalOpen || pageMapOpen || WorkspaceDialogOpen) return;
        if (overlayCards.TryGetValue(card, out var existing))
        {
            // Recreating the popup HWND blinks the card and swallows the click,
            // so the caret never lands in the composer. Lift the live surface.
            if (!ReferenceEquals(existing.Child, card))
            {
                DetachFromLogicalParent(card);
                existing.Child = card;
            }
            if (!existing.IsOpen) existing.IsOpen = true;
            UpdateOverlayPosition(card);
            BringPopupToFront(existing);
            return;
        }
        var popup = new Popup
        {
            AllowsTransparency = true, StaysOpen = true, Placement = PlacementMode.Absolute,
            PopupAnimation = PopupAnimation.None, Focusable = false
        };
        // Opening the popup runs layout, which re-enters ArrangeCards → ShowOverlay.
        // Own the card in the dictionary before Child/IsOpen so the nested call
        // reuses this popup instead of parenting the card a second time.
        overlayCards[card] = popup;
        popup.Opened += (_, _) => BringPopupToFront(popup);
        DetachFromLogicalParent(card);
        popup.Child = card;
        popup.IsOpen = true;
        UpdateOverlayPosition(card);
        // Popup is a separate WPF HWND. It must be explicitly lifted above
        // hosted application windows; WPF Panel.ZIndex alone cannot cross the
        // native HWND boundary.
        BringPopupToFront(popup);
    }
    private void HideOverlay(WidgetCard card)
    {
        if (overlayCards.TryGetValue(card, out var popup))
        {
            popup.IsOpen = false;
            popup.Child = null;
            overlayCards.Remove(card);
        }
        if (card.Parent == WidgetArea) return;
        DetachFromLogicalParent(card);
        if (!WidgetArea.Children.Contains(card)) WidgetArea.Children.Add(card);
        Panel.SetZIndex(card, card.Config.Kind == "agent" ? 20 : 0);
    }

    private static void DetachFromLogicalParent(FrameworkElement element)
    {
        switch (element.Parent)
        {
            case Popup popup:
                popup.Child = null;
                break;
            case Panel panel:
                panel.Children.Remove(element);
                break;
            case Decorator decorator when ReferenceEquals(decorator.Child, element):
                decorator.Child = null;
                break;
            case ContentControl content when ReferenceEquals(content.Content, element):
                content.Content = null;
                break;
        }
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
        if (IsReportModalOpen || pageMapOpen || WorkspaceDialogOpen) return;
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
        // The position only sticks on an open popup, so it is applied after opening it.
        interactionOverlays[card] = popup;
        popup.IsOpen = true; UpdateInteractionOverlayPosition(card); BringPopupToFront(popup);
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
    private void BringPopupToFront(Popup popup)
    {
        if (popup.Child is not Visual visual || PresentationSource.FromVisual(visual) is not HwndSource source)
            return;
        nint handle = source.Handle;
        EnsurePopupOwned(handle);
        const uint flags = Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE;
        // Chromium video uses a hardware overlay that sits above ordinary owned popups.
        // Stay TOPMOST while PomoDock (or a hosted child) is the foreground app.
        if (AnyExposedNative && ForegroundIsOurs())
        {
            Win32.SetWindowPos(handle, Win32.HWND_TOPMOST, 0, 0, 0, 0, flags);
            return;
        }
        Win32.SetWindowPos(handle, Win32.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
        Win32.SetWindowPos(handle, Win32.HWND_TOP, 0, 0, 0, 0, flags);
    }

    private void EnsurePopupOwned(nint handle)
    {
        if (hwnd == 0 || handle == 0) return;
        nint owner = Win32.GetWindowLongPtr(handle, Win32.GWLP_HWNDPARENT);
        if (owner != hwnd) Win32.SetWindowLongPtr(handle, Win32.GWLP_HWNDPARENT, hwnd);
        long style = Win32.GetWindowLongPtr(handle, Win32.GWL_STYLE).ToInt64();
        if ((style & Win32.WS_CHILD) == 0) return;
        Win32.SetParent(handle, 0);
        Win32.SetWindowLongPtr(handle, Win32.GWL_STYLE, (nint)((style & ~Win32.WS_CHILD) | Win32.WS_POPUP));
        Win32.SetWindowLongPtr(handle, Win32.GWLP_HWNDPARENT, hwnd);
        Win32.SetWindowPos(handle, Win32.HWND_TOP, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);
    }

    private bool ForegroundIsOurs()
    {
        nint fg = Win32.GetForegroundWindow();
        if (fg == 0 || hwnd == 0) return IsActive;
        if (fg == hwnd || Win32.IsChild(hwnd, fg)) return true;
        if (Win32.GetAncestor(fg, Win32.GA_ROOT) == hwnd) return true;
        if (Win32.GetWindowLongPtr(fg, Win32.GWLP_HWNDPARENT) == hwnd) return true;
        foreach (var popup in overlayCards.Values.Concat(interactionOverlays.Values))
        {
            if (popup.Child is Visual visual && PresentationSource.FromVisual(visual) is HwndSource source && source.Handle == fg)
                return true;
        }
        if (timerOverlay?.Child is Visual timerVisual && PresentationSource.FromVisual(timerVisual) is HwndSource timerSource && timerSource.Handle == fg)
            return true;
        foreach (var popup in reportBackdrops.Append(reportPopup).OfType<Popup>())
        {
            if (popup.Child is Visual reportVisual && PresentationSource.FromVisual(reportVisual) is HwndSource reportSource && reportSource.Handle == fg)
                return true;
        }
        return false;
    }

    internal IDisposable PauseOverlayLift()
    {
        overlayLiftPause++;
        return new OverlayLiftScope(this);
    }

    private sealed class OverlayLiftScope : IDisposable
    {
        private MainWindow? window;
        public OverlayLiftScope(MainWindow window) => this.window = window;
        public void Dispose()
        {
            if (window is null) return;
            window.overlayLiftPause = Math.Max(0, window.overlayLiftPause - 1);
            window = null;
        }
    }

    /// <summary>
    /// Hosted apps call HWND_TOP on their own child. That lands between the widget
    /// chrome (WPF) and the agent popup unless we restack overlays afterwards.
    /// While the focus report is open, overlays are parked — restacking the report
    /// popups every tick made the whole modal flash.
    /// </summary>
    internal void LiftOverlaysAboveNatives()
    {
        if (exiting || !IsLoaded || overlayLiftPause > 0 || IsReportModalOpen || pageMapOpen) return;
        if (overlayCards.Count == 0 && interactionOverlays.Count == 0 && timerOverlay is null) return;
        foreach (var popup in overlayCards.Values.Concat(interactionOverlays.Values))
            BringPopupToFront(popup);
        if (timerOverlay is not null) BringPopupToFront(timerOverlay);
        if (agentCard is { Visibility: Visibility.Visible } && overlayCards.TryGetValue(agentCard, out var agentPopup))
            BringPopupToFront(agentPopup);
    }

    private bool AnyExposedNative => cards.Any(card => card.HasExposedNativeSurface);

    internal void NotifyNativeSurfaceChanged() => SyncAgentAirspace();

    private void SyncAgentAirspace(bool claimKeyboard = false)
    {
        if (agentCard is null || exiting || !IsLoaded || pageTransitioning || pageMapOpen || IsReportModalOpen) return;
        if (agentCard.Visibility != Visibility.Visible || overlayLiftPause > 0)
        {
            HideOverlay(agentCard);
            return;
        }
        if (!AnyExposedNative)
        {
            HideOverlay(agentCard);
            return;
        }
        ShowOverlay(agentCard);
        if (claimKeyboard) ClaimOverlayKeyboard(agentCard);
        LiftOverlaysAboveNatives();
        if (interactionOverlays.ContainsKey(agentCard))
            BringInteractionOverlayToFront(agentCard);
    }

    private void ClaimOverlayKeyboard(WidgetCard card)
    {
        if (!overlayCards.TryGetValue(card, out var popup)) return;
        if (TryClaimPopupKeyboard(popup)) return;
        void OnOpened(object? sender, EventArgs e)
        {
            popup.Opened -= OnOpened;
            TryClaimPopupKeyboard(popup);
        }
        popup.Opened += OnOpened;
    }

    private bool TryClaimPopupKeyboard(Popup popup)
    {
        if (popup.Child is not Visual visual || PresentationSource.FromVisual(visual) is not HwndSource source)
            return false;
        nint target = source.Handle;
        foreach (var card in cards) card.YieldExternalKeyboard(target);
        Win32.SetFocus(target);
        return true;
    }
    public void LoadLayout(List<WidgetConfig> widgets)
    {
        foreach (var card in cards.ToArray()) RemoveCard(card);
        var copy = JsonSerializer.Deserialize<List<WidgetConfig>>(JsonSerializer.Serialize(widgets))!;
        foreach (var config in copy) { config.Id = Guid.NewGuid(); AddCard(config, false); }
        SaveState(); UpdatePageNavigation(); Status(L.T("status.layoutLoaded"));
    }
    public void ToggleFullscreen()
    {
        if (!fullscreen)
        {
            HidePageMap();
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
        if (e.Key == Key.Escape && pageMapOpen) { HidePageMap(); e.Handled = true; return; }
        if (e.Key == Key.Escape && NotificationsPopup.IsOpen) { NotificationsPopup.IsOpen = false; e.Handled = true; return; }
        if (e.Key == Key.Escape && agentCard is { Visibility: Visibility.Visible } && Keyboard.FocusedElement is not TextBoxBase and not PasswordBox)
        {
            HideAgentWidget(); e.Handled = true; return;
        }
        if (e.Key == Key.Escape && reportPopup is { IsOpen: true }) { HideReportModal(); e.Handled = true; return; }
        if (e.Key == Key.F11 && !hotkeyRegistered) { ToggleFullscreen(); e.Handled = true; }
        if (e.Key == Key.Escape && fullscreen) { ToggleFullscreen(); e.Handled = true; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.M) { TogglePageMap(); e.Handled = true; return; }
        // While a note, a task or any field holds the caret the keyboard belongs to it:
        // a rich note is a RichTextBox, not a TextBox, so space used to start the timer
        // instead of being typed, and Ctrl+arrows changed page instead of moving a word.
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox) return;
        // Embedded apps (Cursor, etc.) take Win32 focus outside WPF. Do not steal their keys.
        if (cards.Any(card => card.HasKeyboardFocusOnExternal)) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Left) { NavigateWorkspace(-1, 0); e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Right) { NavigateWorkspace(1, 0); e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Up) { NavigateWorkspace(0, -1); e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Down) { NavigateWorkspace(0, 1); e.Handled = true; return; }
        if (e.Key == Key.Space && CurrentTimerWidget is not null && Keyboard.FocusedElement is not ButtonBase) { ToggleTimer(); e.Handled = true; }
    }
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        pageSwipePoll.Stop();
        CancelTimerGesture(); CloseNotificationCenter();
        HidePageMap();
        timerAlertSnooze?.Stop();
        agentWindow?.Shutdown();
        wakeListen?.Dispose();
        wakeListen = null;
        listenPulse.Stop();
        HideAgentWidget();
        HideReportModal(); HideTimerOverlay(); AgendaToast.CloseAll(); voiceDictation.Dispose();
        foreach (var card in interactionOverlays.Keys.ToArray()) HideInteractionOverlay(card);
        foreach (var card in overlayCards.Keys.ToArray()) HideOverlay(card);
        try
        {
            foreach (var card in pageCards.Values.SelectMany(value => value).Concat(cards).Distinct()) card.Release();
        }
        catch (Exception ex) { e.Cancel = true; Status(ex.Message); return; }
        Settings.TimerWasRunning = Timer.Running;
        exiting = true;
        Sounds.StopNoise(); Timer.Pause(DateTimeOffset.UtcNow, Monotonic); SaveState();
        ticker.Stop(); overlayZ.Stop(); statusReset.Stop(); SystemEvents.PowerModeChanged -= PowerChanged;
        Win32.UnregisterHotKey(hwnd, 11); if (focusHook != 0) Win32.UnhookWinEvent(focusHook);
        Sounds.Dispose(); Store.Dispose();
    }
    private void FullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void DragTitle(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) MaximizeClick(sender, e); else if (e.OriginalSource is TextBlock || e.OriginalSource == TitleBar) DragMove(); }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    internal bool VoiceDictationIsVisible => voiceDictation.IsVisible;
    internal bool VoiceDictationReservesTextSpace => voiceDictation.ReservesTextSpace;
    internal FrameworkElement VoiceDictationSurface => voiceDictation.Surface;
    internal bool VoiceDictationFeedbackIsVisible => voiceDictation.FeedbackIsVisible;
    internal string VoiceDictationFeedbackCopy => voiceDictation.FeedbackCopy;
    internal void ShowVoiceDictationFeedbackForDiagnostics() => voiceDictation.ShowFeedbackForDiagnostics();
    internal void BeginVoiceDraftForDiagnostics(string text) => voiceDictation.BeginProvisionalForDiagnostics(text);
    internal void UpdateVoiceDraftForDiagnostics(string text) => voiceDictation.UpdateProvisionalForDiagnostics(text);
    internal bool TimerOverlayIsVisible => timerOverlay is not null;
    internal bool AgentOverlayIsOpen => agentCard is not null && overlayCards.TryGetValue(agentCard, out var popup) && popup.IsOpen;
    internal WidgetCard? AgentCardForDiagnostics => agentCard;
    internal nint AgentOverlayHwndForDiagnostics()
    {
        if (agentCard is null || !overlayCards.TryGetValue(agentCard, out var popup) || popup.Child is not Visual visual)
            return 0;
        return PresentationSource.FromVisual(visual) is HwndSource source ? source.Handle : 0;
    }
    internal void FloatTimerForDiagnostics() => BringTimerToFront();
    internal void ForceTimerOverlayForDiagnostics() => ShowTimerOverlay();
    internal void HideTimerForDiagnostics() => HideTimerOverlay();
    internal void ShowWorkspaceDialogForDiagnostics(Window dialog) => ShowWorkspaceDialog(dialog);

    internal AgentToolResult AddTimerFromAgent()
    {
        if (CurrentTimerWidget is not null)
            return new AgentToolResult(false, "El temporizador ya está en esta página", JsonSerializer.Serialize(new { success = true, changed = false, summary = "El temporizador ya está en esta página" }));
        AddTimerWidget();
        return new AgentToolResult(true, "Widget de temporizador añadido", JsonSerializer.Serialize(new { success = true, changed = true, summary = "Widget de temporizador añadido" }));
    }

    internal AgentToolResult RunTimerFromAgent(string command, string phase, string task, int minutes = 5)
    {
        command = command.Trim().ToLowerInvariant();
        if (command is "select" or "start")
        {
            var selectedPhase = phase.Trim().ToLowerInvariant() switch
            {
                "short" or "shortbreak" => Phase.ShortBreak,
                "long" or "longbreak" => Phase.LongBreak,
                _ => Phase.Focus
            };
            if (selectedPhase != Timer.Phase) Timer.Select(selectedPhase, DateTimeOffset.UtcNow, Monotonic);
        }
        if (!string.IsNullOrWhiteSpace(task))
        {
            if (task.Trim().Equals("free", StringComparison.OrdinalIgnoreCase)
                || task.Trim().Equals("libre", StringComparison.OrdinalIgnoreCase)
                || task.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)
                || task.Trim().Equals("clear", StringComparison.OrdinalIgnoreCase))
                SetSelectedTask(null);
            else
            {
                var matches = Settings.Tasks.Where(item => !item.Done && item.Name.Contains(task, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1)
                    return new AgentToolResult(false, $"Necesito un título de tarea de enfoque más específico para '{task}'", JsonSerializer.Serialize(new { success = false, changed = false, summary = $"Coincidencias de tarea: {matches.Count}" }), false);
                SetSelectedTask(matches[0]);
            }
        }
        string summary;
        switch (command)
        {
            case "pause":
                Timer.Pause(DateTimeOffset.UtcNow, Monotonic); Sounds.StopNoise(); summary = "Temporizador pausado"; break;
            case "reset":
                Sounds.StopNoise(); Timer.Select(Timer.Phase, DateTimeOffset.UtcNow, Monotonic); summary = "Temporizador reiniciado"; break;
            case "skip":
                Sounds.StopNoise(); Timer.Select(Timer.NextPhase(), DateTimeOffset.UtcNow, Monotonic); summary = "Fase omitida"; break;
            case "select":
                summary = "Fase seleccionada"; break;
            case "free" or "clear_task":
                SetSelectedTask(null);
                summary = "Enfoque libre"; break;
            case "snooze" or "posponer":
                return SnoozeTimerAlertFromAgent(minutes);
            case "start":
                if (!Timer.Running)
                {
                    AcknowledgeTimerAlerts();
                    Timer.Start(DateTimeOffset.UtcNow, Monotonic, selectedTask);
                    if (Timer.Phase == Phase.Focus) Sounds.StartNoise();
                    appUsage.Observe(Timer.Phase == Phase.Focus, DateTimeOffset.UtcNow);
                }
                summary = "Temporizador iniciado"; break;
            default:
                return new AgentToolResult(false, $"Comando de temporizador desconocido: {command}", JsonSerializer.Serialize(new { success = false, changed = false, summary = "Comando desconocido" }), false);
        }
        UpdateTimer(); SaveState();
        return new AgentToolResult(true, summary, JsonSerializer.Serialize(new { success = true, changed = true, summary, phase = Timer.Phase.ToString(), Timer.Running }));
    }
}
