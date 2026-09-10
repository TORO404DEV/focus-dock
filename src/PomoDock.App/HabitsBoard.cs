using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// The habit tracker. It reads and writes the shared habit book, so history survives the
/// widget, and it re-lays itself out for every size the card can be dragged to: from a
/// single strip of squares to a full week grid with level, streaks and awards.
/// </summary>
internal sealed class HabitsBoard : Grid, IReskinnable
{
    /// <summary>What fits at the current size. Two equal shapes always draw the same board.</summary>
    private sealed record Shape(bool Strip, bool Stats, bool Level, bool Nav, bool Week, bool Streak, bool Rate,
        bool Menu, bool Map, bool Composer, bool Footer, bool Hint, int Cell, int Row, double NameFont, double DayFont);

    private static readonly string[] DayLetters = ["L", "M", "X", "J", "V", "S", "D"];
    private static readonly string[] MonthNames = ["ENE", "FEB", "MAR", "ABR", "MAY", "JUN", "JUL", "AGO", "SEP", "OCT", "NOV", "DIC"];

    private readonly MainWindow owner;
    private readonly WidgetConfig config;
    private readonly HabitStore store;
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer settle = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private readonly DispatcherTimer applause = new() { Interval = TimeSpan.FromSeconds(8) };

    private DateOnly today = DateOnly.FromDateTime(DateTime.Now);
    private DateOnly week;
    private Shape? shape;
    private HabitAward? celebrating;
    private ScrollViewer? scroller;
    private Grid? columns;
    private TextBox? composer;
    private string draft = "";
    private bool composerFocused;
    private bool listening;

    public HabitsBoard(MainWindow owner, WidgetConfig config)
    {
        this.owner = owner;
        this.config = config;
        store = HabitStore.For(owner.Store);
        week = HabitBook.WeekStart(today);
        if (store.ImportLegacy(config)) owner.SaveState();

        settle.Tick += (_, _) => { settle.Stop(); if (Measure() != shape) Render(); };
        clock.Tick += (_, _) =>
        {
            var now = DateOnly.FromDateTime(DateTime.Now);
            if (now == today) return;
            bool following = week == HabitBook.WeekStart(today);
            today = now;
            if (following) week = HabitBook.WeekStart(today);
            Render();
        };
        applause.Tick += (_, _) => { applause.Stop(); celebrating = null; Render(); };
        SizeChanged += (_, _) => { settle.Stop(); settle.Start(); };
        Loaded += (_, _) =>
        {
            if (!listening) { store.Changed += Render; listening = true; }
            clock.Start();
            Render();
        };
        Unloaded += (_, _) =>
        {
            if (listening) { store.Changed -= Render; listening = false; }
            clock.Stop(); settle.Stop(); applause.Stop();
        };
        Render();
    }

    // ---------------------------------------------------------------- layout

    private Shape Measure()
    {
        double width = ActualWidth > 4 ? ActualWidth : Math.Max(150, config.Width - 22);
        double height = ActualHeight > 4 ? ActualHeight : Math.Max(60, config.Height - 54);

        bool strip = height < 120;
        bool streak = width >= 286;
        bool rate = width >= 470;
        bool menu = width >= 352;
        double reserved = (streak ? 34 : 0) + (rate ? 42 : 0) + (menu ? 26 : 0) + 12;
        double nameRoom = Math.Clamp(width * .28, 56, 150);
        int cell = (int)Math.Floor((width - reserved - nameRoom) / 7) - 2;
        bool grid = cell >= 13;
        cell = Math.Clamp(grid ? cell : 24, 13, width >= 460 ? 38 : 30);

        bool stats = height >= 186;
        return new Shape(
            Strip: strip,
            Stats: stats,
            Level: stats && height >= 224 && width >= 210,
            Nav: grid && height >= 260,
            Week: grid,
            Streak: streak,
            Rate: rate,
            Menu: menu,
            Map: grid && height >= 430,
            Composer: height >= 150,
            Footer: height >= 330,
            Hint: height >= 430,
            Cell: cell,
            Row: Math.Clamp(cell + 8, 22, 38),
            NameFont: Math.Clamp(cell * .4 + 3.4, 10, 13),
            DayFont: Math.Clamp(cell * .34 + 1, 7.5, 10.5));
    }

    /// <summary>Draws again after a theme change, for the colours it mixed itself.</summary>
    public void Reskin() => Render();

    private void Render()
    {
        var current = Measure();
        shape = current;
        Children.Clear();
        RowDefinitions.Clear();
        ColumnDefinitions.Clear();
        scroller = null;
        columns = null;
        composer = null;
        Margin = new Thickness(current.Strip ? 6 : 10, current.Strip ? 4 : 8, current.Strip ? 6 : 10, current.Strip ? 4 : 8);

        var book = store.Book;
        var progress = book.Habits.ToDictionary(habit => habit.Id, habit => HabitProgress.For(habit, today));
        var stats = HabitStats.Compute(book, progress, today);

        if (current.Strip) { RenderStrip(book, progress, stats, current); return; }

        if (celebrating is not null) Stack(Celebration(celebrating), GridLength.Auto);
        if (current.Stats) Stack(StatsPanel(stats, current), GridLength.Auto);
        if (current.Nav) Stack(WeekBar(current), GridLength.Auto);
        Stack(ColumnHeader(current), GridLength.Auto);
        Stack(HabitList(book, progress, current), new GridLength(1, GridUnitType.Star));
        if (current.Map && book.Active.Any()) Stack(ConsistencyMap(book, current), GridLength.Auto);
        if (current.Composer) Stack(Composer(current), GridLength.Auto);
        if (current.Footer && book.Habits.Count > 0) Stack(Footer(book, stats, current), GridLength.Auto);
    }

    private void Stack(UIElement element, GridLength height)
    {
        RowDefinitions.Add(new RowDefinition { Height = height });
        SetRow(element, RowDefinitions.Count - 1);
        Children.Add(element);
    }

    // ------------------------------------------------------------ tiny sizes

