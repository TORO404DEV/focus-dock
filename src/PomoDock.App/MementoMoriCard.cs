using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// Interactive life calendar built for dock widgets: stats and grid reflow by width/height
/// so a small card, a medium panel and a filled canvas all stay readable.
/// </summary>
internal static class MementoMoriCard
{
    private static readonly FontFamily Mono = new("Consolas");
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public const int WeeksPerYear = 52;

    public sealed class Data
    {
        public string? BirthDate { get; set; }
        public int ExpectancyYears { get; set; } = 80;
        /// <summary>Week indices (0 = birth week) the user has painted. Null = not seeded yet.</summary>
        public List<int>? Filled { get; set; }
    }

    public static int LayoutKey(double width, double height)
    {
        int heightTier = height < 100 ? 0 : height < 170 ? 1 : height < 260 ? 2 : height < 380 ? 3 : 4;
        int widthTier = width < 240 ? 0 : width < 340 ? 1 : width < 480 ? 2 : 3;
        int widthScale = Math.Clamp((int)Math.Floor(Math.Max(0, width) / 28), 0, 31);
        int heightScale = Math.Clamp((int)Math.Floor(Math.Max(0, height) / 28), 0, 31);
        return (heightTier << 15) | (widthTier << 10) | (widthScale << 5) | heightScale;
    }

