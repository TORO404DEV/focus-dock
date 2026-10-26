using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PomoDock.Core;

namespace PomoDock.Web;

/// <summary>
/// The workspace, as the desktop's MainWindow draws it: the header with the clock and the chrome
/// buttons, the infinite grid of pages, the Pomodoro and the widgets. The browser stands in for
/// the window frame, so the title bar keeps its place but trades minimise and close for the way
/// back to the website and the desktop download.
/// </summary>
public partial class MainWindow : ComponentBase
{
    public const string LandingUrl = "/es/";
    /// <summary>Always the newest installer, so the web never points at a stale version.</summary>
    private const string DownloadUrl = "https://github.com/TORO404DEV/focus-dock/releases/latest";
    [Inject] private IJSRuntime Runtime { get; set; } = null!;

    internal Browser Browser { get; private set; } = null!;
    public WebStore Store { get; private set; } = null!;
    public Settings Settings { get; private set; } = null!;
    public TimerEngine Timer { get; private set; } = null!;
    public SoundEngine Sounds { get; private set; } = null!;
    private DotNetObjectReference<MainWindow>? self;
    private WorkTask? selectedTask;
    private int ticks;
    private bool loaded, fullscreen, exiting, settingsOpen, askedNotifications, surfacePulse;
    private string headerDate = "", headerTime = "", headerTimer = "";
    private string clockText = "25:00", startText = "", phaseLabel = "", cycleText = "", contextText = "";
    private string statusText = "";
    private double canvasWidth = 720, canvasHeight = 720;
    /// <summary>The widget on top: a config id, the timer's included. The timer starts above the rest.</summary>
    private Guid? front;
    /// <summary>The widget that just landed, so it plays the arrival once.</summary>
    private Guid? arriving;

    public static double Monotonic => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>The landing page in the language on screen.</summary>
    private string Landing => Strings.Code == "en" ? "/en/" : LandingUrl;

    protected override void OnInitialized()
    {
        Browser = new Browser(Runtime);
        Store = new WebStore(Browser);
        try { Settings = Store.Read<Settings>("settings") ?? new(); }
        catch (JsonException) { Settings = new(); }
        Settings.Validate();
        // Remember the last focus task across restarts until the user clears it on purpose.
        selectedTask = Settings.SelectedTaskId is { } remembered
            ? Settings.Tasks.FirstOrDefault(task => task.Id == remembered && !task.Template)
            : null;
        // The language has to be on before anything draws itself, or the first paint speaks Spanish.
        Strings.Use(Settings.Language);
        Timer = new(Settings);
        Sounds = new(Settings, Browser);
        InitializeNotifications();
        appliedTimerAtBottom = Settings.TimerAtBottom;
        Timer.GapDetected += () => Status(L.T("status.gapDetected"));
        Timer.Finished += OnFinished;
        try
        {
            if (Store.Read<Session>("checkpoint") is { } checkpoint && !Store.Sessions().Any(s => s.Id == checkpoint.Id))
            { Timer.Restore(checkpoint); Status(L.T("status.recovered")); }
        }
        catch (JsonException) { }
        InitializeWorkspacePages();
        front = CurrentTimerWidget?.Id;
        loaded = true;
        ApplyTheme();
        UpdateTimer();
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
        {
            self = DotNetObjectReference.Create(this);
            Browser.Attach(self);
            MeasureCanvas();
            ResumeTimerIfNeeded();
            ShowUnreadNotificationModals();
            StateHasChanged();
        }
        AfterRenderPages();
    }

    /// <summary>Picks up a pomodoro that was running when the app last saved state.</summary>
    private void ResumeTimerIfNeeded()
    {
        if (!Settings.TimerWasRunning || Timer.Active is null || Timer.Running) return;
        Timer.Start(DateTimeOffset.UtcNow, Monotonic, selectedTask);
        if (Timer.Phase == Phase.Focus) Sounds.StartNoise();
        UpdateTimer();
        Status(L.T("status.running"));
    }

    [JSInvokable]
    public void Tick()
    {
        if (exiting) return;
        Timer.Tick(DateTimeOffset.UtcNow, Monotonic);
        UpdateTimer();
        if (++ticks % 15 == 0) SaveState();
        StateHasChanged();
    }

