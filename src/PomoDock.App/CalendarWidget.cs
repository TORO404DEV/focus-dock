using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// The calendar card: a month grid, an hour-by-hour week and a running agenda over the same
/// events. The events themselves live in the database, so two calendars on two pages always
/// show the same appointments and reminders keep ringing when the card is not on screen.
/// </summary>
internal sealed class CalendarWidget : Grid
{
    /// <summary>What this particular card remembers between sessions.</summary>
    private sealed class CardState
    {
        public string View { get; set; } = "month";
        public bool ShowDone { get; set; } = true;
    }

    // Base metrics at scale 1. Every size in the card is derived from them, so the whole
    // widget grows with the room it is given instead of staying small on a big monitor.
    private double HourHeight => S(42);
    private double Gutter => S(44);

    private readonly MainWindow owner;
    private readonly WidgetConfig config;
    private readonly AgendaStore agenda;
    private readonly CardState state;
    private readonly Grid surface = new();
    private readonly TextBlock periodLabel;
    private readonly TextBlock summaryLabel;
    private readonly StackPanel viewSwitch;
    private readonly TextBox quickAdd;
    private readonly TextBlock quickHint;
    private readonly DispatcherTimer minute = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Action agendaChanged;
    private readonly List<Button> chrome = [];
    private readonly Button addButton;
    private readonly Button detailedButton;
    private readonly Grid quickField;

    private DateOnly cursor = DateOnly.FromDateTime(DateTime.Now);
    private DateOnly selected = DateOnly.FromDateTime(DateTime.Now);
    private DateOnly rendered;
    private string filter = "";
    private Border? nowLine;
    private Canvas? nowColumn;
    private double scale = 1;
    private double renderedHeight;

    public CalendarWidget(MainWindow owner, WidgetConfig config)
    {
        this.owner = owner;
        this.config = config;
        agenda = AgendaStore.For(owner.Store);
        state = ReadState(config.Value);

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        Margin = new Thickness(4, 4, 4, 2);

        // Toolbar: where we are, and how we want to look at it.
        var toolbar = new Grid { Margin = new Thickness(0, 2, 0, 6) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nav = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        chrome.Add(Small("‹", "Periodo anterior", () => Move(-1)));
        chrome.Add(Small("›", "Periodo siguiente", () => Move(1)));
        chrome.Add(Small("HOY", "Volver a hoy", GoToday));
        foreach (var button in chrome) nav.Children.Add(button);
        toolbar.Children.Add(nav);

        periodLabel = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"), FontSize = 12, FontWeight = FontWeights.Black,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0),
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(periodLabel, 1);
        toolbar.Children.Add(periodLabel);

        viewSwitch = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(viewSwitch, 2);
        toolbar.Children.Add(viewSwitch);
        Children.Add(toolbar);

        summaryLabel = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"), FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = AgendaVisuals.Resource("Muted"), Margin = new Thickness(0, 0, 0, 6),
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(summaryLabel, 1);
        Children.Add(summaryLabel);

        // Quick add: one line of Spanish becomes an event.
        var addRow = new Grid { Margin = new Thickness(0, 0, 0, S(6)) };
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        quickAdd = new TextBox
        {
            Height = 32, Margin = new Thickness(0), Padding = new Thickness(9, 0, 9, 0), MinWidth = 0,
            FontSize = 12, TextWrapping = TextWrapping.NoWrap,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Escribe en tu idioma: Dentista mañana a las 17:30 durante 45m"
        };
        quickAdd.SetValue(AutomationProperties.NameProperty, "Añadir evento rápido");
        ScrollViewer.SetHorizontalScrollBarVisibility(quickAdd, ScrollBarVisibility.Hidden);
        quickAdd.TextChanged += (_, _) => UpdateHint();
        quickAdd.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        quickField = AgendaVisuals.WithHint(quickAdd, "Dentista mañana a las 17:30 durante 45m");
        quickField.Margin = new Thickness(0, 0, 6, 0);
        var add = addButton = new Button { Content = "+", Padding = new Thickness(0), ToolTip = "Añadir el evento escrito" };
        add.Click += (_, _) => Commit();
        var detailed = detailedButton = new Button { Content = "⋯", Padding = new Thickness(0), Margin = new Thickness(0), ToolTip = "Abrir el formulario completo" };
        detailed.Click += (_, _) => Edit(null, selected, quickAdd.Text.Trim());
        Grid.SetColumn(add, 1); Grid.SetColumn(detailed, 2);
        addRow.Children.Add(quickField); addRow.Children.Add(add); addRow.Children.Add(detailed);
        Grid.SetRow(addRow, 2);
        Children.Add(addRow);


        quickHint = new TextBlock { FontSize = 9, Foreground = AgendaVisuals.Resource("Muted"), Margin = new Thickness(2, 0, 0, 5), TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed };

        var lower = new Grid();
        lower.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        lower.RowDefinitions.Add(new RowDefinition());
        lower.Children.Add(quickHint);
        Grid.SetRow(surface, 1);
        lower.Children.Add(surface);
        Grid.SetRow(lower, 3);
        Children.Add(lower);

        agendaChanged = Render;
        minute.Tick += (_, _) => Tick();
        // Switching workspace page unloads and reloads the same card, so the wiring is symmetric.
        Loaded += (_, _) =>
        {
            agenda.Changed -= agendaChanged;
            agenda.Changed += agendaChanged;
            minute.Start();
            Render();
        };
        Unloaded += (_, _) => { agenda.Changed -= agendaChanged; minute.Stop(); };
        SizeChanged += (_, _) => Rescale();
        Render();
    }

    // ---------------------------------------------------------------- scale

    /// <summary>Every size in the card passes through here before it reaches a control.</summary>
    private double S(double value) => Math.Round(value * scale, 1);

    /// <summary>
    /// Chrome grows more slowly than content. Buttons that doubled in size would eat the width
    /// the month name needs, and a toolbar is there to be reached, not to be read from afar.
    /// </summary>
    private double C(double value) => Math.Round(value * Math.Min(scale, 1.7), 1);

    /// <summary>
    /// Recomputes the scale from the room the card was given. A widget dragged to fill a tall
    /// monitor deserves type to match, so the layout keeps its proportions and the text grows.
    /// </summary>
    private void Rescale()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        double next = Math.Clamp(Math.Min(ActualWidth / 400, ActualHeight / 520), 1, 2.4);
        // Height alone changes how much the day panel and the month grid may take, so a card
        // that only grew taller still deserves a rebuild even when the scale itself is capped.
        bool reshaped = renderedHeight <= 0 || Math.Abs(ActualHeight - renderedHeight) > renderedHeight * 0.12;
        // A small threshold keeps a slow drag from rebuilding the card on every pixel.
        if (Math.Abs(next - scale) < 0.05 && !reshaped) return;
        scale = next;
        Render();
    }

