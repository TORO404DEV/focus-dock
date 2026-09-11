using System.Globalization;
using System.Text;
using System.Windows;
using PomoDock.Core;
using PomoDock.Core.Agent;

namespace PomoDock.App;

/// <summary>Bridges Core agent tools to the live MainWindow stores and timer.</summary>
internal sealed class AgentHostAdapter : IAgentHost
{
    private readonly MainWindow owner;
    private readonly Dictionary<string, Func<bool>> undos = new(StringComparer.OrdinalIgnoreCase);
    private AgentMemoryBook memory;

    public AgentHostAdapter(MainWindow owner)
    {
        this.owner = owner;
        memory = AgentMemory.Load(owner.Store);
    }

    public TimeZoneInfo TimeZone => TimeZoneInfo.Local;
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
    public AgentMemoryBook Memory => memory;
    public AgentSettings AgentSettings => owner.Settings.Agent;
    public IReadOnlyList<Session> Sessions() => owner.Store.Sessions();

    public void SaveMemory(AgentMemoryBook book)
    {
        memory = book;
        AgentMemory.Save(owner.Store, book);
    }

    public string TimerStatus()
    {
        var phase = owner.Timer.Phase.ToString();
        var running = owner.Timer.Running ? "running" : "paused";
        var remaining = TimeSpan.FromSeconds(Math.Max(0, owner.Timer.Remaining)).ToString(@"mm\:ss");
        return $"{phase} · {running} · {remaining} left · cycle {owner.Timer.CompletedFocus}";
    }

    public AgentReceipt? TimerStart(string? phase)
    {
        if (!string.IsNullOrWhiteSpace(phase) && Enum.TryParse<Phase>(phase, true, out var parsed) && parsed != owner.Timer.Phase)
            owner.AgentSetPhase(parsed);
        var wasRunning = owner.Timer.Running;
        if (!wasRunning) owner.AgentToggleTimer();
        return Rec("timer.start", wasRunning ? "El temporizador ya estaba en marcha." : "Temporizador iniciado.", () =>
        {
            if (owner.Timer.Running) owner.AgentToggleTimer();
            return true;
        });
    }

    public AgentReceipt? TimerPause()
    {
        if (!owner.Timer.Running) return Rec("timer.pause", "Ya estaba en pausa.", null);
        owner.AgentToggleTimer();
        return Rec("timer.pause", "Temporizador en pausa.", () => { if (!owner.Timer.Running) owner.AgentToggleTimer(); return true; });
    }

    public AgentReceipt? TimerSkip()
    {
        owner.AgentSkip();
        return Rec("timer.skip", "Fase saltada.", null);
    }

    public AgentReceipt? TimerSetPhase(string phase)
    {
        if (!Enum.TryParse<Phase>(phase, true, out var parsed))
            return null;
        var previous = owner.Timer.Phase;
        owner.AgentSetPhase(parsed);
        return Rec("timer.set_phase", $"Fase → {parsed}", () => { owner.AgentSetPhase(previous); return true; });
    }

    public string TasksList()
    {
        var tasks = owner.Settings.Tasks.Where(t => !t.Done).Take(40).ToList();
        if (tasks.Count == 0) return "Sin tareas abiertas.";
        return string.Join("\n", tasks.Select(t => $"• [{t.Id.ToString()[..8]}] {t.Project} / {t.Name} (est {t.Estimate})"));
    }

