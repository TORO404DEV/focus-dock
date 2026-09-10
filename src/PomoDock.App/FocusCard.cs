using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// The focus widget. Today and the current streak always get the largest, clearest type;
/// the goal and the week progressively join them when the card has room.
/// </summary>
internal static class FocusCard
{
    private static readonly string[] DayLetters = ["L", "M", "X", "J", "V", "S", "D"];

    /// <summary>Height controls which sections fit; width controls their density and scale.</summary>
    public static int LayoutKey(double width, double height)
    {
        int heightTier = height < 76 ? 0 : height < 218 ? 1 : height < 310 ? 2 : 3;
        int widthTier = width < 275 ? 0 : width < 390 ? 1 : width < 560 ? 2 : 3;
        // The small buckets let type grow while dragging without rebuilding on every pixel.
        int widthScale = Math.Clamp((int)Math.Floor(Math.Max(0, width) / 36), 0, 31);
        int heightScale = Math.Clamp((int)Math.Floor(Math.Max(0, height) / 42), 0, 31);
        return (heightTier << 15) | (widthTier << 10) | (widthScale << 5) | heightScale;
    }

    public static UIElement Build(MainWindow owner, double width, double height)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var daily = Reports.Daily(owner.Store.Sessions(), TimeZoneInfo.Local);
        double goal = Math.Max(1, owner.Settings.DailyGoalMinutes);
        double minutes = daily.GetValueOrDefault(today);
        int streak = Reports.Streak(daily, today);

        var week = HabitBook.WeekStart(today);
        var days = Enumerable.Range(0, 7).Select(index => week.AddDays(index)).ToArray();
        double weekTotal = days.Sum(day => daily.GetValueOrDefault(day));

        double inset = width < 300 || height < 120 ? 7 : width < 460 ? 10 : 14;
        var root = new Grid { Margin = new Thickness(inset, Math.Max(5, inset - 3), inset, inset) };
        if (height < 76)
        {
            root.Children.Add(Compact(minutes, streak, width, height));
            return root;
        }

        // A week needs real vertical room. Showing it in a short card used to squeeze the
        // primary numbers until both their values and captions were clipped.
        bool showWeek = height >= 218;
        bool showFooter = height >= 310;
        double metricSize = Math.Clamp(Math.Min(width * .105, height * (showWeek ? .19 : .26)), 27, 58);
        double labelSize = Math.Clamp(metricSize * .34, 10.5, 14);
        double iconSize = Math.Clamp(metricSize * .9, 26, 48);

        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (showWeek) root.RowDefinitions.Add(new RowDefinition());
        if (showFooter) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var headline = new Grid();
        headline.ColumnDefinitions.Add(new ColumnDefinition());
        headline.ColumnDefinitions.Add(new ColumnDefinition());
        var todayTile = MetricTile(Hours(minutes), "HORAS HOY", MetricIcon.Clock, metricSize, labelSize, iconSize, false);
        todayTile.Margin = new Thickness(0, 0, 4, 0);
        headline.Children.Add(todayTile);
        var streakTile = MetricTile(streak.ToString(CultureInfo.InvariantCulture), streak == 1 ? "DÍA DE RACHA" : "DÍAS DE RACHA", MetricIcon.Flame, metricSize, labelSize, iconSize, true);
        streakTile.Margin = new Thickness(4, 0, 0, 0);
        streakTile.ToolTip = streak == 0 ? "Enfócate hoy para encender tu racha" : $"{streak} días seguidos con al menos un minuto de enfoque";
        Grid.SetColumn(streakTile, 1);
        headline.Children.Add(streakTile);
        root.Children.Add(headline);

        var progress = new Grid { Margin = new Thickness(0, height < 120 ? 8 : 11, 0, 0) };
        progress.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        progress.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        progress.Children.Add(Meter(minutes / goal, height >= 205 ? 11 : 8));
        var caption = Text(minutes >= goal
            ? $"META CUMPLIDA  ·  {Hours(minutes - goal)} EXTRA"
            : $"FALTAN {Hours(goal - minutes)} PARA TU META DE {Hours(goal)}", Math.Clamp(labelSize - 1, 10, 12.5), "Muted", FontWeights.Bold);
        caption.Margin = new Thickness(1, 6, 0, 0);
        caption.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetRow(caption, 1);
        progress.Children.Add(caption);
        Grid.SetRow(progress, 1);
        root.Children.Add(progress);

        if (showWeek)
        {
            var chart = Week(daily, days, today, goal, Math.Clamp(labelSize - 1, 9.5, 12));
            Grid.SetRow(chart, 2);
            root.Children.Add(chart);
        }