    /// <summary>The tab is going away: the clock stops where it is, as the desktop does on close.</summary>
    [JSInvokable]
    public void Closing()
    {
        if (exiting) return;
        Settings.TimerWasRunning = Timer.Running;
        exiting = true;
        Sounds.StopNoise();
        Timer.Pause(DateTimeOffset.UtcNow, Monotonic);
        SaveState();
        exiting = false;
        // A page restored from the back/forward cache carries on where it stopped.
        if (Settings.TimerWasRunning) ResumeTimerIfNeeded();
    }

    public void SaveState()
    {
        SaveCurrentWorkspacePage();
        Settings.Fullscreen = fullscreen;
        Settings.SelectedTaskId = selectedTask?.Id;
        if (!exiting) Settings.TimerWasRunning = Timer.Running;
        Store.Write("settings", Settings);
        bool settingsSaved = Store.LastWriteSucceeded;
        if (Timer.Active is { } active) Store.Write("checkpoint", active);
        else Store.Remove("checkpoint");
        if (!settingsSaved) Status(L.T("web.storageFull"));
    }

    /// <summary>Sets the focus task. Only free-focus (null) or picking another task clears the previous one.</summary>
    internal void SetSelectedTask(WorkTask? task)
    {
        selectedTask = task;
        task?.Touch();
        Settings.SelectedTaskId = task?.Id;
    }

    internal WorkTask? SelectedTask => selectedTask;

    private void OnFinished(Session session)
    {
        if (session.Phase == Phase.Focus && session.TaskId is { } id)
            Settings.Tasks.FirstOrDefault(task => task.Id == id)?.Touch(session.Ended);
        Store.Complete(session);
        if (session.Outcome != Outcome.Completed) return;
        Sounds.StopNoise();
        Sounds.Completed(session.Phase);
        Status(session.Phase == Phase.Focus ? L.T("status.focusComplete") : L.T("status.breakComplete"));
        var next = Timer.NextPhase();
        _ = InvokeAsync(() =>
        {
            Timer.Select(next, DateTimeOffset.UtcNow, Monotonic);
            if ((next == Phase.Focus && Settings.AutoFocus) || (next != Phase.Focus && Settings.AutoBreak))
            {
                Timer.Start(DateTimeOffset.UtcNow, Monotonic, selectedTask);
                if (next == Phase.Focus) Sounds.StartNoise();
            }
            AnimateTimer(); UpdateTimer(); SaveState();
            AnnounceTimerComplete(session.Phase, next);
            StateHasChanged();
        });
    }

    /// <summary>
    /// The colour of the phase the timer is in. The three colours are pastels picked for paper, so
    /// in the dark they are laid over the card instead of painted onto it: the phase still reads as
    /// a colour, and the clock stays light on dark instead of light on a pale slab.
    /// </summary>
    private string PhaseColor()
    {
        var hex = Timer.Phase == Phase.Focus ? Settings.FocusColor : Timer.Phase == Phase.ShortBreak ? Settings.ShortBreakColor : Settings.LongBreakColor;
        if (!Theme.TryParse(hex, out var phase)) return "var(--surface)";
        return Theme.Hex(Settings.Dark ? Theme.Mix(phase, Theme.DarkSurface, .13) : phase);
    }

    private IEnumerable<(Phase Phase, string Label)> PhaseButtons() =>
    [
        (Phase.Focus, L.T("timer.pomodoro")),
        (Phase.ShortBreak, L.T("timer.shortBreak")),
        (Phase.LongBreak, L.T("timer.longBreak"))
    ];

    private void UpdateTimer()
    {
        UpdateHeaderClock();
        var remaining = TimeSpan.FromSeconds(Math.Ceiling(Timer.Remaining));
        clockText = $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
        startText = Timer.Running ? L.T("timer.pause") : Timer.Active is null ? L.T("timer.start") : L.T("timer.resume");
        var label = Timer.Phase == Phase.Focus ? L.T("timer.phaseFocusName") : Timer.Phase == Phase.ShortBreak ? L.T("timer.phaseShort") : L.T("timer.phaseLong");
        phaseLabel = $"{Timer.CompletedFocus + 1:00} / {label}";
        cycleText = L.T("timer.cycle", Timer.CompletedFocus % Settings.LongInterval + 1, Settings.LongInterval);
        contextText = Timer.Active is { } s ? L.T("timer.contextActive", s.Project, s.Task) : selectedTask is null ? L.T("timer.freeFocusButton") : L.T("timer.contextChosen", selectedTask.Project, selectedTask.Name);
        Browser.SetTitle(headerTimer.Length > 0 ? L.T("web.titleRunning", headerTimer) : L.T("web.titleIdle"));
    }

