using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PomoDock.Core;
using Microsoft.Win32;

namespace PomoDock.App;

public sealed class ReportWindow : Window
{
    private readonly MainWindow owner;
    private readonly StackPanel content = new();
    private readonly DatePicker from = new() { SelectedDate = DateTime.Today.AddDays(-6), Width = 130 };
    private readonly DatePicker to = new() { SelectedDate = DateTime.Today, Width = 130 };
    private readonly ComboBox project = new() { MinWidth = 145 };
    private List<Session> filtered = [];
    public ReportWindow(MainWindow owner)
    {
        this.owner = owner; Owner = owner; Title = "POMODOCK / Report"; Width = 790; Height = 850; MinWidth = 470; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterOwner; WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.CanResizeWithGrip;
        var shell = new DockPanel { Margin = new Thickness(24) }; Content = shell;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); shell.Children.Add(top);
        top.Children.Add(Dialogs.Heading("LO QUE MIDES,\nLO PUEDES MEJORAR."));
        var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) }; top.Children.Add(filters);
        var range = new ComboBox { Width = 125, ItemsSource = new[] { "Hoy", "7 días", "Este mes", "Este año", "Todo" }, SelectedIndex = 1, Margin = new Thickness(0, 0, 8, 0) };
        filters.Children.Add(range); filters.Children.Add(from); filters.Children.Add(new TextBlock { Text = " → ", VerticalAlignment = VerticalAlignment.Center }); filters.Children.Add(to);
        project.ItemsSource = new[] { "Todos los proyectos" }.Concat(owner.Store.Sessions().Select(s => s.Project).Distinct().Order()); project.SelectedIndex = 0; project.Margin = new Thickness(8, 0, 0, 0); filters.Children.Add(project);
        var export = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) }; top.Children.Add(export);
        export.Children.Add(Dialogs.Button("CSV", ExportCsv)); export.Children.Add(Dialogs.Button("BACKUP JSON", ExportJson)); export.Children.Add(Dialogs.Button("IMPORTAR SESIONES", Import));
        var scroller = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; shell.Children.Add(scroller);
        range.SelectionChanged += (_, _) =>
        {
            var today = DateTime.Today;
            from.SelectedDate = range.SelectedIndex switch { 0 => today, 1 => today.AddDays(-6), 2 => new(today.Year, today.Month, 1), 3 => new(today.Year, 1, 1), _ => owner.Store.Sessions().Select(s => s.Started.LocalDateTime.Date).DefaultIfEmpty(today).Min() };
            to.SelectedDate = today; Refresh();
        };
        from.SelectedDateChanged += (_, _) => Refresh(); to.SelectedDateChanged += (_, _) => Refresh(); project.SelectionChanged += (_, _) => Refresh();
        Refresh(); Dialogs.Modalize(this);
    }
    private void Refresh()
    {
        content.Children.Clear();
        if (from.SelectedDate is null || to.SelectedDate is null || from.SelectedDate > to.SelectedDate)
        { content.Children.Add(new TextBlock { Text = "Selecciona un intervalo válido." }); return; }
        var start = DateOnly.FromDateTime(from.SelectedDate.Value); var end = DateOnly.FromDateTime(to.SelectedDate.Value);
        var sessions = owner.Store.Sessions().Where(s => s.Phase == Phase.Focus && (project.SelectedIndex <= 0 || s.Project == project.SelectedItem?.ToString())).ToList();
        filtered = sessions.Where(s => Reports.MinutesIn(s, start, end, TimeZoneInfo.Local) > 0).ToList();
        var daily = Reports.Daily(sessions, TimeZoneInfo.Local);
        double total = daily.Where(p => p.Key >= start && p.Key <= end).Sum(p => p.Value);
        int span = end.DayNumber - start.DayNumber + 1;
        double previous = daily.Where(p => p.Key >= start.AddDays(-span) && p.Key < start).Sum(p => p.Value);
        int complete = filtered.Count(s => s.Outcome == Outcome.Completed && DateOnly.FromDateTime(s.Ended.LocalDateTime) >= start && DateOnly.FromDateTime(s.Ended.LocalDateTime) <= end);
        var stats = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 18) };
        stats.Children.Add(Stat($"{(int)total / 60:00}:{(int)total % 60:00}", "HORAS : MINUTOS"));
        stats.Children.Add(Stat(complete.ToString(), "SESIONES COMPLETAS"));
        stats.Children.Add(Stat(daily.Count(p => p.Key >= start && p.Key <= end && p.Value > 0).ToString(), "DÍAS CON ENFOQUE"));
        content.Children.Add(stats);
        string comparison = previous > 0 ? $"{(total - previous) / previous * 100:+0;-0;0}% frente a los {span} días anteriores ({previous:0} min)." : "Sin actividad en el periodo anterior para comparar.";
        content.Children.Add(new TextBlock { Text = comparison, FontSize = 12, Margin = new Thickness(0, 0, 0, 20) });
        content.Children.Add(Section("01 / ENFOQUE EN EL TIEMPO"));
        var bins = new List<(string Label, double Minutes)>();
        // Bound visual work for long histories while preserving all minutes.
        if (span <= 31) for (var d = start; d <= end; d = d.AddDays(1)) bins.Add((d.ToString("dd/MM"), daily.GetValueOrDefault(d)));
        else
        {
            int binDays = (int)Math.Ceiling(span / 24d);
            for (var d = start; d <= end; d = d.AddDays(binDays)) { var last = d.AddDays(binDays - 1); if (last > end) last = end; bins.Add(($"{d:dd/MM}–{last:dd/MM}", daily.Where(p => p.Key >= d && p.Key <= last).Sum(p => p.Value))); }
        }
        content.Children.Add(new BarChart(bins) { Height = 200, Margin = new Thickness(0, 8, 0, 16) });
        content.Children.Add(Section("02 / A DÓNDE FUE TU TIEMPO"));
        var breakdown = filtered.GroupBy(s => s.Project).Select(g => (Name: g.Key, Minutes: g.Sum(s => Reports.MinutesIn(s, start, end, TimeZoneInfo.Local)))).OrderByDescending(g => g.Minutes).ToList();
        foreach (var item in breakdown)
        {
            var line = new Grid { Margin = new Thickness(0, 6, 0, 6) }; line.ColumnDefinitions.Add(new()); line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            line.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold });
            var number = new TextBlock { Text = $"{item.Minutes:0} min · {(total > 0 ? item.Minutes / total * 100 : 0):0}%", FontFamily = new FontFamily("Consolas") }; Grid.SetColumn(number, 1); line.Children.Add(number); content.Children.Add(line);
        }
        if (breakdown.Count == 0) content.Children.Add(new TextBlock { Text = "Tu primera sesión aparecerá aquí.", Margin = new Thickness(0, 12, 0, 12) });
        content.Children.Add(Section("03 / CONSTANCIA Y PLANIFICACIÓN"));
        var ended = filtered.Where(s => DateOnly.FromDateTime(s.Ended.LocalDateTime) >= start && DateOnly.FromDateTime(s.Ended.LocalDateTime) <= end).ToList();
        content.Children.Add(new TextBlock { Text = $"{ended.Count(s => s.Outcome != Outcome.Completed)} sesiones parciales · {ended.Sum(s => s.Pauses)} pausas registradas\nRacha actual: {Reports.Streak(daily, DateOnly.FromDateTime(DateTime.Now))} días.\nLas pausas se cuentan en la sesión que termina dentro del periodo.", FontSize = 12, Margin = new Thickness(0, 8, 0, 14) });
        foreach (var task in owner.Settings.Tasks.Where(t => !t.Template && filtered.Any(s => s.TaskId == t.Id)))
        {
            var actual = sessions.Where(s => s.TaskId == task.Id).Sum(s => s.Seconds) / 60;
            content.Children.Add(new TextBlock { Text = $"{task.Name}: {actual:0} min acumulados / {task.Estimate * owner.Settings.FocusMinutes} min estimados al ritmo actual", FontSize = 12, Margin = new Thickness(0, 3, 0, 6) });
        }
        content.Children.Add(Section("04 / TUS SESIONES"));
        content.Children.Add(new TextBlock { Text = "Tiempo real confirmado, sin descansos ni pausas. El historial muestra las sesiones con enfoque dentro del intervalo.", FontSize = 11, Margin = new Thickness(0, 4, 0, 12) });
        foreach (var session in filtered.Take(200))
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var edit = Dialogs.Button("EDITAR", () => Edit(session)); DockPanel.SetDock(edit, Dock.Right); row.Children.Add(edit);
            row.Children.Add(new TextBlock { Text = $"{session.Started.LocalDateTime:dd MMM HH:mm}  ·  {Reports.MinutesIn(session, start, end, TimeZoneInfo.Local):0.#} min  ·  {(session.Outcome == Outcome.Completed ? "completa" : "parcial")}\n{session.Project} / {session.Task}{(session.OriginalSeconds.HasValue ? "  [corregida]" : "")}", FontSize = 12 });
            content.Children.Add(row);
        }
        if (filtered.Count > 200) content.Children.Add(new TextBlock { Text = "Se muestran las 200 sesiones más recientes. Reduce el intervalo o exporta el historial completo." });
    }
    private static FrameworkElement Stat(string value, string label)
    {
        var stack = new StackPanel { Margin = new Thickness(12) }; stack.Children.Add(new TextBlock { Text = value, FontFamily = new FontFamily("Consolas"), FontSize = 32, FontWeight = FontWeights.Bold }); stack.Children.Add(new TextBlock { Text = label, FontSize = 9 });
        var border = new Border { BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0), Child = stack }; border.SetResourceReference(BorderBrushProperty, "Line"); return border;
    }
    private static TextBlock Section(string text) => new() { Text = text, FontWeight = FontWeights.Black, FontSize = 13, Margin = new Thickness(0, 18, 0, 8) };
    private void Edit(Session session)
    {
        var projectName = Dialogs.Prompt(this, "CLASIFICAR SESIÓN", "Proyecto", session.Project); if (projectName is null) return;
        var taskName = Dialogs.Prompt(this, "CLASIFICAR SESIÓN", "Tarea", session.Task); if (taskName is null) return;
        var minutes = Dialogs.Prompt(this, "CORREGIR DURACIÓN", "Minutos reales (la corrección queda identificada)", (session.Seconds / 60).ToString("0.###", CultureInfo.CurrentCulture)); if (minutes is null) return;
        if (!double.TryParse(minutes, out var value) || !double.IsFinite(value) || value <= 0 || value > 1440) { Dialogs.Alert(this, "DURACIÓN INVÁLIDA", "La duración debe estar entre 0 y 1440 minutos."); return; }
        session.Project = string.IsNullOrWhiteSpace(projectName) ? "Sin proyecto" : projectName; session.Task = string.IsNullOrWhiteSpace(taskName) ? "Enfoque libre" : taskName;
        if (Math.Abs(value * 60 - session.Seconds) > 0.1)
        {
            session.OriginalSeconds ??= session.Seconds;
            session.Segments = [new(session.Ended.AddMinutes(-value), session.Ended)]; session.Started = session.Segments[0].Start;
            session.Note = "Duración corregida manualmente. Se asigna un intervalo continuo anterior a la hora de fin.";
        }
        // Reclassification must not keep attributing time to the old task ID.
        session.TaskId = owner.Settings.Tasks.FirstOrDefault(t => t.Name == session.Task && t.Project == session.Project && !t.Template)?.Id;
        owner.Store.Save(session); Refresh();
    }
    private void ExportCsv()
    {
        var dialog = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "pomo-dock-report.csv" };
        if (dialog.ShowDialog(this) == true) { Store.ExportCsv(dialog.FileName, filtered); owner.Status("CSV EXPORTADO · Incluye la duración completa de las sesiones seleccionadas."); }
    }
    private void ExportJson()
    {
        var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = "pomo-dock-backup.json" };
        if (dialog.ShowDialog(this) == true) { owner.SaveState(); owner.Store.ExportJson(dialog.FileName); owner.Status("BACKUP EXPORTADO · Historial y configuración."); }
    }
    private void Import()
    {
        var dialog = new OpenFileDialog { Filter = "PomoDock JSON|*.json" };
        if (dialog.ShowDialog(this) == true)
        {
            try { int count = owner.Store.ImportJson(dialog.FileName); owner.Status($"IMPORTADAS {count} SESIONES · Se conservaron las existentes."); Refresh(); }
            catch (Exception ex) { Dialogs.Alert(this, "IMPORTACIÓN FALLIDA", "No se pudo importar: " + ex.Message); }
        }
    }
}

