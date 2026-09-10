using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PomoDock.Core;

namespace PomoDock.App;

public sealed class ReportWindow : Window
{
    private enum ReportTab { Summary, Detail }
    private enum PeriodMode { Week, Month, Year }

    private static readonly string[] MonthLabels = ["ENE", "FEB", "MAR", "ABR", "MAY", "JUN", "JUL", "AGO", "SEP", "OCT", "NOV", "DIC"];
    /// <summary>Sunday first, matching <see cref="DayOfWeek"/>, and named by the chosen language.</summary>
    private static string[] DayLabels =>
    [
        L.T("report.dayShort.sunday"), L.T("report.dayShort.monday"), L.T("report.dayShort.tuesday"),
        L.T("report.dayShort.wednesday"), L.T("report.dayShort.thursday"), L.T("report.dayShort.friday"),
        L.T("report.dayShort.saturday")
    ];
    private static readonly Color[] SeriesPalette =
    [
        Color.FromRgb(232, 93, 93), Color.FromRgb(242, 184, 75), Color.FromRgb(101, 196, 102),
        Color.FromRgb(78, 134, 217), Color.FromRgb(168, 107, 221), Color.FromRgb(224, 122, 56)
    ];

    private readonly MainWindow owner;
    private readonly StackPanel content = new();
    private readonly Button summaryTab;
    private readonly Button detailTab;
    private readonly Button weekTab;
    private readonly Button monthTab;
    private readonly Button yearTab;
    private StackedBarChart? chart;
    private List<Session> sessions = [];
    private List<Session> exportSessions = [];
    private ReportTab tab = ReportTab.Summary;
    private PeriodMode period = PeriodMode.Year;
    private DateOnly anchor = DateOnly.FromDateTime(DateTime.Today);

    public ReportWindow(MainWindow owner)
    {
        this.owner = owner;
        Title = "POMODOCK / REPORTE";
        Width = 760;
        Height = 760;
        MinWidth = 610;
        MinHeight = 540;
        MaxWidth = Math.Max(540, SystemParameters.WorkArea.Width - 24);
        MaxHeight = Math.Max(500, SystemParameters.WorkArea.Height - 24);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        // A window of our own gets no Window style, so its content would inherit black text.
        SetResourceReference(ForegroundProperty, "Ink");
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = false;

        var shell = new DockPanel { Margin = new Thickness(18) };
        Content = shell;

        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        shell.Children.Add(top);

        var titleRow = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition());
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel();
        title.Children.Add(new TextBlock { Text = L.T("report.title"), FontSize = 23, FontWeight = FontWeights.Black });
        title.Children.Add(new TextBlock { Text = L.T("report.tagline"), FontFamily = Mono(), FontSize = 9, Foreground = Resource("Muted") });
        titleRow.Children.Add(title);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(ActionButton(L.T("report.import"), Import, L.T("report.importTip")));
        actions.Children.Add(ActionButton(L.T("report.csv"), ExportCsv, L.T("report.csvTip")));
        actions.Children.Add(ActionButton(L.T("report.backup"), ExportJson, L.T("report.backupTip")));
        Grid.SetColumn(actions, 1);
        titleRow.Children.Add(actions);
        top.Children.Add(titleRow);

        var navigation = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        navigation.ColumnDefinitions.Add(new ColumnDefinition());
        navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tabs = new UniformGrid { Columns = 2, Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        summaryTab = NavButton(L.T("report.tabSummary"), () => { tab = ReportTab.Summary; Render(); });
        detailTab = NavButton(L.T("report.tabDetail"), () => { tab = ReportTab.Detail; Render(); });
        tabs.Children.Add(summaryTab);
        tabs.Children.Add(detailTab);
        navigation.Children.Add(tabs);
        var periods = new UniformGrid { Columns = 3, Width = 240 };
        weekTab = NavButton(L.T("report.week"), () => ChangePeriod(PeriodMode.Week));
        monthTab = NavButton(L.T("report.month"), () => ChangePeriod(PeriodMode.Month));
        yearTab = NavButton(L.T("report.year"), () => ChangePeriod(PeriodMode.Year));
        periods.Children.Add(weekTab);
        periods.Children.Add(monthTab);
        periods.Children.Add(yearTab);
        Grid.SetColumn(periods, 1);
        navigation.Children.Add(periods);
        top.Children.Add(navigation);

        var scroller = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        content.SetBinding(WidthProperty, new Binding(nameof(ScrollViewer.ViewportWidth)) { Source = scroller });
        shell.Children.Add(scroller);

        ReloadData();
    }

