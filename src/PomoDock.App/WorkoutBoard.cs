using System.Globalization;
using System.IO;
using System.Threading;
using System.Media;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// Mancuernas widget scaled to the card: mockup layout, full-bleed covers, custom editor.
/// </summary>
internal sealed class WorkoutBoard : Grid, IReskinnable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static Task<CoreWebView2Environment>? sharedEnv;
    private static readonly SolidColorBrush Black = Brushes.Black;
    private static readonly SolidColorBrush White = Brushes.White;
    private static readonly Brush Dim = Brush("#B3B3B3");
    private static readonly Brush Bar = Brushes.White;
    private static readonly Brush Blue = Brush("#0A84FF");
    private static readonly FontFamily Icons = new("Segoe MDL2 Assets");

    private readonly MainWindow owner;
    private readonly WidgetConfig config;
    private readonly WidgetCard? card;
    private readonly ContentControl pages = new();
    private readonly Border playerHost = new() { Background = Black, Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer settle = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly DispatcherTimer posterRefresh = new() { Interval = TimeSpan.FromMilliseconds(160) };

    private State data = new();
    private Scale S = Scale.Fallback;
    private int layoutKey = -1;
    private WebView2? demo;
    private bool nativeNotified;
    private bool playerShellReady;
    private DateTime sessionStart;
    private int sessionKg;
    private Dictionary<string, double> kgOverride = [];

    public WorkoutBoard(MainWindow owner, WidgetConfig config, WidgetCard? card)
    {
        this.owner = owner;
        this.config = config;
        this.card = card;
        data = Read(config);
        kgOverride = data.Kg ?? [];
        data.Customs ??= [];
        Background = Black;
        ClipToBounds = true;
        Children.Add(pages);
        Children.Add(playerHost);
        SkinChrome();
        settle.Tick += (_, _) => { settle.Stop(); if (Measure().Key != layoutKey) Render(); };
        posterRefresh.Tick += (_, _) => { posterRefresh.Stop(); if (data.View != "player") Render(); };
        SizeChanged += (_, _) => { settle.Stop(); settle.Start(); };
        Loaded += (_, _) => { SkinChrome(); Render(); };
        Unloaded += (_, _) => { settle.Stop(); posterRefresh.Stop(); DisposeDemo(); };
        Render();
    }

    public void Reskin() { SkinChrome(); Render(); }
    internal bool HasDemoSurface => demo is not null && playerHost.Visibility == Visibility.Visible;

    private sealed record Scale(double W, double H, double Title, double Sub, double Body, double Small, double Pad, double Nav, double Tab, double Icon, int Key)
    {
        public static readonly Scale Fallback = Of(380, 640);
        public static Scale Of(double w, double h)
        {
            w = Math.Max(280, w);
            h = Math.Max(360, h);
            double title = Math.Clamp(w * 0.062, 20, 34);
            double sub = Math.Clamp(w * 0.038, 13, 18);
            double body = Math.Clamp(w * 0.045, 15, 22);
            double small = Math.Clamp(w * 0.032, 11, 15);
            double pad = Math.Clamp(w * 0.04, 12, 22);
            double nav = Math.Clamp(h * 0.08, 52, 72);
            double tab = Math.Clamp(h * 0.07, 44, 58);
            double icon = Math.Clamp(w * 0.055, 18, 26);
            int key = ((int)(w / 20) << 8) | (int)(h / 20);
            return new(w, h, title, sub, body, small, pad, nav, tab, icon, key);
        }
    }

    internal sealed class State
    {
        public string View { get; set; } = "home";
        public string Tab { get; set; } = "std";
        public string? ProgramId { get; set; }
        public int Day { get; set; }
        public int Move { get; set; }
        public int Set { get; set; }
        public bool Resting { get; set; }
        public string? RestNext { get; set; }
        public int RestDone { get; set; }
        public int RestSets { get; set; }
        public int RestSeconds { get; set; }
        public bool UseLb { get; set; }
        public int HeightCm { get; set; } = 175;
        public double BodyKg { get; set; } = 85;
        public int Volume { get; set; } = 80;
        public bool Voice { get; set; }
        public Dictionary<string, double>? Kg { get; set; }
        public List<LogRow> Log { get; set; } = [];
        public List<WorkoutPrograms.Program> Customs { get; set; } = [];
    }

    internal sealed class LogRow
    {
        public DateTime When { get; set; }
        public string Program { get; set; } = "";
        public string Day { get; set; } = "";
        public int Kg { get; set; }
        public int Cal { get; set; }
        public int Seconds { get; set; }
    }

    internal static State Read(WidgetConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Value)) return new State();
        try
        {
            var data = JsonSerializer.Deserialize<State>(config.Value, Json);
            if (data is null || string.IsNullOrWhiteSpace(data.View)) return new State();
            data.Log ??= [];
            data.Kg ??= [];
            data.Customs ??= [];
            if (data.View == "player") { data.View = "program"; data.Resting = false; }
            return data;
        }
        catch { return new State(); }
    }

    private void Persist()
    {
        data.Kg = kgOverride;
        config.Value = JsonSerializer.Serialize(data);
        owner.SaveState();
    }

    private void SkinChrome() => card?.ApplySkin(Black, White, Brush("#1A1A1A"));
    private WorkoutPrograms.Program? Resolve() => WorkoutPrograms.Find(data.ProgramId, data.Customs);
    private bool IsCustom => data.ProgramId?.StartsWith("c-", StringComparison.Ordinal) == true;

    private Scale Measure()
    {
        double w = ActualWidth > 8 ? ActualWidth : Math.Max(300, config.Width - 22);
        double h = ActualHeight > 8 ? ActualHeight : Math.Max(420, config.Height - 46);
        return Scale.Of(w, h);
    }

    private void Render()
    {
        S = Measure();
        layoutKey = S.Key;
        if (data.View is "player")
        {
            pages.Visibility = Visibility.Collapsed;
            playerHost.Visibility = Visibility.Visible;
            _ = EnsurePlayerAsync();
            return;
        }
        HidePlayer();
        pages.Visibility = Visibility.Visible;
        pages.Content = data.View switch
        {
            "program" => BuildProgram(),
            "done" => BuildDone(),
            "edit" => BuildEditor(),
            "pick" => BuildPicker(),
            _ => BuildHome()
        };
    }

    // ---------------------------------------------------------------- home

    private FrameworkElement BuildHome()
    {
        var root = new DockPanel { LastChildFill = true, Background = Black };
        string title = data.Tab switch
        {
            "log" => "Mancuernas",
            "set" => "Ajustes",
            "me" => "Mancuernas",
            "custom" => "Mancuernas",
            _ => "Mancuernas"
        };
        string sub = data.Tab switch
        {
            "log" => "Estadísticas",
            "set" => "Perfil",
            "me" => "Progreso",
            "custom" => "Personalizadas",
            _ => "Estándar"
        };
        var header = BuildHeader(title, sub, back: data.Tab == "set", gear: data.Tab != "set");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var tabs = BuildTabs();
        DockPanel.SetDock(tabs, Dock.Top);
        root.Children.Add(tabs);
        root.Children.Add(data.Tab switch
        {
            "log" => BuildHistoryBody(),
            "set" => BuildSettingsBody(),
            "me" => BuildProgressBody(),
            "custom" => BuildCustomBody(),
            _ => BuildStandardBody()
        });
        return root;
    }

    private FrameworkElement BuildStandardBody()
    {
        var programs = WorkoutPrograms.All;
        double chrome = S.Nav + S.Tab + 4;
        double cover = Math.Max(72, (S.H - chrome) / Math.Max(1, programs.Count));
        var list = new StackPanel();
        foreach (var program in programs)
        {
            var pick = program;
            list.Children.Add(ProgramCard(pick, cover, () => OpenProgram(pick.Id)));
        }
        return list;
    }

    private Border ProgramCard(WorkoutPrograms.Program program, double height, Action click)
    {
        var layer = PhotoLayer(program.CoverId, height, AlignmentY.Center);
        var name = Txt(program.Name, S.Title, FontWeights.SemiBold);
        name.VerticalAlignment = VerticalAlignment.Bottom;
        name.Margin = new Thickness(S.Pad, 0, S.Pad, S.Pad + S.Small + 4);
        var cad = Txt(program.Cadence, S.Small, FontWeights.Normal, Dim);
        cad.HorizontalAlignment = HorizontalAlignment.Right;
        cad.VerticalAlignment = VerticalAlignment.Bottom;
        cad.Margin = new Thickness(S.Pad, 0, S.Pad, S.Pad * 0.55);
        layer.Children.Add(name);
        layer.Children.Add(cad);
        var border = new Border { Child = layer, Cursor = Cursors.Hand, Background = Black };
        border.MouseLeftButtonUp += (_, e) => { click(); e.Handled = true; };
        return border;
    }

    private void OpenProgram(string id)
    {
        data.ProgramId = id;
        data.Day = 0;
        data.View = "program";
        Persist();
        Render();
    }

    private FrameworkElement BuildCustomBody()
    {
        var root = new DockPanel { LastChildFill = true };
        var banner = new Grid { Height = Math.Clamp(S.H * 0.22, 110, 180), ClipToBounds = true, Background = Brush("#141414") };
        banner.Children.Add(new TextBlock
        {
            Text = "\uE8AB",
            FontFamily = Icons,
            FontSize = S.H * 0.12,
            Foreground = Brush("#2A2A2A"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        var create = Txt("Crear entrenamiento", S.Title, FontWeights.SemiBold);
        create.VerticalAlignment = VerticalAlignment.Center;
        create.Margin = new Thickness(S.Pad, 0, 0, 0);
        banner.Children.Add(create);
        banner.Cursor = Cursors.Hand;
        banner.MouseLeftButtonUp += (_, e) => { NewCustom(); e.Handled = true; };
        DockPanel.SetDock(banner, Dock.Top);
        root.Children.Add(banner);

        var list = new StackPanel();
        if (data.Customs.Count == 0)
            list.Children.Add(Txt("Toca arriba para armar tu rutina con mancuernas y banca.", S.Small, FontWeights.Normal, Dim, new Thickness(S.Pad, S.Pad, S.Pad, 0)));
        foreach (var program in data.Customs)
        {
            var pick = program;
            var row = ProgramCard(pick, Math.Clamp(S.H * 0.16, 88, 140), () => OpenProgram(pick.Id));
            list.Children.Add(row);
        }
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return root;
    }

    private void NewCustom()
    {
        var created = new WorkoutPrograms.Program
        {
            Id = "c-" + Guid.NewGuid().ToString("N")[..8],
            Name = "Mi rutina",
            Cadence = "Personalizada",
            CoverId = "0294",
            RestSet = 30,
            RestMove = 60,
            Days = [new WorkoutPrograms.Day { Label = "Día 1", Moves = [] }]
        };
        data.Customs.Add(created);
        data.ProgramId = created.Id;
        data.Day = 0;
        data.View = "edit";
        Persist();
        Render();
    }

    private FrameworkElement BuildHistoryBody()
    {
        var list = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        double tons = data.Log.Sum(x => x.Kg) / 1000.0;
        var total = Txt($"{tons:0} toneladas", Math.Clamp(S.Title * 1.15, 24, 40), FontWeights.SemiBold, White, new Thickness(S.Pad, 6, S.Pad, 10));
        total.HorizontalAlignment = HorizontalAlignment.Center;
        list.Children.Add(total);
        if (data.Log.Count == 0)
            list.Children.Add(Txt("Aún no hay sesiones. Empieza una rutina en Estándar.", S.Body, FontWeights.Normal, Dim, new Thickness(S.Pad, 12, S.Pad, 0)));
        string? dayHeader = null;
        foreach (var row in data.Log.OrderByDescending(x => x.When))
        {
            string head = row.When.ToString("dddd, d 'de' MMMM", new CultureInfo("es-ES"));
            if (!string.Equals(head, dayHeader, StringComparison.Ordinal))
            {
                dayHeader = head;
                var h = Txt(head, S.Small, FontWeights.Normal, Dim, new Thickness(S.Pad, 14, S.Pad, 8));
                h.HorizontalAlignment = HorizontalAlignment.Center;
                list.Children.Add(h);
            }
            list.Children.Add(HistoryRow(row));
        }
        return new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private Border HistoryRow(LogRow row)
    {
        var grid = new Grid { Margin = new Thickness(S.Pad * 0.4, 0, S.Pad * 0.4, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(S.Icon * 2.4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var thumb = new Border
        {
            Width = S.Icon * 2.2,
            Height = S.Icon * 2.2,
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Child = PhotoLayer(CoverIdOf(row.Program), S.Icon * 2.2, AlignmentY.Center)
        };
        grid.Children.Add(thumb);
        var mid = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(Txt(row.Program, S.Body, FontWeights.SemiBold));
        if (!string.IsNullOrWhiteSpace(row.Day))
            mid.Children.Add(Txt(row.Day, S.Small, FontWeights.Normal, Dim));
        mid.Children.Add(Txt($"{row.Kg:N0} kg", S.Small, FontWeights.Normal, Dim));
        Grid.SetColumn(mid, 1);
        grid.Children.Add(mid);
        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(Txt($"\uE9D9  {row.Cal}", S.Small, FontWeights.SemiBold));
        right.Children.Add(Txt(TimeSpan.FromSeconds(row.Seconds).ToString(@"mm\:ss"), S.Small, FontWeights.Normal, Dim));
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return new Border { Child = grid, Padding = new Thickness(8, 10, 8, 10), Background = Brush("#111"), Margin = new Thickness(0, 0, 0, 1) };
    }

    private FrameworkElement BuildProgressBody()
    {
        var stack = new StackPanel { Margin = new Thickness(S.Pad, 8, S.Pad, S.Pad) };
        int weeks = 8;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var start = today.AddDays(-(int)today.DayOfWeek);
        var bars = new Grid { Height = Math.Clamp(S.H * 0.28, 120, 220), Margin = new Thickness(0, 8, 0, 16) };
        double max = 1;
        var weekKg = new double[weeks];
        for (int i = 0; i < weeks; i++)
        {
            var from = start.AddDays(-7 * (weeks - 1 - i));
            var to = from.AddDays(7);
            weekKg[i] = data.Log.Where(x => DateOnly.FromDateTime(x.When) >= from && DateOnly.FromDateTime(x.When) < to).Sum(x => x.Kg);
            max = Math.Max(max, weekKg[i]);
        }
        bars.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < weeks; i++) bars.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < weeks; i++)
        {
            double frac = weekKg[i] / max;
            var col = new DockPanel { Margin = new Thickness(3, 0, 3, 0) };
            var fill = new Border
            {
                Background = White,
                Height = Math.Max(4, frac * (bars.Height - 8)),
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(2)
            };
            col.Children.Add(fill);
            Grid.SetColumn(col, i + 1);
            bars.Children.Add(col);
        }
        stack.Children.Add(Txt("Volumen semanal", S.Body, FontWeights.SemiBold));
        stack.Children.Add(bars);
        stack.Children.Add(Metric($"{data.Log.Count}", "entrenamientos"));
        stack.Children.Add(Metric($"{data.Log.Sum(x => x.Kg):N0} kg", "levantados"));
        stack.Children.Add(Metric($"{data.Log.Sum(x => x.Cal)}", "calorías"));
        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private StackPanel Metric(string value, string label)
    {
        var p = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        p.Children.Add(Txt(value, S.Title, FontWeights.SemiBold));
        p.Children.Add(Txt(label, S.Small, FontWeights.Normal, Dim));
        return p;
    }

    private FrameworkElement BuildSettingsBody()
    {
        var stack = new StackPanel { Margin = new Thickness(S.Pad, 8, S.Pad, S.Pad) };
        stack.Children.Add(Txt("Perfil", S.Small, FontWeights.SemiBold, Dim, new Thickness(0, 4, 0, 10)));
        stack.Children.Add(SettingLine("Estatura", $"{data.HeightCm} cm", () => NudgeHeight(1), () => NudgeHeight(-1)));
        stack.Children.Add(SettingLine("Peso", $"{data.BodyKg:0} kg", () => NudgeBody(1), () => NudgeBody(-1)));
        stack.Children.Add(Txt("Sonidos", S.Small, FontWeights.SemiBold, Dim, new Thickness(0, 18, 0, 10)));
        stack.Children.Add(Txt("Volumen de la señal sonora", S.Body, FontWeights.Normal));
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = data.Volume, Margin = new Thickness(0, 8, 0, 8) };
        slider.ValueChanged += (_, _) => { data.Volume = (int)slider.Value; Persist(); };
        stack.Children.Add(slider);
        var voice = new CheckBox
        {
            Content = "Guía por voz",
            IsChecked = data.Voice,
            Foreground = White,
            FontSize = S.Body,
            Margin = new Thickness(0, 4, 0, 12)
        };
        voice.Checked += (_, _) => { data.Voice = true; Persist(); };
        voice.Unchecked += (_, _) => { data.Voice = false; Persist(); };
        stack.Children.Add(voice);
        stack.Children.Add(Txt("Equipo", S.Small, FontWeights.SemiBold, Dim, new Thickness(0, 10, 0, 10)));
        var units = new UniformGrid { Columns = 2, Height = S.Nav * 0.7 };
        units.Children.Add(UnitChip("KG", !data.UseLb, () => { data.UseLb = false; Persist(); Render(); }));
        units.Children.Add(UnitChip("LB", data.UseLb, () => { data.UseLb = true; Persist(); Render(); }));
        stack.Children.Add(units);
        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void NudgeHeight(int d) { data.HeightCm = Math.Clamp(data.HeightCm + d, 140, 220); Persist(); Render(); }
    private void NudgeBody(double d) { data.BodyKg = Math.Clamp(data.BodyKg + d, 40, 180); Persist(); Render(); }

    // ---------------------------------------------------------------- program + editor

    private FrameworkElement BuildProgram()
    {
        var program = Resolve();
        if (program is null) { data.View = "home"; return BuildHome(); }
        if (program.Days.Count == 0) program.Days.Add(new WorkoutPrograms.Day { Label = "Día 1" });
        data.Day = Math.Clamp(data.Day, 0, program.Days.Count - 1);
        var day = program.Days[data.Day];
        RefreshEstimates(program, day);

        var root = new DockPanel { Background = Black };
        var header = BuildHeader(program.Name, null, true, gear: false, edit: IsCustom);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        double coverH = Math.Clamp(S.H * 0.26, 140, 220);
        var cover = PhotoLayer(program.CoverId, coverH, AlignmentY.Center);
        var cad = Txt(program.Cadence, S.Body, FontWeights.Normal);
        cad.HorizontalAlignment = HorizontalAlignment.Center;
        cad.VerticalAlignment = VerticalAlignment.Center;
        cover.Children.Add(cad);
        DockPanel.SetDock(cover, Dock.Top);
        root.Children.Add(cover);

        var stats = new UniformGrid { Columns = 3, Margin = new Thickness(S.Pad * 0.4, S.Pad * 0.6, S.Pad * 0.4, S.Pad * 0.5) };
        stats.Children.Add(Stat($"\uE9D9  {day.Calories} cal"));
        stats.Children.Add(Stat($"\uE805  {WorkoutPrograms.VolumeKg(day):N0} kg"));
        stats.Children.Add(Stat($"\uE916  {day.Minutes} minutos"));
        DockPanel.SetDock(stats, Dock.Top);
        root.Children.Add(stats);

        var start = new Button
        {
            Content = "EMPEZAR ENTRENAMIENTO",
            FontWeight = FontWeights.Bold,
            FontSize = S.Body,
            Height = Math.Clamp(S.Nav * 0.85, 44, 56),
            BorderThickness = new Thickness(0),
            Background = Bar,
            Foreground = Black,
            Cursor = Cursors.Hand
        };
        start.Click += (_, _) => BeginSession(program, day);
        DockPanel.SetDock(start, Dock.Top);
        root.Children.Add(start);

        var list = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        if (program.Days.Count > 1)
        {
            var dayNav = new DockPanel { Margin = new Thickness(S.Pad * 0.5, 6, S.Pad * 0.5, 12) };
            var prev = IconBtn("\uE76B", () => { data.Day = (data.Day + program.Days.Count - 1) % program.Days.Count; Persist(); Render(); });
            var next = IconBtn("\uE76C", () => { data.Day = (data.Day + 1) % program.Days.Count; Persist(); Render(); });
            DockPanel.SetDock(prev, Dock.Left);
            DockPanel.SetDock(next, Dock.Right);
            dayNav.Children.Add(prev);
            dayNav.Children.Add(next);
            var label = Txt(string.IsNullOrWhiteSpace(day.Label) ? $"Día {data.Day + 1}" : day.Label, S.Body, FontWeights.SemiBold);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            dayNav.Children.Add(label);
            list.Children.Add(dayNav);
        }
        int i = 0;
        foreach (var move in day.Moves)
        {
            double kg = KgOf(program.Id, data.Day, i, move.Kg);
            list.Children.Add(MoveRow(move, kg));
            i++;
        }
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return root;
    }

    private static void RefreshEstimates(WorkoutPrograms.Program program, WorkoutPrograms.Day day)
    {
        if (!program.Id.StartsWith("c-", StringComparison.Ordinal)) return;
        day.Calories = WorkoutPrograms.EstimateCal(day);
        day.Minutes = WorkoutPrograms.EstimateMin(day, program.RestSet);
    }

    private Border MoveRow(WorkoutPrograms.Move move, double kg)
    {
        var row = new Border { Padding = new Thickness(S.Pad * 0.7, S.Pad * 0.55, S.Pad, S.Pad * 0.55), Background = Black };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(S.Icon * 2.2) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Border
        {
            Width = S.Icon * 2,
            Height = S.Icon * 2,
            ClipToBounds = true,
            Child = PhotoLayer(move.CatalogId, S.Icon * 2, AlignmentY.Center)
        };
        grid.Children.Add(icon);
        var mid = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(Txt(move.Name, S.Body, FontWeights.SemiBold));
        mid.Children.Add(Txt(WorkoutPrograms.Scheme(move), S.Small, FontWeights.Normal, Dim));
        Grid.SetColumn(mid, 1);
        grid.Children.Add(mid);
        var w = Txt(WeightLabel(kg), S.Body, FontWeights.SemiBold, Dim);
        w.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(w, 2);
        grid.Children.Add(w);
        row.Child = grid;
        return row;
    }

    private FrameworkElement BuildEditor()
    {
        var program = Resolve();
        if (program is null || !IsCustom) { data.View = "home"; data.Tab = "custom"; return BuildHome(); }
        if (program.Days.Count == 0) program.Days.Add(new WorkoutPrograms.Day { Label = "Día 1" });
        data.Day = Math.Clamp(data.Day, 0, program.Days.Count - 1);
        var day = program.Days[data.Day];

        var root = new DockPanel { Background = Black };
        var top = new DockPanel { Height = S.Nav, Margin = new Thickness(8, 4, 8, 4) };
        var close = IconBtn("\uE711", () => { data.View = "program"; Persist(); Render(); });
        var save = IconBtn("\uE73E", () => { RefreshEstimates(program, day); data.View = "program"; Persist(); Render(); }, Blue);
        DockPanel.SetDock(close, Dock.Left);
        DockPanel.SetDock(save, Dock.Right);
        top.Children.Add(close);
        top.Children.Add(save);
        var ht = Txt("Rutina de entrenamiento", S.Body, FontWeights.SemiBold);
        ht.HorizontalAlignment = HorizontalAlignment.Center;
        ht.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(ht);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var form = new StackPanel { Margin = new Thickness(S.Pad, 4, S.Pad, S.Pad) };
        form.Children.Add(Txt("Nombre del entrenamiento", S.Small, FontWeights.Normal, Dim));
        var name = new TextBox
        {
            Text = program.Name,
            FontSize = S.Body,
            Margin = new Thickness(0, 6, 0, 14),
            Padding = new Thickness(10, 8, 10, 8),
            Background = White,
            Foreground = Black
        };
        name.TextChanged += (_, _) => { program.Name = name.Text.Trim().Length == 0 ? "Mi rutina" : name.Text.Trim(); Persist(); };
        form.Children.Add(name);
        form.Children.Add(SettingLine("Descanso entre series", $"{program.RestSet} s",
            () => { program.RestSet = Math.Min(180, program.RestSet + 5); Persist(); Render(); },
            () => { program.RestSet = Math.Max(15, program.RestSet - 5); Persist(); Render(); }));
        form.Children.Add(SettingLine("Descanso entre ejercicios", $"{program.RestMove} s",
            () => { program.RestMove = Math.Min(180, program.RestMove + 5); Persist(); Render(); },
            () => { program.RestMove = Math.Max(15, program.RestMove - 5); Persist(); Render(); }));

        form.Children.Add(Txt(day.Label, S.Body, FontWeights.SemiBold, White, new Thickness(0, 16, 0, 8)));
        int mi = 0;
        foreach (var move in day.Moves.ToList())
        {
            int index = mi;
            form.Children.Add(EditorMove(program, day, index, move));
            mi++;
        }
        form.Children.Add(GhostLink("AGREGAR EJERCICIO", () => { data.View = "pick"; Persist(); Render(); }));
        form.Children.Add(GhostLink("AGREGAR DÍA", () =>
        {
            program.Days.Add(new WorkoutPrograms.Day { Label = $"Día {program.Days.Count + 1}", Moves = [] });
            data.Day = program.Days.Count - 1;
            Persist(); Render();
        }));
        if (data.Customs.Count > 0)
            form.Children.Add(GhostLink("ELIMINAR RUTINA", () =>
            {
                data.Customs.RemoveAll(p => p.Id == program.Id);
                data.View = "home";
                data.Tab = "custom";
                data.ProgramId = null;
                Persist(); Render();
            }));

        root.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return root;
    }

    private Border EditorMove(WorkoutPrograms.Program program, WorkoutPrograms.Day day, int index, WorkoutPrograms.Move move)
    {
        var box = new Border { Margin = new Thickness(0, 0, 0, 14), Padding = new Thickness(0, 8, 0, 8) };
        var stack = new StackPanel();
        var head = new DockPanel();
        var icon = new Border { Width = S.Icon * 2, Height = S.Icon * 2, ClipToBounds = true, Child = PhotoLayer(move.CatalogId, S.Icon * 2, AlignmentY.Center) };
        DockPanel.SetDock(icon, Dock.Left);
        var del = GhostLink("✕", () => { day.Moves.RemoveAt(index); Persist(); Render(); });
        DockPanel.SetDock(del, Dock.Right);
        head.Children.Add(icon);
        head.Children.Add(del);
        var title = Txt(move.Name, S.Body, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(10, 0, 8, 0);
        head.Children.Add(title);
        stack.Children.Add(head);

        for (int s = 0; s < move.Reps.Length; s++)
        {
            int si = s;
            var line = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            line.Children.Add(Txt($"Serie {si + 1}", S.Small, FontWeights.Normal, Dim));
            var plus = IconBtn("\uE710", () => { move.Reps[si] = Math.Min(30, move.Reps[si] + 1); Persist(); Render(); }, tiny: true);
            var minus = IconBtn("\uE738", () => { move.Reps[si] = Math.Max(1, move.Reps[si] - 1); Persist(); Render(); }, tiny: true);
            DockPanel.SetDock(plus, Dock.Right);
            var reps = Txt(move.Reps[si].ToString(), S.Body, FontWeights.SemiBold);
            reps.Margin = new Thickness(10, 6, 10, 0);
            DockPanel.SetDock(minus, Dock.Right);
            line.Children.Add(plus);
            line.Children.Add(reps);
            line.Children.Add(minus);
            stack.Children.Add(line);
        }
        var kgRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var kgBtn = new Button
        {
            Content = WeightLabel(move.Kg),
            FontSize = S.Small,
            Padding = new Thickness(12, 6, 12, 6),
            Background = Brushes.Transparent,
            Foreground = White,
            BorderBrush = White,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
        kgBtn.Click += (_, _) =>
        {
            move.Kg = move.Kg >= 20 ? 4 : move.Kg + 1;
            Persist(); Render();
        };
        kgRow.Children.Add(kgBtn);
        stack.Children.Add(kgRow);
        stack.Children.Add(GhostLink("AGREGAR SERIE", () =>
        {
            move.Reps = [.. move.Reps, move.Reps.Length > 0 ? move.Reps[^1] : 12];
            Persist(); Render();
        }));
        box.Child = stack;
        return box;
    }

    private FrameworkElement BuildPicker()
    {
        var root = new DockPanel { Background = Black };
        var header = BuildHeader("Añadir ejercicio", null, true, gear: false);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var list = new StackPanel();
        foreach (var ex in WorkoutCatalog.Exercises)
        {
            var pick = ex;
            var row = MoveRow(new WorkoutPrograms.Move
            {
                Name = WorkoutPrograms.Spanish(pick.Id, pick.Name),
                CatalogId = pick.Id,
                Reps = [12],
                Kg = 6
            }, 6);
            row.Cursor = Cursors.Hand;
            row.MouseLeftButtonUp += (_, e) =>
            {
                var program = Resolve();
                if (program is null) return;
                var day = program.Days[Math.Clamp(data.Day, 0, program.Days.Count - 1)];
                day.Moves.Add(new WorkoutPrograms.Move
                {
                    Name = WorkoutPrograms.Spanish(pick.Id, pick.Name),
                    CatalogId = pick.Id,
                    Reps = [12],
                    Kg = 6
                });
                if (string.IsNullOrWhiteSpace(program.CoverId) || program.CoverId == "0294")
                    program.CoverId = pick.Id;
                data.View = "edit";
                Persist();
                Render();
                e.Handled = true;
            };
            list.Children.Add(row);
        }
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return root;
    }

    private FrameworkElement BuildDone()
    {
        var program = Resolve();
        var last = data.Log.LastOrDefault();
        var root = new DockPanel { Background = Black };
        var header = BuildHeader("Mancuernas", null, true, gear: false);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var cover = PhotoLayer(program?.CoverId ?? "0300", Math.Max(220, S.H * 0.55), AlignmentY.Center);
        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        col.Children.Add(Txt("Entrenamiento completado", S.Title, FontWeights.Normal));
        if (last is not null)
        {
            col.Children.Add(Txt(last.When.ToString("d 'de' MMMM", new CultureInfo("es-ES")), S.Small, FontWeights.Normal, Dim, new Thickness(0, 16, 0, 0)));
            col.Children.Add(Txt(last.When.ToString("dddd", new CultureInfo("es-ES")), S.Small, FontWeights.Normal, Dim));
            col.Children.Add(Txt(last.Program, S.Title, FontWeights.SemiBold, White, new Thickness(0, 18, 0, 10)));
            col.Children.Add(Txt($"\uE805  {last.Kg:N0} kg", S.Body, FontWeights.Normal));
            col.Children.Add(Txt($"\uE9D9  {last.Cal} cal", S.Body, FontWeights.Normal));
            col.Children.Add(Txt($"\uE916  {Math.Max(1, last.Seconds / 60)} minutos", S.Body, FontWeights.Normal));
        }
        cover.Children.Add(col);
        root.Children.Add(cover);
        var check = new Button
        {
            Width = Math.Clamp(S.Icon * 2.6, 52, 64),
            Height = Math.Clamp(S.Icon * 2.6, 52, 64),
            Content = "\uE73E",
            FontFamily = Icons,
            FontSize = S.Icon,
            Background = White,
            Foreground = Black,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 24),
            Cursor = Cursors.Hand
        };
        check.Click += (_, _) => { data.View = "home"; Persist(); Render(); };
        DockPanel.SetDock(check, Dock.Bottom);
        root.Children.Add(check);
        return root;
    }

    // ---------------------------------------------------------------- player (unchanged flow, Resolve)

    private void BeginSession(WorkoutPrograms.Program program, WorkoutPrograms.Day day)
    {
        if (day.Moves.Count == 0)
        {
            owner.Status("Añade al menos un ejercicio.");
            return;
        }
        data.Move = 0; data.Set = 0; data.Resting = false; data.View = "player";
        sessionStart = DateTime.Now; sessionKg = 0;
        Persist(); Render(); _ = PushPlayerAsync();
    }

    private void HidePlayer()
    {
        playerHost.Visibility = Visibility.Collapsed;
        if (demo is not null) demo.Visibility = Visibility.Collapsed;
        if (nativeNotified) owner.NotifyNativeSurfaceChanged();
    }

    private async Task EnsurePlayerAsync()
    {
        try
        {
            sharedEnv ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(owner.Store.DirectoryPath, "browser-workout"));
            var env = await sharedEnv;
            demo ??= new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Black };
            if (playerHost.Child != demo) playerHost.Child = demo;
            demo.Visibility = Visibility.Visible;
            if (!nativeNotified) { nativeNotified = true; owner.NotifyNativeSurfaceChanged(); }
            await demo.EnsureCoreWebView2Async(env);
            var core = demo.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = true;
            if (!playerShellReady)
            {
                core.WebMessageReceived -= OnPlayerMessage;
                core.WebMessageReceived += OnPlayerMessage;
                core.NavigateToString(WorkoutPlayerHtml.Shell());
                playerShellReady = true;
            }
            else await PushPlayerAsync();
        }
        catch (Exception ex)
        {
            owner.Status(L.T("workout.videoFailDetail", ex.Message));
            data.View = "program"; Persist(); Render();
        }
    }

    private void OnPlayerMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw = e.TryGetWebMessageAsString();
        if (string.IsNullOrWhiteSpace(raw)) return;
        using var doc = JsonDocument.Parse(raw);
        string cmd = doc.RootElement.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
        Dispatcher.Invoke(() => HandlePlayer(cmd, doc.RootElement));
    }

    private void HandlePlayer(string cmd, JsonElement root)
    {
        if (cmd == "ready") { _ = PushPlayerAsync(); return; }
        if (cmd == "back") { data.View = "program"; data.Resting = false; Persist(); Render(); return; }
        if (cmd == "kg")
        {
            int delta = root.TryGetProperty("delta", out var d) ? d.GetInt32() : 0;
            NudgeKg(delta); _ = PushPlayerAsync(); return;
        }
        if (cmd is "skipRest" or "restDone")
        {
            if (cmd == "restDone") Ding();
            data.Resting = false; Persist(); _ = PushPlayerAsync(); return;
        }
        if (cmd == "doneSet") CompleteSet();
    }

    private void Ding()
    {
        if (data.Volume <= 0) return;
        try { SystemSounds.Asterisk.Play(); } catch { /* ignore */ }
    }

    private void CompleteSet()
    {
        var ctx = Live();
        if (ctx is null) return;
        var (program, day, move, kg) = ctx.Value;
        sessionKg += (int)Math.Round(2 * kg * move.Reps[data.Set]);
        bool lastSet = data.Set >= move.Reps.Length - 1;
        bool lastMove = data.Move >= day.Moves.Count - 1;
        data.RestSets = move.Reps.Length;
        data.RestDone = data.Set + 1;
        data.RestNext = lastSet ? (lastMove ? "Fin" : day.Moves[data.Move + 1].Name) : $"Serie {data.Set + 2} de {move.Reps.Length}";
        data.RestSeconds = lastSet ? program.RestMove : program.RestSet;
        if (lastSet && lastMove) { FinishSession(program, day); return; }
        if (lastSet) { data.Move++; data.Set = 0; data.Resting = true; }
        else { data.Set++; data.Resting = true; }
        Persist(); _ = PushPlayerAsync();
    }

    private void FinishSession(WorkoutPrograms.Program program, WorkoutPrograms.Day day)
    {
        int seconds = Math.Max(1, (int)(DateTime.Now - sessionStart).TotalSeconds);
        data.Log.Add(new LogRow
        {
            When = DateTime.Now, Program = program.Name, Day = day.Label,
            Kg = sessionKg, Cal = day.Calories, Seconds = seconds
        });
        if (data.Log.Count > 80) data.Log = data.Log.TakeLast(80).ToList();
        data.View = "done"; data.Resting = false; Persist(); Render();
    }

    private async Task PushPlayerAsync()
    {
        if (demo?.CoreWebView2 is null) return;
        var ctx = Live();
        if (ctx is null) return;
        var (program, day, move, kg) = ctx.Value;
        string video = WorkoutCatalog.Find(move.CatalogId)?.VideoUrl(false) ?? "";
        int sets = move.Reps.Length;
        string next = data.Resting ? (data.RestNext ?? "") : data.Set + 1 < sets ? $"Serie {data.Set + 2}" : data.Move + 1 < day.Moves.Count ? day.Moves[data.Move + 1].Name : "Fin";
        int dotsTotal = data.Resting ? Math.Max(1, data.RestSets) : sets;
        int dotsDone = data.Resting ? data.RestDone : data.Set;
        object payload = data.Resting
            ? new { mode = "rest", video, nav = "Entrenamiento", navs = program.Name, title = "Tiempo de descanso", sub = "", hint = "", kg = "", num = Math.Max(15, data.RestSeconds).ToString(), act = "Detener", next, sets = dotsTotal, done = dotsDone, rest = Math.Max(15, data.RestSeconds) }
            : new { mode = "set", video, nav = "Entrenamiento", navs = program.Name, title = move.Name, sub = $"Serie {data.Set + 1} de {sets}", hint = $"Haz {move.Reps[Math.Clamp(data.Set, 0, sets - 1)]} repeticiones", kg = WeightLabel(kg), num = move.Reps[Math.Clamp(data.Set, 0, sets - 1)].ToString(), act = "Listo", next, sets = dotsTotal, done = dotsDone, rest = 0 };
        demo.CoreWebView2.PostWebMessageAsJson(WorkoutPlayerHtml.MessageJson(payload));
        await Task.CompletedTask;
    }

    private (WorkoutPrograms.Program program, WorkoutPrograms.Day day, WorkoutPrograms.Move move, double kg)? Live()
    {
        var program = Resolve();
        if (program is null || program.Days.Count == 0) return null;
        var day = program.Days[Math.Clamp(data.Day, 0, program.Days.Count - 1)];
        if (day.Moves.Count == 0) return null;
        data.Move = Math.Clamp(data.Move, 0, day.Moves.Count - 1);
        var move = day.Moves[data.Move];
        if (move.Reps.Length == 0) return null;
        data.Set = Math.Clamp(data.Set, 0, move.Reps.Length - 1);
        return (program, day, move, KgOf(program.Id, data.Day, data.Move, move.Kg));
    }

    private void NudgeKg(int delta)
    {
        var ctx = Live();
        if (ctx is null) return;
        var (program, _, _, kg) = ctx.Value;
        kgOverride[Key(program.Id, data.Day, data.Move)] = Math.Clamp(kg + delta, 1, 80);
        Persist();
    }

    private double KgOf(string programId, int day, int move, double fallback) =>
        kgOverride.TryGetValue(Key(programId, day, move), out var kg) ? kg : fallback;
    private static string Key(string programId, int day, int move) => $"{programId}:{day}:{move}";

    private void DisposeDemo()
    {
        if (demo is not null)
        {
            try { if (demo.CoreWebView2 is not null) demo.CoreWebView2.WebMessageReceived -= OnPlayerMessage; demo.Dispose(); } catch { }
            demo = null;
        }
        playerShellReady = false;
        playerHost.Child = null;
        if (nativeNotified) { nativeNotified = false; owner.NotifyNativeSurfaceChanged(); }
    }

    // ---------------------------------------------------------------- chrome

    private FrameworkElement BuildHeader(string title, string? sub, bool back, bool gear = true, bool edit = false)
    {
        var bar = new Grid { MinHeight = S.Nav, Margin = new Thickness(6, 2, 6, 2) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(S.Nav * 0.7) });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(S.Nav * 0.7) });
        if (back)
        {
            var b = IconBtn("\uE76B", () =>
            {
                if (data.View == "home" && data.Tab == "set")
                {
                    data.Tab = "std";
                    Persist();
                    Render();
                    return;
                }
                data.View = data.View is "pick" or "edit" ? (data.View == "pick" ? "edit" : "program") : "home";
                Persist();
                Render();
            });
            bar.Children.Add(b);
        }
        var titles = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var t = Txt(title, S.Body, FontWeights.SemiBold);
        t.TextAlignment = TextAlignment.Center;
        titles.Children.Add(t);
        if (!string.IsNullOrWhiteSpace(sub))
        {
            var s = Txt(sub, S.Small, FontWeights.Normal, Dim);
            s.TextAlignment = TextAlignment.Center;
            s.HorizontalAlignment = HorizontalAlignment.Center;
            titles.Children.Add(s);
        }
        Grid.SetColumn(titles, 1);
        bar.Children.Add(titles);
        if (edit)
        {
            var e = IconBtn("\uE70F", () => { data.View = "edit"; Persist(); Render(); });
            Grid.SetColumn(e, 2);
            bar.Children.Add(e);
        }
        else if (gear)
        {
            var g = IconBtn("\uE713", () => { data.Tab = "set"; data.View = "home"; Persist(); Render(); });
            Grid.SetColumn(g, 2);
            bar.Children.Add(g);
        }
        return bar;
    }

    private UniformGrid BuildTabs()
    {
        var tabs = new UniformGrid { Columns = 5, Height = S.Tab };
        tabs.Children.Add(Tab("\uE805", "std"));
        tabs.Children.Add(Tab("\uE90F", "custom"));
        tabs.Children.Add(Tab("\uE8A5", "log"));
        tabs.Children.Add(Tab("\uE9D2", "me"));
        tabs.Children.Add(Tab("\uE713", "set"));
        return tabs;
    }

    private Button Tab(string glyph, string id)
    {
        bool on = data.Tab == id;
        var b = new Button
        {
            Content = glyph,
            FontFamily = Icons,
            FontSize = S.Icon,
            BorderThickness = new Thickness(0, 0, 0, on ? 2 : 0),
            BorderBrush = White,
            Background = Brushes.Transparent,
            Foreground = on ? White : Dim,
            Cursor = Cursors.Hand
        };
        b.Click += (_, _) => { data.Tab = id; data.View = "home"; Persist(); Render(); };
        return b;
    }

    private Button IconBtn(string glyph, Action click, Brush? bg = null, bool tiny = false)
    {
        double size = tiny ? S.Icon * 1.6 : S.Nav * 0.62;
        var b = new Button
        {
            Content = glyph,
            FontFamily = Icons,
            FontSize = tiny ? S.Small + 2 : S.Icon,
            Width = size,
            Height = size,
            Background = bg ?? Brush("#1A1A1A"),
            Foreground = bg is null ? White : White,
            BorderBrush = Brush("#444"),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Padding = new Thickness(0)
        };
        b.Click += (_, _) => click();
        return b;
    }

    private Button UnitChip(string label, bool on, Action click)
    {
        var b = new Button
        {
            Content = label,
            Background = on ? White : Brush("#222"),
            Foreground = on ? Black : White,
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.Bold,
            FontSize = S.Body,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 8, 0)
        };
        b.Click += (_, _) => click();
        return b;
    }

    private Button GhostLink(string text, Action click)
    {
        var b = new Button
        {
            Content = text,
            FontSize = S.Small,
            FontWeight = FontWeights.Bold,
            Foreground = Dim,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(0, 8, 0, 8)
        };
        b.Click += (_, _) => click();
        return b;
    }

    private Grid SettingLine(string label, string value, Action plus, Action minus)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10), MinHeight = S.Nav * 0.55 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lab = Txt(label, S.Body, FontWeights.Normal);
        lab.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(lab);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(IconBtn("\uE738", minus, tiny: true));
        var v = Txt(value, S.Body, FontWeights.SemiBold, White, new Thickness(10, 4, 10, 0));
        right.Children.Add(v);
        right.Children.Add(IconBtn("\uE710", plus, tiny: true));
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    private TextBlock Stat(string text)
    {
        var t = Txt(text, S.Small, FontWeights.SemiBold, Dim);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.FontFamily = new FontFamily("Segoe UI, Segoe MDL2 Assets");
        return t;
    }

    private Grid PhotoLayer(string coverId, double height, AlignmentY align)
    {
        var layer = new Grid { Height = height, ClipToBounds = true, Background = Brush("#161616") };
        var img = new Image
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = align == AlignmentY.Top ? VerticalAlignment.Top : VerticalAlignment.Center
        };
        BindThumb(img, coverId, Math.Clamp((int)(height * 2.4), 96, 720));
        layer.Children.Add(img);
        layer.Children.Add(new Border
        {
            Background = new LinearGradientBrush(Color.FromArgb(40, 0, 0, 0), Color.FromArgb(170, 0, 0, 0), 90),
            IsHitTestVisible = false
        });
        return layer;
    }

    private static readonly Dictionary<string, BitmapImage> ThumbCache = new(StringComparer.OrdinalIgnoreCase);

    private void BindThumb(Image img, string catalogId, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(catalogId)) return;
        if (ThumbCache.TryGetValue(catalogId, out var cached))
        {
            img.Source = cached;
            return;
        }
        string url = WorkoutCatalog.Find(catalogId)?.ThumbnailUrl(false) ?? "";
        if (url.Length == 0) return;
        string store = owner.Store.DirectoryPath;
        if (WorkoutVideoCache.IsCached(url, store))
        {
            var local = LoadPoster(WorkoutVideoCache.LocalPath(url, store), decodeWidth);
            if (local is not null)
            {
                ThumbCache[catalogId] = local;
                img.Source = local;
                return;
            }
        }
        _ = FillThumbAsync(img, catalogId, url, store, decodeWidth);
    }

    private async Task FillThumbAsync(Image img, string catalogId, string url, string store, int decodeWidth)
    {
        try
        {
            string path = await WorkoutVideoCache.EnsureLocalAsync(url, store, CancellationToken.None).ConfigureAwait(false);
            var bmp = LoadPoster(path, decodeWidth);
            if (bmp is null) return;
            await Dispatcher.InvokeAsync(() =>
            {
                ThumbCache[catalogId] = bmp;
                if (img.Parent is not null) img.Source = bmp;
                else
                {
                    posterRefresh.Stop();
                    posterRefresh.Start();
                }
            });
        }
        catch { /* poster optional */ }
    }

    private static BitmapImage? LoadPoster(string path, int decodeWidth)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length < 64) return null;
            var bmp = new BitmapImage();
            using var stream = File.OpenRead(path);
            bmp.BeginInit();
            bmp.StreamSource = stream;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private string CoverIdOf(string programName) =>
        WorkoutPrograms.All.Concat(data.Customs)
            .FirstOrDefault(p => p.Name.Equals(programName, StringComparison.OrdinalIgnoreCase))?.CoverId ?? "0300";

    private string WeightLabel(double kg) =>
        data.UseLb ? $"{Math.Round(kg * 2.20462):0} lb" : $"{kg:0} kg";

    private static TextBlock Txt(string text, double size, FontWeight weight, Brush? brush = null, Thickness? margin = null) =>
        new()
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            Foreground = brush ?? White,
            TextWrapping = TextWrapping.Wrap,
            Margin = margin ?? new Thickness(0)
        };

    private static SolidColorBrush Brush(string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex)!;
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