internal sealed class BarChart(List<(string Label, double Minutes)> bins) : FrameworkElement
{
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var ink = (Brush)FindResource("Ink"); var muted = (Brush)FindResource("Muted");
        double left = 44, bottom = ActualHeight - 30, width = Math.Max(1, ActualWidth - left - 4), height = bottom - 24;
        double max = Math.Max(1, Math.Ceiling(bins.Select(b => b.Minutes).DefaultIfEmpty(0).Max() / 10) * 10);
        void Text(string value, double x, double y, Brush brush) => dc.DrawText(new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Consolas"), 10, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
        Text("MIN", 0, 0, muted);
        for (int i = 0; i <= 2; i++) { double y = bottom - height * i / 2; dc.DrawLine(new Pen(muted, 0.4), new(left, y), new(ActualWidth, y)); Text((max * i / 2).ToString("0"), 0, y - 6, muted); }
        int count = Math.Max(1, bins.Count); double cell = width / count;
        for (int i = 0; i < bins.Count; i++)
        {
            double h = height * bins[i].Minutes / max;
            dc.DrawRectangle(ink, null, new Rect(left + i * cell + 2, bottom - h, Math.Max(1, cell - 4), h));
            int every = Math.Max(1, (int)Math.Ceiling(62 / cell));
            if (i % every == 0) Text(bins[i].Label.Split('–')[0], left + i * cell, bottom + 8, muted);
        }
    }
    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e); var pos = e.GetPosition(this); int index = (int)((pos.X - 44) / Math.Max(1, ActualWidth - 48) * bins.Count);
        ToolTip = index >= 0 && index < bins.Count ? $"{bins[index].Label}: {bins[index].Minutes:0.#} minutos de enfoque" : null;
    }
}
