using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// A compact task cockpit: projects stay in their own rail, while adding, finding and focusing a
/// task happen in one clear work area. Large collections never turn into a wall of project chips.
/// </summary>
public sealed class TasksWindow : Window
{
    /// <summary>Stored on every task and session since the first version, so it stays as it is.</summary>
    private const string NoProject = "Sin proyecto";

    private readonly MainWindow owner;
    private readonly WorkTask? current;
    /// <summary>Finished pomodoros per task, read once: none can finish while this is open.</summary>
    private readonly Dictionary<Guid, int> finished;

    private readonly TextBox newName = new();
    private readonly ComboBox newProject = new();
    private readonly Counter newEstimate = new(1);
    private readonly TextBox search = new();
    private readonly StackPanel projectList = new();
    private readonly StackPanel projectActions = new();
    private readonly StackPanel frequentHost = new();
    private readonly TextBlock taskSummary = new();
    private readonly StackPanel list = new();
    private readonly ScrollViewer projectScroller = new();

    /// <summary>The project the list is narrowed to, or null for all of them.</summary>
    private string? filter;
    private WorkTask? editing;
    private bool showDone;

    public WorkTask? SelectedTask { get; private set; }
    public bool SelectionChanged { get; private set; }
    internal bool ProjectRailScrollsForDiagnostics => projectScroller.ScrollableHeight > 0;
    internal int VisibleProjectRowsForDiagnostics => projectList.Children.Count;
    internal int VisibleTaskRowsForDiagnostics => list.Children.Count;

    public TasksWindow(MainWindow owner, WorkTask? current = null)
    {
        this.owner = owner;
        this.current = current;
        Owner = owner; Title = L.T("tasks.title"); Width = 700; Height = 720; MinWidth = 560; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowStyle = WindowStyle.None; AllowsTransparency = true;
        Background = Brushes.Transparent; ResizeMode = ResizeMode.CanResizeWithGrip;
        // A window of our own gets no Window style, so its content would inherit black text.
        SetResourceReference(ForegroundProperty, "Ink");

        finished = owner.Store.Sessions()
            .Where(s => s.TaskId is not null && s.Phase == Phase.Focus && s.Outcome == Outcome.Completed)
            .GroupBy(s => s.TaskId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());

        var page = new Grid { Margin = new Thickness(18, 15, 18, 14) };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition());
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.Children.Add(Intro());

