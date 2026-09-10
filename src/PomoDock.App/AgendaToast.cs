using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PomoDock.App.Native;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// The reminder itself: a card in the corner of the screen. It has no owner window, so it still
/// appears while PomoDock is minimised or covered, and it can postpone or finish the event
/// without the user opening anything.
/// </summary>
internal sealed class AgendaToast : Window
{
    private const double CardWidth = 380;
    private const double Gap = 10;
    private static readonly List<AgendaToast> open = [];

    private readonly DispatcherTimer life = new() { Interval = TimeSpan.FromSeconds(55) };
    private readonly MainWindow owner;
    private readonly EventHandler ownerClosed;
    private bool leaving;

    /// <summary>Raises a reminder card. Older cards step aside when the corner fills up.</summary>
    public static void Present(MainWindow owner, ReminderCue cue, DateTime now, Action<ReminderCue, int> snooze, Action<ReminderCue> complete)
    {
        while (open.Count >= 4) open[0].Leave();
        var toast = new AgendaToast(owner, cue, now, snooze, complete);
        open.Add(toast);
        toast.Show();
        Reflow();
    }

    /// <summary>Closes every card, used when PomoDock itself is closing.</summary>
    public static void CloseAll()
    {
        foreach (var toast in open.ToArray()) toast.Close();
    }

    internal static int OpenCount => open.Count;
    internal static AgendaToast? Newest => open.Count == 0 ? null : open[^1];

    private AgendaToast(MainWindow owner, ReminderCue cue, DateTime now, Action<ReminderCue, int> snooze, Action<ReminderCue> complete)
    {
        this.owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        // A window of our own gets no Window style, so its content would inherit black text.
        SetResourceReference(ForegroundProperty, "Ink");
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = CardWidth;
        var area = SystemParameters.WorkArea;
        Left = area.Right - CardWidth - 18;
        Top = area.Bottom - 200;
        Content = Card(cue, now, snooze, complete);

        SourceInitialized += (_, _) => HideFromSwitcher();
        SizeChanged += (_, _) => Reflow();
        // A card the user is reading never disappears under the cursor.
        MouseEnter += (_, _) => life.Stop();
        MouseLeave += (_, _) => { if (!leaving) life.Start(); };
        life.Tick += (_, _) => Leave();
        life.Start();
        ownerClosed = (_, _) => Close();
        owner.Closed += ownerClosed;
        Closed += (_, _) =>
        {
            life.Stop();
            owner.Closed -= ownerClosed;
            open.Remove(this);
            Reflow();
        };
        Loaded += (_, _) => Enter();
    }

    private UIElement Card(ReminderCue cue, DateTime now, Action<ReminderCue, int> snooze, Action<ReminderCue> complete)
    {
        var item = cue.Event;
        var frame = new Border
        {
            BorderBrush = AgendaVisuals.Resource("Edge"),
            BorderThickness = new Thickness(2),
            Background = AgendaVisuals.Resource("Surface")
        };
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.Children.Add(new Border { Background = AgendaVisuals.Solid(item.Color) });

        var stack = new StackPanel { Margin = new Thickness(15, 12, 12, 13) };
        Grid.SetColumn(stack, 1);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string lead = item.AllDay ? AgendaVisuals.DayLabel(cue.Series) : AgendaVisuals.Countdown(cue.Start, now);
        header.Children.Add(new TextBlock
        {
            Text = L.T("agenda.reminderLead", lead),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = AgendaVisuals.Solid(item.Color),
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
        close.Click += (_, _) => Leave();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        stack.Children.Add(header);

        stack.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontSize = 18,
            FontWeight = FontWeights.Black,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 52,
            Margin = new Thickness(0, 6, 0, 4)
        });

        string when = item.AllDay
            ? L.T("agenda.toastAllDay", AgendaVisuals.LongDayLabel(cue.Series))
            : L.T("agenda.toastTime", AgendaVisuals.DayLabel(cue.Series), cue.Start.ToString("HH:mm", CultureInfo.InvariantCulture), item.EndOn(cue.Series).ToString("HH:mm", CultureInfo.InvariantCulture));
        stack.Children.Add(new TextBlock { Text = when, FontFamily = new FontFamily("Consolas"), FontSize = 11, FontWeight = FontWeights.Bold });

        if (item.Location.Length > 0)
            stack.Children.Add(new TextBlock { Text = "◈  " + item.Location, FontSize = 11, Foreground = AgendaVisuals.Resource("Muted"), Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        if (item.Notes.Length > 0)
            stack.Children.Add(new TextBlock { Text = item.Notes, FontSize = 11, Foreground = AgendaVisuals.Resource("Muted"), Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap, MaxHeight = 48 });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var done = new Button { Content = L.T("agenda.toastDone"), FontSize = 11, Padding = new Thickness(12, 7, 12, 7), Background = AgendaVisuals.Resource("Ink"), Foreground = AgendaVisuals.Resource("Paper") };
        done.Click += (_, _) => { complete(cue); Leave(); };
        actions.Children.Add(done);
        foreach (int minutes in new[] { 5, 15 })
        {
            var later = new Button { Content = L.T("agenda.snoozeButton", minutes), FontSize = 11, Padding = new Thickness(11, 7, 11, 7), ToolTip = L.T("agenda.snoozeTip", minutes) };
            later.Click += (_, _) => { snooze(cue, minutes); Leave(); };
            actions.Children.Add(later);
        }
        stack.Children.Add(actions);

        layout.Children.Add(stack);
        frame.Child = layout;
        // Anywhere outside the buttons brings PomoDock back to the front.
        frame.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && Ancestor<Button>(source) is not null) return;
            if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
            owner.Activate();
        };
        frame.Cursor = Cursors.Hand;
        return frame;
    }

    private static T? Ancestor<T>(DependencyObject source) where T : DependencyObject => Ancestors.Find<T>(source);

    /// <summary>Keeps the card out of Alt+Tab: it is a notification, not a window to switch to.</summary>
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
        life.Stop();
        if (owner.Settings.ReduceMotion) { Close(); return; }
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Stacks the open cards upwards from the bottom-right corner of the work area.</summary>
    private static void Reflow()
    {
        var area = SystemParameters.WorkArea;
        double bottom = area.Bottom - 18;
        for (int index = open.Count - 1; index >= 0; index--)
        {
            var toast = open[index];
            double height = toast.ActualHeight > 0 ? toast.ActualHeight : 160;
            bottom -= height;
            toast.Left = area.Right - CardWidth - 18;
            toast.Top = Math.Max(area.Top + 8, bottom);
            bottom -= Gap;
        }
    }
}