        if (showFooter)
        {
            int counted = days.Count(day => day <= today && daily.GetValueOrDefault(day) > 0);
            var line = new Grid { Margin = new Thickness(0, 9, 0, 0) };
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var total = Text($"ESTA SEMANA  {Hours(weekTotal)}", Math.Clamp(labelSize - .5, 10, 12.5), "Muted", FontWeights.Bold);
            total.FontFamily = Mono;
            line.Children.Add(total);
            var average = Text(counted == 0 ? "SIN DÍAS AÚN" : $"MEDIA  {Hours(weekTotal / counted)}", Math.Clamp(labelSize - .5, 10, 12.5), "Muted");
            average.FontFamily = Mono;
            average.ToolTip = "Media de los días de esta semana con enfoque";
            Grid.SetColumn(average, 1);
            line.Children.Add(average);
            Grid.SetRow(line, 3);
            root.Children.Add(line);
        }
        return root;
    }

    /// <summary>Even at the minimum card height, both primary metrics keep an icon and readable type.</summary>
    private static UIElement Compact(double minutes, int streak, double width, double height)
    {
        double font = Math.Clamp(Math.Min(width / 8.4, height * .48), 21, 34);
        double icon = Math.Clamp(font * .8, 20, 28);
        var line = new Grid { VerticalAlignment = VerticalAlignment.Center };
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition());
        var today = MetricTile(Hours(minutes), "HOY", MetricIcon.Clock, font, 10.5, icon, false, true);
        today.Margin = new Thickness(0, 0, 3, 0);
        line.Children.Add(today);
        var run = MetricTile(streak.ToString(CultureInfo.InvariantCulture), "RACHA", MetricIcon.Flame, font, 10.5, icon, true, true);
        run.Margin = new Thickness(3, 0, 0, 0);
        Grid.SetColumn(run, 1);
        line.Children.Add(run);
        return line;
    }

    private static Border MetricTile(string value, string caption, MetricIcon icon, double numberSize, double labelSize, double iconSize, bool inverse, bool compact = false)
    {
        var tile = new Border
        {
            Padding = new Thickness(compact ? 6 : 9, compact ? 4 : 7, compact ? 6 : 9, compact ? 4 : 7),
            BorderThickness = new Thickness(1)
        };
        tile.SetResourceReference(Border.BackgroundProperty, inverse ? "Chrome" : "Raised");
        tile.SetResourceReference(Border.BorderBrushProperty, inverse ? "Chrome" : "Edge");

        string ink = inverse ? "ChromeInk" : "Ink";
        var row = new Grid();
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var badge = IconBadge(icon, iconSize, inverse);
        badge.Margin = new Thickness(0, 0, compact ? 6 : 9, 0);
        row.Children.Add(badge);

        // The number owns the remaining first row. Viewbox only scales down, so values such as
        // 10:25 remain whole at narrow widths instead of being clipped to "10:".
        var number = Text(value, numberSize, ink, FontWeights.Black);
        number.FontFamily = Mono;
        number.LineHeight = numberSize;
        var numberBox = new Viewbox
        {
            Child = number,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            MaxHeight = numberSize
        };
        Grid.SetColumn(numberBox, 1);
        row.Children.Add(numberBox);

        // Give the caption the full tile width rather than the small remainder beside the icon.
        var label = Text(caption, labelSize, inverse ? "ChromeInk" : "Muted", FontWeights.Bold);
        label.Opacity = inverse ? .78 : 1;
        label.Margin = new Thickness(1, compact ? 2 : 5, 0, 0);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var labelBox = new Viewbox
        {
            Child = label,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxHeight = labelSize + 2
        };
        Grid.SetRow(labelBox, 1);
        Grid.SetColumnSpan(labelBox, 2);
        row.Children.Add(labelBox);
        tile.Child = row;
        return tile;
    }

    private static Border IconBadge(MetricIcon icon, double size, bool inverse)
    {
        var badge = new Border { Width = size, Height = size, Padding = new Thickness(size * .19), BorderThickness = new Thickness(1) };
        badge.SetResourceReference(Border.BackgroundProperty, inverse ? "Surface" : "Accent");
        badge.SetResourceReference(Border.BorderBrushProperty, inverse ? "ChromeInk" : "Edge");
        string brush = inverse ? "Ink" : "AccentInk";
        badge.Child = icon == MetricIcon.Clock ? ClockIcon(brush) : FlameIcon(brush);
        return badge;
    }

    private static UIElement ClockIcon(string brush)
    {
        var icon = new Grid();
        var face = new Ellipse { StrokeThickness = 1.8 };
        face.SetResourceReference(Shape.StrokeProperty, brush);
        icon.Children.Add(face);
        var hour = new Border { Width = 1.8, Height = 5.2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4.2) };
        hour.SetResourceReference(Border.BackgroundProperty, brush);
        icon.Children.Add(hour);
        var minute = new Border { Width = 5.5, Height = 1.8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(4.5, 2.3, 0, 0), RenderTransform = new RotateTransform(24) };
        minute.SetResourceReference(Border.BackgroundProperty, brush);
        icon.Children.Add(minute);
        return icon;
    }

    private static UIElement FlameIcon(string brush)
    {
        var flame = new Path
        {
            Data = Geometry.Parse("M12,1 C13.2,5.2 18.5,7.1 18.5,13.1 C18.5,17.2 15.6,20.4 12,20.4 C8.1,20.4 5.4,17.4 5.4,13.6 C5.4,10.8 6.8,8.7 8.7,6.8 C8.7,9.8 10.1,11 11.1,11.7 C10.7,8.2 12.8,6.2 12,1 Z M12.1,12.1 C14.1,14 14.5,15.2 14.5,16.4 C14.5,18 13.4,19.1 12,19.1 C10.5,19.1 9.5,18 9.5,16.5 C9.5,15.3 10.2,14.2 12.1,12.1 Z"),
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.25,
            StrokeLineJoin = PenLineJoin.Round
        };
        flame.SetResourceReference(Shape.FillProperty, brush);
        flame.SetResourceReference(Shape.StrokeProperty, brush);
        return flame;
    }

    /// <summary>The calendar week, Monday first, with the days that reached the goal filled in.</summary>
    private static UIElement Week(Dictionary<DateOnly, double> daily, DateOnly[] days, DateOnly today, double goal, double labelSize)
    {
        double peak = Math.Max(goal, days.Select(day => daily.GetValueOrDefault(day)).DefaultIfEmpty(0).Max());
        var chart = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        chart.RowDefinitions.Add(new RowDefinition());
        chart.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < 7; i++) chart.ColumnDefinitions.Add(new ColumnDefinition());

        for (int i = 0; i < days.Length; i++)
        {
            var day = days[i];
            double value = daily.GetValueOrDefault(day);
            bool reached = value >= goal;

            var column = new Grid { Margin = new Thickness(3, 0, 3, 0), MinHeight = 30 };
            var track = new Border();
            track.SetResourceReference(Border.BackgroundProperty, "Raised");
            column.Children.Add(track);
            if (value > 0)
            {
                double share = Math.Min(1, value / peak);
                var proportion = new Grid();
                proportion.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Max(.0001, 1 - share), GridUnitType.Star) });
                proportion.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Max(.06, share), GridUnitType.Star) });
                var fill = new Border();
                fill.SetResourceReference(Border.BackgroundProperty, "Ink");
                if (!reached) fill.Opacity = .58;
                Grid.SetRow(fill, 1);
                proportion.Children.Add(fill);
                column.Children.Add(proportion);
            }
            if (day > today) column.Opacity = .35;
            column.ToolTip = day > today ? $"{DayLetters[i]} {day:dd/MM} · aún no llega" : $"{DayLetters[i]} {day:dd/MM} · {Hours(value)} de enfoque";
            Grid.SetColumn(column, i);
            chart.Children.Add(column);

            var label = new Border { Margin = new Thickness(3, 4, 3, 0), Padding = new Thickness(0, 2, 0, 2) };
            if (day == today) label.SetResourceReference(Border.BackgroundProperty, "Ink");
            var letter = Text(DayLetters[i], labelSize, day == today ? "Paper" : "Muted", FontWeights.Bold);
            letter.HorizontalAlignment = HorizontalAlignment.Center;
            label.Child = letter;
            Grid.SetRow(label, 1);
            Grid.SetColumn(label, i);
            chart.Children.Add(label);
        }
        return chart;
    }

    /// <summary>A proportional bar that keeps its ratio at any widget width.</summary>
    private static UIElement Meter(double share, double height)
    {
        share = Math.Clamp(share, 0, 1);
        var track = new Grid { Height = height };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.0001, share), GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.0001, 1 - share), GridUnitType.Star) });
        var back = new Border();
        back.SetResourceReference(Border.BackgroundProperty, "Raised");
        Grid.SetColumnSpan(back, 2);
        track.Children.Add(back);
        if (share > 0)
        {
            var fill = new Border();
            fill.SetResourceReference(Border.BackgroundProperty, "Ink");
            track.Children.Add(fill);
        }
        return track;
    }

    private enum MetricIcon { Clock, Flame }
    private static FontFamily Mono => new("Consolas");

    private static string Hours(double minutes)
    {
        int rounded = (int)Math.Round(Math.Max(0, minutes));
        return $"{rounded / 60}:{rounded % 60:00}";
    }

    private static TextBlock Text(string text, double size, string brush, FontWeight? weight = null)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (weight is not null) block.FontWeight = weight.Value;
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }
}
