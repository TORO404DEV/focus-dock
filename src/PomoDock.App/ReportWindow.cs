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
    private static readonly string[] DayLabels = ["DOM", "LUN", "MAR", "MIÉ", "JUE", "VIE", "SÁB"];
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
    private List<Session> sessions = [];
    private List<Session> exportSessions = [];
    private ReportTab tab = ReportTab.Summary;
    private PeriodMode period = PeriodMode.Year;
    private DateOnly anchor = DateOnly.FromDateTime(DateTime.Today);

    public ReportWindow(MainWindow owner)
    {
        this.owner = owner;
        Title = "POMODOCK / REPORTE";
        Width = 820;
        Height = 880;
        MinWidth = 610;
        MinHeight = 540;
        MaxWidth = Math.Max(610, SystemParameters.WorkArea.Width - 24);
        MaxHeight = Math.Max(540, SystemParameters.WorkArea.Height - 24);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
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
        title.Children.Add(new TextBlock { Text = "REPORTE DE ENFOQUE", FontSize = 23, FontWeight = FontWeights.Black });
        title.Children.Add(new TextBlock { Text = "LO QUE MIDES, LO PUEDES MEJORAR.", FontFamily = Mono(), FontSize = 9, Foreground = Resource("Muted") });
        titleRow.Children.Add(title);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(ActionButton("⇩ IMPORTAR", Import, "Importar historial de Pomofocus o PomoDock"));
        actions.Children.Add(ActionButton("⇧ CSV", ExportCsv, "Exportar el periodo visible"));
        actions.Children.Add(ActionButton("□ BACKUP", ExportJson, "Crear respaldo completo"));
        Grid.SetColumn(actions, 1);
        titleRow.Children.Add(actions);
        top.Children.Add(titleRow);

        var navigation = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        navigation.ColumnDefinitions.Add(new ColumnDefinition());
        navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tabs = new UniformGrid { Columns = 2, Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        summaryTab = NavButton("▥  RESUMEN", () => { tab = ReportTab.Summary; Render(); });
        detailTab = NavButton("☷  DETALLE", () => { tab = ReportTab.Detail; Render(); });
        tabs.Children.Add(summaryTab);
        tabs.Children.Add(detailTab);
        navigation.Children.Add(tabs);
        var periods = new UniformGrid { Columns = 3, Width = 240 };
        weekTab = NavButton("SEMANA", () => ChangePeriod(PeriodMode.Week));
        monthTab = NavButton("MES", () => ChangePeriod(PeriodMode.Month));
        yearTab = NavButton("AÑO", () => ChangePeriod(PeriodMode.Year));
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
        if (Content is not FrameworkElement shell) throw new InvalidOperationException("El reporte ya está abierto.");
        Content = null;
        var frame = new Border
        {
            Width = width,
            Height = height,
            Background = Resource("Surface"),
            BorderBrush = Resource("Line"),
            BorderThickness = new Thickness(2)
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        root.RowDefinitions.Add(new RowDefinition());
        var header = new DockPanel { Background = Resource("Ink"), LastChildFill = true };
        var dismiss = new Button
        {
            Content = "×", Padding = new Thickness(14, 2, 14, 2), Margin = new Thickness(0),
            BorderThickness = new Thickness(0), Foreground = Resource("Paper"), Background = Brushes.Transparent,
            FontSize = 18, ToolTip = "Cerrar reporte"
        };
        dismiss.Click += (_, _) => close();
        DockPanel.SetDock(dismiss, Dock.Right);
        header.Children.Add(dismiss);
        header.Children.Add(new TextBlock
        {
            Text = "REPORTE / TU HISTORIAL",
            Foreground = Resource("Paper"), FontWeight = FontWeights.Bold, FontSize = 11,
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
        var accessDays = Reports.AccessDays(sessions, TimeZoneInfo.Local);
        var summary = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 20) };
        summary.Children.Add(Stat("◷", FormatHoursCompact(totalMinutes), "HORAS ENFOCADO"));
        summary.Children.Add(Stat("▦", accessDays.ToString("N0"), "DÍAS REGISTRADOS"));
        summary.Children.Add(Stat("♨", Reports.Streak(daily, today).ToString("N0"), "RACHA ACTUAL"));
        content.Children.Add(summary);

        content.Children.Add(Section("HORAS DE ENFOQUE", "TIEMPO REAL CONFIRMADO · SIN DESCANSOS"));
        var range = Range();
        content.Children.Add(PeriodNavigator(range));

        var data = BuildChart(range.Start, range.End);
        exportSessions = data.Sessions;
        var chartFrame = new Border { BorderBrush = Resource("Line"), BorderThickness = new Thickness(1.5), Padding = new Thickness(12), Margin = new Thickness(0, 12, 0, 12) };
        if (data.TotalMinutes <= 0)
        {
            chartFrame.Child = new TextBlock
            {
                Text = "SIN SESIONES EN ESTE PERIODO\nUsa las flechas para explorar tu historial.",
                FontFamily = Mono(), FontSize = 14, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            chartFrame.Height = 300;
        }
        else
        {
            chartFrame.Child = new StackedBarChart(data.Buckets, data.Colors) { Height = 300 };
        }
        content.Children.Add(chartFrame);

        var rangeStats = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        rangeStats.ColumnDefinitions.Add(new ColumnDefinition());
        rangeStats.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rangeStats.Children.Add(new TextBlock { Text = $"{FormatDuration(data.TotalMinutes)}  EN ESTE PERIODO", FontFamily = Mono(), FontWeight = FontWeights.Bold, FontSize = 18 });
        var sessionCount = new TextBlock { Text = $"{data.Sessions.Count:N0} SESIONES", FontFamily = Mono(), FontSize = 11, Foreground = Resource("Muted"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(sessionCount, 1);
        rangeStats.Children.Add(sessionCount);
        content.Children.Add(rangeStats);

        content.Children.Add(Section("PROYECTOS", "DISTRIBUCIÓN DEL PERIODO"));
        if (data.ProjectTotals.Count == 0)
        {
            content.Children.Add(new TextBlock { Text = "Tu actividad aparecerá aquí cuando completes una sesión." });
            return;
        }
        foreach (var item in data.ProjectTotals)
            content.Children.Add(ProjectRow(item.Name, item.Minutes, data.TotalMinutes, data.Colors.GetValueOrDefault(item.SeriesName, Resource("Muted"))));
    }

    private void RenderDetail()
    {
        var range = Range();
        content.Children.Add(Section("DETALLE DE SESIONES", "BUSCA, REVISA Y CORRIGE TU HISTORIAL"));
        content.Children.Add(PeriodNavigator(range));

        var candidates = sessions.Where(s => Reports.MinutesIn(s, range.Start, range.End, TimeZoneInfo.Local) > 0).ToList();
        exportSessions = candidates;
        var filters = new Grid { Margin = new Thickness(0, 12, 0, 10) };
        filters.ColumnDefinitions.Add(new ColumnDefinition());
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        var search = new TextBox { ToolTip = "Buscar proyecto, tarea o nota", Margin = new Thickness(0, 0, 8, 0), MinHeight = 38 };
        search.SetValue(AutomationProperties.NameProperty, "Buscar sesiones");
        var projects = new ComboBox
        {
            ItemsSource = new[] { "TODOS LOS PROYECTOS" }.Concat(candidates.Select(ProjectName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value)).ToList(),
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
            var selectedProject = projects.SelectedItem?.ToString() ?? "TODOS LOS PROYECTOS";
            var visible = candidates.Where(s =>
                (selectedProject == "TODOS LOS PROYECTOS" || string.Equals(ProjectName(s), selectedProject, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(query) || $"{s.Project} {s.Task} {s.Note}".Contains(query, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(s => s.Started).ToList();
            exportSessions = visible;
            var minutes = visible.Sum(s => Reports.MinutesIn(s, range.Start, range.End, TimeZoneInfo.Local));
            metadata.Text = $"{visible.Count:N0} SESIONES   ·   {FormatDuration(minutes)}   ·   {PeriodLabel(range)}";
            if (visible.Count == 0)
            {
                list.Children.Add(new TextBlock { Text = "No hay sesiones que coincidan con estos filtros.", Margin = new Thickness(0, 18, 0, 18) });
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
                list.Children.Add(new TextBlock { Text = "Se muestran las 500 sesiones más recientes de este filtro. Exporta CSV para obtenerlas todas.", FontSize = 11, Foreground = Resource("Muted"), Margin = new Thickness(0, 10, 0, 10) });
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
        if (totals.Any(item => !primary.Contains(item.Name))) colors["OTROS"] = new SolidColorBrush(Color.FromRgb(115, 118, 108));

        var projectTotals = totals.Select(item => primary.Contains(item.Name) ? item : item with { SeriesName = "OTROS" }).ToList();
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
                var key = primary.Contains(pair.Key) ? pair.Key : "OTROS";
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

    private FrameworkElement ProjectRow(string name, double minutes, double total, Brush color)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 1), Background = Resource("Surface"), MinHeight = 48 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var swatch = new Border { Width = 11, Height = 11, Background = color, BorderBrush = Resource("Line"), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(swatch);
        var label = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        var value = new TextBlock { Text = $"{FormatDuration(minutes)}   {(total > 0 ? minutes / total * 100 : 0):0}%", FontFamily = Mono(), FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        return row;
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
        var edit = ActionButton("EDITAR", () => Edit(session), "Editar esta sesión");
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
        return new Border { BorderBrush = Resource("Line"), BorderThickness = new Thickness(1.5), Background = Resource("Surface"), Margin = new Thickness(0, 0, 8, 0), Child = stack };
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

    private static string ProjectName(Session session) => string.IsNullOrWhiteSpace(session.Project) ? "Sin proyecto" : session.Project.Trim();
    private static DateOnly LocalDate(DateTimeOffset value) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.Local).DateTime);
    private static string FormatHoursCompact(double minutes) => Math.Floor(minutes / 60).ToString("N0", CultureInfo.CurrentCulture);
    private static string FormatDuration(double minutes)
    {
        int total = Math.Max(0, (int)Math.Round(minutes));
        return $"{total / 60:00}:{total % 60:00}";
    }
    private static FontFamily Mono() => new("Consolas");
    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    private void Edit(Session session)
    {
        var projectName = Dialogs.Prompt(owner, "CLASIFICAR SESIÓN", "Proyecto", session.Project);
        if (projectName is null) return;
        var taskName = Dialogs.Prompt(owner, "CLASIFICAR SESIÓN", "Tarea", session.Task);
        if (taskName is null) return;
        var minutes = Dialogs.Prompt(owner, "CORREGIR DURACIÓN", "Minutos reales (la corrección queda identificada)", (session.Seconds / 60).ToString("0.###", CultureInfo.CurrentCulture));
        if (minutes is null) return;
        if (!double.TryParse(minutes, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || !double.IsFinite(value) || value <= 0 || value > 1440)
        {
            Dialogs.Alert(owner, "DURACIÓN INVÁLIDA", "La duración debe estar entre 0 y 1440 minutos.");
            return;
        }
        session.Project = string.IsNullOrWhiteSpace(projectName) ? "Sin proyecto" : projectName;
        session.Task = string.IsNullOrWhiteSpace(taskName) ? "Enfoque libre" : taskName;
        if (Math.Abs(value * 60 - session.Seconds) > .1)
        {
            session.OriginalSeconds ??= session.Seconds;
            session.Segments = [new(session.Ended.AddMinutes(-value), session.Ended)];
            session.Started = session.Segments[0].Start;
            session.Note = "Duración corregida manualmente. Se asigna un intervalo continuo anterior a la hora de fin.";
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
        owner.Status($"CSV EXPORTADO · {exportSessions.Count:N0} sesiones del periodo visible.");
    }

    private void ExportJson()
    {
        var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = $"pomodock-backup-{DateTime.Today:yyyy-MM-dd}.json" };
        if (dialog.ShowDialog(owner) != true) return;
        owner.SaveState();
        owner.Store.ExportJson(dialog.FileName);
        owner.Status("BACKUP EXPORTADO · Historial y configuración.");
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
                int count = owner.Store.ImportJson(dialog.FileName);
                owner.Status($"IMPORTADAS {count:N0} SESIONES · Se conservaron las existentes.");
            }
            else
            {
                var result = PomofocusCsv.Import(owner.Store, owner.Settings, dialog.FileName);
                owner.Status($"POMOFOCUS · {result.Imported:N0} IMPORTADAS · {result.Skipped:N0} EXISTENTES · {result.Invalid:N0} OMITIDAS.");
            }
            ReloadData();
        }
        catch (Exception ex)
        {
            Dialogs.Alert(owner, "IMPORTACIÓN FALLIDA", "No se pudo importar: " + ex.Message);
        }
    }

    internal int VisibleSessionCount => exportSessions.Count;
    internal void ShowDetailForDiagnostics()
    {
        tab = ReportTab.Detail;
        Render();
    }

    private sealed record ReportBucket(string Label, Dictionary<string, double> Values);
    private sealed record ProjectTotal(string Name, double Minutes, string SeriesName);
    private sealed record ChartData(List<ReportBucket> Buckets, Dictionary<string, Brush> Colors, List<ProjectTotal> ProjectTotals, List<Session> Sessions, double TotalMinutes);

    private sealed class StackedBarChart(List<ReportBucket> buckets, Dictionary<string, Brush> colors) : FrameworkElement
    {
        private const double Left = 54;
        private const double Bottom = 34;

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
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var position = e.GetPosition(this);
            int index = (int)((position.X - Left) / Math.Max(1, ActualWidth - Left - 5) * buckets.Count);
            if (index < 0 || index >= buckets.Count) { ToolTip = null; return; }
            var bucket = buckets[index];
            var lines = bucket.Values.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key}: {FormatDuration(pair.Value)}");
            ToolTip = $"{bucket.Label.Replace('\n', ' ')} · {FormatDuration(bucket.Values.Values.Sum())}\n{string.Join("\n", lines)}";
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