    public static Data Read(WidgetConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Value)) return new Data();
        try
        {
            var data = JsonSerializer.Deserialize<Data>(config.Value, Json) ?? new Data();
            data.ExpectancyYears = Math.Clamp(data.ExpectancyYears <= 0 ? 80 : data.ExpectancyYears, 40, 100);
            return data;
        }
        catch { return new Data(); }
    }

    public static void Write(WidgetConfig config, Data data)
    {
        data.ExpectancyYears = Math.Clamp(data.ExpectancyYears <= 0 ? 80 : data.ExpectancyYears, 40, 100);
        if (data.Filled is { Count: > 0 })
            data.Filled = data.Filled.Distinct().OrderBy(x => x).ToList();
        config.Value = JsonSerializer.Serialize(data);
    }

    public static bool TryParseBirth(string? text, out DateOnly birth)
    {
        birth = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        string[] formats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy"];
        if (DateOnly.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out birth))
            return birth.Year >= 1900 && birth.Year <= DateTime.Now.Year;
        if (DateOnly.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out birth))
            return birth.Year >= 1900 && birth.Year <= DateTime.Now.Year;
        return false;
    }

    public static int WeeksLived(DateOnly birth, DateOnly today) =>
        Math.Max(0, (today.DayNumber - birth.DayNumber) / 7);

    public static HashSet<int> EnsureFilled(Data data, DateOnly birth, DateOnly today, int totalWeeks)
    {
        int lived = Math.Min(WeeksLived(birth, today), totalWeeks);
        if (data.Filled is null)
        {
            data.Filled = Enumerable.Range(0, lived).ToList();
            return data.Filled.ToHashSet();
        }
        return data.Filled.Where(i => i >= 0 && i < totalWeeks).ToHashSet();
    }

    public static UIElement Build(MainWindow owner, WidgetConfig config, double width, double height)
    {
        var data = Read(config);
        double inset = width < 280 || height < 140 ? 5 : width < 420 ? 8 : 11;
        var root = new Grid { Margin = new Thickness(inset, Math.Max(3, inset - 2), inset, inset) };

        if (!TryParseBirth(data.BirthDate, out var birth) || birth > DateOnly.FromDateTime(DateTime.Now))
        {
            root.Children.Add(Setup(width, height));
            return root;
        }

        int years = data.ExpectancyYears;
        int total = years * WeeksPerYear;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var filled = EnsureFilled(data, birth, today, total);
        if (string.IsNullOrWhiteSpace(config.Value) || Read(config).Filled is null)
        {
            data.Filled = filled.OrderBy(x => x).ToList();
            Write(config, data);
            owner.SaveState();
        }

        int livedCount = filled.Count;
        int left = Math.Max(0, total - livedCount);
        double livedRatio = Math.Clamp(livedCount / (double)Math.Max(1, total), 0, 1);
        int ageYears = AgeYears(birth, today);

        bool strip = height < 100 || width < 200;
        bool sideBySide = width >= 380 && height >= 150;
        bool showGrid = height >= 170;
        bool showFooter = height >= 280 && width >= 240;
        bool showAxis = width >= 280 && height >= 220;
        bool showTip = width >= 400 && height >= 300;

        double heroCap = Math.Max(18, Math.Min(44, Math.Min(width * .11, height * (showGrid ? .09 : .18))));
        double hero = Math.Clamp(heroCap, 18, 44);
        double label = Math.Clamp(hero * .36, 10, 12.5);
        double tileNum = Math.Clamp(hero * .72, 15, 26);

        if (strip)
        {
            root.Children.Add(Compact(left, livedRatio, livedCount, ageYears, width, height));
            return root;
        }

        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (showGrid) root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        if (showFooter) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = StatsHeader(left, livedRatio, livedCount, ageYears, hero, label, tileNum, sideBySide, showTip);
        head.Margin = new Thickness(0, 0, 0, showGrid ? 6 : 0);
        root.Children.Add(head);

        if (showGrid)
        {
            var board = new LifeCalendarBoard(owner, config, birth, years, filled, showAxis);
            Grid.SetRow(board, 1);
            root.Children.Add(board);
        }

        if (showFooter)
        {
            var foot = Text(L.T("memento.footer"), Math.Clamp(label, 10.5, 12.5), "Muted", FontWeights.SemiBold);
            foot.TextWrapping = TextWrapping.Wrap;
            foot.Margin = new Thickness(0, 8, 0, 0);
            foot.MaxHeight = Math.Max(28, height * .12);
            Grid.SetRow(foot, showGrid ? 2 : 1);
            root.Children.Add(foot);
        }
        return root;
    }

    /// <summary>Stats packed for widget chrome: Dock Right tiles, never a star-star gutter.</summary>
    private static FrameworkElement StatsHeader(int left, double livedRatio, int livedCount, int ageYears,
        double hero, double label, double tileNum, bool sideBySide, bool showTip)
    {
        var stack = new StackPanel();
        stack.Children.Add(Text(L.T("memento.latin"), Math.Clamp(label - 1, 9.5, 11.5), "Muted", FontWeights.Bold));

        if (sideBySide)
        {
            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 0, 0) };
            var tiles = MetricPair(livedCount, ageYears, tileNum, label);
            tiles.Width = Math.Clamp(hero * 7.2, 160, 240);
            tiles.Margin = new Thickness(10, 0, 0, 0);
            tiles.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(tiles, Dock.Right);
            row.Children.Add(tiles);
            row.Children.Add(PrimaryStat(left, livedRatio, hero, label, showTip));
            stack.Children.Add(row);
        }
        else
        {
            stack.Children.Add(PrimaryStat(left, livedRatio, hero, label, false));
            var tiles = MetricPair(livedCount, ageYears, Math.Min(tileNum, 20), label);
            tiles.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(tiles);
            if (showTip)
            {
                var tip = Text(L.T("memento.paintTip"), 10, "Muted");
                tip.Margin = new Thickness(0, 5, 0, 0);
                stack.Children.Add(tip);
            }
        }
        return stack;
    }

    private static UIElement PrimaryStat(int left, double livedRatio, double hero, double label, bool showTip)
    {
        var primary = new StackPanel();
        var weeks = Text(left.ToString("N0", CultureInfo.CurrentCulture), hero, "Ink", FontWeights.Black);
        weeks.ToolTip = L.T("memento.weeksLeftTip");
        primary.Children.Add(new Viewbox
        {
            Child = weeks,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxHeight = hero + 2,
            MaxWidth = 220
        });
        primary.Children.Add(Text(L.T("memento.weeksLeft"), label, "Muted", FontWeights.Bold));
        var percent = Text(L.T("memento.livedPercent", livedRatio * 100), Math.Clamp(label, 10.5, 12.5), "Ink", FontWeights.Bold);
        percent.Margin = new Thickness(0, 4, 0, 0);
        percent.TextWrapping = TextWrapping.Wrap;
        primary.Children.Add(percent);
        if (showTip)
        {
            var tip = Text(L.T("memento.paintTip"), 10, "Muted");
            tip.Margin = new Thickness(0, 4, 0, 0);
            primary.Children.Add(tip);
        }
        return primary;
    }

    private static FrameworkElement MetricPair(int livedCount, int ageYears, double numberSize, double labelSize)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var lived = MetaTile(livedCount.ToString("N0", CultureInfo.CurrentCulture), L.T("memento.weeksLived"), numberSize, labelSize);
        lived.Margin = new Thickness(0, 0, 4, 0);
        row.Children.Add(lived);
        var age = MetaTile(ageYears.ToString(CultureInfo.InvariantCulture), L.T("memento.ageYears"), numberSize, labelSize);
        age.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(age, 1);
        row.Children.Add(age);
        return row;
    }

    private static int AgeYears(DateOnly birth, DateOnly today)
    {
        int years = today.Year - birth.Year;
        if (today < birth.AddYears(years)) years--;
        return Math.Max(0, years);
    }

    public static void SyncToToday(MainWindow owner, WidgetConfig config)
    {
        var data = Read(config);
        if (!TryParseBirth(data.BirthDate, out var birth)) return;
        int total = data.ExpectancyYears * WeeksPerYear;
        int lived = Math.Min(WeeksLived(birth, DateOnly.FromDateTime(DateTime.Now)), total);
        data.Filled = Enumerable.Range(0, lived).ToList();
        Write(config, data);
        owner.SaveState();
    }

    private static UIElement Compact(int weeksLeft, double livedRatio, int livedCount, int ageYears, double width, double height)
    {
        double fontCap = Math.Max(12, Math.Min(26, height * .42));
        double font = Math.Clamp(Math.Min(width / 9, height * .42), Math.Min(15, fontCap), fontCap);
        if (width >= 300)
        {
            var line = new Grid { VerticalAlignment = VerticalAlignment.Center };
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.Children.Add(MiniMetric(weeksLeft.ToString("N0", CultureInfo.CurrentCulture), L.T("memento.weeksLeft"), font));
            var mid = MiniMetric(livedCount.ToString("N0", CultureInfo.CurrentCulture), L.T("memento.weeksLived"), font);
            mid.Margin = new Thickness(4, 0, 4, 0);
            Grid.SetColumn(mid, 1);
            line.Children.Add(mid);
            var age = MiniMetric(ageYears.ToString(CultureInfo.InvariantCulture), L.T("memento.ageYears"), font);
            Grid.SetColumn(age, 2);
            line.Children.Add(age);
            return line;
        }

        var strip = new Grid();
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        strip.ColumnDefinitions.Add(new ColumnDefinition());
        var mark = Text("†", Math.Clamp(font, 14, 18), "Ink", FontWeights.Bold);
        mark.VerticalAlignment = VerticalAlignment.Center;
        mark.Margin = new Thickness(0, 0, 8, 0);
        strip.Children.Add(mark);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(Text(L.T("memento.compact", weeksLeft), font, "Ink", FontWeights.Bold));
        words.Children.Add(Text(L.T("memento.livedPercent", livedRatio * 100), Math.Clamp(font * .55, 9, 11), "Muted", FontWeights.Bold));
        Grid.SetColumn(words, 1);
        strip.Children.Add(words);
        return strip;
    }

    private static Border MiniMetric(string value, string caption, double numberSize)
    {
        var tile = new Border { Padding = new Thickness(5, 3, 5, 3), BorderThickness = new Thickness(1) };
        tile.SetResourceReference(Border.BackgroundProperty, "Raised");
        tile.SetResourceReference(Border.BorderBrushProperty, "Edge");
        var stack = new StackPanel();
        stack.Children.Add(new Viewbox
        {
            Child = Text(value, numberSize, "Ink", FontWeights.Black),
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            MaxHeight = numberSize + 1,
            HorizontalAlignment = HorizontalAlignment.Left
        });
        stack.Children.Add(Text(caption, Math.Clamp(numberSize * .4, 8, 10), "Muted", FontWeights.Bold));
        tile.Child = stack;
        return tile;
    }

    private static UIElement Setup(double width, double height)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Text(L.T("memento.latin"), 11, "Muted", FontWeights.Bold));
        var title = Text(L.T("memento.setupTitle"), Math.Clamp(width * .06, 14, 22), "Ink", FontWeights.Black);
        title.Margin = new Thickness(0, 4, 0, 6);
        title.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(title);
        if (height >= 90)
        {
            var tip = Text(L.T("memento.setupTip"), Math.Clamp(width * .035, 10, 12), "Muted");
            tip.TextWrapping = TextWrapping.Wrap;
            stack.Children.Add(tip);
        }
        return stack;
    }

    private static Border MetaTile(string value, string label, double numberSize, double labelSize)
    {
        var tile = new Border
        {
            Padding = new Thickness(Math.Clamp(numberSize * .32, 5, 9), Math.Clamp(numberSize * .25, 4, 7),
                Math.Clamp(numberSize * .32, 5, 9), Math.Clamp(numberSize * .25, 4, 7)),
            BorderThickness = new Thickness(1.5)
        };
        tile.SetResourceReference(Border.BorderBrushProperty, "Edge");
        tile.SetResourceReference(Border.BackgroundProperty, "Raised");
        var stack = new StackPanel();
        stack.Children.Add(new Viewbox
        {
            Child = Text(value, numberSize, "Ink", FontWeights.Black),
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxHeight = numberSize + 2
        });
        var caption = Text(label, Math.Clamp(labelSize - 1, 9, 11), "Muted", FontWeights.Bold);
        caption.TextWrapping = TextWrapping.Wrap;
        caption.Margin = new Thickness(0, 2, 0, 0);
        stack.Children.Add(caption);
        tile.Child = stack;
        return tile;
    }

    private static TextBlock Text(string text, double size, string brush, FontWeight? weight = null)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            FontFamily = Mono,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }

    private sealed class LifeCalendarBoard : FrameworkElement
    {
        private readonly MainWindow owner;
        private readonly WidgetConfig config;
        private readonly DateOnly birth;
        private readonly int years;
        private readonly HashSet<int> filled;
        private readonly bool showAxis;
        private double cellW;
        private double cellH;
        private double pad;
        private double labelW;
        private double headerH;
        private int hover = -1;

        public LifeCalendarBoard(MainWindow owner, WidgetConfig config, DateOnly birth, int years, HashSet<int> filled, bool showAxis)
        {
            this.owner = owner;
            this.config = config;
            this.birth = birth;
            this.years = years;
            this.filled = filled;
            this.showAxis = showAxis;
            ClipToBounds = true;
            Focusable = false;
            Cursor = Cursors.Hand;
            ToolTip = L.T("memento.paintTip");
            SnapsToDevicePixels = true;
        }

        protected override Size MeasureOverride(Size available) =>
            new(double.IsInfinity(available.Width) ? 280 : Math.Max(40, available.Width),
                double.IsInfinity(available.Height) ? 200 : Math.Max(40, available.Height));

        protected override Size ArrangeOverride(Size final)
        {
            // Fill the whole board: independent cell width/height so wide widgets
            // never leave a dead column beside a height-limited square grid.
            labelW = showAxis ? (final.Width >= 360 ? 22 : 16) : 0;
            headerH = showAxis ? 12 : 0;
            double gridW = Math.Max(20, final.Width - labelW);
            double gridH = Math.Max(20, final.Height - headerH);
            cellW = Math.Max(1.2, gridW / WeeksPerYear);
            cellH = Math.Max(1.2, gridH / Math.Max(1, years));
            pad = Math.Clamp(Math.Min(cellW, cellH) * .18, 0.35, 1.2);
            return final;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var ink = Brush("Ink");
            var edge = Brush("Edge");
            var muted = Brush("Muted");
            var raised = Brush("Raised");
            var paper = Brush("Paper");
            var surface = Brush("Surface");
            int current = WeeksLived(birth, DateOnly.FromDateTime(DateTime.Now));
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var type = new Typeface(Mono, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

            // Background of the board so gaps between cells read as the chrome, not empty void.
            dc.DrawRectangle(surface, null, new Rect(labelW, headerH, cellW * WeeksPerYear, cellH * years));

            double axisSize = Math.Clamp(Math.Min(cellW, cellH) + 2, 6.5, 9);
            if (showAxis)
            {
                for (int w = 0; w < WeeksPerYear; w += 13)
                {
                    var ft = new FormattedText((w + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, type, axisSize, muted, dpi);
                    dc.DrawText(ft, new Point(labelW + w * cellW + 1, 0));
                }
            }

            for (int y = 0; y < years; y++)
            {
                double top = headerH + y * cellH;
                if (showAxis && (y % 5 == 0 || y == years - 1))
                {
                    var ft = new FormattedText(y.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, type, axisSize, muted, dpi);
                    dc.DrawText(ft, new Point(0, top + Math.Max(0, (cellH - ft.Height) / 2)));
                }
                for (int w = 0; w < WeeksPerYear; w++)
                {
                    int index = y * WeeksPerYear + w;
                    double x = labelW + w * cellW;
                    var rect = new Rect(x + pad * .5, top + pad * .5, Math.Max(0.8, cellW - pad), Math.Max(0.8, cellH - pad));
                    bool on = filled.Contains(index);
                    double stroke = index == hover ? 1.1 : Math.Min(cellW, cellH) < 4 ? 0.25 : 0.45;
                    dc.DrawRectangle(on ? ink : raised, new Pen(edge, stroke), rect);
                    if (index == current && !on)
                        dc.DrawRectangle(null, new Pen(ink, Math.Clamp(Math.Min(cellW, cellH) * .2, 0.8, 1.4)), rect);
                    else if (index == current && on && Math.Min(cellW, cellH) >= 3.5)
                        dc.DrawRectangle(null, new Pen(paper, 0.9),
                            new Rect(rect.X + 0.6, rect.Y + 0.6, Math.Max(0.6, rect.Width - 1.2), Math.Max(0.6, rect.Height - 1.2)));
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int next = Hit(e.GetPosition(this));
            if (next == hover) return;
            hover = next;
            InvalidateVisual();
            if (hover >= 0)
            {
                int age = hover / WeeksPerYear;
                int week = hover % WeeksPerYear + 1;
                ToolTip = L.T("memento.cellTip", age, week, filled.Contains(hover) ? L.T("memento.cellFilled") : L.T("memento.cellEmpty"));
            }
            else ToolTip = L.T("memento.paintTip");
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            hover = -1;
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            int index = Hit(e.GetPosition(this));
            if (index < 0) return;
            if (!filled.Add(index)) filled.Remove(index);
            var data = Read(config);
            data.Filled = filled.OrderBy(x => x).ToList();
            Write(config, data);
            owner.SaveState();
            Ancestors.Find<WidgetCard>(this)?.Refresh();
            e.Handled = true;
        }

        private int Hit(Point p)
        {
            if (cellW <= 0 || cellH <= 0 || p.X < labelW || p.Y < headerH) return -1;
            int w = (int)((p.X - labelW) / cellW);
            int y = (int)((p.Y - headerH) / cellH);
            if (w < 0 || w >= WeeksPerYear || y < 0 || y >= years) return -1;
            return y * WeeksPerYear + w;
        }

        private Brush Brush(string key) =>
            TryFindResource(key) as Brush ?? (key == "Ink" ? Brushes.Black : key == "Paper" ? Brushes.White : key == "Surface" ? Brushes.WhiteSmoke : Brushes.Gray);
    }
}
