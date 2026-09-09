using System.Windows;
using System.Windows.Controls;
using PomoDock.Core;

namespace PomoDock.App;

public sealed class TasksWindow : Window
{
    public WorkTask? SelectedTask { get; private set; }
    public bool SelectionChanged { get; private set; }
    public TasksWindow(MainWindow owner)
    {
        Owner = owner; Title = "POMODOCK / Tareas y proyectos"; Width = 640; Height = 700; MinWidth = 440; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = System.Windows.Media.Brushes.Transparent; ResizeMode = ResizeMode.CanResizeWithGrip;
        var panel = new DockPanel { Margin = new Thickness(22) }; Content = panel;
        var heading = Dialogs.Heading("QUÉ VAS A HACER."); DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var tabs = new TabControl(); panel.Children.Add(tabs);
        var tasks = new TabItem { Header = "TAREAS" }; var projects = new TabItem { Header = "PROYECTOS" }; var templates = new TabItem { Header = "PLANTILLAS" }; tabs.Items.Add(tasks); tabs.Items.Add(projects); tabs.Items.Add(templates);
        var taskPanel = new DockPanel { Margin = new Thickness(10) }; tasks.Content = taskPanel;
        var form = new StackPanel(); DockPanel.SetDock(form, Dock.Top); taskPanel.Children.Add(form);
        form.Children.Add(new TextBlock { Text = "Nueva tarea" }); var name = new TextBox(); form.Children.Add(name);
        var project = new ComboBox { IsEditable = false }; form.Children.Add(project);
        var estimate = new TextBox { Text = "1", ToolTip = "Pomodoros estimados" }; form.Children.Add(new TextBlock { Text = "Pomodoros estimados" }); form.Children.Add(estimate);
        var list = new ListBox();
        var templateList = new ListBox();
        void Refresh()
        {
            project.ItemsSource = new[] { "Sin proyecto" }.Concat(owner.Settings.Projects).Distinct().ToList(); if (project.SelectedIndex < 0) project.SelectedIndex = 0;
            list.Items.Clear();
            foreach (var task in owner.Settings.Tasks.Where(t => !t.Template).OrderBy(t => t.Done))
            {
                int completed = owner.Store.Sessions().Count(s => s.TaskId == task.Id && s.Phase == Phase.Focus && s.Outcome == Outcome.Completed);
                list.Items.Add(new ListBoxItem { Content = $"{(task.Done ? "✓" : "○")} {task.Name}\n{task.Project}   ·   {completed}/{task.Estimate} pomodoros", Tag = task, Padding = new Thickness(8), HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }
        }
        var add = Dialogs.Button("+ AÑADIR", () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || !int.TryParse(estimate.Text, out int value) || value < 1 || value > 999) { Dialogs.Alert(this, "TAREA INVÁLIDA", "Escribe un nombre y una estimación entre 1 y 999."); return; }
            owner.Settings.Tasks.Add(new() { Name = name.Text.Trim(), Project = project.SelectedItem?.ToString() ?? "Sin proyecto", Estimate = value }); name.Clear(); Refresh(); owner.SaveState();
        }); form.Children.Add(add);
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(actions, Dock.Bottom); taskPanel.Children.Add(actions);
        WorkTask? Current() => (list.SelectedItem as ListBoxItem)?.Tag as WorkTask;
        actions.Children.Add(Dialogs.Button("ENFOCAR", () => { if (Current() is { } t) { SelectedTask = t; SelectionChanged = true; Close(); } }));
        actions.Children.Add(Dialogs.Button("LIBRE", () => { SelectedTask = null; SelectionChanged = true; Close(); }));
        actions.Children.Add(Dialogs.Button("✓ / ○", () => { if (Current() is { } t) { t.Done = !t.Done; Refresh(); owner.SaveState(); } }));
        actions.Children.Add(Dialogs.Button("EDITAR", () => { if (Current() is { } t) { var text = Dialogs.Prompt(this, "EDITAR TAREA", "Nombre", t.Name); if (!string.IsNullOrWhiteSpace(text)) t.Name = text; Refresh(); owner.SaveState(); } }));
        actions.Children.Add(Dialogs.Button("PLANTILLA", () => { if (Current() is { } t) { owner.Settings.Tasks.Add(new() { Name = t.Name, Project = t.Project, Estimate = t.Estimate, Template = true }); owner.SaveState(); RefreshTemplates(); } }));
        taskPanel.Children.Add(list); Refresh();
        var projectPanel = new StackPanel { Margin = new Thickness(14) }; projects.Content = projectPanel;
        projectPanel.Children.Add(new TextBlock { Text = "Los proyectos organizan tu trabajo. Las sesiones conservan el nombre que tenían al registrarse.", Margin = new Thickness(0, 0, 0, 12) });
        var projectName = new TextBox(); projectPanel.Children.Add(projectName); var projectList = new ListBox { MinHeight = 160 };
        void RefreshProjects() => projectList.ItemsSource = owner.Settings.Projects.ToList();
        projectPanel.Children.Add(Dialogs.Button("+ CREAR PROYECTO", () => { var p = projectName.Text.Trim(); if (p.Length > 0 && p != "Sin proyecto" && !owner.Settings.Projects.Contains(p)) { owner.Settings.Projects.Add(p); projectName.Clear(); RefreshProjects(); Refresh(); owner.SaveState(); } }));
        projectPanel.Children.Add(projectList); RefreshProjects();
        var templatePanel = new DockPanel { Margin = new Thickness(14) }; templates.Content = templatePanel;
        void RefreshTemplates() => templateList.ItemsSource = owner.Settings.Tasks.Where(t => t.Template).ToList();
        var use = Dialogs.Button("CREAR TAREA DESDE PLANTILLA", () => { if (templateList.SelectedItem is WorkTask t) { owner.Settings.Tasks.Add(new() { Name = t.Name, Project = t.Project, Estimate = t.Estimate }); Refresh(); owner.SaveState(); tabs.SelectedItem = tasks; } });
        DockPanel.SetDock(use, Dock.Bottom); templatePanel.Children.Add(use); templatePanel.Children.Add(templateList); RefreshTemplates(); Dialogs.Modalize(this);
    }
}
