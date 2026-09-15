using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PomoDock.Core;

namespace PomoDock.App;

/// <summary>
/// Mission desk: pick what to focus on first. Creating tasks and browsing projects stay secondary
/// so a long backlog does not bury the next action.
/// </summary>
public sealed class TasksWindow : Window
{
    /// <summary>Stored on every task and session since the first version, so it stays as it is.</summary>
    private const string NoProject = "Sin proyecto";

    private readonly MainWindow owner;
    private readonly WorkTask? current;
    /// <summary>Finished pomodoros per task, read once: none can finish while this is open.</summary>
    private readonly Dictionary<Guid, int> finished;
    /// <summary>Most recent focus end per task — backfills recency for tasks saved before timestamps.</summary>
    private readonly Dictionary<Guid, DateTimeOffset> lastFocus;
    private readonly Dictionary<Guid, int> listIndex = new();

    private readonly TextBox newName = new();
    private readonly ComboBox newProject = new();
    private readonly Counter newEstimate = new(1);
    private readonly TextBox search = new();
    private readonly ComboBox projectFilter = new();
    private readonly StackPanel projectList = new();
    private readonly StackPanel projectActions = new();
    private readonly StackPanel frequentHost = new();
    private readonly StackPanel suggestHost = new();
    private readonly StackPanel list = new();
    private readonly TextBlock taskSummary = new();
    private readonly Border createHost = new();
    private readonly ScrollViewer projectScroller = new();
    private readonly Grid projectRailHost = new();

    /// <summary>The project the list is narrowed to, or null for all of them.</summary>
    private string? filter;
    private WorkTask? editing;
    private bool showDone;
    private bool creating;
    private bool showCatalog;

    public WorkTask? SelectedTask { get; private set; }
    public bool SelectionChanged { get; private set; }
    internal bool ProjectRailScrollsForDiagnostics => projectScroller.ScrollableHeight > 0;
    internal int VisibleProjectRowsForDiagnostics => projectList.Children.Count;
    internal int VisibleTaskRowsForDiagnostics => list.Children.Count + suggestHost.Children.Count;

