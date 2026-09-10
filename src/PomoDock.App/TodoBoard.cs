using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// The To Do card: what is left today, what is overdue, and the order the user chose. Tasks stay
/// inside the widget, so two cards are two independent lists — one per page, one per project.
/// </summary>
internal sealed class TodoBoard : Grid
{
    private readonly MainWindow owner;
    private readonly WidgetConfig config;
    private readonly TodoBook book;
    private readonly TextBlock counter;
    private readonly TextBlock meta;
    private readonly Grid progressTrack;
    private readonly Border progressFill;
    private double share;
    private readonly TextBox input;
    private readonly StackPanel filters;
    private readonly StackPanel tools;
    private readonly StackPanel list;
    /// <summary>The task being renamed in place, if any.</summary>
    private Guid editing;

    public TodoBoard(MainWindow owner, WidgetConfig config)
    {
        this.owner = owner;
        this.config = config;
        book = Read(config.Value);

        for (int row = 0; row < 4; row++) RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        Margin = new Thickness(4, 6, 4, 2);

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        counter = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 13, FontWeight = FontWeights.Bold };
        meta = new TextBlock { FontSize = 10, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
        Grid.SetColumn(meta, 1);
        head.Children.Add(counter); head.Children.Add(meta);
        Children.Add(head);

        // A flat bar instead of the system ProgressBar: its green belongs to another theme.
        progressFill = new Border { Background = AgendaVisuals.Resource("Ink"), HorizontalAlignment = HorizontalAlignment.Left };
        progressTrack = new Grid { Height = 6, Margin = new Thickness(0, 8, 0, 8), Background = AgendaVisuals.Fade("Line", 45) };
        progressTrack.Children.Add(progressFill);
        progressTrack.SizeChanged += (_, _) => PaintProgress();
        Grid.SetRow(progressTrack, 1);
        Children.Add(progressTrack);

        var addRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        input = new TextBox
        {
            Height = 32, Margin = new Thickness(0), Padding = new Thickness(9, 0, 9, 0), MinWidth = 0, FontSize = 12,
            TextWrapping = TextWrapping.NoWrap, VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Escribe la tarea. Añade ! para prioridad y hoy, mañana o 12/09 para ponerle fecha."
        };
        input.SetValue(AutomationProperties.NameProperty, "Nueva tarea");
        ScrollViewer.SetHorizontalScrollBarVisibility(input, ScrollBarVisibility.Hidden);
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { input.Clear(); e.Handled = true; }
        };
        var field = AgendaVisuals.WithHint(input, "Comprar café mañana !");
        field.Margin = new Thickness(0, 0, 6, 0);
        var add = new Button { Content = "+ AÑADIR", FontSize = 10, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0), Height = 32 };
        add.Click += (_, _) => Commit();
        Grid.SetColumn(add, 1);
        addRow.Children.Add(field); addRow.Children.Add(add);
        Grid.SetRow(addRow, 2);
        Children.Add(addRow);

        var bar = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filters = new StackPanel { Orientation = Orientation.Horizontal };
        tools = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(tools, 1);
        bar.Children.Add(filters); bar.Children.Add(tools);
        Grid.SetRow(bar, 3);
        Children.Add(bar);

        list = new StackPanel();
        var scroller = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroller, 4);
        Children.Add(scroller);

        Render();
    }

    /// <summary>Rows currently drawn. The self test uses it to prove a stored list really renders.</summary>
    internal int RowCount => list.Children.OfType<Border>().Count();

    // ---------------------------------------------------------------- storage

    private static TodoBook Read(string payload)
    {
        // Older cards stored the same field names with fewer fields, so they load straight in.
        if (string.IsNullOrWhiteSpace(payload)) return new TodoBook();
        TodoBook? stored = null;
        try { stored = JsonSerializer.Deserialize<TodoBook>(payload); }
        catch (JsonException) { }
        var book = stored ?? new TodoBook();
        book.Normalize();
        return book;
    }

    private void Save()
    {
        book.Normalize();
        config.Value = JsonSerializer.Serialize(book);
        owner.SaveState();
    }

    private void Commit()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var task = book.Add(input.Text, today);
        if (task is null) { input.SelectAll(); return; }
        input.Clear();
        // A task typed while looking at another slice would vanish; show where it landed.
        if (book.Filter == "done" || (book.Filter == "today" && !task.IsDueToday(today) && !task.IsOverdue(today))) book.Filter = "all";
        Save();
        Render();
    }

    private void PaintProgress() => progressFill.Width = Math.Max(0, progressTrack.ActualWidth * share);

    // ---------------------------------------------------------------- render

    private void Render()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var counts = book.Counts(today);

        counter.Text = $"{counts.Done:00} / {counts.Total:00} HECHAS";
        if (counts.Overdue > 0)
        {
            meta.Text = $"{counts.Overdue:00} VENCIDA{(counts.Overdue == 1 ? "" : "S")}";
            meta.Foreground = AgendaVisuals.Solid("red");
            meta.FontWeight = FontWeights.Bold;
        }
        else
        {
            meta.Text = counts.Today > 0 ? $"{counts.Today:00} PARA HOY" : $"{counts.Open:00} PENDIENTES";
            meta.Foreground = AgendaVisuals.Resource("Muted");
            meta.FontWeight = FontWeights.Normal;
        }
        share = counts.Total == 0 ? 0 : (double)counts.Done / counts.Total;
        PaintProgress();

        filters.Children.Clear();
        foreach (var (key, label) in new[] { ("all", "TODAS"), ("open", "PENDIENTES"), ("today", "HOY"), ("done", "HECHAS") })
        {
            bool active = book.Filter == key;
            var chip = new Button
            {
                Content = label, FontSize = 9, Padding = new Thickness(7, 5, 7, 5), Margin = new Thickness(0, 0, 4, 0),
                Background = active ? AgendaVisuals.Resource("Ink") : AgendaVisuals.Resource("Surface"),
                Foreground = active ? AgendaVisuals.Resource("Paper") : AgendaVisuals.Resource("Ink")
            };
            chip.Click += (_, _) => { book.Filter = key; Save(); Render(); };
            filters.Children.Add(chip);
        }

        tools.Children.Clear();
        if (counts.Open > 1)
        {
            var sort = new Button { Content = "⇅", FontSize = 12, Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(0, 0, 4, 0), ToolTip = "Ordenar por fecha y prioridad" };
            sort.Click += (_, _) => { book.SortByUrgency(today); Save(); Render(); owner.Status("LISTA ORDENADA · Primero lo vencido, luego lo más cercano."); };
            tools.Children.Add(sort);
        }
        if (counts.Done > 0)
        {
            var clear = new Button { Content = "LIMPIAR", FontSize = 9, Padding = new Thickness(7, 5, 7, 5), Margin = new Thickness(0), ToolTip = "Eliminar todas las tareas hechas" };
            clear.Click += (_, _) =>
            {
                int removed = book.ClearDone();
                Save(); Render();
                owner.Status($"{removed} TAREA{(removed == 1 ? "" : "S")} HECHA{(removed == 1 ? "" : "S")} FUERA DE LA LISTA.");
            };
            tools.Children.Add(clear);
        }

        list.Children.Clear();
        var visible = book.Visible(today);
        foreach (var task in visible) list.Children.Add(Row(task, today));
        if (visible.Count == 0) list.Children.Add(Empty(counts));
    }

    private UIElement Empty((int Total, int Done, int Open, int Overdue, int Today) counts) => new TextBlock
    {
        Text = book.Filter switch
        {
            "done" => "Nada terminado todavía. Marca una tarea y aparecerá aquí.",
            "today" => counts.Open > 0 ? "Nada con fecha para hoy. Ponle fecha a una tarea con el botón ◷." : "Nada para hoy.",
            "open" => counts.Total > 0 ? "Todo hecho. Disfruta el hueco." : "Sin tareas. Escribe arriba la primera.",
            _ => "SIN TAREAS · Añade la primera cosa que quieres sacar adelante."
        },
        FontSize = 11,
        Foreground = AgendaVisuals.Resource("Muted"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(2, 14, 2, 8)
    };

    private UIElement Row(TodoTask task, DateOnly today)
    {
        bool overdue = task.IsOverdue(today);
        var accent = AgendaVisuals.Solid(task.ColorKey());
        var frame = new Border
        {
            BorderBrush = AgendaVisuals.Fade("Line", 80),
            BorderThickness = new Thickness(1),
            Background = AgendaVisuals.Resource("Surface"),
            Margin = new Thickness(0, 0, 0, 5),
            Opacity = task.Done ? 0.6 : 1
        };
        var line = new Grid();
        // A bar on the left edge carries the priority, so urgency reads before a single word does.
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 0 });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = new Button
        {
            Content = task.Done ? "✓" : "○", Width = 26, Height = 26, FontSize = 12, Padding = new Thickness(0),
            Margin = new Thickness(5, 5, 6, 5),
            Background = task.Done ? AgendaVisuals.Resource("Ink") : AgendaVisuals.Resource("Surface"),
            Foreground = task.Done ? AgendaVisuals.Resource("Paper") : AgendaVisuals.Resource("Muted"),
            ToolTip = task.Done ? "Marcar como pendiente" : "Completar tarea"
        };
        check.SetValue(AutomationProperties.NameProperty, (task.Done ? "Marcar pendiente: " : "Completar: ") + task.Title);
        check.Click += (_, _) => { task.SetDone(!task.Done); Save(); Render(); };
        Grid.SetColumn(check, 1);
        line.Children.Add(new Border { Background = task.Priority == TodoPriority.None ? Brushes.Transparent : accent });
        line.Children.Add(check);

        var priority = new Button
        {
            Content = "!", Width = 20, Height = 26, FontSize = 13, FontWeight = FontWeights.Black, Padding = new Thickness(0),
            Margin = new Thickness(0, 5, 4, 5), BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Foreground = accent,
            Opacity = task.Priority == TodoPriority.None ? 0.22 : 1,
            ToolTip = task.PriorityLabel() + " · pulsa para cambiar"
        };
        priority.SetValue(AutomationProperties.NameProperty, task.PriorityLabel());
        priority.Click += (_, _) => { task.Priority = task.NextPriority(); Save(); Render(); };
        Grid.SetColumn(priority, 2);
        line.Children.Add(priority);

        if (editing == task.Id)
        {
            var rename = new TextBox { Text = task.Title, FontSize = 12, Margin = new Thickness(0, 4, 4, 4), Padding = new Thickness(4, 2, 4, 2), MinWidth = 0 };
            rename.SetValue(AutomationProperties.NameProperty, "Renombrar tarea");
            void Finish(bool keep)
            {
                if (editing != task.Id) return;
                editing = Guid.Empty;
                var text = rename.Text.Trim();
                if (keep && text.Length > 0) { task.Title = text; Save(); }
                Render();
            }
            rename.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { Finish(true); e.Handled = true; }
                else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
            };
            rename.LostFocus += (_, _) => Finish(true);
            rename.Loaded += (_, _) => { rename.Focus(); rename.SelectAll(); };
            Grid.SetColumn(rename, 3);
            line.Children.Add(rename);
        }
        else
        {
            var title = new TextBlock
            {
                Text = task.Title, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(0, 8, 4, 8),
                Foreground = task.Done ? AgendaVisuals.Resource("Muted") : AgendaVisuals.Resource("Ink")
            };
            if (task.Done) title.TextDecorations = TextDecorations.Strikethrough;
            Grid.SetColumn(title, 3);
            line.Children.Add(title);
        }

        string due = task.DueLabel(today);
        var date = new Button
        {
            Content = due.Length > 0 ? due : "◷",
            FontSize = due.Length > 0 ? 9 : 11,
            FontWeight = overdue ? FontWeights.Bold : FontWeights.Normal,
            Padding = new Thickness(due.Length > 0 ? 6 : 5, 4, due.Length > 0 ? 6 : 5, 4),
            Margin = new Thickness(0, 5, 3, 5),
            BorderThickness = new Thickness(due.Length > 0 ? 1 : 0),
            Background = overdue ? AgendaVisuals.Wash("red") : Brushes.Transparent,
            Foreground = overdue ? AgendaVisuals.Solid("red") : AgendaVisuals.Resource("Ink"),
            Opacity = due.Length > 0 ? 1 : 0.3,
            ToolTip = due.Length > 0 ? $"Vence {task.Due:dd/MM/yyyy} · pulsa para cambiar" : "Sin fecha · pulsa para vencer hoy"
        };
        date.SetValue(AutomationProperties.NameProperty, due.Length > 0 ? "Fecha: " + due : "Poner fecha");
        date.Click += (_, _) => { task.Due = task.NextDue(today); Save(); Render(); };
        Grid.SetColumn(date, 4);
        line.Children.Add(date);

        var more = new Button
        {
            Content = "⋯", Width = 22, Height = 26, FontSize = 13, Padding = new Thickness(0), Margin = new Thickness(0, 5, 4, 5),
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, ToolTip = "Más acciones"
        };
        more.SetValue(AutomationProperties.NameProperty, "Acciones de la tarea");
        more.Click += (_, _) => Actions(task);
        Grid.SetColumn(more, 5);
        line.Children.Add(more);

        frame.Child = line;
        frame.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2 || e.OriginalSource is DependencyObject source && Ancestor<Button>(source) is not null) return;
            editing = task.Id;
            Render();
        };
        frame.ToolTip = Tooltip(task, today);
        return frame;
    }

    private static string Tooltip(TodoTask task, DateOnly today)
    {
        var lines = new List<string> { task.Title, task.PriorityLabel() };
        if (task.Due is { } due) lines.Add(task.IsOverdue(today) ? $"Venció el {due:dd/MM/yyyy}" : $"Vence el {due:dd/MM/yyyy}");
        lines.Add(task.Done ? "Hecha · doble clic para renombrar" : "Doble clic para renombrar");
        return string.Join("\n", lines);
    }

    private void Actions(TodoTask task)
    {
        int choice = Dialogs.Choose(owner, "TAREA", ["Renombrar", "Elegir fecha", "Quitar fecha", "Subir", "Bajar", "Eliminar"]);
        var today = DateOnly.FromDateTime(DateTime.Now);
        switch (choice)
        {
            case 0:
                editing = task.Id;
                Render();
                return;
            case 1:
                var typed = Dialogs.Prompt(owner, "FECHA DE LA TAREA", "Día en formato dd/mm/aaaa", task.Due?.ToString("dd/MM/yyyy") ?? today.ToString("dd/MM/yyyy"));
                if (typed is null) return;
                if (DateOnly.TryParseExact(typed.Trim(), ["dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d/M/yy"], AgendaVisuals.Spanish, System.Globalization.DateTimeStyles.None, out var parsed)) task.Due = parsed;
                else { owner.Status("FECHA NO RECONOCIDA · Usa el formato dd/mm/aaaa."); return; }
                break;
            case 2:
                task.Due = null;
                break;
            case 3:
            case 4:
                if (!book.Move(task, choice == 3 ? -1 : 1)) return;
                break;
            case 5:
                book.Items.RemoveAll(item => item.Id == task.Id);
                owner.Status($"TAREA ELIMINADA · {task.Title}");
                break;
            default:
                return;
        }
        Save();
        Render();
    }

    private static T? Ancestor<T>(DependencyObject source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }
}