    private void StartClick() { Sounds.Button(Timer.Running ? "pause" : "start"); ToggleTimer(); }

    internal void ToggleTimer()
    {
        if (Timer.Running)
        {
            Timer.Pause(DateTimeOffset.UtcNow, Monotonic);
            Sounds.StopNoise();
            Status(L.T("status.paused"));
        }
        else
        {
            // Starting the next phase acknowledges the alarm that announced the previous one.
            AcknowledgeTimerAlerts();
            Timer.Start(DateTimeOffset.UtcNow, Monotonic, selectedTask);
            if (Timer.Phase == Phase.Focus) Sounds.StartNoise();
            Status(L.T("status.running"));
            // The browser can only ring from another tab with permission, and asking needs a click.
            if (!askedNotifications) { askedNotifications = true; Browser.AskNotifications(); }
        }
        UpdateTimer(); SaveState();
    }

    private void PhaseClick(Phase phase)
    {
        if (phase == Timer.Phase) return;
        Sounds.Button("phase"); Sounds.StopNoise(); Timer.Select(phase, DateTimeOffset.UtcNow, Monotonic); UpdateTimer(); AnimateTimer(); SaveState();
    }

    private void ResetClick() { Sounds.Button("reset"); Sounds.StopNoise(); Timer.Select(Timer.Phase, DateTimeOffset.UtcNow, Monotonic); UpdateTimer(); SaveState(); Status(L.T("status.reset")); }

    private void SkipClick() { Sounds.Button("skip"); Sounds.StopNoise(); var next = Timer.NextPhase(); Timer.Select(next, DateTimeOffset.UtcNow, Monotonic); UpdateTimer(); AnimateTimer(); SaveState(); }

    private void AnimateTimer()
    {
        if (Settings.ReduceMotion) return;
        surfacePulse = true;
    }

    public void Status(string text) => statusText = text;

    /// <summary>Draws the workspace again after a widget changed something the whole page shows.</summary>
    internal void Refresh() => StateHasChanged();

    private void UpdateHeaderClock()
    {
        var now = DateTime.Now;
        var culture = Strings.Culture;
        headerDate = now.ToString("ddd d MMM", culture).ToUpper(culture);
        headerTime = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        // Always show how much of the active pomodoro remains — not only wall-clock time.
        if (Timer.Running || Timer.Active is not null)
        {
            var remaining = TimeSpan.FromSeconds(Math.Ceiling(Timer.Remaining));
            string mark = Timer.Running ? "❚❚" : "▶";
            headerTimer = $"{mark} {(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
        }
        else headerTimer = "";
    }

    private void HeaderTimerClick()
    {
        Sounds.Button(Timer.Running ? "pause" : "start");
        ToggleTimer();
    }

    private bool tasksOpen;

    private void TasksClick() => tasksOpen = true;

