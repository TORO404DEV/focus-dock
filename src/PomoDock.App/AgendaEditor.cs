using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// The full event form: when it happens, how often it repeats, when it should ring and how it
/// looks. Editing always works on a copy, so cancelling a change leaves the agenda untouched.
/// </summary>
internal static class AgendaEditor
{
    private static readonly string[] DateFormats = ["dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd/MM/yy", "d/M/yy"];
    private static readonly string[] TimeFormats = ["HH:mm", "H:mm", "HH.mm", "H.mm", "HHmm"];
    private static readonly int[] TimedReminders = [0, 5, 10, 15, 30, 60, 120, 1440];
    private static readonly int[] AllDayReminders = [0, 1440, 2880, 10080];
    private static readonly DayOfWeek[] Week =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    /// <summary>Opens the form. Returns true when the agenda changed.</summary>
    public static bool Show(MainWindow owner, AgendaStore agenda, AgendaEvent? existing, DateOnly day, string draftTitle = "", TimeOnly? at = null)
    {
        bool creating = existing is null;
        var item = existing?.Copy() ?? new AgendaEvent
        {
            Title = draftTitle,
            Start = day.ToDateTime(at ?? new TimeOnly(NextHour(day), 0)),
            Minutes = 60
        };
        var window = Dialogs.Window(owner, creating ? "NUEVO EVENTO" : "EDITAR EVENTO", 560, 780);
        window.MinWidth = 460; window.MinHeight = 520;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        window.Content = root;

        var form = new StackPanel { Margin = new Thickness(24, 20, 24, 16) };
        var scroller = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(scroller);

        form.Children.Add(Dialogs.Heading(creating ? "NUEVO EVENTO" : "EDITAR EVENTO"));

        var title = Entry(item.Title, L.T("agendaEditor.titleHelp"));
        title.FontSize = 16; title.FontWeight = FontWeights.Bold;
        title.SetValue(AutomationProperties.NameProperty, L.T("agendaEditor.titleName"));
        form.Children.Add(Label(L.T("agendaEditor.titleLabel")));
        form.Children.Add(title);

        var allDay = new CheckBox { Content = L.T("agendaEditor.allDay"), IsChecked = item.AllDay, FontSize = 12, Margin = new Thickness(0, 2, 0, 6) };
        form.Children.Add(allDay);

        form.Children.Add(Label("FECHA"));
        var dateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var date = Entry(item.Start.ToString("dd/MM/yyyy", Strings.Culture), L.T("agendaEditor.dateFormatHelp"));
        date.Width = 130; date.Margin = new Thickness(0, 0, 8, 0);
        date.SetValue(AutomationProperties.NameProperty, "Fecha del evento");
        dateRow.Children.Add(date);
        foreach (var (label, shift) in new (string, int)[] { ("HOY", 0), ("+1 D", 1), ("+7 D", 7) })
        {
            var jump = new Button { Content = label, FontSize = 10, Padding = new Thickness(9, 6, 9, 6), Margin = new Thickness(0, 0, 5, 0) };
            jump.Click += (_, _) =>
            {
                var current = shift == 0 ? DateOnly.FromDateTime(DateTime.Now) : (TryDate(date.Text, out var parsed) ? parsed : DateOnly.FromDateTime(DateTime.Now)).AddDays(shift);
                date.Text = current.ToString("dd/MM/yyyy", Strings.Culture);
            };
            dateRow.Children.Add(jump);
        }
        form.Children.Add(dateRow);

        // Timed events carry a start and an end; all-day events carry a number of days instead.
        var timedRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        for (int column = 0; column < 3; column++) timedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = column == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var start = Entry(item.Start.ToString("HH:mm", CultureInfo.InvariantCulture), L.T("agendaEditor.startHelp"));
        start.Width = 88; start.Margin = new Thickness(0, 0, 10, 0);
        start.SetValue(AutomationProperties.NameProperty, "Hora de inicio");
        var end = Entry(item.Start.AddMinutes(Math.Max(5, item.Minutes)).ToString("HH:mm", CultureInfo.InvariantCulture), L.T("agendaEditor.endHelp"));
        end.Width = 88; end.Margin = new Thickness(0, 0, 12, 0);
        end.SetValue(AutomationProperties.NameProperty, "Hora de fin");
        var span = new TextBlock { FontSize = 11, Foreground = AgendaVisuals.Resource("Muted"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(end, 1); Grid.SetColumn(span, 2);
        timedRow.Children.Add(start); timedRow.Children.Add(end); timedRow.Children.Add(span);
        var timedBlock = new StackPanel();
        var timedLabels = new Grid();
        timedLabels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(98) });
        timedLabels.ColumnDefinitions.Add(new ColumnDefinition());
        var startLabel = Label("INICIO"); var endLabel = Label("FIN"); Grid.SetColumn(endLabel, 1);
        timedLabels.Children.Add(startLabel); timedLabels.Children.Add(endLabel);
        timedBlock.Children.Add(timedLabels); timedBlock.Children.Add(timedRow);
        form.Children.Add(timedBlock);

        var daysBlock = new StackPanel();
        daysBlock.Children.Add(Label(L.T("agendaEditor.daysLabel")));
        var dayCount = Entry(Math.Max(1, item.Minutes / 1440).ToString(CultureInfo.InvariantCulture), L.T("agendaEditor.daysHelp"));
        dayCount.Width = 88;
        dayCount.SetValue(AutomationProperties.NameProperty, L.T("agendaEditor.daysName"));
        daysBlock.Children.Add(dayCount);
        form.Children.Add(daysBlock);

        form.Children.Add(Label("COLOR"));
        string color = item.Color;
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        form.Children.Add(swatches);
        void PaintSwatches()
        {
            swatches.Children.Clear();
            foreach (var option in AgendaPalette.All)
            {
                bool active = option.Key == color;
                var swatch = new Button
                {
                    Width = 38, Height = 28, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(0),
                    Background = AgendaVisuals.Solid(option.Key),
                    BorderBrush = AgendaVisuals.Resource("Ink"),
                    BorderThickness = new Thickness(active ? 3.5 : 1),
                    ToolTip = option.Name,
                    Content = active ? "✓" : "",
                    Foreground = Brushes.White,
                    FontSize = 13
                };
                swatch.SetValue(AutomationProperties.NameProperty, "Color " + option.Name);
                swatch.Click += (_, _) => { color = option.Key; PaintSwatches(); };
                swatches.Children.Add(swatch);
            }
        }
        PaintSwatches();

        form.Children.Add(Label(L.T("agendaEditor.repeatLabel")));
        // The list is read by index, so translating these never changes which RepeatKind is chosen.
        var repeat = new ComboBox { Margin = new Thickness(0, 4, 0, 6), ItemsSource = new[] { L.T("agendaEditor.repeatNone"), L.T("agendaEditor.repeatDaily"), L.T("agendaEditor.repeatWeekly"), L.T("agendaEditor.repeatMonthly"), L.T("agendaEditor.repeatYearly") } };
        repeat.SelectedIndex = (int)item.Repeat;
        repeat.SetValue(AutomationProperties.NameProperty, L.T("agendaEditor.repeatName"));
        form.Children.Add(repeat);

        var repeatDetail = new StackPanel();
        var intervalRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        intervalRow.Children.Add(new TextBlock { Text = "Cada", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var interval = Entry(item.Interval.ToString(CultureInfo.InvariantCulture), L.T("agendaEditor.intervalHelp"));
        interval.Width = 62; interval.Margin = new Thickness(0, 0, 8, 0);
        interval.SetValue(AutomationProperties.NameProperty, L.T("agendaEditor.intervalName"));
        intervalRow.Children.Add(interval);
        var intervalUnit = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        intervalRow.Children.Add(intervalUnit);
        repeatDetail.Children.Add(intervalRow);

        var weekdays = new List<DayOfWeek>(item.Days.Count > 0 ? item.Days : [item.Start.DayOfWeek]);
        var weekRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        repeatDetail.Children.Add(weekRow);
        void PaintWeek()
        {
            weekRow.Children.Clear();
            foreach (var weekday in Week)
            {
                bool active = weekdays.Contains(weekday);
                var toggle = Chip(AgendaEvent.ShortDay(weekday), active);
                toggle.Width = 34;
                toggle.SetValue(AutomationProperties.NameProperty, weekday.ToString());
                toggle.Click += (_, _) =>
                {
                    if (!weekdays.Remove(weekday)) weekdays.Add(weekday);
                    if (weekdays.Count == 0) weekdays.Add(weekday);
                    PaintWeek();
                };
                weekRow.Children.Add(toggle);
            }
        }
        PaintWeek();

        var untilRow = new StackPanel { Orientation = Orientation.Horizontal };
        untilRow.Children.Add(new TextBlock { Text = "Hasta", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var until = Entry(item.Until?.ToString("dd/MM/yyyy", Strings.Culture) ?? "", L.T("agendaEditor.untilHelp"));
        until.Width = 130;
        until.SetValue(AutomationProperties.NameProperty, "Repetir hasta");
        untilRow.Children.Add(until);
        untilRow.Children.Add(new TextBlock { Text = L.T("agendaEditor.untilNote"), FontSize = 10, Foreground = AgendaVisuals.Resource("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
        repeatDetail.Children.Add(untilRow);
        form.Children.Add(repeatDetail);

        form.Children.Add(Label("RECORDATORIOS"));
        var reminders = new List<int>(item.Reminders);
        var reminderRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        form.Children.Add(reminderRow);
        var reminderHint = new TextBlock { FontSize = 10, Foreground = AgendaVisuals.Resource("Muted"), Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
        form.Children.Add(reminderHint);

        form.Children.Add(Label("LUGAR"));
        var place = Entry(item.Location, L.T("agendaEditor.placeHelp"));
        place.SetValue(AutomationProperties.NameProperty, "Lugar");
        form.Children.Add(place);

        form.Children.Add(Label("NOTAS"));
        var notes = Entry(item.Notes, L.T("agendaEditor.notesHelp"));
        notes.AcceptsReturn = true; notes.TextWrapping = TextWrapping.Wrap; notes.Height = 74; notes.VerticalContentAlignment = VerticalAlignment.Top;
        notes.SetValue(AutomationProperties.NameProperty, "Notas");
        form.Children.Add(notes);

        var preview = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"), FontSize = 11, FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap
        };
        form.Children.Add(preview);

        // Everything the form can change flows through here, so the preview never lies.
        void Sync()
        {
            bool whole = allDay.IsChecked == true;
            timedBlock.Visibility = whole ? Visibility.Collapsed : Visibility.Visible;
            daysBlock.Visibility = whole ? Visibility.Visible : Visibility.Collapsed;
            var kind = (RepeatKind)Math.Max(0, repeat.SelectedIndex);
            repeatDetail.Visibility = kind == RepeatKind.None ? Visibility.Collapsed : Visibility.Visible;
            weekRow.Visibility = kind == RepeatKind.Weekly ? Visibility.Visible : Visibility.Collapsed;
            intervalUnit.Text = kind switch
            {
                RepeatKind.Daily => L.T("agendaEditor.unitDays"),
                RepeatKind.Weekly => "semana(s), en:",
                RepeatKind.Monthly => "mes(es)",
                _ => L.T("agendaEditor.unitYears")
            };
            var presets = whole ? AllDayReminders : TimedReminders;
            reminders.RemoveAll(minutes => !presets.Contains(minutes));
            reminderRow.Children.Clear();
            foreach (int minutes in presets)
            {
                bool active = reminders.Contains(minutes);
                var chip = Chip(ChipLabel(minutes, whole), active);
                chip.Click += (_, _) =>
                {
                    if (!reminders.Remove(minutes)) reminders.Add(minutes);
                    Sync();
                };
                reminderRow.Children.Add(chip);
            }
            reminderHint.Text = reminders.Count == 0
                ? L.T("agendaEditor.noReminders")
                : L.T("agendaEditor.withReminders");
            preview.Text = Preview(Draft(), whole);
        }

        // Builds the event as the form currently reads it, without touching the agenda.
        AgendaEvent Draft()
        {
            var draft = item.Copy();
            draft.Title = title.Text.Trim();
            draft.Location = place.Text.Trim();
            draft.Notes = notes.Text.Trim();
            draft.Color = color;
            draft.AllDay = allDay.IsChecked == true;
            var when = TryDate(date.Text, out var parsedDate) ? parsedDate : DateOnly.FromDateTime(item.Start);
            if (draft.AllDay)
            {
                draft.Start = when.ToDateTime(TimeOnly.MinValue);
                int days = int.TryParse(dayCount.Text.Trim(), out int value) ? Math.Clamp(value, 1, 90) : 1;
                draft.Minutes = days * 1440;
            }
            else
            {
                var from = TryTime(start.Text, out var parsedStart) ? parsedStart : TimeOnly.FromDateTime(item.Start);
                var to = TryTime(end.Text, out var parsedEnd) ? parsedEnd : from.AddMinutes(60);
                int minutes = (int)(to - from).TotalMinutes;
                if (minutes <= 0) minutes += 1440;
                draft.Start = when.ToDateTime(from);
                draft.Minutes = Math.Clamp(minutes, 5, 1440 * 14);
            }
            draft.Repeat = (RepeatKind)Math.Max(0, repeat.SelectedIndex);
            draft.Interval = int.TryParse(interval.Text.Trim(), out int every) ? Math.Clamp(every, 1, 99) : 1;
            draft.Days = draft.Repeat == RepeatKind.Weekly ? [.. weekdays] : [];
            draft.Until = TryDate(until.Text, out var lastDay) ? lastDay : null;
            draft.Reminders = [.. reminders];
            draft.Normalize();
            return draft;
        }

        allDay.Checked += (_, _) => Sync();
        allDay.Unchecked += (_, _) => Sync();
        repeat.SelectionChanged += (_, _) => Sync();
        foreach (var field in new[] { title, date, start, end, dayCount, interval, until, place })
            field.TextChanged += (_, _) => { span.Text = SpanLabel(start.Text, end.Text); Sync(); };
        Sync();
        span.Text = SpanLabel(start.Text, end.Text);

        var footer = new Grid { Margin = new Thickness(24, 0, 24, 20) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        bool changed = false;

        if (!creating)
        {
            var remove = new Button { Content = "ELIMINAR", FontSize = 11, Padding = new Thickness(12, 9, 12, 9) };
            remove.Click += (_, _) =>
            {
                if (existing!.Repeat == RepeatKind.None)
                {
                    if (Dialogs.Choose(owner, "ELIMINAR EVENTO", ["Eliminar", "Cancelar"]) != 0) return;
                    agenda.Remove(existing);
                }
                else
                {
                    int choice = Dialogs.Choose(owner, L.T("agendaEditor.deleteRepeatTitle"), [L.T("agendaEditor.deleteThisDay"), L.T("agendaEditor.deleteSeries"), L.T("common.cancel")]);
                    if (choice == 0) { existing.Cancel(day); agenda.Save(); }
                    else if (choice == 1) agenda.Remove(existing);
                    else return;
                }
                changed = true;
                window.DialogResult = true;
            };
            footer.Children.Add(remove);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(buttons, 1);
        var save = new Button { Content = "GUARDAR", FontSize = 12, Padding = new Thickness(16, 9, 16, 9), Background = AgendaVisuals.Resource("Ink"), Foreground = AgendaVisuals.Resource("Paper"), IsDefault = true };
        save.Click += (_, _) =>
        {
            var draft = Draft();
            if (draft.Title.Length == 0) { title.Focus(); preview.Text = "PONLE UN NOMBRE AL EVENTO PARA GUARDARLO."; return; }
            if (!TryDate(date.Text, out _)) { date.Focus(); preview.Text = L.T("agendaEditor.badDate"); return; }
            if (!draft.AllDay && !TryTime(start.Text, out _)) { start.Focus(); preview.Text = L.T("agendaEditor.badTime"); return; }
            if (creating) agenda.Add(draft); else agenda.Replace(draft);
            changed = true;
            window.DialogResult = true;
        };
        buttons.Children.Add(save);
        var cancel = new Button { Content = "CANCELAR", FontSize = 12, Padding = new Thickness(14, 9, 14, 9), Margin = new Thickness(0), IsCancel = true };
        cancel.Click += (_, _) => window.DialogResult = false;
        buttons.Children.Add(cancel);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);

        window.Loaded += (_, _) => { Dialogs.Modalize(window); title.Focus(); title.SelectAll(); };
        window.ShowDialog();
        return changed;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 9,
        FontWeight = FontWeights.Bold,
        Foreground = AgendaVisuals.Resource("Muted"),
        Margin = new Thickness(0, 8, 0, 0)
    };

    private static TextBox Entry(string text, string tooltip) => new()
    {
        Text = text,
        ToolTip = tooltip,
        FontSize = 13,
        Margin = new Thickness(0, 4, 0, 6),
        Padding = new Thickness(9, 7, 9, 7),
        MinWidth = 0
    };

    private static Button Chip(string text, bool active) => new()
    {
        Content = text,
        FontSize = 10,
        Padding = new Thickness(10, 6, 10, 6),
        Margin = new Thickness(0, 0, 5, 5),
        Background = active ? AgendaVisuals.Resource("Ink") : AgendaVisuals.Resource("Surface"),
        Foreground = active ? AgendaVisuals.Resource("Paper") : AgendaVisuals.Resource("Ink")
    };

    private static string ChipLabel(int minutes, bool allDay) => minutes switch
    {
        0 => allDay ? L.T("agendaEditor.reminderAllDay") : L.T("agendaEditor.reminderAtStart"),
        < 60 => $"{minutes} MIN",
        1440 => L.T("agendaEditor.reminderOneDay"),
        2880 => L.T("agendaEditor.reminderTwoDays"),
        10080 => "1 SEMANA",
        _ => $"{minutes / 60} H"
    };

    private static string SpanLabel(string from, string to)
    {
        if (!TryTime(from, out var start) || !TryTime(to, out var end)) return "";
        int minutes = (int)(end - start).TotalMinutes;
        if (minutes <= 0) minutes += 1440;
        return "· " + AgendaVisuals.DurationLabel(minutes);
    }

    private static string Preview(AgendaEvent draft, bool allDay)
    {
        if (draft.Title.Length == 0) return "";
        var day = DateOnly.FromDateTime(draft.Start);
        string when = allDay
            ? L.T("agendaEditor.previewAllDay", AgendaVisuals.LongDayLabel(day))
            : L.T("agendaEditor.previewTime", AgendaVisuals.LongDayLabel(day), $"{draft.Start:HH:mm}", $"{draft.EndOn(day):HH:mm}");
        string repeat = draft.Repeat == RepeatKind.None ? "" : " · " + draft.RepeatLabel().ToUpper(Strings.Culture);
        string alerts = draft.Reminders.Count == 0 ? L.T("agendaEditor.previewNoAlert") : " · " + draft.ReminderLabel(draft.Reminders[0]).ToUpper(Strings.Culture);
        return when + repeat + alerts;
    }

    /// <summary>A new event opens on the next full hour, the way a diary is written.</summary>
    private static int NextHour(DateOnly day)
    {
        var now = DateTime.Now;
        if (day != DateOnly.FromDateTime(now)) return 9;
        return Math.Clamp(now.Hour + 1, 0, 23);
    }

    private static bool TryDate(string text, out DateOnly value) =>
        DateOnly.TryParseExact(text.Trim(), DateFormats, Strings.Culture, DateTimeStyles.None, out value);

    private static bool TryTime(string text, out TimeOnly value) =>
        TimeOnly.TryParseExact(text.Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
}
