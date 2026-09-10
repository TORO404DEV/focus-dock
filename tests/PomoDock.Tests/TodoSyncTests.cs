using PomoDock.Core;

/// <summary>Tasks read like calendar lines and live on in the calendar and its reminders.</summary>
internal static class TodoSyncTests
{
    // Wednesday 9 September 2026, midday.
    private static readonly DateTime Now = new(2026, 9, 9, 12, 0, 0);

    public static void Run(Action<string, Action> test, Action<double, double> equal, Action<bool, string> assert)
    {
        test("a task line reads like a calendar line", () =>
        {
            // The line from the report: the deadline is an hour today.
            var wood = TodoBook.Read("Ir por madera antes de 2pm", Now)!;
            assert(wood.Task.Title == "Ir por madera", $"madera: título '{wood.Task.Title}'");
            assert(wood.Task.Due == new DateOnly(2026, 9, 9) && wood.Task.At == new TimeOnly(14, 0), "madera: hoy a las 14:00");
            assert(wood.Draft is not null, "madera: va al calendario");

            var light = TodoBook.Read("!! Pagar luz el viernes 18", Now)!;
            assert(light.Task.Priority == TodoPriority.High && light.Task.Due == new DateOnly(2026, 9, 18) && light.Task.At is null, "luz: urgente, viernes 18");

            var report = TodoBook.Read("Entregar informe antes del viernes", Now)!;
            assert(report.Task.Title == "Entregar informe" && report.Task.Due == new DateOnly(2026, 9, 11), "informe: antes del viernes");

            var bread = TodoBook.Read("Comprar pan", Now)!;
            assert(bread.Task.Due is null && bread.Draft is null, "pan: sin fecha ni calendario");
        });

        test("a dated task becomes a calendar entry that rings", () =>
        {
            var call = TodoBook.Read("Llamar a Ana mañana a las 5 avísame 30 min antes", Now)!;
            var task = call.Task;
            var item = TodoSync.ToEvent(task, null, call.Draft);
            assert(item.TaskId == task.Id && item.Title == "Llamar a Ana", "la entrada lleva la tarea");
            assert(item.Start == new DateTime(2026, 9, 10, 17, 0, 0) && !item.AllDay, "mañana a las 17:00");
            assert(item.Reminders.Single() == 30, "el aviso de la línea se conserva");

            var agenda = new AgendaBook { Events = [item] };
            var cues = agenda.Cues(new DateTime(2026, 9, 10, 16, 0, 0), new DateTime(2026, 9, 10, 17, 0, 0)).ToList();
            assert(cues.Count == 1 && cues[0].FireAt == new DateTime(2026, 9, 10, 16, 30, 0), "suena media hora antes");

            // Ticking the task off silences the entry.
            task.SetDone(true);
            TodoSync.ToEvent(task, item, null);
            assert(item.IsDone(new DateOnly(2026, 9, 10)), "la entrada queda hecha");
            equal(0, agenda.Cues(new DateTime(2026, 9, 10, 16, 0, 0), new DateTime(2026, 9, 10, 17, 0, 0)).Count());

            // An undated task gets an all-day entry that rings in the morning.
            var loose = new TodoTask { Title = "Revisar", Due = new DateOnly(2026, 9, 12) };
            var morning = TodoSync.ToEvent(loose, null, null);
            assert(morning.AllDay && morning.Reminders.Single() == 0, "todo el día, aviso a primera hora");
        });

        test("what the calendar does comes back to the task", () =>
        {
            var task = new TodoTask { Title = "Revisar contrato", Due = new DateOnly(2026, 9, 10), At = new TimeOnly(10, 0) };
            var item = TodoSync.ToEvent(task, null, null);

            item.Start = new DateTime(2026, 9, 12, 9, 0, 0);
            item.Title = "Revisar contrato final";
            assert(TodoSync.FromEvent(task, item), "mover la entrada cambia la tarea");
            assert(task.Due == new DateOnly(2026, 9, 12) && task.At == new TimeOnly(9, 0) && task.Title == "Revisar contrato final", "la tarea sigue a la entrada");

            // "✓ LISTO" on the reminder card completes the task.
            item.SetDone(new DateOnly(2026, 9, 12), true);
            assert(TodoSync.FromEvent(task, item) && task.Done, "el aviso respondido completa la tarea");
            assert(!TodoSync.FromEvent(task, item), "sin cambios, nada que escribir");
        });

        test("a repeating task advances instead of disappearing", () =>
        {
            var trash = TodoBook.Read("Sacar la basura todos los martes", Now)!;
            var task = trash.Task;
            assert(task.Due == new DateOnly(2026, 9, 15), "la primera es el martes 15");
            var item = TodoSync.ToEvent(task, null, trash.Draft);
            assert(item.Repeat == RepeatKind.Weekly, "la entrada se repite cada semana");

            TodoSync.Complete(task, item);
            assert(item.IsDone(new DateOnly(2026, 9, 15)), "se tacha este martes en el calendario");
            assert(!task.Done && task.Due == new DateOnly(2026, 9, 22), "la tarea pasa al martes siguiente");

            // Checking the next one off from the calendar moves it along as well.
            item.SetDone(new DateOnly(2026, 9, 22), true);
            assert(TodoSync.FromEvent(task, item) && task.Due == new DateOnly(2026, 9, 29), "y otra semana más");
        });
    }
}