    private async Task TasksClosed((bool Changed, WorkTask? Task) result)
    {
        tasksOpen = false;
        if (result.Changed)
        {
            var (applied, status) = await TryApplyTaskSwitch(result.Task);
            if (applied) Status(status);
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
    internal async Task<(bool Applied, string Status)> TryApplyTaskSwitch(WorkTask? next)
    {
        string status = L.T("status.contextUpdated");
        if (selectedTask?.Id == next?.Id || Timer.Active is null)
        {
            SetSelectedTask(next);
            return (true, status);
        }

        int choice = await Choose(L.T("tasks.switchTimerTitle"),
        [
            L.T("tasks.switchTimerSavePrevious"),
            L.T("tasks.switchTimerKeepMoving"),
            L.T("common.cancel")
        ]);
        if (choice is < 0 or 2) return (false, status);

        if (choice == 0)
        {
            Sounds.StopNoise();
            Timer.Pause(DateTimeOffset.UtcNow, Monotonic);
            Timer.Finish(Outcome.Partial, DateTimeOffset.UtcNow);
            SetSelectedTask(next);
            return (true, L.T("status.contextUpdated"));
        }

        Timer.Rebind(next);
        SetSelectedTask(next);
        return (true, L.T("status.contextMoved"));
    }

    private async Task ReportClick()
    {
        await Alert(L.T("chrome.report").Split('·')[0].Trim(), L.T("web.comingSoon"));
    }

    private async Task AgentClick()
    {
        Sounds.Button("click");
        await Alert(L.T("agent.name").ToUpper(Strings.Culture), L.T("web.comingSoon"));
    }

    private void SettingsClick()
    {
        settingsOpen = true;
    }

    private void CloseSettings()
    {
        settingsOpen = false;
        ApplyLiveSettings();
        SaveState();
        StateHasChanged();
    }

    /// <summary>Puts a settings change on screen at once, so the panel shows its effect while it is open.</summary>
    public void ApplyLiveSettings()
    {
        Settings.Validate(); Strings.Use(Settings.Language); ApplyTimerPosition(); ApplyTheme();
        UpdateTimer(); Sounds.Refresh();
        StateHasChanged();
    }

    public void ApplyTheme()
    {
        string accent = Theme.TryParse(Settings.AccentColor, out var color) ? Theme.Hex(color) : "#D7D9D1";
        Browser.ApplyTheme(Settings.Dark, Settings.ReduceMotion, accent, Theme.Hex(Theme.Contrast(color)), Strings.Code);
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
    }

    private bool appliedTimerAtBottom;

    private void FullscreenClick() => Browser.ToggleFullscreen();

    [JSInvokable]
    public void FullscreenChanged(bool on)
    {
        fullscreen = on;
        MeasureSoon();
        StateHasChanged();
    }

    [JSInvokable]
    public void ViewportChanged()
    {
        MeasureCanvas();
        StateHasChanged();
    }

    private async void MeasureSoon()
    {
        await Task.Delay(60);
        MeasureCanvas();
        StateHasChanged();
    }

    private void MeasureCanvas()
    {
        var size = Browser.CanvasSize();
        if (size.Length < 2 || size[0] < 1 || size[1] < 1) return;
        canvasWidth = size[0];
        canvasHeight = size[1];
        ArrangeCards();
    }

    /// <summary>Keys the desktop owns: the page map, the page arrows, fullscreen and Space for the timer.</summary>
    [JSInvokable]
    public bool OnKey(string key, bool control, bool shift, bool typing, bool onButton)
    {
        bool handled = OnKeyCore(key, control, typing, onButton);
        if (handled) StateHasChanged();
        return handled;
    }

    private bool OnKeyCore(string key, bool control, bool typing, bool onButton)
    {
        if (dialogs.Count > 0)
        {
            if (key == "Escape") { dialogs[^1].Cancel(); return true; }
            return false;
        }
        if (settingsOpen)
        {
            if (key == "Escape") { CloseSettings(); return true; }
            return false;
        }
        // The desk handles its own Escape: it first closes an editor, then itself.
        if (tasksOpen) return false;
        if (key == "Escape" && pageMapOpen) { HidePageMap(); return true; }
        if (key == "Escape" && notificationsOpen) { notificationsOpen = false; return true; }
        if (key == "F11") { Browser.ToggleFullscreen(); return true; }
        if (control && (key == "m" || key == "M")) { TogglePageMap(); return true; }
        // While a note, a task or any field holds the caret the keyboard belongs to it.
        if (typing) return false;
        if (control && key == "ArrowLeft") { NavigateWorkspace(-1, 0); return true; }
        if (control && key == "ArrowRight") { NavigateWorkspace(1, 0); return true; }
        if (control && key == "ArrowUp") { NavigateWorkspace(0, -1); return true; }
        if (control && key == "ArrowDown") { NavigateWorkspace(0, 1); return true; }
        if (key == " " && CurrentTimerWidget is not null && !onButton && !pageMapOpen) { ToggleTimer(); return true; }
        return false;
    }

    public void Dispose() => self?.Dispose();
}
