using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PomoDock.App.Native;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// One centered, sticky card per unread notification. Unlike the corner toast it does not time out —
/// it waits until the user acts, so a reminder that rang while they were away is still waiting.
/// </summary>
internal sealed class NotificationModal : Window
{
    private const double CardWidth = 420;
    private const double Gap = 12;
    private static readonly List<NotificationModal> open = [];

    private readonly MainWindow owner;
    private readonly Guid notificationId;
    private readonly EventHandler ownerClosed;
    private bool leaving;

    public static int OpenCount => open.Count;
    public static NotificationModal? Newest => open.Count == 0 ? null : open[^1];

    public static bool IsShowing(Guid id) => open.Any(modal => modal.notificationId == id);

    public static void CloseAll()
    {
        foreach (var modal in open.ToArray()) modal.Close();
    }

    public static void Dismiss(Guid id)
    {
        foreach (var modal in open.Where(item => item.notificationId == id).ToArray())
            modal.Leave();
    }

    /// <summary>Calendar / task reminder with LISTO and snooze, matching the toast card language.</summary>
    public static void PresentReminder(
        MainWindow owner,
        ReminderNotification note,
        ReminderCue cue,
        DateTime now,
        Action<ReminderCue, int> snooze,
        Action<ReminderCue> complete,
        Action<Guid> acknowledged)
    {
        if (IsShowing(note.Id)) return;
        while (open.Count >= 5) open[0].Leave();
        var modal = new NotificationModal(owner, note.Id);
        modal.Content = modal.ReminderCard(cue, now, snooze, complete, acknowledged);
        open.Add(modal);
        modal.Show();
        Reflow();
    }

    /// <summary>Timer / generic alert with custom actions.</summary>
    public static void PresentAlert(
        MainWindow owner,
        ReminderNotification note,
        string lead,
        string title,
        string detail,
        string color,
        IReadOnlyList<AlertAction> actions,
        Action<Guid> acknowledged)
    {
        if (IsShowing(note.Id)) return;
        while (open.Count >= 5) open[0].Leave();
        var modal = new NotificationModal(owner, note.Id);
        modal.Content = modal.AlertCard(lead, title, detail, color, actions, acknowledged);
        open.Add(modal);
        modal.Show();
        Reflow();
    }

    /// <summary>Unread history without a live cue — acknowledge only.</summary>
    public static void PresentUnread(MainWindow owner, ReminderNotification note, Action<Guid> acknowledged)
    {
        if (IsShowing(note.Id)) return;
        while (open.Count >= 5) open[0].Leave();
        string lead = note.Kind == "timer"
            ? L.T("notifications.timer")
            : L.T(note.TaskId is null ? "notifications.event" : "notifications.task");
        string detail = note.DeliveredLocal.ToString("ddd dd MMM · HH:mm", Strings.Culture).ToUpper(Strings.Culture);
        var modal = new NotificationModal(owner, note.Id);
        modal.Content = modal.AlertCard(
            L.T("notifications.pendingLead"),
            note.Title,
            detail,
            "rose",
            [new AlertAction(L.T("agenda.toastDone"), () => { }, true)],
            acknowledged);
        open.Add(modal);
        modal.Show();
        Reflow();
    }

    private NotificationModal(MainWindow owner, Guid notificationId)
    {
        this.owner = owner;
        this.notificationId = notificationId;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SetResourceReference(ForegroundProperty, "Ink");
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = CardWidth;

        SourceInitialized += (_, _) => HideFromSwitcher();
        SizeChanged += (_, _) => Reflow();
        ownerClosed = (_, _) => Close();
        owner.Closed += ownerClosed;
        Closed += (_, _) =>
        {
            owner.Closed -= ownerClosed;
            open.Remove(this);
            Reflow();
        };
        Loaded += (_, _) => Enter();
    }

