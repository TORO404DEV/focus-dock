using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// The focus widget. Two numbers carry it — the hours done today and the streak of days
/// with focus — and everything else is context underneath them.
/// </summary>
internal static class FocusCard
{
    private static readonly string[] DayLetters = ["L", "M", "X", "J", "V", "S", "D"];

    /// <summary>Which shape a height resolves to, so the card only rebuilds when it really changes.</summary>
    public static int Tier(double height) => height < 92 ? 0 : height < 150 ? 1 : height < 205 ? 2 : 3;

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

        var root = new Grid { Margin = new Thickness(14, 10, 14, 12) };
        if (height < 92)
        {
            root.Children.Add(Compact(minutes, streak));
            return root;
        }

        bool bars = height >= 150;
        bool footer = height >= 205;
        double big = Math.Clamp(Math.Min(width / 7.5, height / 5.5), 20, 42);

        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (bars) root.RowDefinitions.Add(new RowDefinition());
        if (footer) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var headline = new Grid();
        headline.ColumnDefinitions.Add(new ColumnDefinition());
        headline.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headline.Children.Add(Figure(Hours(minutes), "HORAS HOY", big, HorizontalAlignment.Left));
        var run = Figure(streak.ToString(CultureInfo.InvariantCulture), streak == 1 ? "DÍA SEGUIDO" : "DÍAS SEGUIDOS", big, HorizontalAlignment.Right);
        run.ToolTip = streak == 0 ? "Enfócate hoy para empezar una racha" : $"Días seguidos con al menos un minuto de enfoque";
        Grid.SetColumn(run, 1);
        headline.Children.Add(run);
        root.Children.Add(headline);

        var progress = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        progress.Children.Add(Meter(minutes / goal, 8));
        var caption = Text(minutes >= goal
            ? $"META CUMPLIDA · {Hours(minutes - goal)} EXTRA"
            : $"FALTAN {Hours(goal - minutes)} PARA LA META DE {Hours(goal)}", 9, "Muted", FontWeights.Bold);
        caption.Margin = new Thickness(0, 6, 0, 0);
        caption.TextTrimming = TextTrimming.CharacterEllipsis;
        progress.Children.Add(caption);
        Grid.SetRow(progress, 1);
        root.Children.Add(progress);

        if (bars)
        {
            var chart = Week(daily, days, today, goal);
            Grid.SetRow(chart, 2);
            root.Children.Add(chart);
        }

        if (footer)
        {
            int counted = days.Count(day => day <= today && daily.GetValueOrDefault(day) > 0);
            var line = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var total = Text($"ESTA SEMANA {Hours(weekTotal)}", 9, "Muted", FontWeights.Bold);
            total.FontFamily = Mono;
            line.Children.Add(total);
            var average = Text(counted == 0 ? "SIN DÍAS AÚN" : $"MEDIA {Hours(weekTotal / counted)}", 9, "Muted");
            average.FontFamily = Mono;
            average.ToolTip = "Media de los días de esta semana con enfoque";
            Grid.SetColumn(average, 1);
            line.Children.Add(average);
            Grid.SetRow(line, bars ? 3 : 2);
            root.Children.Add(line);
        }
        return root;
    }

    /// <summary>At its smallest the widget keeps only what was asked for: today and the streak.</summary>
    private static UIElement Compact(double minutes, int streak)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var hours = Text(Hours(minutes), 17, "Ink", FontWeights.Black);
        hours.FontFamily = Mono;
        line.Children.Add(hours);
        var label = Text("HOY", 9, "Muted", FontWeights.Bold);
        label.Margin = new Thickness(6, 0, 14, 0);
        line.Children.Add(label);
        var run = Text(streak.ToString(CultureInfo.InvariantCulture), 17, "Ink", FontWeights.Black);
        run.FontFamily = Mono;
        line.Children.Add(run);
        var days = Text(streak == 1 ? "DÍA" : "DÍAS", 9, "Muted", FontWeights.Bold);
        days.Margin = new Thickness(6, 0, 0, 0);
        line.Children.Add(days);
        return line;
    }

    /// <summary>The calendar week, Monday first, with the days that reached the goal filled in.</summary>
    private static UIElement Week(Dictionary<DateOnly, double> daily, DateOnly[] days, DateOnly today, double goal)
    {
        double peak = Math.Max(goal, days.Select(day => daily.GetValueOrDefault(day)).DefaultIfEmpty(0).Max());
        var chart = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        chart.RowDefinitions.Add(new RowDefinition());
        chart.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < 7; i++) chart.ColumnDefinitions.Add(new ColumnDefinition());

        for (int i = 0; i < days.Length; i++)
        {
            var day = days[i];
            double value = daily.GetValueOrDefault(day);
            bool reached = value >= goal;

            var column = new Grid { Margin = new Thickness(4, 0, 4, 0), MinHeight = 24 };
            var track = new Border { VerticalAlignment = VerticalAlignment.Stretch };
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
                if (!reached) fill.Opacity = .55;
                Grid.SetRow(fill, 1);
                proportion.Children.Add(fill);
                column.Children.Add(proportion);
            }
            if (day > today) column.Opacity = .35;
            column.ToolTip = day > today ? $"{DayLetters[i]} {day:dd/MM} · aún no llega" : $"{DayLetters[i]} {day:dd/MM} · {Hours(value)}";
            Grid.SetColumn(column, i);
            chart.Children.Add(column);

            var label = new Border { Margin = new Thickness(4, 4, 4, 0), Padding = new Thickness(0, 1, 0, 1) };
            if (day == today) label.SetResourceReference(Border.BackgroundProperty, "Ink");
            var letter = Text(DayLetters[i], 8.5, day == today ? "Paper" : "Muted", FontWeights.Bold);
            letter.HorizontalAlignment = HorizontalAlignment.Center;
            label.Child = letter;
            Grid.SetRow(label, 1);
            Grid.SetColumn(label, i);
            chart.Children.Add(label);
        }
        return chart;
    }

    private static FrameworkElement Figure(string value, string caption, double size, HorizontalAlignment align)
    {
        var box = new StackPanel { HorizontalAlignment = align };
        var number = Text(value, size, "Ink", FontWeights.Black);
        number.FontFamily = Mono;
        number.HorizontalAlignment = align;
        box.Children.Add(number);
        var label = Text(caption, Math.Clamp(size * .26, 8, 10), "Muted", FontWeights.Bold);
        label.HorizontalAlignment = align;
        box.Children.Add(label);
        return box;
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

    private static FontFamily Mono => new("Consolas");

    /// <summary>Minutes read as hours: what the widget is asked to show first.</summary>
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