        var workspace = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(174) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition());
        workspace.Children.Add(ProjectRail());
        var tasks = TaskArea();
        Grid.SetColumn(tasks, 2);
        workspace.Children.Add(tasks);
        Grid.SetRow(workspace, 1);
        page.Children.Add(workspace);

        var footer = Footer();
        Grid.SetRow(footer, 2);
        page.Children.Add(footer);
        Content = page;

        // Escape backs out one step: first out of an open editor, then out of the window.
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || e.Handled) return;
            if (editing is not null) { editing = null; Render(); } else Close();
            e.Handled = true;
        };
        Loaded += (_, _) => newName.Focus();
        Render();
        Dialogs.Modalize(this);
    }

    // ------------------------------------------------------------------ layout

    private UIElement Intro()
    {
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var words = new StackPanel();
        var heading = Dialogs.Heading(L.T("tasks.heading"));
        heading.FontSize = 22;
        heading.Margin = new Thickness(0, 0, 0, 3);
        words.Children.Add(heading);
        var hint = Text(L.T("tasks.hint"), 11.5, "Muted");
        hint.TextWrapping = TextWrapping.Wrap;
        words.Children.Add(hint);
        line.Children.Add(words);

        var free = Dialogs.Button(current is null ? L.T("tasks.freeFocusCurrent") : L.T("tasks.freeFocusChoose"), () => Choose(null));
        free.ToolTip = L.T("tasks.freeFocusTip");
        free.Padding = new Thickness(11, 6, 11, 6);
        free.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(free, 1);
        line.Children.Add(free);
        return line;
    }

    /// <summary>Projects use a fixed-height vertical rail, so thirty of them take no more room than five.</summary>
    private UIElement ProjectRail()
    {
        var frame = new Border { Padding = new Thickness(10), BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BackgroundProperty, "Raised");
        frame.SetResourceReference(Border.BorderBrushProperty, "Edge");
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new DockPanel { LastChildFill = true, Margin = new Thickness(2, 0, 0, 8) };
        var add = Quiet("+", CreateProject);
        add.FontSize = 17;
        add.Padding = new Thickness(7, 0, 7, 1);
        add.ToolTip = L.T("tasks.newProjectTip");
        DockPanel.SetDock(add, Dock.Right);
        heading.Children.Add(add);
        heading.Children.Add(Caption(L.T("tasks.projects")));
        root.Children.Add(heading);

        projectScroller.Content = projectList;
        projectScroller.Focusable = false;
        projectScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        projectScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Grid.SetRow(projectScroller, 1);
        root.Children.Add(projectScroller);
        Grid.SetRow(projectActions, 2);
        root.Children.Add(projectActions);
        frame.Child = root;
        return frame;
    }

    private UIElement TaskArea()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(AddBar());
        Grid.SetRow(frequentHost, 1);
        root.Children.Add(frequentHost);
        var toolbar = TaskToolbar();
        Grid.SetRow(toolbar, 2);
        root.Children.Add(toolbar);
        var scroller = new ScrollViewer
        {
            Content = list,
            Focusable = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroller, 3);
        root.Children.Add(scroller);
        return root;
    }

    private UIElement TaskToolbar()
    {
        var line = new Grid { Margin = new Thickness(1, 11, 1, 5) };
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Min(190, Width * .27)) });
        taskSummary.FontSize = 10.5;
        taskSummary.FontWeight = FontWeights.Bold;
        taskSummary.VerticalAlignment = VerticalAlignment.Center;
        taskSummary.SetResourceReference(ForegroundProperty, "Muted");
        line.Children.Add(taskSummary);

        search.SetValue(AutomationProperties.NameProperty, Ui("Buscar tareas", "Search tasks"));
        search.ToolTip = Ui("Buscar por tarea o proyecto", "Search by task or project");
        search.TextChanged += (_, _) => RenderList();
        var field = WithPlaceholder(search, Ui("Buscar…", "Search…"));
        field.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(field, 1);
        line.Children.Add(field);
        return line;
    }

    /// <summary>Name, project, how many pomodoros you expect. Enter adds it.</summary>
    private UIElement AddBar()
    {
        var bar = new Border { Padding = new Thickness(10), BorderThickness = new Thickness(1) };
        bar.SetResourceReference(Border.BackgroundProperty, "Raised");
        bar.SetResourceReference(Border.BorderBrushProperty, "Edge");
        var stack = new StackPanel();
        var label = Caption(L.T("tasks.newTask"));
        label.Margin = new Thickness(1, 0, 0, 5);
        stack.Children.Add(label);

        newName.SetValue(AutomationProperties.NameProperty, L.T("tasks.newTaskName"));
        newName.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Add(); e.Handled = true; } };
        var nameLine = new Grid();
        nameLine.ColumnDefinitions.Add(new ColumnDefinition());
        nameLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var field = WithPlaceholder(newName, L.T("tasks.newTaskPlaceholder"));
        field.Margin = new Thickness(0, 0, 8, 7);
        nameLine.Children.Add(field);
        var add = Primary(Dialogs.Button("+", Add));
        add.SetValue(AutomationProperties.NameProperty, L.T("common.add"));
        add.ToolTip = L.T("common.add");
        add.FontSize = 20;
        add.Padding = new Thickness(15, 4, 15, 5);
        add.Margin = new Thickness(0, 0, 0, 7);
        Grid.SetColumn(add, 1);
        nameLine.Children.Add(add);
        stack.Children.Add(nameLine);

        newProject.ToolTip = L.T("tasks.taskProject");
        newProject.SetValue(AutomationProperties.NameProperty, L.T("tasks.newTaskProject"));
        newEstimate.ToolTip = L.T("tasks.estimateHelp");
        stack.Children.Add(FieldLine(newProject, newEstimate, null));
        bar.Child = stack;
        return bar;
    }

    /// <summary>Project, pomodoros and an optional button, on one line. Shared by adding and editing.</summary>
    private static Grid FieldLine(ComboBox project, Counter estimate, Button? action)
    {
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        project.Margin = new Thickness(0, 0, 12, 0);
        line.Children.Add(project);

        var pair = new StackPanel { Orientation = Orientation.Horizontal };
        var label = Caption(L.T("tasks.pomodoros"));
        label.Margin = new Thickness(0, 0, 8, 0);
        pair.Children.Add(label);
        pair.Children.Add(estimate);
        Grid.SetColumn(pair, 1);
        line.Children.Add(pair);

        if (action is not null)
        {
            action.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(action, 2);
            line.Children.Add(action);
        }
        return line;
    }

    private UIElement Footer()
    {
        var line = new DockPanel { Margin = new Thickness(188, 9, 0, 0), LastChildFill = false };
        var keys = Text(L.T("tasks.keys"), 11, "Muted");
        DockPanel.SetDock(keys, Dock.Right);
        line.Children.Add(keys);
        return line;
    }

    // ------------------------------------------------------------------ drawing

    private void Render()
    {
        var projects = Projects();
        if (filter is not null && filter != NoProject && !projects.Contains(filter)) filter = null;

        var keep = newProject.SelectedItem as string;
        var choices = new[] { NoProject }.Concat(projects).ToList();
        // The list holds the stored names; only what the box paints on screen is translated.
        newProject.ItemsSource = choices;
        newProject.ItemTemplate = ProjectTemplate();
        newProject.SelectedItem = keep is not null && choices.Contains(keep) ? keep : NoProject;

        RenderProjects(projects);
        RenderFrequent();
        RenderList();
    }

    /// <summary>Projects remain scannable as a list even when there are dozens of them.</summary>
    private void RenderProjects(List<string> projects)
    {
        projectList.Children.Clear();
        projectActions.Children.Clear();
        var tasks = owner.Settings.Tasks.Where(task => !task.Template).ToList();
        int Pending(string? project) => tasks.Count(task => !task.Done && (project is null || task.Project == project));

        projectList.Children.Add(ProjectRow(Ui("TODAS", "ALL"), Pending(null), filter is null, () => Filter(null), L.T("tasks.seeAll")));
        foreach (var project in projects)
            projectList.Children.Add(ProjectRow(project, Pending(project), filter == project, () => Filter(project), L.T("tasks.seeProject", project)));
        if (tasks.Any(task => task.Project == NoProject))
            projectList.Children.Add(ProjectRow(ProjectLabel(NoProject), Pending(NoProject), filter == NoProject, () => Filter(NoProject), L.T("tasks.seeNoProject")));

        if (filter is { } chosen && chosen != NoProject)
        {
            var edge = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 6) };
            edge.SetResourceReference(Border.BackgroundProperty, "Edge");
            projectActions.Children.Add(edge);
            var actions = new Grid();
            actions.ColumnDefinitions.Add(new ColumnDefinition());
            actions.ColumnDefinitions.Add(new ColumnDefinition());
            var rename = Quiet("✎ " + Ui("RENOMBRAR", "RENAME"), () => RenameProject(chosen));
            rename.ToolTip = L.T("tasks.renameProject");
            var delete = Quiet("× " + Ui("BORRAR", "DELETE"), () => DeleteProject(chosen));
            delete.ToolTip = L.T("tasks.deleteProject");
            Grid.SetColumn(delete, 1);
            actions.Children.Add(rename);
            actions.Children.Add(delete);
            projectActions.Children.Add(actions);
        }
    }

    private void RenderFrequent()
    {
        frequentHost.Children.Clear();
        var frequent = owner.Settings.Tasks.Where(task => task.Template).ToList();
        if (frequent.Count == 0) return;
        var label = Caption(L.T("tasks.frequent"));
        label.Margin = new Thickness(1, 9, 0, 5);
        frequentHost.Children.Add(label);
        var shelf = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var template in frequent) shelf.Children.Add(FrequentChip(template));
        frequentHost.Children.Add(new ScrollViewer
        {
            Content = shelf,
            Focusable = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
    }

    private void RenderList()
    {
        list.Children.Clear();
        string query = search.Text.Trim();
        var shown = owner.Settings.Tasks.Where(task => !task.Template && (filter is null || task.Project == filter)
            && (query.Length == 0 || task.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || ProjectLabel(task.Project).Contains(query, StringComparison.CurrentCultureIgnoreCase))).ToList();
        var pending = shown.Where(task => !task.Done).OrderByDescending(task => current?.Id == task.Id).ToList();
        var done = shown.Where(task => task.Done).ToList();

        taskSummary.Text = filter is null
            ? L.T("tasks.pending", pending.Count)
            : $"{Short(ProjectLabel(filter)).ToUpper(Strings.Culture)} · {pending.Count:00}";
        if (pending.Count == 0)
            list.Children.Add(Empty(query.Length > 0 ? Ui("SIN RESULTADOS", "NO MATCHES") : shown.Count > 0 ? L.T("tasks.nothingPending")
                : filter is null ? L.T("tasks.emptyAll") : L.T("tasks.emptyProject")));
        foreach (var task in pending) list.Children.Add(task == editing ? Editor(task) : Row(task));

        if (done.Count == 0) return;
        var toggle = Quiet(L.T("tasks.doneSection", showDone ? "▾" : "▸", done.Count), () => { showDone = !showDone; RenderList(); });
        toggle.Margin = new Thickness(-8, 12, 0, 2);
        toggle.HorizontalAlignment = HorizontalAlignment.Left;
        toggle.ToolTip = showDone ? L.T("tasks.hideDone") : L.T("tasks.showDone");
        list.Children.Add(toggle);
        if (showDone)
            foreach (var task in done) list.Children.Add(task == editing ? Editor(task) : Row(task));
    }

    /// <summary>One task: tick, name, project and pomodoros, then what you can do with it.</summary>
    private UIElement Row(WorkTask task)
    {
        bool active = current?.Id == task.Id;
        var card = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent };
        card.SetResourceReference(Border.BorderBrushProperty, "Edge");
        var inner = new Border { Padding = new Thickness(active ? 7 : 10, 8, 3, 8), BorderThickness = new Thickness(active ? 3 : 0, 0, 0, 0) };
        inner.SetResourceReference(Border.BorderBrushProperty, "Ink");
        if (active) card.SetResourceReference(Border.BackgroundProperty, "Raised");
        card.MouseEnter += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "Raised");
        card.MouseLeave += (_, _) =>
        {
            if (active) card.SetResourceReference(Border.BackgroundProperty, "Raised");
            else card.Background = Brushes.Transparent;
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tick = new CheckBox { IsChecked = task.Done, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = task.Done ? L.T("tasks.markPending") : L.T("tasks.markDone") };
        tick.SetValue(AutomationProperties.NameProperty, L.T("tasks.doneName", task.Name));
        tick.Click += (_, _) => { task.Done = tick.IsChecked == true; Save(); };
        grid.Children.Add(tick);

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, ToolTip = L.T("tasks.doubleClickEdit") };
        var name = Text(task.Name, 13, "Ink", FontWeights.SemiBold);
        name.TextWrapping = TextWrapping.Wrap;
        if (task.Done) { name.TextDecorations = TextDecorations.Strikethrough; words.Opacity = .6; }
        words.Children.Add(name);
        var meta = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
        if (filter is null && task.Project != NoProject) meta.Children.Add(ProjectTag(task.Project));
        meta.Children.Add(Progress(finished.GetValueOrDefault(task.Id), task.Estimate));
        words.Children.Add(meta);
        words.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { Edit(task); e.Handled = true; } };
        Grid.SetColumn(words, 1);
        grid.Children.Add(words);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        if (active) actions.Children.Add(CurrentBadge());
        else if (!task.Done)
        {
            var focus = Dialogs.Button("▶", () => Choose(task));
            focus.SetValue(AutomationProperties.NameProperty, L.T("tasks.focus"));
            focus.Width = 37;
            focus.Padding = new Thickness(8, 5, 8, 5);
            focus.ToolTip = L.T("tasks.focusTip");
            actions.Children.Add(focus);
        }
        var edit = Quiet("•••", () => Edit(task));
        edit.SetValue(AutomationProperties.NameProperty, L.T("common.edit"));
        edit.Width = 39;
        edit.Padding = new Thickness(6, 5, 6, 5);
        edit.ToolTip = L.T("tasks.editTip");
        actions.Children.Add(edit);
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        inner.Child = grid;
        card.Child = inner;
        return card;
    }

    /// <summary>The same task opened for editing, in its own place in the list.</summary>
    private UIElement Editor(WorkTask task)
    {
        var card = new Border { Padding = new Thickness(14, 12, 14, 14), Margin = new Thickness(0, 4, 0, 6), BorderThickness = new Thickness(1.5) };
        card.SetResourceReference(Border.BackgroundProperty, "Raised");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        // Wait for the scroller to measure the taller list, or there is nothing yet to scroll to.
        card.Loaded += (_, _) => card.Dispatcher.InvokeAsync(card.BringIntoView, System.Windows.Threading.DispatcherPriority.Background);
        var stack = new StackPanel();
        stack.Children.Add(Caption(L.T("tasks.editTask")));

        var name = new TextBox { Text = task.Name, Margin = new Thickness(0, 6, 0, 8) };
        name.SetValue(AutomationProperties.NameProperty, L.T("tasks.taskName"));
        name.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        stack.Children.Add(name);
        var project = new ComboBox { ItemsSource = new[] { NoProject }.Concat(Projects()).ToList(), SelectedItem = task.Project, ToolTip = L.T("tasks.taskProject"), ItemTemplate = ProjectTemplate() };
        var estimate = new Counter(task.Estimate) { ToolTip = L.T("tasks.estimateHelp") };
        stack.Children.Add(FieldLine(project, estimate, null));

        void Commit()
        {
            var text = name.Text.Trim();
            if (text.Length == 0) { name.Focus(); return; }
            task.Name = text;
            task.Project = project.SelectedItem as string ?? NoProject;
            task.Estimate = estimate.Value;
            editing = null;
            if (filter is not null && filter != task.Project) filter = null;
            Save();
        }
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };

        var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        buttons.Children.Add(Primary(Dialogs.Button(L.T("common.save"), Commit)));
        buttons.Children.Add(Dialogs.Button(L.T("common.cancel"), () => { editing = null; Render(); }));
        var delete = Dialogs.Button(L.T("common.delete"), () => DeleteTask(task));
        delete.ToolTip = L.T("tasks.deleteTip");
        delete.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(delete, Dock.Right);
        buttons.Children.Add(delete);
        bool frequent = FrequentOf(task) is not null;
        var star = Dialogs.Button(frequent ? L.T("tasks.unmakeFrequent") : L.T("tasks.makeFrequent"), () => ToggleFrequent(task));
        star.ToolTip = L.T("tasks.frequentTip");
        star.Margin = new Thickness(0);
        DockPanel.SetDock(star, Dock.Right);
        buttons.Children.Add(star);
        stack.Children.Add(buttons);

        card.Child = stack;
        return card;
    }

    /// <summary>A frequent task: its name adds a fresh copy, the cross takes it off the shelf.</summary>
    private UIElement FrequentChip(WorkTask template)
    {
        var chip = new Border { BorderThickness = new Thickness(1.5), Margin = new Thickness(0, 0, 6, 6) };
        chip.SetResourceReference(Border.BorderBrushProperty, "Edge");
        chip.SetResourceReference(Border.BackgroundProperty, "Surface");
        var pair = new StackPanel { Orientation = Orientation.Horizontal };
        var add = Quiet("+ " + template.Name, () => Insert(new WorkTask { Name = template.Name, Project = template.Project, Estimate = template.Estimate }));
        add.SetResourceReference(ForegroundProperty, "Ink");
        add.ToolTip = L.T("tasks.frequentAdd", template.Name, template.Project, Pomodoros(template.Estimate));
        pair.Children.Add(add);
        var remove = Quiet("×", () => { owner.Settings.Tasks.Remove(template); Save(); });
        remove.ToolTip = L.T("tasks.frequentRemove");
        remove.SetValue(AutomationProperties.NameProperty, L.T("tasks.frequentRemoveName", template.Name));
        pair.Children.Add(remove);
        chip.Child = pair;
        return chip;
    }

    /// <summary>A quiet progress meter keeps large histories readable without turning each row into a chart.</summary>
    private static UIElement Progress(int done, int estimate)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        line.ToolTip = done <= estimate
            ? L.T("tasks.progressOf", done, Pomodoros(estimate))
            : L.T("tasks.progressOver", Pomodoros(done), estimate);
        var track = new Grid { Width = 42, Height = 7, Margin = new Thickness(0, 0, 7, 0), ClipToBounds = true };
        var back = new Border();
        back.SetResourceReference(Border.BackgroundProperty, "Line");
        track.Children.Add(back);
        var fill = new Border
        {
            Width = 42 * Math.Min(1, done / (double)Math.Max(1, estimate)),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        fill.SetResourceReference(Border.BackgroundProperty, "Ink");
        track.Children.Add(fill);
        line.Children.Add(track);
        line.Children.Add(Text($"{done:00}/{estimate:00}", 10.5, done > estimate ? "Ink" : "Muted", FontWeights.SemiBold));
        return line;
    }

    // ------------------------------------------------------------------ changes

    private void Add()
    {
        var text = newName.Text.Trim();
        if (text.Length == 0) { newName.Focus(); return; }
        Insert(new WorkTask { Name = text, Project = newProject.SelectedItem as string ?? NoProject, Estimate = newEstimate.Value });
        newName.Clear();
        newName.Focus();
    }

    /// <summary>New tasks go on top, and the list widens if the project on screen would hide them.</summary>
    private void Insert(WorkTask task)
    {
        owner.Settings.Tasks.Insert(0, task);
        if (filter is not null && filter != task.Project) filter = null;
        Save();
    }

    private void Edit(WorkTask task)
    {
        editing = task;
        if (task.Done) showDone = true;
        Render();
    }

    private void DeleteTask(WorkTask task)
    {
        if (Dialogs.Choose(this, L.T("tasks.deleteTaskTitle"), [L.T("tasks.deleteTaskConfirm", Short(task.Name)), L.T("common.cancel")]) != 0) return;
        owner.Settings.Tasks.Remove(task);
        editing = null;
        Save();
    }

    private void ToggleFrequent(WorkTask task)
    {
        if (FrequentOf(task) is { } existing) owner.Settings.Tasks.Remove(existing);
        else owner.Settings.Tasks.Add(new WorkTask { Name = task.Name, Project = task.Project, Estimate = task.Estimate, Template = true });
        Save();
    }

    private WorkTask? FrequentOf(WorkTask task) =>
        owner.Settings.Tasks.FirstOrDefault(other => other.Template && other.Name == task.Name && other.Project == task.Project);

    /// <summary>Picking a project also makes it the one new tasks go into.</summary>
    private void Filter(string? project)
    {
        filter = project;
        editing = null;
        Render();
        if (project is not null) newProject.SelectedItem = project;
    }

    private void CreateProject()
    {
        var name = Dialogs.Prompt(this, L.T("tasks.newProjectTitle"), L.T("tasks.newProjectLabel"));
        if (string.IsNullOrWhiteSpace(name) || !Available(name)) return;
        owner.Settings.Projects.Add(name);
        owner.SaveState();
        Filter(name);
    }

    /// <summary>Renaming carries its tasks along. Past sessions keep the name they were logged under.</summary>
    private void RenameProject(string old)
    {
        var name = Dialogs.Prompt(this, L.T("tasks.renameProjectTitle"), L.T("tasks.renameProjectLabel"), old);
        if (string.IsNullOrWhiteSpace(name) || name == old || !Available(name)) return;
        int index = owner.Settings.Projects.IndexOf(old);
        if (index >= 0) owner.Settings.Projects[index] = name; else owner.Settings.Projects.Add(name);
        foreach (var task in owner.Settings.Tasks.Where(task => task.Project == old)) task.Project = name;
        owner.SaveState();
        Filter(name);
    }

    /// <summary>Deleting a project never deletes work: its tasks stay, without a project.</summary>
    private void DeleteProject(string project)
    {
        int count = owner.Settings.Tasks.Count(task => !task.Template && task.Project == project);
        string confirm = count == 0 ? L.T("tasks.deleteProjectOnly") : count == 1 ? L.T("tasks.deleteProjectOne") : L.T("tasks.deleteProjectMany", count);
        if (Dialogs.Choose(this, L.T("tasks.deleteProjectTitle", Short(project)), [confirm, L.T("common.cancel")]) != 0) return;
        owner.Settings.Projects.Remove(project);
        foreach (var task in owner.Settings.Tasks.Where(task => task.Project == project)) task.Project = NoProject;
        filter = null;
        Save();
    }

    private bool Available(string name)
    {
        if (name != NoProject && !Projects().Contains(name)) return true;
        Dialogs.Alert(this, L.T("tasks.nameTakenTitle"), L.T("tasks.nameTakenBody", name));
        return false;
    }

    /// <summary>Choosing the focus you already have changes nothing, so it must not cut a running session.</summary>
    private void Choose(WorkTask? task)
    {
        if (task?.Id != current?.Id) { SelectedTask = task; SelectionChanged = true; }
        Close();
    }

    private void Save()
    {
        owner.SaveState();
        Render();
    }

    /// <summary>Projects in the order they were made, plus any a task names that the list lost.</summary>
    private List<string> Projects() => owner.Settings.Projects
        .Concat(owner.Settings.Tasks.Select(task => task.Project))
        .Where(project => !string.IsNullOrWhiteSpace(project) && project != NoProject)
        .Distinct()
        .ToList();

    // ------------------------------------------------------------------ pieces

    private static string Pomodoros(int count) => count == 1 ? L.T("tasks.pomodoroOne") : L.T("tasks.pomodoroMany", count);

    private static string Short(string text) => text.Length <= 28 ? text : text[..27] + "…";

    private static string Ui(string es, string en) => Strings.Culture.TwoLetterISOLanguageName == "es" ? es : en;

    private static Button ProjectRow(string name, int count, bool on, Action click, string tip)
    {
        var row = Dialogs.Button("", click);
        row.Padding = new Thickness(9, 7, 8, 7);
        row.Margin = new Thickness(0, 0, 0, 3);
        row.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        row.ToolTip = tip;
        row.SetResourceReference(BackgroundProperty, on ? "Ink" : "Surface");
        row.SetResourceReference(ForegroundProperty, on ? "Paper" : "Ink");
        row.SetResourceReference(BorderBrushProperty, on ? "Ink" : "Edge");
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Text(name, 11.5, on ? "Paper" : "Ink", on ? FontWeights.Bold : FontWeights.Normal);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        content.Children.Add(label);
        var amount = Text(count.ToString("00", System.Globalization.CultureInfo.InvariantCulture), 10.5, on ? "Paper" : "Muted", FontWeights.Bold);
        amount.Margin = new Thickness(7, 0, 0, 0);
        Grid.SetColumn(amount, 1);
        content.Children.Add(amount);
        row.Content = content;
        return row;
    }

    /// <summary>The one button of a group that does the main thing.</summary>
    private static Button Primary(Button button)
    {
        button.SetResourceReference(BackgroundProperty, "Ink");
        button.SetResourceReference(ForegroundProperty, "Paper");
        button.SetResourceReference(BorderBrushProperty, "Ink");
        return button;
    }

    /// <summary>A button that reads as a link: for secondary actions that should not compete.</summary>
    private static Button Quiet(string text, Action click)
    {
        var button = Dialogs.Button(text, click);
        button.Padding = new Thickness(8, 4, 8, 4);
        button.Margin = new Thickness(0);
        button.FontSize = 11.5;
        button.BorderThickness = new Thickness(0);
        button.Background = Brushes.Transparent;
        button.SetResourceReference(ForegroundProperty, "Muted");
        return button;
    }

    private static UIElement CurrentBadge()
    {
        var badge = new Border { Width = 30, Height = 30, Margin = new Thickness(0, 0, 4, 0), ToolTip = L.T("tasks.currentTip") };
        badge.SetResourceReference(Border.BackgroundProperty, "Ink");
        var dot = Text("●", 11, "Paper", FontWeights.Bold);
        dot.HorizontalAlignment = HorizontalAlignment.Center;
        badge.Child = dot;
        return badge;
    }

    /// <summary>
    /// Paints a project name. Everything but the "no project" placeholder is the user's own words,
    /// which are never translated; that one value is stored in Spanish from the first version and
    /// so is swapped for the current language only on its way to the screen.
    /// </summary>
    private static string ProjectLabel(string project) => project == NoProject ? L.T("common.noProject") : project;

    private static DataTemplate ProjectTemplate()
    {
        var template = new DataTemplate(typeof(string));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(".") { Converter = new ProjectNameConverter() });
        template.VisualTree = text;
        return template;
    }

    private sealed class ProjectNameConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            value is string project ? ProjectLabel(project) : value;

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value;
    }

    private static UIElement ProjectTag(string project)
    {
        var tag = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        tag.SetResourceReference(Border.BorderBrushProperty, "Edge");
        tag.Child = Text(project, 11, "Ink");
        return tag;
    }

    private static Grid WithPlaceholder(TextBox box, string text)
    {
        var field = new Grid();
        box.Margin = new Thickness(0);
        field.Children.Add(box);
        var ghost = Text(text, 14, "Muted");
        ghost.IsHitTestVisible = false;
        ghost.Margin = new Thickness(12, 0, 0, 0);
        box.TextChanged += (_, _) => ghost.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        field.Children.Add(ghost);
        return field;
    }

    private static TextBlock Caption(string text) => Text(text, 10.5, "Muted", FontWeights.Bold);

    private static TextBlock Section(string text)
    {
        var title = Caption(text);
        title.Margin = new Thickness(0, 8, 0, 4);
        return title;
    }

    private static TextBlock Empty(string text)
    {
        var note = Text(text, 12.5, "Muted");
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, 10, 0, 10);
        return note;
    }

    private static TextBlock Text(string text, double size, string brush, FontWeight? weight = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        if (weight is not null) block.FontWeight = weight.Value;
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }

    /// <summary>A whole number you change with − and +, or by typing it. Always 1 to 999.</summary>
    private sealed class Counter : StackPanel
    {
        private readonly TextBox box;

        public Counter(int value)
        {
            Orientation = Orientation.Horizontal;
            box = new TextBox
            {
                Width = 46, Margin = new Thickness(0), Padding = new Thickness(4, 7, 4, 7), BorderThickness = new Thickness(0, 1.5, 0, 1.5),
                TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
            };
            box.SetValue(AutomationProperties.NameProperty, L.T("tasks.pomodoros"));
            box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
            box.LostFocus += (_, _) => Value = Value;
            Value = value;
            Children.Add(Step("−", -1, L.T("counter.less")));
            Children.Add(box);
            Children.Add(Step("+", 1, L.T("counter.more")));
        }

        public int Value
        {
            get => int.TryParse(box.Text, out int value) ? Math.Clamp(value, 1, 999) : 1;
            set => box.Text = Math.Clamp(value, 1, 999).ToString();
        }

        private Button Step(string sign, int delta, string label)
        {
            var step = Dialogs.Button(sign, () => Value += delta);
            step.Padding = new Thickness(11, 4, 11, 4);
            step.Margin = new Thickness(0);
            step.FontSize = 14;
            step.SetValue(AutomationProperties.NameProperty, label);
            return step;
        }
    }
}