    public AgentReceipt? TasksAdd(string name, string? project, int? estimate)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return null;
        var task = new WorkTask
        {
            Name = name,
            Project = string.IsNullOrWhiteSpace(project) ? "Sin proyecto" : project.Trim(),
            Estimate = Math.Clamp(estimate ?? 1, 1, 99)
        };
        owner.Settings.Tasks.Insert(0, task);
        if (!owner.Settings.Projects.Contains(task.Project, StringComparer.OrdinalIgnoreCase))
            owner.Settings.Projects.Add(task.Project);
        owner.SaveState();
        return Rec("tasks.add", $"Tarea añadida: {task}", () =>
        {
            owner.Settings.Tasks.RemoveAll(t => t.Id == task.Id);
            owner.SaveState();
            return true;
        });
    }

    public AgentReceipt? TasksComplete(string id)
    {
        var task = FindTask(id);
        if (task is null) return null;
        task.Done = true;
        owner.SaveState();
        return Rec("tasks.complete", $"Completada: {task.Name}", () =>
        {
            task.Done = false;
            owner.SaveState();
            return true;
        });
    }

    public string TodoList()
    {
        var book = TodoStore.For(owner.Store).Book;
        var open = book.Items.Where(t => !t.Done).OrderBy(t => t.Order).Take(40).ToList();
        if (open.Count == 0) return "To Do vacío.";
        return string.Join("\n", open.Select(t => $"• [{t.Id.ToString()[..8]}] {t.Title}{(t.Due is { } d ? $" · {d}" : "")}"));
    }

    public AgentReceipt? TodoAdd(string title, string? due, string? priority)
    {
        var store = TodoStore.For(owner.Store);
        var line = title?.Trim() ?? "";
        if (line.Length == 0) return null;
        if (!string.IsNullOrWhiteSpace(due)) line += " " + due.Trim();
        if (!string.IsNullOrWhiteSpace(priority)) line = priority.Trim() + " " + line;
        var task = store.Add(line, DateTime.Now);
        if (task is null) return null;
        return Rec("todo.add", $"To Do: {task.Title}", () =>
        {
            store.Book.Items.RemoveAll(t => t.Id == task.Id);
            store.Save();
            return true;
        });
    }

    public AgentReceipt? TodoComplete(string id)
    {
        var store = TodoStore.For(owner.Store);
        var task = store.Book.Items.FirstOrDefault(t => t.Id.ToString().StartsWith(id, StringComparison.OrdinalIgnoreCase));
        if (task is null) return null;
        store.SetDone(task, true);
        return Rec("todo.complete", $"Hecho: {task.Title}", () =>
        {
            store.SetDone(task, false);
            return true;
        });
    }

    public string HabitsList()
    {
        var book = HabitStore.For(owner.Store).Book;
        var habits = book.Active.Take(40).ToList();
        if (habits.Count == 0) return "Sin hábitos activos.";
        var today = DateOnly.FromDateTime(DateTime.Now);
        return string.Join("\n", habits.Select(h => $"• [{h.Id.ToString()[..8]}] {h.Name} · hoy {(h.IsComplete(today) ? "✓" : "·")}"));
    }

    public AgentReceipt? HabitsTick(string id)
    {
        var store = HabitStore.For(owner.Store);
        var habit = store.Book.Habits.FirstOrDefault(h => h.Id.ToString().StartsWith(id, StringComparison.OrdinalIgnoreCase));
        if (habit is null) return null;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var before = habit.Count(today);
        habit.SetCount(today, Math.Min(habit.Target, before + 1));
        store.Save();
        return Rec("habits.tick", $"Marcado: {habit.Name}", () =>
        {
            habit.SetCount(today, before);
            store.Save();
            return true;
        });
    }

    public AgentReceipt? HabitsAdd(string name, string? cadence)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return null;
        var store = HabitStore.For(owner.Store);
        var habit = store.Add(name);
        if (!string.IsNullOrWhiteSpace(cadence) && Enum.TryParse<HabitCadence>(cadence, true, out var c))
            habit.Cadence = c;
        store.Save();
        return Rec("habits.add", $"Hábito: {habit.Name}", () =>
        {
            store.Book.Habits.RemoveAll(h => h.Id == habit.Id);
            store.Save();
            return true;
        });
    }

    public string AgendaList(int days)
    {
        days = Math.Clamp(days, 1, 60);
        var book = AgendaStore.For(owner.Store).Book;
        var from = DateOnly.FromDateTime(DateTime.Now);
        var to = from.AddDays(days);
        var lines = new List<string>();
        foreach (var ev in book.Events)
        {
            foreach (var day in ev.Starts(from, to).Take(8))
            {
                if (ev.IsCancelled(day)) continue;
                lines.Add($"• {day:dd/MM} {ev.Start:HH:mm} {ev.Title}");
            }
        }
        return lines.Count == 0 ? "Sin eventos próximos." : string.Join("\n", lines.OrderBy(l => l).Take(40));
    }

    public AgentReceipt? AgendaAdd(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return null;
        var parsed = AgendaQuickAdd.Parse(text, DateTime.Now);
        if (parsed is null) return null;
        var store = AgendaStore.For(owner.Store);
        store.Book.Events.Add(parsed);
        store.Save();
        return Rec("agenda.add", $"Evento: {parsed.Title}", () =>
        {
            store.Book.Events.RemoveAll(e => e.Id == parsed.Id);
            store.Save();
            return true;
        });
    }

    public AgentReceipt? NotesAdd(string text, string? color)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return null;
        var noteColor = string.IsNullOrWhiteSpace(color) ? "paper" : color.Trim().ToLowerInvariant();
        var xaml = $"<Section xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Paragraph>{System.Security.SecurityElement.Escape(text)}</Paragraph></Section>";
        var data = new NotesWidgetData { Color = noteColor, DocumentXaml = xaml, UpdatedUtc = DateTime.UtcNow };
        var config = new WidgetConfig
        {
            Kind = "notes",
            Title = text.Length <= 28 ? text.ToUpperInvariant() : text[..25].ToUpperInvariant() + "…",
            Value = System.Text.Json.JsonSerializer.Serialize(data, Store.JsonOptions),
            Width = 280, Height = 220
        };
        Application.Current.Dispatcher.Invoke(() => owner.AgentAddNotesWidget(config));
        return Rec("notes.add", $"Nota creada: {config.Title}", () =>
        {
            Application.Current.Dispatcher.Invoke(() => owner.AgentRemoveWidget(config.Id));
            return true;
        });
    }

    public string SettingsSnapshot()
    {
        var s = owner.Settings;
        return $"focus={s.FocusMinutes} short={s.ShortMinutes} long={s.LongMinutes} interval={s.LongInterval} goal={s.DailyGoalMinutes} lang={s.Language} agentModel={s.Agent.Model}";
    }

    public string WorkspaceList()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < owner.Settings.WorkspacePages.Count; i++)
        {
            var page = owner.Settings.WorkspacePages[i];
            sb.AppendLine($"{i + 1}. {page.Name} · {page.WidgetCount} widgets");
        }
        return sb.Length == 0 ? "Sin páginas." : sb.ToString().TrimEnd();
    }

    public bool Undo(string receiptId, out string message)
    {
        if (undos.TryGetValue(receiptId, out var action))
        {
            var ok = action();
            message = ok ? "Deshecho." : "No se pudo deshacer.";
            if (ok) undos.Remove(receiptId);
            return ok;
        }
        if (receiptId.StartsWith("memory:", StringComparison.OrdinalIgnoreCase))
        {
            var id = receiptId["memory:".Length..];
            if (AgentMemory.Forget(memory, id: id))
            {
                SaveMemory(memory);
                message = "Recuerdo eliminado.";
                return true;
            }
        }
        message = "No hay recibo reversible con ese id.";
        return false;
    }

    private WorkTask? FindTask(string id) =>
        owner.Settings.Tasks.FirstOrDefault(t => t.Id.ToString().StartsWith(id, StringComparison.OrdinalIgnoreCase));

    private AgentReceipt Rec(string tool, string summary, Func<bool>? undo)
    {
        var receipt = new AgentReceipt
        {
            Tool = tool,
            Summary = summary,
            CanUndo = undo is not null,
            UndoToken = Guid.NewGuid().ToString("N")
        };
        if (undo is not null)
        {
            undos[receipt.Id] = undo;
            undos[receipt.UndoToken] = undo;
        }
        return receipt;
    }
}
