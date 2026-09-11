using System.Text.Json;
using PomoDock.Core;

/// <summary>Task list rules: shorthand, ordering, filters and the counts the card shows.</summary>
internal static class TodoTests
{
    private static readonly DateOnly Today = new(2026, 9, 9); // Wednesday

    public static void Run(Action<string, Action> test, Action<double, double> equal, Action<bool, string> assert)
    {
        void Test(string name, Action body) => test(name, body);

        Test("shorthand reads priority and dates and leaves the rest as the title", () =>
        {
            var coffee = TodoBook.Parse("Comprar café mañana !", Today)!;
            assert(coffee.Title == "Comprar café", "the title keeps only the words");
            assert(coffee.Due == Today.AddDays(1), "mañana means tomorrow");
            assert(coffee.Priority == TodoPriority.Medium, "one bang is medium priority");

            var invoice = TodoBook.Parse("!! Enviar la factura hoy", Today)!;
            assert(invoice.Priority == TodoPriority.High && invoice.Due == Today, "two bangs are high priority");
            assert(invoice.Title == "Enviar la factura", "the trailing connector is trimmed away");

            var dated = TodoBook.Parse("Renovar el pasaporte 12/10", Today)!;
            assert(dated.Due == new DateOnly(2026, 10, 12), "a bare day and month is read as this year");
            // A date already gone points at the next time it comes round.
            assert(TodoBook.Parse("Pagar 03/02", Today)!.Due == new DateOnly(2027, 2, 3), "a past date rolls to next year");
            // A bang inside a word is emphasis, not a priority.
            var shout = TodoBook.Parse("Llamar a mamá ¡urgente!", Today)!;
            assert(shout.Priority == TodoPriority.None && shout.Title.EndsWith("¡urgente!"), "an attached bang stays in the title");
            assert(TodoBook.Parse("  hoy  ", Today) is null && TodoBook.Parse("", Today) is null, "a task needs a name");
        });

        Test("spoken intent and exclusive deadlines are shared with the calendar", () =>
        {
            var classes = TodoBook.Read("Recuérdame registrar materias antes del lunes", new DateTime(2026, 9, 9, 12, 0, 0))!;
            assert(classes.Task.Title == "Registrar materias", "the command is not the task title");
            assert(classes.Task.Due == new DateOnly(2026, 9, 13), "before Monday means Sunday, not Monday");
            assert(classes.Draft?.Title == classes.Task.Title && classes.Draft.Start == new DateTime(2026, 9, 13), "calendar receives the same clean item");

            var nested = TodoBook.Read("Oye, recuérdame que tengo que entregar papeles antes del viernes", new DateTime(2026, 9, 9, 12, 0, 0))!;
            assert(nested.Task.Title == "Entregar papeles" && nested.Task.Due == new DateOnly(2026, 9, 10), "nested spoken intent is removed and the deadline stays exclusive");
        });

        Test("new tasks land on top and can be moved by hand", () =>
        {
            var book = new TodoBook();
            book.Add("Primera", Today); book.Add("Segunda", Today); book.Add("Tercera", Today);
            assert(book.Ordered().Select(task => task.Title).SequenceEqual(["Tercera", "Segunda", "Primera"]), "the newest task is visible first");
            var second = book.Ordered()[1];
            assert(book.Move(second, -1), "a task moves up");
            assert(book.Ordered()[0] == second, "the moved task took the first slot");
            assert(!book.Move(book.Ordered()[0], -1), "the first task cannot move further up");
            assert(!book.Move(book.Ordered()[^1], 1), "the last task cannot move further down");
        });

        Test("finished tasks sink, keep their moment and can be cleared", () =>
        {
            var book = new TodoBook();
            book.Add("Uno", Today); book.Add("Dos", Today); book.Add("Tres", Today);
            var top = book.Ordered()[0];
            top.SetDone(true);
            assert(book.Ordered()[^1] == top, "a finished task drops to the bottom");
            assert(top.DoneUtc is not null, "finishing a task records when it happened");
            top.SetDone(false);
            assert(top.DoneUtc is null && book.Ordered()[0] == top, "undoing it restores its place");
            top.SetDone(true);
            equal(1, book.ClearDone());
            equal(2, book.Items.Count);
        });

        Test("filters and counts describe the day", () =>
        {
            var book = new TodoBook();
            var late = book.Add("Atrasada", Today)!; late.Due = Today.AddDays(-2);
            var now = book.Add("Del día", Today)!; now.Due = Today;
            var later = book.Add("Más adelante", Today)!; later.Due = Today.AddDays(4);
            var loose = book.Add("Suelta", Today)!;
            loose.SetDone(true);

            var counts = book.Counts(Today);
            equal(4, counts.Total); equal(1, counts.Done); equal(3, counts.Open); equal(1, counts.Overdue); equal(1, counts.Today);

            book.Filter = "today";
            assert(book.Visible(Today).Select(task => task.Title).OrderBy(title => title).SequenceEqual(["Atrasada", "Del día"]), "today shows what is due and what is late");
            book.Filter = "open";
            equal(3, book.Visible(Today).Count);
            book.Filter = "done";
            assert(book.Visible(Today).Single().Title == "Suelta", "done shows only finished work");
            book.Filter = "inventado"; book.Normalize();
            assert(book.Filter == "all", "an unknown filter falls back to the whole list");

            assert(late.DueLabel(Today) == "VENCIDA" && now.DueLabel(Today) == "HOY", "badges name the urgent days");
            assert(later.DueLabel(Today) == "13/09" && loose.DueLabel(Today) == "", "a distant day shows its date and no date shows nothing");
        });

        Test("one tidy puts the overdue and the urgent first", () =>
        {
            var book = new TodoBook();
            var loose = book.Add("Suelta", Today)!;
            var soon = book.Add("Revisar", Today)!; soon.Due = Today.AddDays(2);
            var late = book.Add("Atrasada", Today)!; late.Due = Today.AddDays(-1);
            var urgent = book.Add("Urgente", Today)!; urgent.Due = Today; urgent.Priority = TodoPriority.High;
            book.SortByUrgency(Today);
            assert(book.Ordered().Select(task => task.Title).SequenceEqual(["Atrasada", "Urgente", "Revisar", "Suelta"]), "urgency decides the order");
            assert(book.Ordered().Select(task => task.Order).SequenceEqual([0, 1, 2, 3]), "the order stays dense");
            assert(loose.Due is null, "sorting never invents a date");
        });

        Test("a list written by an older version keeps every task", () =>
        {
            // The previous widget stored exactly these fields; the new ones take their defaults.
            const string legacy = """
            {"Items":[{"Id":"6f9619ff-8b86-d011-b42d-00c04fc964ff","Title":"Tarea vieja","Done":true,"CreatedUtc":"2026-09-01T10:00:00Z"}]}
            """;
            var book = JsonSerializer.Deserialize<TodoBook>(legacy)!;
            book.Normalize();
            var task = book.Items.Single();
            assert(task.Title == "Tarea vieja" && task.Done, "the old task survived");
            assert(task.Priority == TodoPriority.None && task.Due is null, "the new fields start empty");
            assert(task.DoneUtc is not null, "a task already done gets a completion moment");

            var round = JsonSerializer.Deserialize<TodoBook>(JsonSerializer.Serialize(book))!;
            round.Normalize();
            assert(round.Items.Single().Title == "Tarea vieja", "the list survives a save and reload");
        });
    }
}