    internal FrameworkElement TakeModalContent(Action close, double width, double height)
    {
        if (Content is not FrameworkElement shell) throw new InvalidOperationException(L.T("report.alreadyOpen"));
        Content = null;
        var frame = new Border
        {
            Width = width,
            Height = height,
            ClipToBounds = true,
            Background = Resource("Surface"),
            BorderBrush = Resource("Edge"),
            BorderThickness = new Thickness(2)
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        root.RowDefinitions.Add(new RowDefinition());
        var header = new DockPanel { Background = Resource("Chrome"), LastChildFill = true };
        var dismiss = new Button
        {
            Content = "×", Padding = new Thickness(14, 2, 14, 2), Margin = new Thickness(0),
            BorderThickness = new Thickness(0), Foreground = Resource("ChromeInk"), Background = Brushes.Transparent,
            FontSize = 18, ToolTip = L.T("report.close")
        };
        dismiss.Click += (_, _) => close();
        DockPanel.SetDock(dismiss, Dock.Right);
        header.Children.Add(dismiss);
        header.Children.Add(new TextBlock
        {
            Text = L.T("report.modalTitle"),
            Foreground = Resource("ChromeInk"), FontWeight = FontWeights.Bold, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0)
        });
        root.Children.Add(header);
        Grid.SetRow(shell, 1);
        root.Children.Add(shell);
        frame.Child = root;
        frame.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { close(); e.Handled = true; } };
        return frame;
    }

    private void ReloadData()
    {
        sessions = owner.Store.Sessions().Where(s => s.Phase == Phase.Focus).OrderByDescending(s => s.Started).ToList();
        Render();
    }

    private void ChangePeriod(PeriodMode mode)
    {
        period = mode;
        anchor = DateOnly.FromDateTime(DateTime.Today);
        Render();
    }

    private void Render()
    {
        content.Children.Clear();
        chart = null;
        SetActive(summaryTab, tab == ReportTab.Summary);
        SetActive(detailTab, tab == ReportTab.Detail);
        SetActive(weekTab, period == PeriodMode.Week);
        SetActive(monthTab, period == PeriodMode.Month);
        SetActive(yearTab, period == PeriodMode.Year);
        if (tab == ReportTab.Summary) RenderSummary(); else RenderDetail();
    }

    private void RenderSummary()
    {
        var daily = Reports.Daily(sessions, TimeZoneInfo.Local);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var totalMinutes = daily.Sum(pair => pair.Value);

        content.Children.Add(Hero(totalMinutes));
        content.Children.Add(StatStrip(daily, today, totalMinutes));
        content.Children.Add(Rhythm(daily));

        content.Children.Add(Section(L.T("report.focusHours"), L.T("report.focusHoursDetail")));
        var range = Range();
        content.Children.Add(PeriodNavigator(range));

        var data = BuildChart(range.Start, range.End);
        exportSessions = data.Sessions;
        var chartFrame = new Border { Background = Resource("Surface"), BorderBrush = Fade("Line", 45), BorderThickness = new Thickness(1), Padding = new Thickness(10, 12, 12, 8), Margin = new Thickness(0, 10, 0, 10) };
        if (data.TotalMinutes <= 0)
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            empty.Children.Add(new TextBlock { Text = L.T("report.noPeriodData"), FontFamily = Mono(), FontSize = 13, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center });
            empty.Children.Add(new TextBlock { Text = L.T("report.noPeriodHint"), FontSize = 11, Foreground = Resource("Muted"), TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
            chartFrame.Child = empty;
            chartFrame.Height = 200;
        }
        else
        {
            chart = new StackedBarChart(data.Buckets, data.Colors) { Height = 210 };
            chartFrame.Child = chart;
        }
        content.Children.Add(chartFrame);

        // The period total is the headline of this block, so it is set as a number, not a line of text.
        var rangeStats = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        rangeStats.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rangeStats.ColumnDefinitions.Add(new ColumnDefinition());
        var total = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        total.Children.Add(new TextBlock { Text = FormatDuration(data.TotalMinutes), FontFamily = Mono(), FontWeight = FontWeights.Black, FontSize = 30, VerticalAlignment = VerticalAlignment.Bottom });
        total.Children.Add(new TextBlock { Text = L.T("report.periodTotal"), FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Resource("Muted"), Margin = new Thickness(9, 0, 0, 7), VerticalAlignment = VerticalAlignment.Bottom });
        rangeStats.Children.Add(total);
        var sessionCount = new TextBlock
        {
            Text = $"{data.Sessions.Count.ToString("N0", Strings.Culture)} {L.T("report.statSessions")}", FontFamily = Mono(), FontSize = 11,
            Foreground = Resource("Muted"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 7)
        };
        Grid.SetColumn(sessionCount, 1);
        rangeStats.Children.Add(sessionCount);
        content.Children.Add(rangeStats);

        content.Children.Add(Section(L.T("report.projects"), L.T("report.projectsDetail")));
        if (data.ProjectTotals.Count == 0)
        {
            content.Children.Add(new TextBlock { Text = L.T("report.noActivity") });
            return;
        }
        foreach (var item in data.ProjectTotals)
            content.Children.Add(ProjectRow(item.Name, item.Minutes, data.TotalMinutes, data.Colors.GetValueOrDefault(item.SeriesName, Resource("Muted"))));
    }

    /// <summary>
    /// The one number worth a screenshot: every hour of real focus, on an inverted band, with the
    /// rank it earned and how far the next one is. Everything below it is context for this.
    /// </summary>
    private FrameworkElement Hero(double totalMinutes)
    {
        var rank = FocusProfile.Of(sessions);
        var band = new Border { Background = Resource("Chrome"), Padding = new Thickness(22, 18, 22, 18), Margin = new Thickness(0, 0, 0, 12) };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = L.T("report.heroLead"), FontFamily = Mono(), FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = Resource("ChromeInk"), Opacity = .65
        });

        int hours = (int)(totalMinutes / 60), minutes = (int)Math.Round(totalMinutes % 60);
        var figure = new TextBlock
        {
            Text = L.T("report.heroHours", hours.ToString("N0", Strings.Culture), minutes.ToString("00", CultureInfo.InvariantCulture)),
            FontFamily = Mono(), FontSize = 52, FontWeight = FontWeights.Black, Foreground = Resource("ChromeInk"),
            Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.NoWrap
        };
        // Only ever shrinks, so a four-digit hour count stays whole instead of being clipped.
        stack.Children.Add(new Viewbox { Child = figure, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left, MaxHeight = 58 });

        if (totalMinutes <= 0)
        {
            stack.Children.Add(new TextBlock { Text = L.T("report.heroEmpty"), FontSize = 12, Foreground = Resource("ChromeInk"), Opacity = .7, Margin = new Thickness(0, 8, 0, 0) });
            band.Child = stack;
            return band;
        }

        var rankRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        rankRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rankRow.ColumnDefinitions.Add(new ColumnDefinition());
        var chip = new Border { Background = Resource("Accent"), Padding = new Thickness(10, 5, 10, 5), VerticalAlignment = VerticalAlignment.Center };
        chip.Child = new TextBlock
        {
            Text = L.T("report.heroRank", rank.Level.ToString("00", CultureInfo.InvariantCulture), rank.Name),
            FontFamily = Mono(), FontSize = 11, FontWeight = FontWeights.Black, Foreground = Resource("AccentInk")
        };
        rankRow.Children.Add(chip);
        var toNext = new TextBlock
        {
            Text = rank.IsHighest ? L.T("report.heroTop") : L.T("report.heroNext", rank.ToNext.ToString("0.#", Strings.Culture), FocusProfile.NextName(rank)),
            FontFamily = Mono(), FontSize = 10, Foreground = Resource("ChromeInk"), Opacity = .7,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap
        };
        Grid.SetColumn(toNext, 1);
        rankRow.Children.Add(toNext);
        stack.Children.Add(rankRow);

        // At the top rank there is nothing left to fill, so the bar is drawn complete on purpose.
        var track = new Grid { Height = 6, Margin = new Thickness(0, 10, 0, 0), Background = new SolidColorBrush(Color.FromArgb(60, 240, 240, 235)) };
        var fill = new Border { Background = Resource("Accent"), HorizontalAlignment = HorizontalAlignment.Left };
        track.SizeChanged += (_, _) => fill.Width = Math.Max(0, track.ActualWidth * rank.Share);
        track.Children.Add(fill);
        stack.Children.Add(track);

        band.Child = stack;
        return band;
    }

    /// <summary>Four numbers that say whether the habit is holding: streaks, days, and a daily average.</summary>
    private FrameworkElement StatStrip(Dictionary<DateOnly, double> daily, DateOnly today, double totalMinutes)
    {
        int activeDays = Math.Max(1, Reports.AccessDays(sessions, TimeZoneInfo.Local));
        var outcomes = Reports.Outcomes(sessions);
        int totalSessions = outcomes.Completed + outcomes.Partial;
        var strip = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 4) };
        strip.Children.Add(Tile(Reports.Streak(daily, today).ToString("N0", Strings.Culture), L.T("report.statStreak"), L.T("report.statStreakUnit"), true));
        strip.Children.Add(Tile(Reports.BestStreak(daily).ToString("N0", Strings.Culture), L.T("report.statBestStreak"), L.T("report.statStreakUnit"), false));
        strip.Children.Add(Tile(Reports.AccessDays(sessions, TimeZoneInfo.Local).ToString("N0", Strings.Culture), L.T("report.statDays"), "", false));
        strip.Children.Add(Tile(Spelled(totalMinutes / activeDays), L.T("report.statAverage"),
            totalSessions > 0 ? L.T("report.statCompleted", (outcomes.Completed * 100 / Math.Max(1, totalSessions)).ToString("0", CultureInfo.InvariantCulture)) : "", false));
        return strip;
    }

    private static FrameworkElement Tile(string value, string label, string detail, bool accent)
    {
        var stack = new StackPanel { Margin = new Thickness(13, 11, 13, 11) };
        stack.Children.Add(new TextBlock { Text = value, FontFamily = Mono(), FontSize = 26, FontWeight = FontWeights.Black, TextWrapping = TextWrapping.NoWrap });
        stack.Children.Add(new TextBlock { Text = label, FontSize = 8.5, FontWeight = FontWeights.Bold, Foreground = Resource("Muted"), Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap });
        if (detail.Length > 0)
            stack.Children.Add(new TextBlock { Text = detail, FontFamily = Mono(), FontSize = 8.5, Foreground = Resource("Muted"), Opacity = .8, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.NoWrap });
        return new Border
        {
            Background = Resource("Surface"),
            BorderBrush = accent ? Resource("Line") : Fade("Line", 55),
            BorderThickness = new Thickness(accent ? 1.5 : 1),
            Margin = new Thickness(0, 0, 6, 0),
            Child = stack
        };
    }

    /// <summary>
    /// When the work actually happens. Two small profiles — the hours of the day and the days of
    /// the week — plus the peak of each, which is the part people quote about themselves.
    /// </summary>
    private FrameworkElement Rhythm(Dictionary<DateOnly, double> daily)
    {
        var hours = Reports.ByHour(sessions, TimeZoneInfo.Local);
        var week = Reports.ByWeekday(daily);
        var best = Reports.BestDay(daily);
        var panel = new StackPanel();
        panel.Children.Add(Section(L.T("report.rhythm"), L.T("report.rhythmDetail")));

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(214) });

        var charts = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        charts.Children.Add(MiniHeader(L.T("report.byHour")));
        charts.Children.Add(new Profile(hours, index => index % 6 == 0 ? $"{index:00}" : "", 54));
        charts.Children.Add(MiniHeader(L.T("report.byWeekday")));
        // Monday first: the weekday array is Sunday-first to match DayOfWeek.
        var ordered = Enumerable.Range(0, 7).Select(index => week[(index + 1) % 7]).ToArray();
        charts.Children.Add(new Profile(ordered, index => DayLabels[(index + 1) % 7], 46));
        columns.Children.Add(charts);

        var callouts = new StackPanel();
        int peakHour = Array.IndexOf(hours, hours.Max());
        int peakDay = Array.IndexOf(week, week.Max());
        callouts.Children.Add(Callout(L.T("report.peakHour"), hours.Max() > 0 ? L.T("report.peakHourValue", peakHour.ToString("00", CultureInfo.InvariantCulture)) : "—"));
        callouts.Children.Add(Callout(L.T("report.peakDay"), week.Max() > 0 ? DayLabels[peakDay] : "—"));
        // Spelled out rather than hh:mm: "12/02 · 03:39" reads as a clock time, not as three hours.
        callouts.Children.Add(Callout(L.T("report.bestDay"), best.Minutes > 0
            ? L.T("report.bestDayValue", best.Day.ToString("dd/MM", CultureInfo.InvariantCulture), Spelled(best.Minutes))
            : "—"));
        Grid.SetColumn(callouts, 1);
        columns.Children.Add(callouts);

        panel.Children.Add(columns);
        return panel;
    }

    private static FrameworkElement MiniHeader(string text) => new TextBlock
    {
        Text = text, FontFamily = Mono(), FontSize = 8.5, FontWeight = FontWeights.Bold,
        Foreground = Resource("Muted"), Margin = new Thickness(0, 8, 0, 5)
    };

    private static FrameworkElement Callout(string label, string value)
    {
        var stack = new StackPanel { Margin = new Thickness(12, 9, 12, 9) };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 8.5, FontWeight = FontWeights.Bold, Foreground = Resource("Muted") });
        stack.Children.Add(new TextBlock { Text = value, FontFamily = Mono(), FontSize = 19, FontWeight = FontWeights.Black, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.NoWrap });
        return new Border { Background = Resource("Raised"), Margin = new Thickness(0, 0, 0, 6), Child = stack };
    }

    private static Brush Fade(string key, byte alpha)
    {
        if (Application.Current.Resources[key] is not SolidColorBrush source) return Resource(key);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, source.Color.R, source.Color.G, source.Color.B));
        brush.Freeze();
        return brush;
    }

    private void RenderDetail()
    {
        var range = Range();
        content.Children.Add(Section(L.T("report.detailTitle"), L.T("report.detailSubtitle")));
        content.Children.Add(PeriodNavigator(range));

        var candidates = sessions.Where(s => Reports.MinutesIn(s, range.Start, range.End, TimeZoneInfo.Local) > 0).ToList();
        exportSessions = candidates;
        var filters = new Grid { Margin = new Thickness(0, 12, 0, 10) };
        filters.ColumnDefinitions.Add(new ColumnDefinition());
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        var search = new TextBox { ToolTip = L.T("report.searchTip"), Margin = new Thickness(0, 0, 8, 0), MinHeight = 38 };
        search.SetValue(AutomationProperties.NameProperty, L.T("report.searchName"));
        var allProjects = L.T("report.allProjects");
        var projects = new ComboBox
        {
            ItemsSource = new[] { allProjects }.Concat(candidates.Select(ProjectName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value)).ToList(),
            SelectedIndex = 0,
            Margin = new Thickness(0),
            MinHeight = 38
        };
        filters.Children.Add(search);
        Grid.SetColumn(projects, 1);
        filters.Children.Add(projects);
        content.Children.Add(filters);

        var metadata = new TextBlock { FontFamily = Mono(), FontSize = 10, Foreground = Resource("Muted"), Margin = new Thickness(0, 0, 0, 10) };
        content.Children.Add(metadata);
        var list = new StackPanel();
        content.Children.Add(list);

        void RefreshList()
        {
            list.Children.Clear();
            var query = search.Text.Trim();
            var selectedProject = projects.SelectedItem?.ToString() ?? allProjects;
            var visible = candidates.Where(s =>
                (selectedProject == allProjects || string.Equals(ProjectName(s), selectedProject, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(query) || $"{s.Project} {s.Task} {s.Note}".Contains(query, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(s => s.Started).ToList();
            exportSessions = visible;
            var minutes = visible.Sum(s => Reports.MinutesIn(s, range.Start, range.End, TimeZoneInfo.Local));
            metadata.Text = L.T("report.sessionsMeta", visible.Count.ToString("N0", Strings.Culture), FormatDuration(minutes), PeriodLabel(range));
            if (visible.Count == 0)
            {
                list.Children.Add(new TextBlock { Text = L.T("report.noMatches"), Margin = new Thickness(0, 18, 0, 18) });
                return;
            }
            DateOnly? previousDay = null;
            foreach (var session in visible.Take(500))
            {
                var day = LocalDate(session.Started);
                if (day != previousDay)
                {
                    list.Children.Add(new TextBlock { Text = $"{DayLabels[(int)day.DayOfWeek]}  {day:dd/MM/yyyy}", FontFamily = Mono(), FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0, previousDay is null ? 4 : 16, 0, 6) });
                    previousDay = day;
                }
                list.Children.Add(SessionRow(session, range.Start, range.End));
            }
            if (visible.Count > 500)
                list.Children.Add(new TextBlock { Text = L.T("report.capped"), FontSize = 11, Foreground = Resource("Muted"), Margin = new Thickness(0, 10, 0, 10) });
        }
        search.TextChanged += (_, _) => RefreshList();
        projects.SelectionChanged += (_, _) => RefreshList();
        RefreshList();
    }

    private FrameworkElement PeriodNavigator((DateOnly Start, DateOnly End) range)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var previous = NavButton("←", () => { anchor = period switch { PeriodMode.Week => anchor.AddDays(-7), PeriodMode.Month => anchor.AddMonths(-1), _ => anchor.AddYears(-1) }; Render(); });
        previous.Width = 52;
        row.Children.Add(previous);
        var label = new TextBlock { Text = PeriodLabel(range), FontFamily = Mono(), FontSize = 14, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        var next = NavButton("→", () => { anchor = period switch { PeriodMode.Week => anchor.AddDays(7), PeriodMode.Month => anchor.AddMonths(1), _ => anchor.AddYears(1) }; Render(); });
        next.Width = 52;
        next.IsEnabled = NextRangeStart() <= DateOnly.FromDateTime(DateTime.Today);
        Grid.SetColumn(next, 2);
        row.Children.Add(next);
        return row;
    }

    private ChartData BuildChart(DateOnly start, DateOnly end)
    {
        var selectedSessions = sessions.Where(s => Reports.MinutesIn(s, start, end, TimeZoneInfo.Local) > 0).ToList();
        var dailyByProject = sessions.GroupBy(ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => Reports.Daily(group, TimeZoneInfo.Local), StringComparer.OrdinalIgnoreCase);
        var totals = dailyByProject.Select(pair => new ProjectTotal(pair.Key, Sum(pair.Value, start, end), pair.Key))
            .Where(item => item.Minutes > .001).OrderByDescending(item => item.Minutes).ToList();
        var primary = totals.Take(SeriesPalette.Length).Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var colors = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);
        int colorIndex = 0;
        foreach (var name in totals.Select(item => item.Name).Where(primary.Contains)) colors[name] = new SolidColorBrush(SeriesPalette[colorIndex++]);
        if (totals.Any(item => !primary.Contains(item.Name))) colors[OtherProjects] = new SolidColorBrush(Color.FromRgb(115, 118, 108));

        var projectTotals = totals.Select(item => primary.Contains(item.Name) ? item : item with { SeriesName = OtherProjects }).ToList();
        var buckets = new List<ReportBucket>();
        if (period == PeriodMode.Week)
        {
            for (var day = start; day <= end; day = day.AddDays(1)) buckets.Add(Bucket($"{DayLabels[(int)day.DayOfWeek]}\n{day:dd}", day, day));
        }
        else if (period == PeriodMode.Month)
        {
            for (var day = start; day <= end; day = day.AddDays(1)) buckets.Add(Bucket(day.Day.ToString(CultureInfo.InvariantCulture), day, day));
        }
        else
        {
            for (int month = 1; month <= 12; month++)
            {
                var first = new DateOnly(anchor.Year, month, 1);
                buckets.Add(Bucket(MonthLabels[month - 1], first, new DateOnly(anchor.Year, month, DateTime.DaysInMonth(anchor.Year, month))));
            }
        }
        return new ChartData(buckets, colors, projectTotals, selectedSessions, projectTotals.Sum(item => item.Minutes));

        ReportBucket Bucket(string label, DateOnly from, DateOnly to)
        {
            var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in dailyByProject)
            {
                var minutes = Sum(pair.Value, from, to);
                if (minutes <= .001) continue;
                var key = primary.Contains(pair.Key) ? pair.Key : OtherProjects;
                values[key] = values.GetValueOrDefault(key) + minutes;
            }
            return new ReportBucket(label, values);
        }
    }

    private static double Sum(Dictionary<DateOnly, double> daily, DateOnly start, DateOnly end) => daily.Where(pair => pair.Key >= start && pair.Key <= end).Sum(pair => pair.Value);

    private (DateOnly Start, DateOnly End) Range()
    {
        if (period == PeriodMode.Month)
        {
            var start = new DateOnly(anchor.Year, anchor.Month, 1);
            return (start, new DateOnly(anchor.Year, anchor.Month, DateTime.DaysInMonth(anchor.Year, anchor.Month)));
        }
        if (period == PeriodMode.Year) return (new DateOnly(anchor.Year, 1, 1), new DateOnly(anchor.Year, 12, 31));
        int offset = ((int)anchor.DayOfWeek + 6) % 7;
        var monday = anchor.AddDays(-offset);
        return (monday, monday.AddDays(6));
    }

    private DateOnly NextRangeStart()
    {
        var range = Range();
        return period switch { PeriodMode.Week => range.Start.AddDays(7), PeriodMode.Month => range.Start.AddMonths(1), _ => range.Start.AddYears(1) };
    }

    private string PeriodLabel((DateOnly Start, DateOnly End) range) => period switch
    {
        PeriodMode.Week when range.Start.Month == range.End.Month => $"{range.Start:dd}–{range.End:dd} {MonthLabels[range.Start.Month - 1]} {range.End:yyyy}",
        PeriodMode.Week => $"{range.Start:dd} {MonthLabels[range.Start.Month - 1]} – {range.End:dd} {MonthLabels[range.End.Month - 1]} {range.End:yyyy}",
        PeriodMode.Month => $"{MonthLabels[range.Start.Month - 1]} {range.Start:yyyy}",
        _ => range.Start.Year.ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>
    /// One project, drawn as the share it actually took. The bar is the row's own background rather
    /// than a swatch beside it, so the ranking is legible as a shape before a single number is read.
    /// </summary>
    private FrameworkElement ProjectRow(string name, double minutes, double total, Brush color)
    {
        double share = total > 0 ? minutes / total : 0;
        var frame = new Grid { Margin = new Thickness(0, 0, 0, 4), MinHeight = 44 };

        var track = new Border { Background = Fade("Line", 22) };
        frame.Children.Add(track);

        // A proportional column pair keeps the fill exact at any width, with no size handler.
        var proportion = new Grid();
        proportion.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.0001, share), GridUnitType.Star) });
        proportion.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.0001, 1 - share), GridUnitType.Star) });
        var fill = new Border { Background = color, Opacity = .32 };
        proportion.Children.Add(fill);
        var edge = new Border { Width = 3, Background = color, HorizontalAlignment = HorizontalAlignment.Left };
        proportion.Children.Add(edge);
        frame.Children.Add(proportion);

        var row = new Grid { Margin = new Thickness(11, 0, 11, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = name, FontWeight = FontWeights.Bold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(0, 0, 10, 0)
        });
        var value = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        value.Children.Add(new TextBlock { Text = FormatDuration(minutes), FontFamily = Mono(), FontWeight = FontWeights.Bold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        value.Children.Add(new TextBlock
        {
            Text = L.T("report.share", (share * 100).ToString("0", CultureInfo.InvariantCulture)),
            FontFamily = Mono(), FontWeight = FontWeights.Black, FontSize = 13, MinWidth = 44,
            TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0)
        });
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        frame.Children.Add(row);
        return frame;
    }

    private FrameworkElement SessionRow(Session session, DateOnly start, DateOnly end)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 5), Background = Resource("Surface"), MinHeight = 62 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var time = new TextBlock { Text = session.Started.LocalDateTime.ToString("HH:mm"), FontFamily = Mono(), FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(time);
        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 7, 8, 7) };
        copy.Children.Add(new TextBlock { Text = session.Task, FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        copy.Children.Add(new TextBlock { Text = ProjectName(session), FontSize = 10, Foreground = Resource("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        Grid.SetColumn(copy, 1);
        row.Children.Add(copy);
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(new TextBlock { Text = FormatDuration(Reports.MinutesIn(session, start, end, TimeZoneInfo.Local)), FontFamily = Mono(), FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) });
        var edit = ActionButton(L.T("common.edit"), () => Edit(session), L.T("report.editSession"));
        edit.Padding = new Thickness(8, 5, 8, 5);
        right.Children.Add(edit);
        Grid.SetColumn(right, 2);
        row.Children.Add(right);
        return row;
    }

    private static FrameworkElement Stat(string icon, string value, string label)
    {
        var stack = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 21, Margin = new Thickness(0, 4, 10, 0), Foreground = Resource("Muted") });
        line.Children.Add(new TextBlock { Text = value, FontFamily = Mono(), FontSize = 29, FontWeight = FontWeights.Black });
        stack.Children.Add(line);
        stack.Children.Add(new TextBlock { Text = label, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Resource("Muted"), Margin = new Thickness(0, 4, 0, 0) });
        return new Border { BorderBrush = Resource("Edge"), BorderThickness = new Thickness(1.5), Background = Resource("Surface"), Margin = new Thickness(0, 0, 8, 0), Child = stack };
    }

    private static FrameworkElement Section(string title, string subtitle)
    {
        var row = new Grid { Margin = new Thickness(0, 10, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.Black });
        var detail = new TextBlock { Text = subtitle, FontFamily = Mono(), FontSize = 9, Foreground = Resource("Muted"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(detail, 1);
        row.Children.Add(detail);
        return row;
    }

    private static Button NavButton(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 5, 0), FontSize = 10 };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button ActionButton(string text, Action action, string tooltip)
    {
        var button = Dialogs.Button(text, action);
        button.ToolTip = tooltip;
        button.Padding = new Thickness(10, 7, 10, 7);
        button.FontSize = 10;
        return button;
    }

    private static void SetActive(Button button, bool active)
    {
        button.Background = active ? Resource("Ink") : Resource("Surface");
        button.Foreground = active ? Resource("Paper") : Resource("Ink");
    }

    /// <summary>The bucket every project past the palette falls into, named in the current language.</summary>
    private static string OtherProjects => L.T("report.otherProjects");
    private static string ProjectName(Session session) => string.IsNullOrWhiteSpace(session.Project) ? L.T("common.noProject") : session.Project.Trim();
    private static DateOnly LocalDate(DateTimeOffset value) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.Local).DateTime);
    private static string FormatHoursCompact(double minutes) => Math.Floor(minutes / 60).ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>A duration in words rather than hh:mm, for the places a clock reading would mislead.</summary>
    private static string Spelled(double minutes)
    {
        int total = Math.Max(0, (int)Math.Round(minutes));
        return total < 60
            ? L.T("common.minutesShort", total)
            : total % 60 == 0 ? L.T("common.hoursShort", total / 60) : L.T("common.hoursMinutes", total / 60, total % 60);
    }
    private static string FormatDuration(double minutes)
    {
        int total = Math.Max(0, (int)Math.Round(minutes));
        return $"{total / 60:00}:{total % 60:00}";
    }
    private static FontFamily Mono() => new("Consolas");
    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    private void Edit(Session session)
    {
        var projectName = Dialogs.Prompt(owner, L.T("report.classifyTitle"), L.T("report.classifyProject"), session.Project);
        if (projectName is null) return;
        var taskName = Dialogs.Prompt(owner, L.T("report.classifyTitle"), L.T("report.classifyTask"), session.Task);
        if (taskName is null) return;
        var minutes = Dialogs.Prompt(owner, L.T("report.fixDurationTitle"), L.T("report.fixDurationLabel"), (session.Seconds / 60).ToString("0.###", CultureInfo.CurrentCulture));
        if (minutes is null) return;
        if (!double.TryParse(minutes, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || !double.IsFinite(value) || value <= 0 || value > 1440)
        {
            Dialogs.Alert(owner, L.T("report.badDurationTitle"), L.T("report.badDurationBody"));
            return;
        }
        session.Project = string.IsNullOrWhiteSpace(projectName) ? "Sin proyecto" : projectName;
        session.Task = string.IsNullOrWhiteSpace(taskName) ? "Enfoque libre" : taskName;
        if (Math.Abs(value * 60 - session.Seconds) > .1)
        {
            session.OriginalSeconds ??= session.Seconds;
            session.Segments = [new(session.Ended.AddMinutes(-value), session.Ended)];
            session.Started = session.Segments[0].Start;
            session.Note = L.T("report.correctedNote");
        }
        session.TaskId = owner.Settings.Tasks.FirstOrDefault(task => task.Name == session.Task && task.Project == session.Project && !task.Template)?.Id;
        owner.Store.Save(session);
        ReloadData();
    }

    private void ExportCsv()
    {
        var dialog = new SaveFileDialog { Filter = "CSV|*.csv", FileName = $"pomodock-{DateTime.Today:yyyy-MM-dd}.csv" };
        if (dialog.ShowDialog(owner) != true) return;
        Store.ExportCsv(dialog.FileName, exportSessions);
        owner.Status(L.T("report.csvExported", exportSessions.Count.ToString("N0", Strings.Culture)));
    }

    private void ExportJson()
    {
        var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = $"pomodock-backup-{DateTime.Today:yyyy-MM-dd}.json" };
        if (dialog.ShowDialog(owner) != true) return;
        owner.SaveState();
        owner.Store.ExportJson(dialog.FileName);
        owner.Status(L.T("report.backupExported"));
    }

    private void Import()
    {
        var dialog = new OpenFileDialog { Filter = "Pomofocus / PomoDock|*.csv;*.tsv;*.json|CSV de Pomofocus|*.csv;*.tsv|PomoDock JSON|*.json|Todos los archivos|*.*", Multiselect = false };
        if (dialog.ShowDialog(owner) != true) return;
        try
        {
            var backupDirectory = Path.Combine(owner.Store.DirectoryPath, "backups");
            Directory.CreateDirectory(backupDirectory);
            owner.Store.Backup(Path.Combine(backupDirectory, $"antes-de-importar-{DateTime.Now:yyyyMMdd-HHmmss}.db"));
            if (string.Equals(Path.GetExtension(dialog.FileName), ".json", StringComparison.OrdinalIgnoreCase))
            {
                // Habits and events go through their live stores: writing the rows directly would
                // be overwritten by the copy every open widget keeps in memory.
                var backup = Store.ReadBackup(dialog.FileName);
                int count = owner.Store.ImportSessions(backup);
                int habits = HabitStore.For(owner.Store).Merge(backup.Habits);
                int events = AgendaStore.For(owner.Store).Merge(backup.Agenda);
                int notes = NoteArchiveStore.For(owner.Store).Merge(backup.Notes);
                owner.Status(L.T("report.imported", count.ToString("N0", Strings.Culture), habits.ToString("N0", Strings.Culture), events.ToString("N0", Strings.Culture), notes.ToString("N0", Strings.Culture)));
            }
            else
            {
                var result = PomofocusCsv.Import(owner.Store, owner.Settings, dialog.FileName);
                owner.Status(L.T("report.importedPomofocus", result.Imported.ToString("N0", Strings.Culture), result.Skipped.ToString("N0", Strings.Culture), result.Invalid.ToString("N0", Strings.Culture)));
            }
            ReloadData();
        }
        catch (Exception ex)
        {
            Dialogs.Alert(owner, L.T("report.importFailedTitle"), L.T("report.importFailedBody", ex.Message));
        }
    }

    internal int VisibleSessionCount => exportSessions.Count;
    internal void ShowDetailForDiagnostics()
    {
        tab = ReportTab.Detail;
        Render();
    }
    internal bool ShowChartHoverForDiagnostics() => chart?.ShowFirstPopulatedBucketForDiagnostics() == true;

    /// <summary>
    /// A small profile of one series — the hours of the day, the days of the week. It is drawn
    /// rather than built from elements because it is two dozen bars that never need to be clicked,
    /// and it fills the tallest bar solid so the shape reads at a glance instead of being studied.
    /// </summary>
    private sealed class Profile(double[] values, Func<int, string> label, double barHeight) : FrameworkElement
    {
        private const double Labels = 13;

        protected override Size MeasureOverride(Size available) => new(available.Width, barHeight + Labels);

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (values.Length == 0) return;
            var ink = Resource("Ink");
            var muted = Resource("Muted");
            var track = Fade("Line", 30);
            double max = values.Max();
            double cell = ActualWidth / values.Length;
            double width = Math.Max(2, Math.Min(cell - 2, cell * .74));
            double peak = Array.IndexOf(values, max);
            for (int index = 0; index < values.Length; index++)
            {
                double x = index * cell + (cell - width) / 2;
                dc.DrawRectangle(track, null, new Rect(x, 0, width, barHeight));
                if (max > 0 && values[index] > 0)
                {
                    double height = Math.Max(1.5, barHeight * values[index] / max);
                    // The busiest bar is full ink; the rest sit back so the peak is the thing you see.
                    dc.DrawRectangle(index == peak ? ink : Fade("Ink", 105), null, new Rect(x, barHeight - height, width, height));
                }
                var text = label(index);
                if (text.Length == 0) continue;
                var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(Mono(), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 8, muted,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(formatted, new Point(index * cell + (cell - formatted.Width) / 2, barHeight + 3));
            }
        }
    }

    private sealed record ReportBucket(string Label, Dictionary<string, double> Values);
    private sealed record ProjectTotal(string Name, double Minutes, string SeriesName);
    private sealed record ChartData(List<ReportBucket> Buckets, Dictionary<string, Brush> Colors, List<ProjectTotal> ProjectTotals, List<Session> Sessions, double TotalMinutes);

    private sealed class StackedBarChart(List<ReportBucket> buckets, Dictionary<string, Brush> colors) : FrameworkElement
    {
        private const double Left = 54;
        private const double Bottom = 34;
        private int hoverIndex = -1;
        private Point hoverPosition;

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            var ink = Resource("Ink");
            var muted = Resource("Muted");
            var grid = new Pen(new SolidColorBrush(Color.FromArgb(70, 102, 105, 94)), .7);
            double bottom = Math.Max(40, ActualHeight - Bottom);
            double width = Math.Max(1, ActualWidth - Left - 5);
            double height = Math.Max(1, bottom - 22);
            double rawMax = buckets.Select(bucket => bucket.Values.Values.Sum()).DefaultIfEmpty(0).Max() / 60;
            double maxHours = NiceMaximum(rawMax);
            DrawText(dc, "H", 2, 0, muted, 9, FontWeights.Bold);
            for (int line = 0; line <= 4; line++)
            {
                double value = maxHours * line / 4;
                double y = bottom - height * line / 4;
                dc.DrawLine(grid, new Point(Left, y), new Point(ActualWidth, y));
                DrawText(dc, value.ToString(value < 10 ? "0.#" : "0", CultureInfo.CurrentCulture), 2, y - 7, muted, 9, FontWeights.Normal);
            }
            int count = Math.Max(1, buckets.Count);
            double cell = width / count;
            double barWidth = Math.Clamp(cell * .62, 2, 46);
            for (int index = 0; index < buckets.Count; index++)
            {
                double x = Left + index * cell + (cell - barWidth) / 2;
                double used = 0;
                foreach (var entry in buckets[index].Values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (!colors.TryGetValue(entry.Key, out var brush)) continue;
                    double segment = height * (entry.Value / 60) / maxHours;
                    dc.DrawRectangle(brush, new Pen(ink, .45), new Rect(x, bottom - used - segment, barWidth, segment));
                    used += segment;
                }
                int every = LabelFrequency(count, cell);
                if (index % every == 0 || index == buckets.Count - 1)
                    DrawText(dc, buckets[index].Label.Replace('\n', ' '), Left + index * cell, bottom + 8, muted, 8, FontWeights.Normal);
            }
            if (hoverIndex >= 0 && hoverIndex < buckets.Count)
                DrawHoverCard(dc, buckets[hoverIndex]);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var position = e.GetPosition(this);
            var next = BucketAt(position);
            if (next == hoverIndex && position == hoverPosition) return;
            hoverIndex = next;
            hoverPosition = position;
            Cursor = hoverIndex >= 0 ? Cursors.Hand : Cursors.Arrow;
            AutomationProperties.SetHelpText(this, hoverIndex >= 0 ? HoverText(buckets[hoverIndex]) : string.Empty);
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            hoverIndex = -1;
            Cursor = Cursors.Arrow;
            AutomationProperties.SetHelpText(this, string.Empty);
            InvalidateVisual();
        }

        private int BucketAt(Point position)
        {
            if (buckets.Count == 0 || position.X < Left || position.X > ActualWidth - 5 || position.Y < 10 || position.Y > ActualHeight - Bottom)
                return -1;
            var width = Math.Max(1, ActualWidth - Left - 5);
            int index = Math.Clamp((int)((position.X - Left) / width * buckets.Count), 0, buckets.Count - 1);
            return buckets[index].Values.Values.Sum() > .001 ? index : -1;
        }

        private void DrawHoverCard(DrawingContext dc, ReportBucket bucket)
        {
            var ink = Resource("Ink");
            var paper = Resource("Surface");
            var text = new FormattedText(HoverText(bucket), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(Mono(), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 10, ink,
                VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = Math.Max(120, Math.Min(245, ActualWidth - 42)),
                MaxTextHeight = 170
            };
            double cardWidth = Math.Min(ActualWidth - 16, Math.Max(160, text.Width + 24));
            double cardHeight = text.Height + 20;
            double x = Math.Clamp(hoverPosition.X + 14, 8, Math.Max(8, ActualWidth - cardWidth - 8));
            double y = hoverPosition.Y - cardHeight - 12;
            if (y < 8) y = Math.Min(ActualHeight - cardHeight - 8, hoverPosition.Y + 14);
            var rect = new Rect(x, Math.Max(8, y), cardWidth, cardHeight);
            dc.DrawRectangle(paper, new Pen(ink, 1.5), rect);
            dc.DrawText(text, new Point(rect.X + 12, rect.Y + 10));
        }

        private static string HoverText(ReportBucket bucket)
        {
            var total = bucket.Values.Values.Sum();
            var rows = bucket.Values.OrderByDescending(pair => pair.Value)
                .Select(pair => $"{pair.Key}  {FormatDuration(pair.Value)}");
            return L.T("report.bucketTotal", bucket.Label.Replace('\n', ' '), FormatDuration(total)) + "\n" + string.Join("\n", rows);
        }

        internal bool ShowFirstPopulatedBucketForDiagnostics()
        {
            hoverIndex = buckets.FindIndex(bucket => bucket.Values.Values.Sum() > .001);
            if (hoverIndex < 0) return false;
            double width = Math.Max(1, ActualWidth - Left - 5);
            double cell = width / Math.Max(1, buckets.Count);
            hoverPosition = new Point(Left + (hoverIndex + .5) * cell, Math.Max(30, ActualHeight / 2));
            InvalidateVisual();
            return true;
        }

        private static int LabelFrequency(int count, double cell) => count <= 12 ? 1 : Math.Max(1, (int)Math.Ceiling(28 / Math.Max(1, cell)));
        private static double NiceMaximum(double value)
        {
            if (value <= 0) return 1;
            double exponent = Math.Pow(10, Math.Floor(Math.Log10(value)));
            double fraction = value / exponent;
            double nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10;
            return nice * exponent;
        }
        private void DrawText(DrawingContext dc, string value, double x, double y, Brush brush, double size, FontWeight weight)
        {
            var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(Mono(), FontStyles.Normal, weight, FontStretches.Normal), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(x, y));
        }
    }
}