    /// <summary>The pieces that live outside a rebuild still have to follow the scale.</summary>
    private void ScaleChrome()
    {
        periodLabel.FontSize = C(12);
        periodLabel.Margin = new Thickness(C(10), 0, C(8), 0);
        summaryLabel.FontSize = C(9);
        summaryLabel.Margin = new Thickness(0, 0, 0, C(6));
        quickHint.FontSize = C(9);
        quickAdd.FontSize = C(12);
        quickAdd.Height = C(32);
        quickAdd.Padding = new Thickness(C(9), 0, C(9), 0);
        if (quickField.Children.Count > 1 && quickField.Children[1] is TextBlock placeholder)
            placeholder.Margin = new Thickness(quickAdd.Padding.Left + C(3), 0, C(8), 0);
        foreach (var button in chrome)
        {
            button.FontSize = C(10);
            button.Padding = new Thickness(C(8), C(5), C(8), C(5));
            button.MinWidth = C(28);
            button.Margin = new Thickness(0, 0, C(4), 0);
        }
        addButton.FontSize = C(16); addButton.Width = C(34); addButton.Height = C(32); addButton.Margin = new Thickness(0, 0, C(5), 0);
        detailedButton.FontSize = C(14); detailedButton.Width = C(34); detailedButton.Height = C(32);
    }

    // ---------------------------------------------------------------- state

    private static CardState ReadState(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return new CardState();
        try { return JsonSerializer.Deserialize<CardState>(payload) ?? new CardState(); }
        catch (JsonException) { return new CardState(); }
    }

    private void SaveState()
    {
        config.Value = JsonSerializer.Serialize(state);
        owner.SaveState();
    }

    private void Move(int direction)
    {
        cursor = state.View switch
        {
            "week" => cursor.AddDays(7 * direction),
            "agenda" => cursor.AddDays(14 * direction),
            _ => cursor.AddMonths(direction)
        };
        if (state.View != "month") selected = cursor;
        Render();
    }

    private void GoToday()
    {
        cursor = selected = DateOnly.FromDateTime(DateTime.Now);
        Render();
    }

    private void SetView(string view)
    {
        state.View = view;
        cursor = selected;
        SaveState();
        Render();
    }