    /// <summary>At postage-stamp sizes the board becomes one row of squares: today, nothing else.</summary>
    private void RenderStrip(HabitBook book, Dictionary<Guid, HabitProgress> progress, HabitStats stats, Shape current)
    {
        double height = ActualHeight > 4 ? ActualHeight : config.Height - 54;
        bool single = height < 76;
        int size = (int)Math.Clamp(single ? height - 12 : height - 26, 14, 26);

        var strip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var habit in book.Active)
        {
            var value = progress[habit.Id];
            var chip = new Button
            {
                Width = size,
                Height = size,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 3, 0),
                BorderThickness = new Thickness(value.DueToday ? 1.4 : 1),
                FontSize = Math.Max(8, size * .46),
                FontWeight = FontWeights.Bold,
                Content = habit.Name.Length > 0 ? habit.Name[..1].ToUpper(CultureInfo.CurrentCulture) : "·",
                ToolTip = $"{habit.Name} · {(value.DoneToday ? "hecho hoy" : "pendiente")} · racha {value.Streak}"
            };
            chip.SetResourceReference(Control.BackgroundProperty, value.DoneToday ? "Ink" : "Surface");
            chip.SetResourceReference(Control.ForegroundProperty, value.DoneToday ? "Paper" : value.DueToday ? "Ink" : "Muted");
            chip.SetResourceReference(Control.BorderBrushProperty, value.DueToday ? "Line" : "Muted");
            System.Windows.Automation.AutomationProperties.SetName(chip, chip.ToolTip.ToString());
            chip.Click += (_, _) => Toggle(habit, today);
            strip.Children.Add(chip);
        }
        if (!book.Active.Any())
            strip.Children.Add(Text("SIN HÁBITOS", 9.5, "Muted"));

        var summary = Text($"{stats.DoneToday:00}/{stats.DueToday:00}", single ? 11 : 12.5, "Ink", FontWeights.Bold);
        summary.FontFamily = Mono;
        summary.Margin = new Thickness(0, 0, 8, 0);
        summary.ToolTip = $"Hoy · nivel {stats.Level} · racha {stats.Streak}";

        var scroll = new ScrollViewer { Content = strip, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };

        if (single)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition());
            RowDefinitions.Add(new RowDefinition());
            SetColumn(summary, 0); SetColumn(scroll, 1);
            Children.Add(summary); Children.Add(scroll);
            return;
        }

        var head = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };
        var flame = Text(stats.Streak > 0 ? $"RACHA {stats.Streak}" : "—", 9.5, "Muted");
        flame.FontFamily = Mono;
        DockPanel.SetDock(flame, Dock.Right);
        head.Children.Add(flame);
        head.Children.Add(summary);
        Stack(head, GridLength.Auto);
        Stack(scroll, new GridLength(1, GridUnitType.Star));
    }

    // -------------------------------------------------------------- sections

    private UIElement Celebration(HabitAward award)
    {
        var frame = new Border { Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand, ToolTip = award.Detail };
        frame.SetResourceReference(Border.BackgroundProperty, "Ink");
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        var mark = Text("◆", 11, "Paper", FontWeights.Bold);
        mark.Margin = new Thickness(0, 0, 7, 0);
        var name = Text("LOGRO · " + award.Name, 9.5, "Paper", FontWeights.Bold);
        line.Children.Add(mark);
        line.Children.Add(name);
        frame.Child = line;
        frame.MouseLeftButtonUp += (_, _) => { applause.Stop(); celebrating = null; Render(); };
        return frame;
    }

    private UIElement StatsPanel(HabitStats stats, Shape current)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var count = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        var big = Text($"{stats.DoneToday:00}/{stats.DueToday:00}", 19, "Ink", FontWeights.Black);
        big.FontFamily = Mono;
        count.Children.Add(big);
        var todayLabel = Text("HOY", 9, "Muted", FontWeights.Bold);
        todayLabel.Margin = new Thickness(7, 0, 0, 3);
        todayLabel.VerticalAlignment = VerticalAlignment.Bottom;
        count.Children.Add(todayLabel);
        head.Children.Add(count);

        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var level = Text($"NIVEL {stats.Level:00}", 11, "Ink", FontWeights.Black);
        level.HorizontalAlignment = HorizontalAlignment.Right;
        level.ToolTip = $"{stats.Xp} XP acumulados · {stats.ToNextLevel} XP para el nivel {stats.Level + 1}";
        right.Children.Add(level);
        var flame = Text(stats.Streak > 0 ? $"RACHA {stats.Streak} {(stats.Streak == 1 ? "DÍA" : "DÍAS")}" : "SIN RACHA", 9, "Muted", FontWeights.Bold);
        flame.FontFamily = Mono;
        flame.HorizontalAlignment = HorizontalAlignment.Right;
        flame.ToolTip = $"Días perfectos seguidos · mejor marca {stats.BestStreak}";
        right.Children.Add(flame);
        SetColumn(right, 1);
        head.Children.Add(right);
        panel.Children.Add(head);

        panel.Children.Add(TodayBar(stats));

        if (current.Level)
        {
            var xp = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            xp.ColumnDefinitions.Add(new ColumnDefinition());
            xp.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var bar = Meter(stats.LevelShare, 4);
            bar.VerticalAlignment = VerticalAlignment.Center;
            bar.Margin = new Thickness(0, 0, 8, 0);
            bar.ToolTip = $"{stats.LevelXp} / {stats.LevelSpan} XP del nivel {stats.Level}";
            xp.Children.Add(bar);
            var note = Text($"{stats.Xp} XP · {stats.ToNextLevel} AL {stats.Level + 1:00}", 8.5, "Muted");
            note.FontFamily = Mono;
            SetColumn(note, 1);
            xp.Children.Add(note);
            panel.Children.Add(xp);
        }
        return panel;
    }

    /// <summary>One block per habit due today: the day reads at a glance, even from across the room.</summary>
    private UIElement TodayBar(HabitStats stats)
    {
        int slots = Math.Max(stats.DueToday, stats.DoneToday);
        if (slots is 0 or > 20) return Meter(stats.TodayShare, 7, new Thickness(0, 8, 0, 0));
        var strip = new UniformGrid { Columns = slots, Height = 7, Margin = new Thickness(0, 8, 0, 0) };
        for (int i = 0; i < slots; i++)
        {
            var block = new Border { Margin = new Thickness(0, 0, i == slots - 1 ? 0 : 2, 0) };
            block.SetResourceReference(Border.BackgroundProperty, i < stats.DoneToday ? "Ink" : "Raised");
            strip.Children.Add(block);
        }
        strip.ToolTip = $"{stats.DoneToday} de {stats.DueToday} hábitos de hoy";
        return strip;
    }

    private UIElement WeekBar(Shape current)
    {
        var bar = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var back = Chip("‹", "Semana anterior");
        back.Click += (_, _) => { week = week.AddDays(-7); Render(); };
        bar.Children.Add(back);

        var end = week.AddDays(6);
        string range = week.Month == end.Month
            ? $"{week.Day:00} – {end.Day:00} {MonthNames[end.Month - 1]}"
            : $"{week.Day:00} {MonthNames[week.Month - 1]} – {end.Day:00} {MonthNames[end.Month - 1]}";
        var label = Text(range, 9.5, "Ink", FontWeights.Bold);
        label.FontFamily = Mono;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        SetColumn(label, 1);
        bar.Children.Add(label);

        var current7 = HabitBook.WeekStart(today);
        if (week != current7 && current.Streak)
        {
            var now = Chip("HOY", "Volver a la semana actual", 30);
            now.Click += (_, _) => { week = HabitBook.WeekStart(today); Render(); };
            SetColumn(now, 2);
            bar.Children.Add(now);
        }

        var next = Chip("›", "Semana siguiente");
        next.IsEnabled = week < current7;
        next.Click += (_, _) => { week = week.AddDays(7); Render(); };
        SetColumn(next, 3);
        bar.Children.Add(next);
        return bar;
    }

    private UIElement ColumnHeader(Shape current)
    {
        var header = TrackGrid(current);
        header.Margin = new Thickness(0, 0, 0, 3);

        var title = Text("HÁBITO", 8.5, "Muted", FontWeights.Bold);
        title.Margin = new Thickness(2, 0, 4, 0);
        header.Children.Add(title);

        var days = current.Week ? Enumerable.Range(0, 7).Select(index => week.AddDays(index)).ToArray() : new[] { today };
        for (int i = 0; i < days.Length; i++)
        {
            var day = days[i];
            bool isToday = day == today;
            var cell = new Border { Padding = new Thickness(0, 2, 0, 2), Margin = new Thickness(1, 0, 1, 0) };
            if (isToday) cell.SetResourceReference(Border.BackgroundProperty, "Ink");
            var box = new StackPanel();
            var letter = Text(current.Week ? DayLetters[i] : "HOY", current.Week ? current.DayFont : 7.5, isToday ? "Paper" : "Muted", FontWeights.Bold);
            letter.HorizontalAlignment = HorizontalAlignment.Center;
            box.Children.Add(letter);
            if (current.Week && current.Cell >= 20)
            {
                var number = Text(day.Day.ToString("00", CultureInfo.InvariantCulture), current.DayFont - .5, isToday ? "Paper" : "Muted");
                number.FontFamily = Mono;
                number.HorizontalAlignment = HorizontalAlignment.Center;
                box.Children.Add(number);
            }
            cell.Child = box;
            cell.ToolTip = day.ToString("dddd dd/MM", CultureInfo.CurrentCulture);
            SetColumn(cell, i + 1);
            header.Children.Add(cell);
        }

        if (current.Streak)
        {
            var streak = Text("RACHA", 8, "Muted", FontWeights.Bold);
            streak.HorizontalAlignment = HorizontalAlignment.Center;
            SetColumn(streak, TrackIndex(current, days.Length, "streak"));
            header.Children.Add(streak);
        }
        if (current.Rate)
        {
            var rate = Text("30 DÍAS", 8, "Muted", FontWeights.Bold);
            rate.HorizontalAlignment = HorizontalAlignment.Center;
            SetColumn(rate, TrackIndex(current, days.Length, "rate"));
            header.Children.Add(rate);
        }
        columns = header;
        return header;
    }

    private UIElement HabitList(HabitBook book, Dictionary<Guid, HabitProgress> progress, Shape current)
    {
        var list = new StackPanel();
        foreach (var habit in book.Active) list.Children.Add(HabitRow(habit, progress[habit.Id], current));
        if (!book.Active.Any()) list.Children.Add(EmptyState(book, current));
        var view = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false
        };
        // The fixed header has to slide left exactly as much as the scrollbar takes,
        // otherwise the weekday letters drift away from their column.
        view.ScrollChanged += (_, _) => SyncHeaderInset();
        view.Loaded += (_, _) => SyncHeaderInset();
        scroller = view;
        return view;
    }

    /// <summary>The first thing a new user sees, and what is left when everything is archived.</summary>
    private UIElement EmptyState(HabitBook book, Shape current)
    {
        var box = new StackPanel { Margin = new Thickness(2, current.Stats ? 26 : 12, 2, 10) };
        bool archived = book.Archived.Any();
        var title = Text(archived ? "TODO ARCHIVADO" : "EMPIEZA POR UNO", 13, "Ink", FontWeights.Black);
        box.Children.Add(title);
        var detail = Text(archived
            ? "Restaura un hábito desde el pie del widget, o escribe uno nuevo."
            : "Escribe abajo la práctica que quieres sostener y marca cada día que la cumplas.", 10.5, "Muted");
        detail.TextWrapping = TextWrapping.Wrap;
        detail.Margin = new Thickness(0, 6, 0, 0);
        box.Children.Add(detail);
        if (current.Stats && !archived)
        {
            var examples = Text("LEER 20 MINUTOS   ·   ESTIRAR   ·   SIN MÓVIL AL DESPERTAR", 8.5, "Muted", FontWeights.Bold);
            examples.TextWrapping = TextWrapping.Wrap;
            examples.Margin = new Thickness(0, 12, 0, 0);
            box.Children.Add(examples);
        }
        return box;
    }

    private void SyncHeaderInset()
    {
        if (scroller is null || columns is null) return;
        double inset = scroller.ComputedVerticalScrollBarVisibility == Visibility.Visible ? SystemParameters.VerticalScrollBarWidth : 0;
        var margin = columns.Margin;
        if (Math.Abs(margin.Right - inset) > .5) columns.Margin = new Thickness(margin.Left, margin.Top, inset, margin.Bottom);
    }

    private UIElement HabitRow(Habit habit, HabitProgress value, Shape current)
    {
        var frame = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent, MinHeight = current.Row };
        frame.SetResourceReference(Border.BorderBrushProperty, "Muted");

        var line = TrackGrid(current);
        line.Height = current.Row;

        // A grid, not a stack: only a bounded cell lets a long name end in an ellipsis.
        var name = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0) };
        name.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        name.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Text(habit.Name, current.NameFont, "Ink", FontWeights.SemiBold);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        name.Children.Add(label);
        if (current.Menu && CadenceBadge(habit) is { } badge)
        {
            var chip = Text(badge, Math.Max(7.5, current.NameFont - 3), "Muted");
            chip.FontFamily = Mono;
            chip.Margin = new Thickness(6, 1, 0, 0);
            chip.VerticalAlignment = VerticalAlignment.Center;
            SetColumn(chip, 1);
            name.Children.Add(chip);
        }
        name.Background = Brushes.Transparent;
        name.Cursor = Cursors.Hand;
        name.ToolTip = $"{habit.Name}\nRacha {value.Streak} · mejor {value.Best} · {value.Rate * 100:0}% en 30 días\nPulsa para ver el detalle";
        name.MouseLeftButtonUp += (_, _) => ShowDetail(habit);
        line.Children.Add(name);

        var days = current.Week ? Enumerable.Range(0, 7).Select(index => week.AddDays(index)).ToArray() : new[] { today };
        for (int i = 0; i < days.Length; i++)
        {
            var cell = DayCell(habit, days[i], current);
            SetColumn(cell, i + 1);
            line.Children.Add(cell);
        }

        if (current.Streak)
        {
            var streak = Text(value.StreakInWeeks ? $"{value.Streak}s" : $"{value.Streak}", Math.Max(9, current.NameFont - 1), value.Streak > 0 ? "Ink" : "Muted", FontWeights.Bold);
            streak.FontFamily = Mono;
            streak.HorizontalAlignment = HorizontalAlignment.Center;
            streak.VerticalAlignment = VerticalAlignment.Center;
            streak.ToolTip = value.StreakInWeeks
                ? $"{value.Streak} semanas seguidas cumpliendo {habit.TimesPerWeek}/semana · mejor {value.Best}"
                : $"Racha actual {value.Streak} · mejor marca {value.Best}";
            SetColumn(streak, TrackIndex(current, days.Length, "streak"));
            line.Children.Add(streak);
        }

        if (current.Rate)
        {
            var rate = Text($"{value.Rate * 100:0}%", Math.Max(9, current.NameFont - 2), value.Rate >= .8 ? "Ink" : "Muted");
            rate.FontFamily = Mono;
            rate.HorizontalAlignment = HorizontalAlignment.Center;
            rate.VerticalAlignment = VerticalAlignment.Center;
            rate.ToolTip = "Días cumplidos de los que tocaban en los últimos 30 días";
            SetColumn(rate, TrackIndex(current, days.Length, "rate"));
            line.Children.Add(rate);
        }

        if (current.Menu)
        {
            var menu = new Button
            {
                Content = "⋯",
                Width = 22,
                Height = Math.Min(24, current.Row - 4),
                Padding = new Thickness(0),
                Margin = new Thickness(2, 0, 0, 0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontSize = 13,
                ToolTip = "Opciones del hábito"
            };
            menu.Click += (_, _) => HabitMenu(habit);
            SetColumn(menu, TrackIndex(current, days.Length, "menu"));
            line.Children.Add(menu);
        }

        frame.Child = line;
        frame.MouseRightButtonUp += (_, e) => { HabitMenu(habit); e.Handled = true; };
        frame.MouseEnter += (_, _) => frame.SetResourceReference(Border.BackgroundProperty, "Paper");
        frame.MouseLeave += (_, _) => frame.Background = Brushes.Transparent;
        return frame;
    }

    private UIElement DayCell(Habit habit, DateOnly day, Shape current)
    {
        int count = habit.Count(day);
        bool done = count >= habit.Target;
        bool future = day > today;
        bool isToday = day == today;
        bool due = habit.IsDue(day);

        var button = new Button
        {
            Width = current.Cell,
            Height = current.Cell,
            Padding = new Thickness(0),
            Margin = new Thickness(1, 0, 1, 0),
            BorderThickness = new Thickness(isToday ? 2 : due ? 1.2 : 1),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !future
        };
        button.SetResourceReference(Control.BorderBrushProperty, due || done ? "Line" : "Muted");
        button.SetResourceReference(Control.BackgroundProperty, done ? "Ink" : isToday ? "Paper" : "Surface");
        if (future) button.Opacity = .3;
        else if (!due && !done) button.Opacity = .55;

        if (!done && count > 0) button.Content = Gauge(count, habit.Target, current.Cell - 6);
        else if (!done && !due && !future)
        {
            var free = Text("·", Math.Max(8, current.Cell * .5), "Muted");
            free.HorizontalAlignment = HorizontalAlignment.Center;
            free.VerticalAlignment = VerticalAlignment.Center;
            button.Content = free;
        }

        string state = future ? "aún no llega" : done ? "completado" : count > 0 ? $"{count}/{habit.Target}" : due ? "pendiente" : "día libre";
        button.ToolTip = $"{habit.Name} · {day:dd/MM} · {state}"
            + (future ? "" : habit.Target > 1 ? "\nClic para sumar una repetición · clic derecho para restarla" : "\nClic para marcar el día · clic derecho para deshacerlo");
        System.Windows.Automation.AutomationProperties.SetName(button, $"{habit.Name}, {day:dd/MM}, {state}");
        button.Click += (_, _) => Toggle(habit, day);
        button.MouseRightButtonUp += (_, e) => { if (!future) { habit.Advance(day, -1); Commit(); } e.Handled = true; };
        return button;
    }

    private UIElement Composer(Shape current)
    {
        double height = current.Row >= 30 ? 32 : 28;
        var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var field = new Grid();
        var input = new TextBox
        {
            Height = height,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(9, 0, 8, 0),
            MinWidth = 0,
            FontSize = 12,
            Text = draft,
            TextWrapping = TextWrapping.NoWrap,
            VerticalContentAlignment = VerticalAlignment.Center,
            MaxLength = 80
        };
        // A narrow, resizable widget can scroll the caret out of view while it is measured.
        ScrollViewer.SetHorizontalScrollBarVisibility(input, ScrollBarVisibility.Hidden);
        input.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Nuevo hábito");

        var hint = Text("Nuevo hábito…", 11.5, "Muted");
        hint.IsHitTestVisible = false;
        hint.Margin = new Thickness(11, 0, 8, 0);
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.Visibility = draft.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        input.TextChanged += (_, _) =>
        {
            draft = input.Text;
            hint.Visibility = draft.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        input.GotKeyboardFocus += (_, _) => composerFocused = true;
        input.LostKeyboardFocus += (_, _) => composerFocused = false;
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { AddHabit(); e.Handled = true; } };
        field.Children.Add(input);
        field.Children.Add(hint);
        row.Children.Add(field);

        var add = new Button
        {
            Content = current.Streak ? "+ HÁBITO" : "+",
            Height = height,
            Padding = new Thickness(current.Streak ? 10 : 9, 0, current.Streak ? 10 : 9, 0),
            Margin = new Thickness(0),
            FontSize = 11,
            ToolTip = "Añadir hábito"
        };
        add.Click += (_, _) => AddHabit();
        SetColumn(add, 1);
        row.Children.Add(add);

        composer = input;
        if (composerFocused) Dispatcher.BeginInvoke(new Action(() => { input.Focus(); input.CaretIndex = input.Text.Length; }), DispatcherPriority.Input);
        return row;
    }

    private UIElement Footer(HabitBook book, HabitStats stats, Shape current)
    {
        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

        var awards = new WrapPanel();
        foreach (var award in stats.Awards.TakeLast(current.Hint ? 6 : 3))
        {
            var chip = new Border { Padding = new Thickness(5, 2, 5, 2), Margin = new Thickness(0, 0, 4, 4), BorderThickness = new Thickness(1), ToolTip = award.Detail };
            chip.SetResourceReference(Border.BorderBrushProperty, "Muted");
            var text = Text("◆ " + award.Name, 8, "Muted", FontWeights.Bold);
            chip.Child = text;
            awards.Children.Add(chip);
        }
        if (stats.Awards.Count == 0)
        {
            var pending = Text("SIN LOGROS AÚN · Marca tu primer día para empezar.", 8.5, "Muted");
            awards.Children.Add(pending);
        }
        footer.Children.Add(awards);

        var line = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var summary = Text($"{stats.Total} DÍAS REGISTRADOS · {stats.Rate * 100:0}% EN 30 DÍAS", 8.5, "Muted");
        summary.FontFamily = Mono;
        summary.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(summary);
        int archived = book.Archived.Count();
        if (archived > 0)
        {
            var restore = Chip($"ARCHIVADOS {archived}", "Restaurar o borrar hábitos archivados", 84);
            restore.Click += (_, _) => ManageArchived();
            SetColumn(restore, 1);
            line.Children.Add(restore);
        }
        footer.Children.Add(line);

        if (current.Hint)
        {
            var hint = Text("Clic en un día para marcarlo · clic derecho para retroceder · clic en el nombre para ver su mapa.", 8.5, "Muted");
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 6, 0, 0);
            footer.Children.Add(hint);
        }
        return footer;
    }

    // ---------------------------------------------------------------- actions

    private void AddHabit()
    {
        var name = (composer?.Text ?? draft).Trim();
        if (name.Length == 0) { owner.Status("ESCRIBE UN HÁBITO ANTES DE AÑADIRLO."); return; }
        if (store.Book.Active.Any(habit => string.Equals(habit.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            owner.Status("YA SIGUES ESE HÁBITO.");
            return;
        }
        store.Add(name);
        draft = "";
        composerFocused = true;
        Commit();
        owner.Status($"HÁBITO AÑADIDO · {name.ToUpper(CultureInfo.CurrentCulture)}");
    }

    private void Toggle(Habit habit, DateOnly day)
    {
        if (day > today) return;
        int count = habit.Advance(day);
        owner.Sounds.Button(count >= habit.Target ? "start" : count == 0 ? "reset" : "skip");
        Commit();
    }

    /// <summary>Writes the book and unlocks whatever the change just earned.</summary>
    private void Commit()
    {
        var book = store.Book;
        var progress = book.Habits.ToDictionary(habit => habit.Id, habit => HabitProgress.For(habit, today));
        var stats = HabitStats.Compute(book, progress, today);
        var fresh = stats.Awards.FirstOrDefault(award => !book.Awards.Contains(award.Id));
        foreach (var award in stats.Awards.Where(award => !book.Awards.Contains(award.Id))) book.Awards.Add(award.Id);
        if (fresh is not null)
        {
            celebrating = fresh;
            applause.Stop();
            applause.Start();
            owner.Sounds.Button("phase");
            owner.Status($"LOGRO DESBLOQUEADO · {fresh.Name} — {fresh.Detail}");
        }
        store.Save();
    }

    private void HabitMenu(Habit habit)
    {
        var options = new[]
        {
            "Renombrar",
            "Frecuencia · " + CadenceLabel(habit),
            $"Meta diaria · {habit.Target} {(habit.Target == 1 ? "vez" : "veces")}",
            "Ver detalle y mapa",
            "Subir",
            "Bajar",
            "Archivar",
            "Eliminar"
        };
        switch (Dialogs.Choose(owner, "HÁBITO / " + habit.Name.ToUpper(CultureInfo.CurrentCulture), options))
        {
            case 0:
                var name = Dialogs.Prompt(owner, "RENOMBRAR HÁBITO", "Nombre", habit.Name);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var trimmed = name.Trim();
                    habit.Name = trimmed.Length > 80 ? trimmed[..80] : trimmed;
                    Commit();
                }
                break;
            case 1:
                if (EditCadence(habit)) Commit();
                break;
            case 2:
                var target = Dialogs.Prompt(owner, "META DIARIA", "Repeticiones que completan el día (1–20)", habit.Target.ToString(CultureInfo.InvariantCulture));
                if (int.TryParse(target, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                {
                    habit.Target = Math.Clamp(value, 1, 20);
                    Commit();
                }
                break;
            case 3: ShowDetail(habit); break;
            case 4: if (store.Book.Move(habit, -1)) Commit(); break;
            case 5: if (store.Book.Move(habit, 1)) Commit(); break;
            case 6:
                habit.Archived = true;
                Commit();
                owner.Status("HÁBITO ARCHIVADO · Su historial se conserva.");
                break;
            case 7: ConfirmDelete(habit); break;
        }
    }

    private void ConfirmDelete(Habit habit)
    {
        var choice = Dialogs.Choose(owner, "ELIMINAR HÁBITO", ["Archivar y conservar el historial", "Eliminar para siempre", "Cancelar"]);
        if (choice == 0) { habit.Archived = true; Commit(); owner.Status("HÁBITO ARCHIVADO · Su historial se conserva."); }
        else if (choice == 1) { store.Book.Habits.Remove(habit); Commit(); owner.Status("HÁBITO ELIMINADO."); }
    }

    private void ManageArchived()
    {
        var archived = store.Book.Archived.ToList();
        if (archived.Count == 0) return;
        var names = archived.Select(habit => habit.Name).Append("Cancelar").ToArray();
        int index = Dialogs.Choose(owner, "HÁBITOS ARCHIVADOS", names);
        if (index < 0 || index >= archived.Count) return;
        var target = archived[index];
        int action = Dialogs.Choose(owner, target.Name.ToUpper(CultureInfo.CurrentCulture), ["Restaurar", "Eliminar para siempre", "Cancelar"]);
        if (action == 0) { target.Archived = false; Commit(); owner.Status("HÁBITO RESTAURADO."); }
        else if (action == 1) { store.Book.Habits.Remove(target); Commit(); owner.Status("HÁBITO ELIMINADO."); }
    }

    private bool EditCadence(Habit habit)
    {
        var window = Dialogs.Window(owner, "FRECUENCIA", 520, 470);
        var stack = new StackPanel { Margin = new Thickness(24) };
        window.Content = stack;
        stack.Children.Add(Dialogs.Heading("FRECUENCIA"));
        stack.Children.Add(Text($"Cada cuánto cuenta «{habit.Name}». Las rachas solo juzgan los días que tocan.", 11.5, "Muted"));

        var cadence = habit.Cadence;
        var days = new HashSet<DayOfWeek>(habit.Days);
        int times = habit.TimesPerWeek;

        var modes = new StackPanel { Margin = new Thickness(0, 16, 0, 8) };
        var buttons = new List<(HabitCadence Mode, Button Button)>();
        foreach (var (mode, label) in new[] { (HabitCadence.Daily, "TODOS LOS DÍAS"), (HabitCadence.Selected, "DÍAS CONCRETOS"), (HabitCadence.Weekly, "VECES POR SEMANA") })
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 0, 6), HorizontalContentAlignment = HorizontalAlignment.Left };
            buttons.Add((mode, button));
            modes.Children.Add(button);
        }
        stack.Children.Add(modes);

        var picker = new UniformGrid { Columns = 7, Margin = new Thickness(0, 4, 0, 6) };
        var pickerButtons = new List<(DayOfWeek Day, Button Button)>();
        foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
        {
            var button = new Button { Content = DayLetters[((int)day + 6) % 7], Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(0, 8, 0, 8), FontSize = 12 };
            pickerButtons.Add((day, button));
            picker.Children.Add(button);
        }
        stack.Children.Add(picker);

        var counter = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
        var less = new Button { Content = "−", Width = 40, Padding = new Thickness(0, 6, 0, 6) };
        var more = new Button { Content = "+", Width = 40, Padding = new Thickness(0, 6, 0, 6) };
        var amount = Text("", 15, "Ink", FontWeights.Black);
        amount.FontFamily = Mono;
        amount.Margin = new Thickness(10, 0, 10, 0);
        amount.VerticalAlignment = VerticalAlignment.Center;
        counter.Children.Add(less);
        counter.Children.Add(amount);
        counter.Children.Add(more);
        stack.Children.Add(counter);

        var explain = Text("", 11, "Muted");
        explain.TextWrapping = TextWrapping.Wrap;
        explain.Margin = new Thickness(0, 2, 0, 14);
        stack.Children.Add(explain);

        void Sync()
        {
            foreach (var (mode, button) in buttons)
            {
                button.SetResourceReference(Control.BackgroundProperty, mode == cadence ? "Ink" : "Surface");
                button.SetResourceReference(Control.ForegroundProperty, mode == cadence ? "Paper" : "Ink");
            }
            picker.Visibility = cadence == HabitCadence.Selected ? Visibility.Visible : Visibility.Collapsed;
            counter.Visibility = cadence == HabitCadence.Weekly ? Visibility.Visible : Visibility.Collapsed;
            foreach (var (day, button) in pickerButtons)
            {
                button.SetResourceReference(Control.BackgroundProperty, days.Contains(day) ? "Ink" : "Surface");
                button.SetResourceReference(Control.ForegroundProperty, days.Contains(day) ? "Paper" : "Ink");
            }
            amount.Text = times.ToString(CultureInfo.InvariantCulture);
            explain.Text = cadence switch
            {
                HabitCadence.Selected => days.Count == 0 ? "Elige al menos un día." : "Los demás días quedan libres y no rompen la racha.",
                HabitCadence.Weekly => $"Cumple {times} {(times == 1 ? "día" : "días")} cualesquiera dentro de cada semana. La racha cuenta semanas.",
                _ => "Cada día cuenta y cada día suma a la racha."
            };
        }
        foreach (var (mode, button) in buttons) button.Click += (_, _) => { cadence = mode; Sync(); };
        foreach (var (day, button) in pickerButtons) button.Click += (_, _) => { if (!days.Add(day)) days.Remove(day); Sync(); };
        less.Click += (_, _) => { times = Math.Max(1, times - 1); Sync(); };
        more.Click += (_, _) => { times = Math.Min(7, times + 1); Sync(); };
        Sync();

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var save = Dialogs.Button("GUARDAR", () => window.DialogResult = true);
        save.IsDefault = true;
        actions.Children.Add(save);
        var cancel = Dialogs.Button("CANCELAR", () => window.DialogResult = false);
        cancel.IsCancel = true;
        actions.Children.Add(cancel);
        stack.Children.Add(actions);

        window.Loaded += (_, _) => Dialogs.Modalize(window);
        if (window.ShowDialog() != true) return false;
        if (cadence == HabitCadence.Selected && days.Count == 0) cadence = HabitCadence.Daily;
        habit.Cadence = cadence;
        habit.Days = days.ToList();
        habit.TimesPerWeek = times;
        return true;
    }

    private void ShowDetail(Habit habit)
    {
        var value = HabitProgress.For(habit, today);
        var window = Dialogs.Window(owner, "HÁBITO", 640, 560);
        var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
        window.Content = root;
        root.Children.Add(Dialogs.Heading(habit.Name.ToUpper(CultureInfo.CurrentCulture)));
        var cadence = Text(CadenceLabel(habit) + (habit.Target > 1 ? $" · {habit.Target} veces al día" : ""), 11.5, "Muted");
        cadence.Margin = new Thickness(0, -10, 0, 16);
        root.Children.Add(cadence);

        var tiles = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 18) };
        foreach (var (caption, text) in new[]
        {
            (value.StreakInWeeks ? "RACHA (SEM)" : "RACHA", value.Streak.ToString(CultureInfo.InvariantCulture)),
            ("MEJOR", value.Best.ToString(CultureInfo.InvariantCulture)),
            ("DÍAS", value.Total.ToString(CultureInfo.InvariantCulture)),
            ("30 DÍAS", $"{value.Rate * 100:0}%")
        })
        {
            var tile = new Border { BorderThickness = new Thickness(1.5), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 8, 0) };
            tile.SetResourceReference(Border.BorderBrushProperty, "Edge");
            var box = new StackPanel();
            var number = Text(text, 22, "Ink", FontWeights.Black);
            number.FontFamily = Mono;
            box.Children.Add(number);
            box.Children.Add(Text(caption, 8.5, "Muted", FontWeights.Bold));
            tile.Child = box;
            tiles.Children.Add(tile);
        }
        root.Children.Add(tiles);

        var map = new StackPanel();
        root.Children.Add(map);
        void DrawMap()
        {
            map.Children.Clear();
            map.Children.Add(Heatmap(habit, 18, () => { Commit(); DrawMap(); }));
        }
        DrawMap();

        var note = Text($"{value.Xp} XP generados por este hábito. Pulsa cualquier día del mapa para corregirlo.", 10.5, "Muted");
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, 16, 0, 14);
        root.Children.Add(note);
        root.Children.Add(Dialogs.Button("CERRAR", () => window.Close()));
        window.Loaded += (_, _) => Dialogs.Modalize(window);
        window.ShowDialog();
    }

    /// <summary>A calendar of the last weeks, Monday on top, so gaps and runs are obvious.</summary>
    private UIElement Heatmap(Habit habit, int weeks, Action onChange)
    {
        const int size = 15, gap = 3;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var start = HabitBook.WeekStart(today).AddDays(-7 * (weeks - 1));

        var months = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        int lastMonth = -1;
        for (int w = 0; w < weeks; w++)
        {
            var monday = start.AddDays(7 * w);
            bool show = monday.Month != lastMonth;
            lastMonth = monday.Month;
            var label = Text(show ? MonthNames[monday.Month - 1] : "", 7.5, "Muted", FontWeights.Bold);
            label.Width = size + gap;
            months.Children.Add(label);
        }
        SetColumn(months, 1);
        grid.Children.Add(months);

        var letters = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
        SetRow(letters, 1);
        foreach (var letter in DayLetters)
        {
            var text = Text(letter, 8, "Muted");
            text.Height = size + gap;
            text.TextAlignment = TextAlignment.Right;
            letters.Children.Add(text);
        }
        grid.Children.Add(letters);

        var body = new StackPanel { Orientation = Orientation.Horizontal };
        SetRow(body, 1);
        SetColumn(body, 1);
        for (int w = 0; w < weeks; w++)
        {
            var column = new StackPanel();
            for (int d = 0; d < 7; d++)
            {
                var day = start.AddDays(7 * w + d);
                bool future = day > today;
                bool done = habit.IsComplete(day);
                int count = habit.Count(day);
                var cell = new Button
                {
                    Width = size,
                    Height = size,
                    Margin = new Thickness(0, 0, gap, gap),
                    Padding = new Thickness(0),
                    BorderThickness = new Thickness(day == today ? 2 : 1),
                    IsEnabled = !future,
                    ToolTip = $"{day:dd/MM/yyyy} · {(future ? "aún no llega" : done ? "completado" : count > 0 ? $"{count}/{habit.Target}" : habit.IsDue(day) ? "sin marcar" : "día libre")}"
                };
                cell.SetResourceReference(Control.BackgroundProperty, done ? "Ink" : "Surface");
                cell.SetResourceReference(Control.BorderBrushProperty, habit.IsDue(day) ? "Line" : "Muted");
                if (!done && count > 0) cell.Content = Gauge(count, habit.Target, size - 5);
                if (future) cell.Opacity = .25;
                else if (!habit.IsDue(day) && !done) cell.Opacity = .5;
                cell.Click += (_, _) => { habit.SetCount(day, done ? 0 : habit.Target); onChange(); };
                column.Children.Add(cell);
            }
            body.Children.Add(column);
        }
        grid.Children.Add(body);

        var scroll = new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        return scroll;
    }

    /// <summary>Every week of the last months as one block: how much of each day was really done.</summary>
    private UIElement ConsistencyMap(HabitBook book, Shape current)
    {
        const int gap = 2;
        int size = Math.Clamp(current.Cell - 8, 8, 13);
        double available = (ActualWidth > 4 ? ActualWidth : config.Width - 22) - 24;
        int weeks = Math.Clamp((int)(available / (size + gap)), 6, 32);
        var habits = book.Active.ToList();
        var done = habits.ToDictionary(habit => habit.Id, habit => habit.CompletedDays());
        // Never draw weeks older than the habits themselves: empty boxes are not history.
        var first = HabitBook.WeekStart(habits.Select(habit => habit.Epoch()).DefaultIfEmpty(today).Min());
        int lived = (HabitBook.WeekStart(today).DayNumber - first.DayNumber) / 7 + 1;
        weeks = Math.Clamp(Math.Min(weeks, lived), 4, 32);
        var start = HabitBook.WeekStart(today).AddDays(-7 * (weeks - 1));

        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(Text($"CONSTANCIA · ÚLTIMAS {weeks} SEMANAS", 8, "Muted", FontWeights.Bold));
        var body = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        for (int w = 0; w < weeks; w++)
        {
            var column = new StackPanel();
            for (int d = 0; d < 7; d++)
            {
                var day = start.AddDays(7 * w + d);
                int due = 0, made = 0;
                foreach (var habit in habits)
                {
                    if (!habit.IsDue(day) || habit.Epoch() > day) continue;
                    due++;
                    if (done[habit.Id].Contains(day)) made++;
                }
                var cell = new Border
                {
                    Width = size,
                    Height = size,
                    Margin = new Thickness(0, 0, gap, gap),
                    BorderThickness = new Thickness(day == today ? 1.4 : .8)
                };
                cell.SetResourceReference(Border.BorderBrushProperty, day == today ? "Line" : "Muted");
                cell.SetResourceReference(Border.BackgroundProperty, due > 0 && made == due ? "Ink" : "Surface");
                if (day > today) cell.Opacity = .2;
                else
                {
                    if (due == 0) cell.Opacity = .35;
                    else if (made > 0 && made < due) cell.Child = Gauge(made, due, size - 3, .55);
                    cell.ToolTip = due == 0 ? $"{day:dd/MM} · nada que cumplir" : $"{day:dd/MM} · {made} de {due} hábitos";
                }
                column.Children.Add(cell);
            }
            body.Children.Add(column);
        }
        panel.Children.Add(body);
        return panel;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A day in progress fills from the bottom, like a glass being filled.</summary>
    private static UIElement Gauge(int count, int target, double size, double opacity = 1)
    {
        var gauge = new Grid { Width = Math.Max(2, size), Height = Math.Max(2, size) };
        var fill = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Height = Math.Max(2, Math.Max(2, size) * Math.Clamp(count / (double)Math.Max(1, target), 0, 1)),
            Opacity = opacity
        };
        fill.SetResourceReference(Border.BackgroundProperty, "Ink");
        gauge.Children.Add(fill);
        return gauge;
    }

    private static FontFamily Mono => new("Consolas");

    private Grid TrackGrid(Shape current)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        int columnCount = current.Week ? 7 : 1;
        for (int i = 0; i < columnCount; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(current.Cell + 2) });
        if (current.Streak) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        if (current.Rate) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        if (current.Menu) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        return grid;
    }

    /// <summary>Where each tracker column sits, so the header and the rows can never drift apart.</summary>
    private static int TrackIndex(Shape current, int days, string column)
    {
        int index = days + 1;
        if (column == "streak") return index;
        if (current.Streak) index++;
        if (column == "rate") return index;
        if (current.Rate) index++;
        return index;
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

    private static Button Chip(string content, string tooltip, double width = 24)
    {
        var button = new Button
        {
            Content = content,
            Width = width,
            Height = 22,
            Padding = new Thickness(0),
            Margin = new Thickness(3, 0, 0, 0),
            FontSize = 9.5,
            FontWeight = FontWeights.Bold,
            ToolTip = tooltip
        };
        return button;
    }

    /// <summary>A proportional bar that keeps its ratio at any widget width.</summary>
    private static Grid Meter(double share, double height, Thickness? margin = null)
    {
        share = Math.Clamp(share, 0, 1);
        var track = new Grid { Height = height, Margin = margin ?? new Thickness(0) };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.0001, share), GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(.0001, 1 - share), GridUnitType.Star) });
        var back = new Border();
        back.SetResourceReference(Border.BackgroundProperty, "Raised");
        SetColumnSpan(back, 2);
        track.Children.Add(back);
        var fill = new Border();
        fill.SetResourceReference(Border.BackgroundProperty, "Ink");
        if (share <= 0) fill.Visibility = Visibility.Collapsed;
        track.Children.Add(fill);
        return track;
    }

    private static string CadenceLabel(Habit habit) => habit.Cadence switch
    {
        HabitCadence.Selected => "Días concretos · " + string.Join(" ", habit.Days.Select(day => DayLetters[((int)day + 6) % 7])),
        HabitCadence.Weekly => $"{habit.TimesPerWeek} veces por semana",
        _ => "Todos los días"
    };

    private static string? CadenceBadge(Habit habit) => habit.Cadence switch
    {
        HabitCadence.Selected => string.Concat(habit.Days.Select(day => DayLetters[((int)day + 6) % 7])),
        HabitCadence.Weekly => $"{habit.TimesPerWeek}/SEM",
        _ => habit.Target > 1 ? $"×{habit.Target}" : null
    };
}