    private UIElement ReminderCard(
        ReminderCue cue,
        DateTime now,
        Action<ReminderCue, int> snooze,
        Action<ReminderCue> complete,
        Action<Guid> acknowledged)
    {
        var item = cue.Event;
        var frame = Shell(AgendaVisuals.Solid(item.Color));
        var stack = Body(frame);

        string lead = item.AllDay ? AgendaVisuals.DayLabel(cue.Series) : AgendaVisuals.Countdown(cue.Start, now);
        stack.Children.Add(Header(L.T("agenda.reminderLead", lead), AgendaVisuals.Solid(item.Color), acknowledged));
        stack.Children.Add(TitleBlock(item.Title));

        string when = item.AllDay
            ? L.T("agenda.toastAllDay", AgendaVisuals.LongDayLabel(cue.Series))
            : L.T("agenda.toastTime", AgendaVisuals.DayLabel(cue.Series), cue.Start.ToString("HH:mm", CultureInfo.InvariantCulture), item.EndOn(cue.Series).ToString("HH:mm", CultureInfo.InvariantCulture));
        stack.Children.Add(new TextBlock { Text = when, FontFamily = new FontFamily("Consolas"), FontSize = 11, FontWeight = FontWeights.Bold });

        if (item.Location.Length > 0)
            stack.Children.Add(new TextBlock { Text = "◈  " + item.Location, FontSize = 11, Foreground = AgendaVisuals.Resource("Muted"), Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var done = PrimaryButton(L.T("agenda.toastDone"));
        done.Click += (_, _) => { complete(cue); Acknowledge(acknowledged); };
        actions.Children.Add(done);
        foreach (int minutes in new[] { 5, 15 })
        {
            var later = SecondaryButton(L.T("agenda.snoozeButton", minutes));
            later.ToolTip = L.T("agenda.snoozeTip", minutes);
            later.Click += (_, _) => { snooze(cue, minutes); Acknowledge(acknowledged); };
            actions.Children.Add(later);
        }
        stack.Children.Add(actions);
        WireActivate(frame);
        return frame;
    }

    private UIElement AlertCard(
        string lead,
        string title,
        string detail,
        string color,
        IReadOnlyList<AlertAction> actions,
        Action<Guid> acknowledged)
    {
        var frame = Shell(AgendaVisuals.Solid(color));
        var stack = Body(frame);
        stack.Children.Add(Header(lead, AgendaVisuals.Solid(color), acknowledged));
        stack.Children.Add(TitleBlock(title));
        if (detail.Length > 0)
            stack.Children.Add(new TextBlock { Text = detail, FontFamily = new FontFamily("Consolas"), FontSize = 11, FontWeight = FontWeights.Bold });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var action in actions)
        {
            var button = action.Primary ? PrimaryButton(action.Label) : SecondaryButton(action.Label);
            var run = action.Run;
            button.Click += (_, _) => { run(); Acknowledge(acknowledged); };
            row.Children.Add(button);
        }
        stack.Children.Add(row);
        WireActivate(frame);
        return frame;
    }

    private Border Shell(Brush accent)
    {
        var frame = new Border
        {
            BorderBrush = AgendaVisuals.Resource("Edge"),
            BorderThickness = new Thickness(2),
            Background = AgendaVisuals.Resource("Surface")
        };
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.Children.Add(new Border { Background = accent });
        frame.Child = layout;
        return frame;
    }

    private static StackPanel Body(Border frame)
    {
        var layout = (Grid)frame.Child!;
        var stack = new StackPanel { Margin = new Thickness(15, 12, 12, 13) };
        Grid.SetColumn(stack, 1);
        layout.Children.Add(stack);
        return stack;
    }

    private UIElement Header(string lead, Brush accent, Action<Guid> acknowledged)
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = lead,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center
        });
        var close = new Button
        {
            Content = "×",
            FontSize = 15,
            Padding = new Thickness(8, 0, 8, 2),
            Margin = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            ToolTip = L.T("agenda.dismiss")
        };
        close.Click += (_, _) => Acknowledge(acknowledged);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        return header;
    }

    private static TextBlock TitleBlock(string title) => new()
    {
        Text = title,
        FontSize = 18,
        FontWeight = FontWeights.Black,
        TextWrapping = TextWrapping.Wrap,
        MaxHeight = 52,
        Margin = new Thickness(0, 6, 0, 4)
    };

    private static Button PrimaryButton(string label) => new()
    {
        Content = label,
        FontSize = 11,
        Padding = new Thickness(12, 7, 12, 7),
        Background = AgendaVisuals.Resource("Ink"),
        Foreground = AgendaVisuals.Resource("Paper")
    };

    private static Button SecondaryButton(string label) => new()
    {
        Content = label,
        FontSize = 11,
        Padding = new Thickness(11, 7, 11, 7)
    };

    private void WireActivate(Border frame)
    {
        frame.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && Ancestors.Find<Button>(source) is not null) return;
            if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
            owner.Activate();
        };
        frame.Cursor = Cursors.Hand;
    }

    private void Acknowledge(Action<Guid> acknowledged)
    {
        acknowledged(notificationId);
        Leave();
    }

    private void HideFromSwitcher()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;
        long style = Win32.GetWindowLongPtr(handle, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtr(handle, Win32.GWL_EXSTYLE, (nint)(style | Win32.WS_EX_TOOLWINDOW));
    }

    private void Enter()
    {
        if (owner.Settings.ReduceMotion) return;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    private void Leave()
    {
        if (leaving) return;
        leaving = true;
        if (owner.Settings.ReduceMotion) { Close(); return; }
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Stacks sticky cards in the middle of the work area.</summary>
    private static void Reflow()
    {
        var area = SystemParameters.WorkArea;
        double total = 0;
        foreach (var modal in open)
            total += (modal.ActualHeight > 0 ? modal.ActualHeight : 170) + Gap;
        if (open.Count > 0) total -= Gap;
        double top = area.Top + Math.Max(24, (area.Height - total) / 2);
        foreach (var modal in open)
        {
            double height = modal.ActualHeight > 0 ? modal.ActualHeight : 170;
            modal.Left = area.Left + (area.Width - CardWidth) / 2;
            modal.Top = Math.Min(area.Bottom - height - 18, Math.Max(area.Top + 8, top));
            top += height + Gap;
        }
    }
}