    /// <summary>Keeps the clock honest without redrawing under the user's hands every half minute.</summary>
    private void Tick()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today != rendered) { Render(); return; }
        if (state.View == "week" && nowLine is not null && nowColumn is not null)
            Canvas.SetTop(nowLine, DateTime.Now.TimeOfDay.TotalMinutes / 60 * HourHeight);
        summaryLabel.Text = Summary(DateTime.Now);
    }

    // ---------------------------------------------------------------- quick add

    private void UpdateHint()
    {
        string text = quickAdd.Text.Trim();
        if (text.Length == 0) { quickHint.Visibility = Visibility.Collapsed; return; }
        quickHint.Visibility = Visibility.Visible;
        var draft = AgendaQuickAdd.Parse(text, DateTime.Now);
        if (draft is null) { quickHint.Text = "Escribe también un nombre para el evento."; return; }
        var day = DateOnly.FromDateTime(draft.Start);
        string when = draft.AllDay
            ? $"{AgendaVisuals.DayLabel(day)} · todo el día"
            : $"{AgendaVisuals.DayLabel(day)} · {draft.Start:HH:mm} – {draft.EndOn(day):HH:mm}";
        string repeat = draft.Repeat == RepeatKind.None ? "" : " · " + draft.RepeatLabel().ToLower(AgendaVisuals.Spanish);
        quickHint.Text = $"↵  {draft.Title}   ·   {when}{repeat}";
    }

    private void Commit()
    {
        string text = quickAdd.Text.Trim();
        if (text.Length == 0) return;
        var draft = AgendaQuickAdd.Parse(text, DateTime.Now);
        if (draft is null)
        {
            // Nothing but a date: the full form is the honest place to finish the entry.
            Edit(null, selected, text);
            return;
        }
        agenda.Add(draft);
        quickAdd.Clear();
        var day = DateOnly.FromDateTime(draft.Start);
        cursor = selected = day;
        owner.Status($"EVENTO CREADO · {draft.Title} · {AgendaVisuals.DayLabel(day)}");
        Render();
    }

    private void Edit(AgendaEvent? item, DateOnly day, string draftTitle = "", TimeOnly? at = null)
    {
        if (AgendaEditor.Show(owner, agenda, item, day, draftTitle, at))
        {
            if (item is null) quickAdd.Clear();
            Render();
        }
    }

    // ---------------------------------------------------------------- render

    private void Render()
    {
        var now = DateTime.Now;
        rendered = DateOnly.FromDateTime(now);
        nowLine = null; nowColumn = null;
        renderedHeight = ActualHeight;
        ScaleChrome();

        periodLabel.Text = state.View switch
        {
            "week" => WeekTitle(),
            "agenda" => "PRÓXIMOS 45 DÍAS",
            _ => AgendaVisuals.MonthLabel(cursor)
        };
        summaryLabel.Text = Summary(now);
        PaintViewSwitch();

        surface.Children.Clear();
        surface.Children.Add(state.View switch
        {
            "week" => BuildWeek(now),
            "agenda" => BuildAgenda(now),
            _ => BuildMonth(now)
        });
    }

    private void PaintViewSwitch()
    {
        viewSwitch.Children.Clear();
        foreach (var (key, label, tip) in new[]
        {
            ("month", "MES", "Rejilla del mes"),
            ("week", "SEM", "Semana hora a hora"),
            ("agenda", "LISTA", "Todo lo que viene, en orden")
        })
        {
            bool active = state.View == key;
            var button = new Button
            {
                Content = label, FontSize = C(9), Padding = new Thickness(C(8), C(6), C(8), C(6)), Margin = new Thickness(0, 0, C(4), 0),
                Background = active ? AgendaVisuals.Resource("Ink") : AgendaVisuals.Resource("Surface"),
                Foreground = active ? AgendaVisuals.Resource("Paper") : AgendaVisuals.Resource("Ink"),
                ToolTip = tip
            };
            button.Click += (_, _) => SetView(key);
            viewSwitch.Children.Add(button);
        }
        var create = new Button
        {
            Content = "+ EVENTO", FontSize = C(9), Padding = new Thickness(C(9), C(6), C(9), C(6)), Margin = new Thickness(C(2), 0, 0, 0),
            Background = AgendaVisuals.Resource("Accent"), ToolTip = "Crear un evento con todos los detalles"
        };
        create.Click += (_, _) => Edit(null, selected);
        viewSwitch.Children.Add(create);
    }

    private string WeekTitle()
    {
        var start = AgendaEvent.WeekStart(cursor);
        var end = start.AddDays(6);
        return start.Month == end.Month
            ? $"{start:dd} – {end:dd} {AgendaVisuals.MonthLabel(start)}"
            : $"{AgendaVisuals.DayLabel(start)} – {AgendaVisuals.DayLabel(end)}";
    }

    private string Summary(DateTime now)
    {
        var counts = agenda.Book.Today(now);
        var next = agenda.Book.Upcoming(now, 14).FirstOrDefault(item => !item.Done);
        string head = counts.Total == 0 ? "HOY · SIN EVENTOS" : $"HOY · {counts.Total:00} EVENTOS · {counts.Done:00} HECHOS";
        if (next is null) return head;
        var today = DateOnly.FromDateTime(now);
        string relative = AgendaVisuals.Relative(next.Day, today);
        string when;
        if (next.AllDay) when = relative.Length > 0 ? relative : AgendaVisuals.DayLabel(next.Day);
        else if (next.Day == today) when = AgendaVisuals.Countdown(next.Start, now);
        else when = $"{AgendaVisuals.DayLabel(next.Day)} {next.Start:HH:mm}";
        return $"{head}   ·   SIGUIENTE: {when} · {next.Title.ToUpper(AgendaVisuals.Spanish)}";
    }

    // ---------------------------------------------------------------- month

    private UIElement BuildMonth(DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var first = AgendaEvent.WeekStart(new DateOnly(cursor.Year, cursor.Month, 1));
        var last = first.AddDays(41);
        var byDay = agenda.Book.Between(first, last).GroupBy(item => item.Day).ToDictionary(group => group.Key, group => group.ToList());

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, S(3)) };
        for (int index = 0; index < 7; index++)
            header.Children.Add(new TextBlock
            {
                Text = AgendaEvent.ShortDay(first.AddDays(index).DayOfWeek),
                FontFamily = new FontFamily("Consolas"), FontSize = S(9), FontWeight = FontWeights.Bold,
                Foreground = AgendaVisuals.Resource("Muted"), HorizontalAlignment = HorizontalAlignment.Center
            });
        root.Children.Add(header);

        var grid = new UniformGrid { Columns = 7, Rows = 6 };
        var edge = AgendaVisuals.Fade("Line", 45);
        var density = new List<(StackPanel Chips, TextBlock More, WrapPanel Dots, int Total)>();
        for (int index = 0; index < 42; index++)
        {
            var day = first.AddDays(index);
            bool outside = day.Month != cursor.Month;
            bool isToday = day == today;
            List<AgendaOccurrence> items = byDay.TryGetValue(day, out var found) ? found : [];

            var cell = new Border
            {
                BorderBrush = edge,
                BorderThickness = new Thickness(0, 0, 1, 1),
                Background = day == selected ? AgendaVisuals.Fade("Accent", 90) : Brushes.Transparent,
                ClipToBounds = true,
                Cursor = Cursors.Hand
            };
            var content = new StackPanel { Margin = new Thickness(S(3), S(2), S(3), S(2)) };

            var numberRow = new Grid();
            numberRow.ColumnDefinitions.Add(new ColumnDefinition());
            numberRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var number = new TextBlock
            {
                Text = day.Day.ToString("00"),
                FontFamily = new FontFamily("Consolas"), FontSize = S(11), FontWeight = FontWeights.Bold,
                Foreground = isToday ? AgendaVisuals.Resource("Paper") : AgendaVisuals.Resource("Ink"),
                Padding = new Thickness(S(3), S(1), S(3), S(1))
            };
            var badge = new Border
            {
                Background = isToday ? AgendaVisuals.Resource("Ink") : Brushes.Transparent,
                Child = number, HorizontalAlignment = HorizontalAlignment.Left
            };
            numberRow.Children.Add(badge);
            if (items.Count > 0)
            {
                var dot = new TextBlock
                {
                    Text = items.Count.ToString("00"), FontFamily = new FontFamily("Consolas"), FontSize = S(8),
                    Foreground = AgendaVisuals.Resource("Muted"), VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(dot, 1);
                numberRow.Children.Add(dot);
            }
            content.Children.Add(numberRow);

            var chips = new StackPanel();
            foreach (var item in items.Take(8)) chips.Children.Add(Chip(item));
            var more = new TextBlock { FontSize = S(8), Foreground = AgendaVisuals.Resource("Muted"), Margin = new Thickness(S(3), S(1), 0, 0), Visibility = Visibility.Collapsed };
            chips.Children.Add(more);
            content.Children.Add(chips);

            // A short card cannot hold a single readable chip, so the day speaks in coloured dots.
            var dots = new WrapPanel { Margin = new Thickness(S(2), S(3), 0, 0), Visibility = Visibility.Collapsed };
            foreach (var item in items.Take(5))
                dots.Children.Add(new Border
                {
                    Width = S(5), Height = S(5), CornerRadius = new CornerRadius(S(3)), Margin = new Thickness(0, 0, S(3), 0),
                    Background = AgendaVisuals.Solid(item.Event.Color), Opacity = item.Done ? 0.35 : 1
                });
            content.Children.Add(dots);
            density.Add((chips, more, dots, items.Count));

            cell.Child = content;
            if (outside) cell.Opacity = 0.4;
            var captured = day;
            cell.MouseLeftButtonDown += (_, e) =>
            {
                selected = captured;
                if (e.ClickCount == 2) Edit(null, captured);
                else Render();
            };
            grid.Children.Add(cell);
        }
        // The month adapts live: chips while there is room for words, dots when the card is short.
        void Density()
        {
            if (grid.ActualHeight <= 0) return;
            // How many chips a cell can actually hold at this size; below one, the day uses dots.
            double room = grid.ActualHeight / 6 - S(22);
            int fits = (int)Math.Floor(room / S(15));
            bool compact = fits < 1;
            foreach (var (chips, more, dots, total) in density)
            {
                chips.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                dots.Visibility = compact && dots.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (compact) continue;
                int shown = Math.Min(fits, total);
                for (int index = 0; index < chips.Children.Count - 1; index++)
                    chips.Children[index].Visibility = index < shown ? Visibility.Visible : Visibility.Collapsed;
                // The counter only appears when the leftover strip can hold it; the day number
                // already carries the total, so a cropped "+2" would add nothing.
                more.Text = $"+{total - shown}";
                more.Visibility = total > shown && room - shown * S(15) >= S(11) ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        grid.SizeChanged += (_, _) => Density();
        grid.Loaded += (_, _) => Density();
        Grid.SetRow(grid, 1);
        root.Children.Add(grid);

        var detail = BuildDayPanel(selected, now);
        Grid.SetRow(detail, 2);
        root.Children.Add(detail);
        return root;
    }

    private UIElement Chip(AgendaOccurrence item)
    {
        string label = item.AllDay || item.Continuation ? item.Title : $"{item.Start:HH:mm} {item.Title}";
        var text = new TextBlock
        {
            Text = label, FontSize = S(9), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = item.Done ? AgendaVisuals.Resource("Muted") : AgendaVisuals.Resource("Ink")
        };
        if (item.Done) text.TextDecorations = TextDecorations.Strikethrough;
        var chip = new Border
        {
            Background = AgendaVisuals.Wash(item.Event.Color),
            BorderBrush = AgendaVisuals.Solid(item.Event.Color),
            BorderThickness = new Thickness(S(2.5), 0, 0, 0),
            Padding = new Thickness(S(3), S(1), S(2), S(1)),
            Margin = new Thickness(0, S(2), 0, 0),
            Child = text,
            ToolTip = Tooltip(item),
            Cursor = Cursors.Hand
        };
        chip.MouseLeftButtonDown += (_, e) => { e.Handled = true; selected = item.Day; Edit(item.Event, item.Series); };
        return chip;
    }

    private static string Tooltip(AgendaOccurrence item)
    {
        var lines = new List<string> { item.Title, $"{AgendaVisuals.LongDayLabel(item.Day)} · {item.TimeLabel()}" };
        if (item.Event.Location.Length > 0) lines.Add(item.Event.Location);
        if (item.Event.Repeat != RepeatKind.None) lines.Add(item.Event.RepeatLabel());
        if (item.Event.Notes.Length > 0) lines.Add(item.Event.Notes);
        return string.Join("\n", lines);
    }

    private UIElement BuildDayPanel(DateOnly day, DateTime now)
    {
        var items = agenda.Book.OnDay(day);
        var panel = new StackPanel { Margin = new Thickness(0, S(8), 0, 0) };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string relative = AgendaVisuals.Relative(day, DateOnly.FromDateTime(now));
        header.Children.Add(new TextBlock
        {
            Text = (relative.Length > 0 ? relative + " · " : "") + AgendaVisuals.LongDayLabel(day),
            FontFamily = new FontFamily("Consolas"), FontSize = S(10), FontWeight = FontWeights.Black,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
        });
        var add = new Button { Content = "+ AÑADIR", FontSize = S(9), Padding = new Thickness(S(8), S(5), S(8), S(5)), Margin = new Thickness(0) };
        add.Click += (_, _) => Edit(null, day);
        Grid.SetColumn(add, 1);
        header.Children.Add(add);
        panel.Children.Add(header);

        var list = new StackPanel { Margin = new Thickness(0, S(6), 0, 0) };
        foreach (var item in items) list.Children.Add(Row(item, now));
        if (items.Count == 0)
            list.Children.Add(new TextBlock { Text = "Día libre. Escribe arriba para reservarlo.", FontSize = S(11), Foreground = AgendaVisuals.Resource("Muted"), Margin = new Thickness(S(2), S(6), 0, S(4)) });
        // The day panel takes a share of the card instead of a fixed strip, so a tall widget shows
        // more of the day. The month grid keeps the larger half: it is what this view is for.
        double panelHeight = Math.Clamp(ActualHeight * 0.26, S(130), S(380));
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = panelHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        return panel;
    }

    // ---------------------------------------------------------------- week

    private UIElement BuildWeek(DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var start = AgendaEvent.WeekStart(cursor);
        var days = Enumerable.Range(0, 7).Select(start.AddDays).ToArray();
        var byDay = agenda.Book.Between(start, start.AddDays(6)).GroupBy(item => item.Day).ToDictionary(group => group.Key, group => group.ToList());
        var edge = AgendaVisuals.Fade("Line", 40);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Gutter) });
        foreach (var _ in days) head.ColumnDefinitions.Add(new ColumnDefinition());
        for (int index = 0; index < days.Length; index++)
        {
            var day = days[index];
            bool isToday = day == today;
            var label = new Border
            {
                Background = isToday ? AgendaVisuals.Resource("Ink") : Brushes.Transparent,
                Padding = new Thickness(0, S(3), 0, S(3)),
                Margin = new Thickness(S(1), 0, S(1), S(3)),
                Cursor = Cursors.Hand
            };
            label.Child = new TextBlock
            {
                Text = $"{AgendaEvent.ShortDay(day.DayOfWeek)} {day.Day:00}",
                FontFamily = new FontFamily("Consolas"), FontSize = S(10), FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = isToday ? AgendaVisuals.Resource("Paper") : AgendaVisuals.Resource("Ink")
            };
            var captured = day;
            label.MouseLeftButtonDown += (_, e) =>
            {
                selected = captured;
                if (e.ClickCount == 2) { state.View = "month"; SaveState(); }
                cursor = captured;
                Render();
            };
            Grid.SetColumn(label, index + 1);
            head.Children.Add(label);
        }
        root.Children.Add(head);

        // All-day entries never fit an hour grid, so they get their own strip on top.
        var strip = new Grid { Margin = new Thickness(0, 0, 0, S(3)) };
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Gutter) });
        foreach (var _ in days) strip.ColumnDefinitions.Add(new ColumnDefinition());
        bool anyAllDay = false;
        for (int index = 0; index < days.Length; index++)
        {
            var column = new StackPanel { Margin = new Thickness(S(1), 0, S(1), 0) };
            List<AgendaOccurrence> dayItems = byDay.TryGetValue(days[index], out var found) ? found : [];
            foreach (var item in dayItems.Where(entry => entry.AllDay))
            {
                anyAllDay = true;
                column.Children.Add(Chip(item));
            }
            Grid.SetColumn(column, index + 1);
            strip.Children.Add(column);
        }
        if (anyAllDay)
        {
            strip.Children.Add(new TextBlock { Text = "TODO EL DÍA", FontFamily = new FontFamily("Consolas"), FontSize = S(8), Foreground = AgendaVisuals.Resource("Muted"), VerticalAlignment = VerticalAlignment.Center });
            Grid.SetRow(strip, 1);
            root.Children.Add(strip);
        }

        var canvasRow = new Grid { Height = 24 * HourHeight };
        canvasRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Gutter) });
        foreach (var _ in days) canvasRow.ColumnDefinitions.Add(new ColumnDefinition());

        // The hour labels are placed, not stacked: only absolute positions stay glued to the lines.
        var hours = new Canvas { Width = Gutter, ClipToBounds = true };
        for (int hour = 0; hour < 24; hour++)
        {
            var label = new TextBlock
            {
                Text = $"{hour:00}:00", FontFamily = new FontFamily("Consolas"), FontSize = S(9),
                Foreground = AgendaVisuals.Resource("Muted"), Width = Gutter - S(7), TextAlignment = TextAlignment.Right
            };
            Canvas.SetTop(label, Math.Max(0, hour * HourHeight - S(5)));
            Canvas.SetLeft(label, 0);
            hours.Children.Add(label);
        }
        canvasRow.Children.Add(hours);

        for (int index = 0; index < days.Length; index++)
        {
            var day = days[index];
            var column = new Canvas { Background = day == today ? AgendaVisuals.Fade("Accent", 40) : Brushes.Transparent, ClipToBounds = true };
            column.MouseLeftButtonDown += (sender, e) =>
            {
                selected = day;
                if (e.ClickCount != 2) return;
                // Double-clicking a slot opens a new event at that half hour, not at a default one.
                double minutes = Math.Clamp(Math.Round(e.GetPosition((IInputElement)sender).Y / HourHeight * 2) * 30, 0, 23 * 60 + 30);
                Edit(null, day, "", TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minutes)));
            };
            for (int hour = 0; hour <= 24; hour++)
            {
                var line = new Border { Height = 1, Background = edge, Width = 4000 };
                Canvas.SetTop(line, hour * HourHeight);
                Canvas.SetLeft(line, 0);
                column.Children.Add(line);
            }
            List<AgendaOccurrence> onDay = byDay.TryGetValue(day, out var found) ? found : [];
            var timed = onDay.Where(item => !item.AllDay).ToList();
            foreach (var (item, lane, lanes) in Lanes(timed))
                column.Children.Add(Block(item, lane, lanes, column));
            if (day == today)
            {
                nowColumn = column;
                nowLine = new Border { Height = S(2), Background = AgendaVisuals.Solid("red"), Width = 4000 };
                Canvas.SetTop(nowLine, now.TimeOfDay.TotalMinutes / 60 * HourHeight);
                Panel.SetZIndex(nowLine, 40);
                column.Children.Add(nowLine);
            }
            Grid.SetColumn(column, index + 1);
            canvasRow.Children.Add(column);
        }

        var scroller = new ScrollViewer { Content = canvasRow, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        // Open on the working day rather than at midnight.
        double focus = (days.Contains(today) ? now.TimeOfDay.TotalHours - 1 : 7) * HourHeight;
        scroller.Loaded += (_, _) => scroller.ScrollToVerticalOffset(Math.Max(0, focus));
        Grid.SetRow(scroller, 2);
        root.Children.Add(scroller);
        return root;
    }

    /// <summary>
    /// Places overlapping events side by side: each one takes the first free lane, and the day
    /// splits into as many columns as the busiest moment needs.
    /// </summary>
    private static List<(AgendaOccurrence Item, int Lane, int Lanes)> Lanes(List<AgendaOccurrence> items)
    {
        var ordered = items.OrderBy(item => item.DayStartMinutes).ThenBy(item => item.DayEndMinutes).ToList();
        var ends = new List<double>();
        var placed = new List<(AgendaOccurrence Item, int Lane)>();
        foreach (var item in ordered)
        {
            int lane = 0;
            while (lane < ends.Count && ends[lane] > item.DayStartMinutes + 0.01) lane++;
            if (lane == ends.Count) ends.Add(0);
            ends[lane] = Math.Max(item.DayEndMinutes, item.DayStartMinutes + 20);
            placed.Add((item, lane));
        }
        int total = Math.Max(1, ends.Count);
        return placed.Select(entry => (entry.Item, entry.Lane, total)).ToList();
    }

    private UIElement Block(AgendaOccurrence item, int lane, int lanes, Canvas column)
    {
        double top = item.DayStartMinutes / 60 * HourHeight;
        double height = Math.Max(S(18), (item.DayEndMinutes - item.DayStartMinutes) / 60 * HourHeight - S(2));
        var text = new StackPanel { Margin = new Thickness(S(4), S(2), S(3), S(2)) };
        var heading = new TextBlock
        {
            Text = item.Title, FontSize = S(10), FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = item.Done ? AgendaVisuals.Resource("Muted") : AgendaVisuals.Resource("Ink")
        };
        if (item.Done) heading.TextDecorations = TextDecorations.Strikethrough;
        text.Children.Add(heading);
        if (height > S(34))
            text.Children.Add(new TextBlock { Text = item.TimeLabel(), FontFamily = new FontFamily("Consolas"), FontSize = S(9), Foreground = AgendaVisuals.Resource("Muted"), TextTrimming = TextTrimming.CharacterEllipsis });

        var block = new Border
        {
            Background = AgendaVisuals.Wash(item.Event.Color),
            BorderBrush = AgendaVisuals.Solid(item.Event.Color),
            BorderThickness = new Thickness(S(3), 0, 0, 0),
            Height = height,
            Child = text,
            ToolTip = Tooltip(item),
            Cursor = Cursors.Hand,
            ClipToBounds = true
        };
        block.MouseLeftButtonDown += (_, e) => { e.Handled = true; Edit(item.Event, item.Series); };
        Canvas.SetTop(block, top);
        Panel.SetZIndex(block, 10 + lane);
        // The canvas has no width of its own until it is measured, so the lanes follow its size.
        void Size()
        {
            double available = Math.Max(40, column.ActualWidth - 2);
            double width = available / lanes;
            block.Width = Math.Max(S(24), width - S(2));
            Canvas.SetLeft(block, S(1) + lane * width);
        }
        column.SizeChanged += (_, _) => Size();
        block.Loaded += (_, _) => Size();
        return block;
    }

    // ---------------------------------------------------------------- agenda list

    private UIElement BuildAgenda(DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var items = agenda.Book.Upcoming(now, 45);
        if (filter.Length > 0)
            items = items.Where(item => item.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || item.Event.Location.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || item.Event.Notes.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (!state.ShowDone) items = items.Where(item => !item.Done).ToList();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        var tools = new Grid { Margin = new Thickness(0, 0, 0, S(6)) };
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var search = new TextBox
        {
            Text = filter, Height = S(28), Padding = new Thickness(S(8), 0, S(8), 0), Margin = new Thickness(0), FontSize = S(11),
            MinWidth = 0, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Filtrar por título, lugar o notas"
        };
        search.SetValue(AutomationProperties.NameProperty, "Filtrar eventos");
        ScrollViewer.SetHorizontalScrollBarVisibility(search, ScrollBarVisibility.Hidden);
        search.TextChanged += (_, _) => { filter = search.Text.Trim(); };
        search.KeyUp += (_, e) => { if (e.Key is Key.Enter or Key.Escape) { if (e.Key == Key.Escape) filter = ""; Render(); } };
        var toggle = new Button
        {
            Content = state.ShowDone ? "OCULTAR HECHOS" : "VER HECHOS", FontSize = S(9), Padding = new Thickness(S(8), S(5), S(8), S(5)), Margin = new Thickness(0)
        };
        toggle.Click += (_, _) => { state.ShowDone = !state.ShowDone; SaveState(); Render(); };
        Grid.SetColumn(toggle, 1);
        var searchField = AgendaVisuals.WithHint(search, "Filtrar por título, lugar o notas · Intro para buscar");
        searchField.Margin = new Thickness(0, 0, S(6), 0);
        tools.Children.Add(searchField); tools.Children.Add(toggle);
        root.Children.Add(tools);

        var list = new StackPanel();
        DateOnly? header = null;
        foreach (var item in items)
        {
            if (header != item.Day)
            {
                header = item.Day;
                string relative = AgendaVisuals.Relative(item.Day, today);
                list.Children.Add(new TextBlock
                {
                    Text = (relative.Length > 0 ? relative + " · " : "") + AgendaVisuals.LongDayLabel(item.Day),
                    FontFamily = new FontFamily("Consolas"), FontSize = S(10), FontWeight = FontWeights.Black,
                    Foreground = item.Day == today ? AgendaVisuals.Resource("Ink") : AgendaVisuals.Resource("Muted"),
                    Margin = new Thickness(0, S(10), 0, S(5))
                });
            }
            list.Children.Add(Row(item, now));
        }
        if (items.Count == 0)
            list.Children.Add(new TextBlock
            {
                Text = filter.Length > 0 ? "Nada coincide con ese filtro." : "No hay nada en las próximas semanas.\nEscribe arriba para reservar tu primer bloque.",
                FontSize = S(11), Foreground = AgendaVisuals.Resource("Muted"), Margin = new Thickness(S(2), S(16), S(2), S(8))
            });

        var scroller = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroller, 1);
        root.Children.Add(scroller);
        return root;
    }

    // ---------------------------------------------------------------- shared row

    private UIElement Row(AgendaOccurrence item, DateTime now)
    {
        bool running = item.IsNow(now);
        var frame = new Border
        {
            BorderBrush = AgendaVisuals.Fade("Line", running ? (byte)255 : (byte)70),
            BorderThickness = new Thickness(1),
            Background = AgendaVisuals.Resource("Surface"),
            Margin = new Thickness(0, 0, 0, S(5)),
            Cursor = Cursors.Hand,
            Opacity = item.Done || item.IsPast(now) ? 0.62 : 1
        };
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(S(4)) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(S(64)) });
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(new Border { Background = AgendaVisuals.Solid(item.Event.Color) });

        var clock = new StackPanel { Margin = new Thickness(S(7), S(6), S(4), S(6)), VerticalAlignment = VerticalAlignment.Center };
        clock.Children.Add(new TextBlock
        {
            Text = item.AllDay ? "TODO" : item.Continuation ? "→" : item.Start.ToString("HH:mm"),
            FontFamily = new FontFamily("Consolas"), FontSize = S(12), FontWeight = FontWeights.Bold
        });
        clock.Children.Add(new TextBlock
        {
            Text = item.AllDay ? "EL DÍA" : AgendaVisuals.DurationLabel(Math.Max(5, (int)(item.DayEndMinutes - item.DayStartMinutes))),
            FontSize = S(8), Foreground = AgendaVisuals.Resource("Muted")
        });
        Grid.SetColumn(clock, 1);
        line.Children.Add(clock);

        var text = new StackPanel { Margin = new Thickness(S(2), S(6), S(6), S(6)), VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock
        {
            Text = item.Title, FontSize = S(12), FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap
        };
        if (item.Done) title.TextDecorations = TextDecorations.Strikethrough;
        text.Children.Add(title);

        var meta = new List<string>();
        if (running) meta.Add("EN CURSO");
        // A countdown only helps while it is imminent; further out, the day and hour say more.
        else if (!item.AllDay && item.Start > now && (item.Start - now).TotalHours < 4) meta.Add(AgendaVisuals.Countdown(item.Start, now));
        if (item.Event.Location.Length > 0) meta.Add(item.Event.Location);
        if (item.Event.Repeat != RepeatKind.None) meta.Add(item.Event.RepeatLabel());
        if (item.Event.Reminders.Count > 0 && !item.Done) meta.Add("🔔 " + item.Event.ReminderLabel(item.Event.Reminders[0]));
        if (meta.Count > 0)
            text.Children.Add(new TextBlock
            {
                Text = string.Join("  ·  ", meta), FontSize = S(9), Foreground = running ? AgendaVisuals.Solid(item.Event.Color) : AgendaVisuals.Resource("Muted"),
                TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(0, S(2), 0, 0)
            });
        Grid.SetColumn(text, 2);
        line.Children.Add(text);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, S(4), 0) };
        var check = new Button
        {
            Content = item.Done ? "✓" : "○", Width = S(26), Height = S(26), Padding = new Thickness(0), Margin = new Thickness(0, 0, S(3), 0), FontSize = S(12),
            Background = item.Done ? AgendaVisuals.Resource("Ink") : AgendaVisuals.Resource("Surface"),
            Foreground = item.Done ? AgendaVisuals.Resource("Paper") : AgendaVisuals.Resource("Muted"),
            ToolTip = item.Done ? "Marcar como pendiente" : "Marcar como hecho"
        };
        check.Click += (_, _) =>
        {
            item.Event.SetDone(item.Series, !item.Done);
            agenda.Save();
            Render();
        };
        actions.Children.Add(check);
        var drop = new Button
        {
            Content = "×", Width = S(26), Height = S(26), Padding = new Thickness(0), Margin = new Thickness(0), FontSize = S(14), BorderThickness = new Thickness(0),
            ToolTip = item.Event.Repeat == RepeatKind.None ? "Eliminar evento" : "Quitar solo este día de la serie"
        };
        drop.Click += (_, _) =>
        {
            if (item.Event.Repeat == RepeatKind.None) agenda.Remove(item.Event);
            else { item.Event.Cancel(item.Series); agenda.Save(); }
            owner.Status($"EVENTO ELIMINADO · {item.Title}");
            Render();
        };
        actions.Children.Add(drop);
        Grid.SetColumn(actions, 3);
        line.Children.Add(actions);

        frame.Child = line;
        frame.ToolTip = Tooltip(item);
        frame.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && Ancestor<Button>(source) is not null) return;
            Edit(item.Event, item.Series);
        };
        return frame;
    }

    private static T? Ancestor<T>(DependencyObject source) where T : DependencyObject => Ancestors.Find<T>(source);

    /// <summary>The type scale the card settled on. The self test checks that it follows the size.</summary>
    internal double Scale => scale;

    /// <summary>Switches view without persisting it, so the self test can photograph all three.</summary>
    internal void ShowView(string view)
    {
        state.View = view;
        Render();
    }

    private static Button Small(string label, string tip, Action action)
    {
        var button = new Button { Content = label, FontSize = 10, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 4, 0), ToolTip = tip, MinWidth = 28 };
        button.Click += (_, _) => action();
        return button;
    }
}