    public TasksWindow(MainWindow owner, WorkTask? current = null)
    {
        this.owner = owner;
        this.current = current;
        Owner = owner; Title = L.T("tasks.title"); Width = 720; Height = 740; MinWidth = 560; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowStyle = WindowStyle.None; AllowsTransparency = true;
        Background = Brushes.Transparent; ResizeMode = ResizeMode.CanResizeWithGrip;
        SetResourceReference(ForegroundProperty, "Ink");
        Dialogs.KeepAboveChrome(this);

        finished = owner.Store.Sessions()
            .Where(s => s.TaskId is not null && s.Phase == Phase.Focus && s.Outcome == Outcome.Completed)
            .GroupBy(s => s.TaskId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        lastFocus = owner.Store.Sessions()
            .Where(s => s.TaskId is not null && s.Phase == Phase.Focus)
            .GroupBy(s => s.TaskId!.Value)
            .ToDictionary(group => group.Key, group => group.Max(session => session.Ended));

        var page = new Grid { Margin = new Thickness(18, 15, 18, 14) };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition());
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.Children.Add(Intro());

        var workspace = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        workspace.ColumnDefinitions.Add(new ColumnDefinition());
        projectRailHost.Children.Add(ProjectRail());
        projectRailHost.Width = 0;
        projectRailHost.Visibility = Visibility.Collapsed;
        workspace.Children.Add(projectRailHost);
        var tasks = TaskArea();
        Grid.SetColumn(tasks, 1);
        workspace.Children.Add(tasks);
        Grid.SetRow(workspace, 1);
        page.Children.Add(workspace);

        var footer = Footer();
        Grid.SetRow(footer, 2);
        page.Children.Add(footer);
        Content = page;

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || e.Handled) return;
            if (editing is not null) { editing = null; Render(); }
            else if (creating) { creating = false; RenderCreate(); }
            else Close();
            e.Handled = true;
        };
        Loaded += (_, _) =>
        {
            if (current is null && !creating) search.Focus();
            else if (creating) newName.Focus();
        };
        Render();
        Dialogs.Modalize(this);
        Dialogs.LiftAboveChrome(this);
    }

    // ------------------------------------------------------------------ layout

    private UIElement Intro()
    {
        var stack = new StackPanel();
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var words = new StackPanel();
        var heading = Dialogs.Heading(L.T("tasks.heading"));
        heading.FontSize = 24;
        heading.Margin = new Thickness(0, 0, 0, 2);
        words.Children.Add(heading);
        int streak = FocusStreak();
        var score = Text(streak <= 0 ? L.T("tasks.scoreFresh") : L.T("tasks.scoreStreak", streak), 12, "Muted", FontWeights.Bold);
        score.Margin = new Thickness(0, 0, 0, 4);
        words.Children.Add(score);
        var hint = Text(L.T("tasks.hint"), 11.5, "Muted");
        hint.TextWrapping = TextWrapping.Wrap;
        words.Children.Add(hint);
        line.Children.Add(words);

        var free = Dialogs.Button(current is null ? L.T("tasks.freeFocusCurrent") : L.T("tasks.freeFocusChoose"), () => Choose(null));
        free.ToolTip = L.T("tasks.freeFocusTip");
        free.Padding = new Thickness(11, 6, 11, 6);
        free.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(free, 1);
        line.Children.Add(free);
        stack.Children.Add(line);

        if (current is { } mission)
        {
            var missionCard = new Border
            {
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(14, 12, 12, 12),
                BorderThickness = new Thickness(2)
            };
            missionCard.SetResourceReference(Border.BackgroundProperty, "Ink");
            missionCard.SetResourceReference(Border.BorderBrushProperty, "Ink");
            var missionGrid = new Grid();
            missionGrid.ColumnDefinitions.Add(new ColumnDefinition());
            missionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var missionWords = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var label = Text(L.T("tasks.continueLabel"), 10.5, "Paper", FontWeights.Bold);
            label.Opacity = .75;
            label.Margin = new Thickness(0, 0, 0, 4);
            missionWords.Children.Add(label);
            var title = Text(mission.Name, 18, "Paper", FontWeights.Black);
            title.TextWrapping = TextWrapping.Wrap;
            missionWords.Children.Add(title);
            if (mission.Project != NoProject)
            {
                var project = Text(ProjectLabel(mission.Project), 12, "Paper");
                project.Opacity = .8;
                project.Margin = new Thickness(0, 3, 0, 0);
                missionWords.Children.Add(project);
            }
            missionGrid.Children.Add(missionWords);
            var continueBtn = Dialogs.Button(L.T("tasks.continue"), () => Choose(mission));
            continueBtn.Padding = new Thickness(18, 10, 18, 10);
            continueBtn.FontSize = 14;
            continueBtn.FontWeight = FontWeights.Bold;
            continueBtn.SetResourceReference(BackgroundProperty, "Paper");
            continueBtn.SetResourceReference(ForegroundProperty, "Ink");
            continueBtn.SetResourceReference(BorderBrushProperty, "Paper");
            continueBtn.VerticalAlignment = VerticalAlignment.Center;
            continueBtn.ToolTip = L.T("tasks.continueTip");
            Grid.SetColumn(continueBtn, 1);
            missionGrid.Children.Add(continueBtn);
            missionCard.Child = missionGrid;
            stack.Children.Add(missionCard);
        }
        return stack;
    }

    private int FocusStreak()
    {
        var daily = Reports.Daily(owner.Store.Sessions(), TimeZoneInfo.Local);
        return Reports.Streak(daily, DateOnly.FromDateTime(DateTime.Now));
    }

    /// <summary>Optional project rail — off by default so the mission list owns the window.</summary>
    private UIElement ProjectRail()
    {
        var frame = new Border { Padding = new Thickness(10), BorderThickness = new Thickness(1), Width = 168, Margin = new Thickness(0, 0, 12, 0) };
        frame.SetResourceReference(Border.BackgroundProperty, "Raised");
        frame.SetResourceReference(Border.BorderBrushProperty, "Edge");
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new DockPanel { LastChildFill = true, Margin = new Thickness(2, 0, 0, 8) };
        var add = Quiet(L.T("tasks.newProject"), CreateProject);
        add.FontSize = 10.5;
        add.FontWeight = FontWeights.Bold;
        add.Padding = new Thickness(6, 2, 6, 2);
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
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        root.Children.Add(ActionBar());
        createHost.Margin = new Thickness(0, 8, 0, 0);
        createHost.Visibility = Visibility.Collapsed;
        Grid.SetRow(createHost, 1);
        root.Children.Add(createHost);
        Grid.SetRow(frequentHost, 2);
        root.Children.Add(frequentHost);
        Grid.SetRow(suggestHost, 3);
        root.Children.Add(suggestHost);
        var scroller = new ScrollViewer
        {
            Content = list,
            Focusable = false,
            Margin = new Thickness(0, 8, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroller, 4);
        root.Children.Add(scroller);
        return root;
    }

    private UIElement ActionBar()
    {
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });

        var create = Primary(Dialogs.Button(L.T("tasks.createToggle"), () =>
        {
            creating = !creating;
            RenderCreate();
            if (creating) { newName.Focus(); newName.SelectAll(); }
        }));
        create.Padding = new Thickness(12, 7, 12, 7);
        create.Margin = new Thickness(0, 0, 8, 0);
        line.Children.Add(create);

        var projectBtn = Dialogs.Button(L.T("tasks.newProject"), CreateProject);
        projectBtn.Padding = new Thickness(10, 7, 10, 7);
        projectBtn.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(projectBtn, 1);
        line.Children.Add(projectBtn);

        projectFilter.ToolTip = L.T("tasks.filterProjectTip");
        projectFilter.SelectionChanged += (_, _) =>
        {
            if (syncingFilter) return;
            if (projectFilter.SelectedItem is string label)
            {
                if (label == Ui("TODAS", "ALL")) Filter(null);
                else if (label == ProjectLabel(NoProject)) Filter(NoProject);
                else Filter(label);
            }
        };
        projectFilter.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(projectFilter, 2);
        line.Children.Add(projectFilter);

        search.SetValue(AutomationProperties.NameProperty, Ui("Buscar tareas", "Search tasks"));
        search.ToolTip = Ui("Buscar por tarea o proyecto", "Search by task or project");
        search.TextChanged += (_, _) => RenderList();
        var field = WithPlaceholder(search, Ui("Buscar…", "Search…"));
        Grid.SetColumn(field, 3);
        line.Children.Add(field);
        return line;
    }

    private void RenderCreate()
    {
        createHost.Child = null;
        createHost.Visibility = creating ? Visibility.Visible : Visibility.Collapsed;
        if (!creating) return;
        createHost.Padding = new Thickness(10);
        createHost.BorderThickness = new Thickness(1);
        createHost.SetResourceReference(Border.BackgroundProperty, "Raised");
        createHost.SetResourceReference(Border.BorderBrushProperty, "Ink");
        createHost.Child = AddBarBody();
    }

    /// <summary>Name, project, how many pomodoros you expect. Enter adds it.</summary>
    private UIElement AddBarBody()
    {
        var stack = new StackPanel();
        var label = Caption(L.T("tasks.newTask"));
        label.Margin = new Thickness(1, 0, 0, 5);
        stack.Children.Add(label);

        newName.SetValue(AutomationProperties.NameProperty, L.T("tasks.newTaskName"));
        newName.KeyDown -= NewNameEnter;
        newName.KeyDown += NewNameEnter;
        var nameLine = new Grid();
        nameLine.ColumnDefinitions.Add(new ColumnDefinition());
        nameLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var field = WithPlaceholder(newName, L.T("tasks.newTaskPlaceholder"));
        field.Margin = new Thickness(0, 0, 8, 7);
        nameLine.Children.Add(field);
        var add = Primary(Dialogs.Button(L.T("tasks.addMission"), Add));
        add.SetValue(AutomationProperties.NameProperty, L.T("common.add"));
        add.ToolTip = L.T("common.add");
        add.Padding = new Thickness(14, 6, 14, 6);
        add.Margin = new Thickness(0, 0, 0, 7);
        Grid.SetColumn(add, 1);
        nameLine.Children.Add(add);
        stack.Children.Add(nameLine);

        newProject.ToolTip = L.T("tasks.taskProject");
        newProject.SetValue(AutomationProperties.NameProperty, L.T("tasks.newTaskProject"));
        newEstimate.ToolTip = L.T("tasks.estimateHelp");
        stack.Children.Add(FieldLine(newProject, newEstimate, null));
        return stack;
    }

    private void NewNameEnter(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Add();
        e.Handled = true;
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
        var line = new DockPanel { Margin = new Thickness(0, 9, 0, 0), LastChildFill = true };
        taskSummary.FontSize = 10.5;
        taskSummary.FontWeight = FontWeights.Bold;
        taskSummary.VerticalAlignment = VerticalAlignment.Center;
        taskSummary.SetResourceReference(ForegroundProperty, "Muted");
        line.Children.Add(taskSummary);
        var keys = Text(L.T("tasks.keys"), 11, "Muted");
        DockPanel.SetDock(keys, Dock.Right);
        line.Children.Add(keys);
        return line;
    }

    // ------------------------------------------------------------------ drawing

    private bool syncingFilter;

    private void Render()
    {
        listIndex.Clear();
        for (int i = 0; i < owner.Settings.Tasks.Count; i++)
            listIndex[owner.Settings.Tasks[i].Id] = i;

        var projects = Projects();
        if (filter is not null && filter != NoProject && !projects.Contains(filter)) filter = null;

        var keep = newProject.SelectedItem as string;
        var choices = new[] { NoProject }.Concat(projects).ToList();
        newProject.ItemsSource = choices;
        newProject.ItemTemplate = ProjectTemplate();
        newProject.SelectedItem = keep is not null && choices.Contains(keep) ? keep : NoProject;

        syncingFilter = true;
        var filterChoices = new List<string> { Ui("TODAS", "ALL") };
        filterChoices.AddRange(projects);
        if (owner.Settings.Tasks.Any(task => !task.Template && task.Project == NoProject))
            filterChoices.Add(ProjectLabel(NoProject));
        projectFilter.ItemsSource = filterChoices;
        projectFilter.SelectedItem = filter is null ? filterChoices[0]
            : filter == NoProject ? ProjectLabel(NoProject)
            : filter;
        syncingFilter = false;

        RenderProjects(projects);
        RenderCreate();
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
        suggestHost.Children.Clear();
        string query = search.Text.Trim();
        var shown = owner.Settings.Tasks.Where(task => !task.Template && (filter is null || task.Project == filter)
            && (query.Length == 0 || task.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || ProjectLabel(task.Project).Contains(query, StringComparison.CurrentCultureIgnoreCase))).ToList();
        var pending = shown.Where(task => !task.Done)
            .OrderByDescending(task => current?.Id == task.Id)
            .ThenByDescending(Recency)
            .ThenBy(task => listIndex.GetValueOrDefault(task.Id, int.MaxValue))
            .ThenBy(task => task.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var done = shown.Where(task => task.Done)
            .OrderByDescending(Recency)
            .ThenBy(task => listIndex.GetValueOrDefault(task.Id, int.MaxValue))
            .ToList();

        taskSummary.Text = filter is null
            ? L.T("tasks.pending", pending.Count)
            : $"{Short(ProjectLabel(filter)).ToUpper(Strings.Culture)} · {pending.Count:00}";

        bool browsing = query.Length > 0 || filter is not null || editing is not null;
        if (!browsing && pending.Count > 0)
        {
            var picks = pending.Where(task => current is null || task.Id != current.Id).Take(4).ToList();
            if (picks.Count > 0)
            {
                var label = Caption(L.T("tasks.pickNext"));
                label.Margin = new Thickness(1, 4, 0, 8);
                suggestHost.Children.Add(label);
                foreach (var task in picks)
                    suggestHost.Children.Add(MissionCard(task));
            }
        }

        if (pending.Count == 0)
        {
            list.Children.Add(Empty(query.Length > 0 ? Ui("SIN RESULTADOS", "NO MATCHES") : shown.Count > 0 ? L.T("tasks.nothingPending")
                : filter is null ? L.T("tasks.emptyAll") : L.T("tasks.emptyProject")));
        }
        else
        {
            var catalog = Quiet(showCatalog ? L.T("tasks.hideCatalog", pending.Count) : L.T("tasks.showCatalog", pending.Count), () =>
            {
                showCatalog = !showCatalog;
                RenderList();
            });
            catalog.Margin = new Thickness(-8, 4, 0, 6);
            catalog.HorizontalAlignment = HorizontalAlignment.Left;
            list.Children.Add(catalog);
            if (showCatalog || browsing)
            {
                foreach (var task in pending)
                    list.Children.Add(task == editing ? Editor(task) : Row(task));
            }
        }

        if (done.Count == 0) return;
        var toggle = Quiet(L.T("tasks.doneSection", showDone ? "▾" : "▸", done.Count), () => { showDone = !showDone; RenderList(); });
        toggle.Margin = new Thickness(-8, 12, 0, 2);
        toggle.HorizontalAlignment = HorizontalAlignment.Left;
        toggle.ToolTip = showDone ? L.T("tasks.hideDone") : L.T("tasks.showDone");
        list.Children.Add(toggle);
        if (showDone)
            foreach (var task in done) list.Children.Add(task == editing ? Editor(task) : Row(task));
    }

    /// <summary>A big next-mission tile: one click starts focus.</summary>
    private UIElement MissionCard(WorkTask task)
    {
        var card = new Border
        {
            Padding = new Thickness(12, 10, 10, 10),
            Margin = new Thickness(0, 0, 0, 8),
            BorderThickness = new Thickness(1.5),
            Cursor = Cursors.Hand
        };
        card.SetResourceReference(Border.BackgroundProperty, "Raised");
        card.SetResourceReference(Border.BorderBrushProperty, "Edge");
        card.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase) { Choose(task); e.Handled = true; } };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = Text(task.Name, 15, "Ink", FontWeights.Bold);
        name.TextWrapping = TextWrapping.Wrap;
        words.Children.Add(name);
        var meta = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        if (task.Project != NoProject) meta.Children.Add(ProjectTag(task.Project));
        meta.Children.Add(Progress(finished.GetValueOrDefault(task.Id), task.Estimate));
        words.Children.Add(meta);
        grid.Children.Add(words);
        var focus = Primary(Dialogs.Button(L.T("tasks.focus"), () => Choose(task)));
        focus.Padding = new Thickness(14, 8, 14, 8);
        focus.FontSize = 12;
        focus.ToolTip = L.T("tasks.focusTip");
        Grid.SetColumn(focus, 1);
        grid.Children.Add(focus);
        card.Child = grid;
        return card;
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
            var focus = Primary(Dialogs.Button(L.T("tasks.focus"), () => Choose(task)));
            focus.SetValue(AutomationProperties.NameProperty, L.T("tasks.focus"));
            focus.Padding = new Thickness(10, 5, 10, 5);
            focus.FontSize = 11;
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
        task.Touch();
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
        if (task?.Id != current?.Id)
        {
            task?.Touch();
            SelectedTask = task;
            SelectionChanged = true;
        }
        Close();
    }

    private void Save()
    {
        owner.SaveState();
        Render();
    }

    /// <summary>Most recently used projects first; brand-new empty projects stay near the top of the named list.</summary>
    private List<string> Projects()
    {
        var named = owner.Settings.Projects
            .Concat(owner.Settings.Tasks.Select(task => task.Project))
            .Where(project => !string.IsNullOrWhiteSpace(project) && project != NoProject)
            .Distinct()
            .ToList();
        return named
            .OrderByDescending(ProjectRecency)
            .ThenByDescending(project =>
            {
                int index = owner.Settings.Projects.IndexOf(project);
                return index < 0 ? int.MinValue : index;
            })
            .ThenBy(project => project, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private DateTimeOffset Recency(WorkTask task)
    {
        if (task.LastUsedUtc is { } used) return used;
        if (lastFocus.TryGetValue(task.Id, out var focused)) return focused;
        if (task.CreatedUtc is { } created) return created;
        // Older saves: keep list order (Insert puts newest at 0).
        int index = listIndex.GetValueOrDefault(task.Id, int.MaxValue);
        return DateTimeOffset.UnixEpoch.AddTicks(Math.Max(0, 1_000_000 - index));
    }

    private DateTimeOffset ProjectRecency(string project)
    {
        var stamps = owner.Settings.Tasks
            .Where(task => !task.Template && task.Project == project)
            .Select(Recency)
            .ToList();
        return stamps.Count == 0 ? DateTimeOffset.MinValue : stamps.Max();
    }

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
        var badge = new Border
        {
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 0, 4, 0),
            ToolTip = L.T("tasks.currentTip")
        };
        badge.SetResourceReference(Border.BackgroundProperty, "Ink");
        var label = Text(L.T("tasks.current"), 10.5, "Paper", FontWeights.Bold);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        badge.Child = label;
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
