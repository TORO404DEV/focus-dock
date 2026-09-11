using PomoDock.Core;

/// <summary>What the calendar's one-line entry has to understand, sentence by sentence.</summary>
internal static class AgendaQuickAddTests
{
    // Wednesday 9 September 2026, midday.
    private static readonly DateTime Now = new(2026, 9, 9, 12, 0, 0);

    public static void Run(Action<string, Action> test, Action<double, double> equal, Action<bool, string> assert)
    {
        void Is(string text, string title, DateTime start, bool allDay, int? minutes = null)
        {
            var draft = AgendaQuickAdd.Parse(text, Now);
            assert(draft is not null, $"'{text}' leyó algo");
            assert(draft!.Title == title, $"'{text}' → título '{draft.Title}', esperaba '{title}'");
            assert(draft.Start == start, $"'{text}' → inicio {draft.Start:yyyy-MM-dd HH:mm}, esperaba {start:yyyy-MM-dd HH:mm}");
            assert(draft.AllDay == allDay, $"'{text}' → todo el día {draft.AllDay}, esperaba {allDay}");
            if (minutes is { } length) assert(draft.Minutes == length, $"'{text}' → {draft.Minutes} min, esperaba {length}");
        }
        AgendaEvent Read(string text) => AgendaQuickAdd.Parse(text, Now) ?? throw new Exception($"'{text}' no leyó nada");

        test("quick add pairs a weekday with its day number", () =>
        {
            // The line from the report: the 18th is a Friday, so both words point at it.
            Is("ir a guadalajara el viernes 18", "Ir a guadalajara", new(2026, 9, 18), true);
            Is("Junta el viernes 18 a las 10", "Junta", new(2026, 9, 18, 10, 0, 0), false);
            Is("Boda el sábado 19", "Boda", new(2026, 9, 19), true);
            // A weekday that does not fit the number is a slip: the number wins.
            Is("Boda el sábado 18", "Boda", new(2026, 9, 18), true);
            Is("Examen el lunes 14 de septiembre a las 8:00", "Examen", new(2026, 9, 14, 8, 0, 0), false);
        });

        test("quick add reads days in every usual shape", () =>
        {
            Is("Dentista el 18 a las 4 y media", "Dentista", new(2026, 9, 18, 16, 30, 0), false);
            Is("Pagar alquiler el 1 de octubre", "Pagar alquiler", new(2026, 10, 1), true);
            Is("Concierto 2 de nov 2026 a las 21h", "Concierto", new(2026, 11, 2, 21, 0, 0), false);
            Is("Vuelo 18/09 a las 6:45am", "Vuelo", new(2026, 9, 18, 6, 45, 0), false);
            Is("Médico 25/12/2026 9:15", "Médico", new(2026, 12, 25, 9, 15, 0), false);
            Is("Ir al banco pasado mañana", "Ir al banco", new(2026, 9, 11), true);
            Is("Cena el sábado a las 9 de la noche", "Cena", new(2026, 9, 12, 21, 0, 0), false);
            Is("Comida con Juan el próximo viernes a mediodía", "Comida con Juan", new(2026, 9, 11, 12, 0, 0), false);
            Is("Cita el viernes de la próxima semana", "Cita", new(2026, 9, 18), true);
            Is("Revisión la semana que viene", "Revisión", new(2026, 9, 14), true);
            Is("Partido el finde", "Partido", new(2026, 9, 12), true);
            Is("Pago de luz fin de mes", "Pago de luz", new(2026, 9, 30), true);
            Is("Leer 20 páginas", "Leer 20 páginas", new(2026, 9, 9), true);
        });

        test("quick add understands relative moments and parts of the day", () =>
        {
            Is("Llamada en 30 minutos", "Llamada", new(2026, 9, 9, 12, 30, 0), false);
            Is("Webinar dentro de 2 horas", "Webinar", new(2026, 9, 9, 14, 0, 0), false);
            Is("Revisar informe en 3 días", "Revisar informe", new(2026, 9, 12), true);
            Is("Café mañana por la tarde", "Café", new(2026, 9, 10, 16, 0, 0), false);
            Is("Estudiar esta noche", "Estudiar", new(2026, 9, 9, 20, 0, 0), false);
            Is("Desayuno mañana a las 8 de la mañana", "Desayuno", new(2026, 9, 10, 8, 0, 0), false);
            Is("Estudiar a las cinco y media de la tarde", "Estudiar", new(2026, 9, 9, 17, 30, 0), false);
            Is("Llamada con cliente mañana 3pm", "Llamada con cliente", new(2026, 9, 10, 15, 0, 0), false);
            Is("Junta a las 3", "Junta", new(2026, 9, 9, 15, 0, 0), false);
        });

        test("quick add turns clock ranges and stays into lengths", () =>
        {
            Is("Clase de 5 a 7", "Clase", new(2026, 9, 9, 17, 0, 0), false, 120);
            // Half past ten already went by today, so it is tomorrow's meeting.
            Is("Reunión de 10:30 a 12", "Reunión", new(2026, 9, 10, 10, 30, 0), false, 90);
            Is("Tutoría mañana a las 4 por hora y media", "Tutoría", new(2026, 9, 10, 16, 0, 0), false, 90);
            Is("Vacaciones del 20 al 25 de septiembre", "Vacaciones", new(2026, 9, 20), true, 6 * 1440);
            Is("Viaje a Monterrey del viernes 18 al domingo 20", "Viaje a Monterrey", new(2026, 9, 18), true, 3 * 1440);
            Is("Examen del 5 al 7", "Examen", new(2026, 10, 5), true, 3 * 1440);
        });

        test("quick add builds series with their days and their end", () =>
        {
            var yoga = Read("Yoga todos los lunes y miércoles a las 7");
            assert(yoga.Repeat == RepeatKind.Weekly && yoga.Days.SequenceEqual([DayOfWeek.Monday, DayOfWeek.Wednesday]), "yoga: lunes y miércoles");
            assert(yoga.Start == new DateTime(2026, 9, 14, 7, 0, 0), "yoga: el primero es el lunes 14");

            var standup = Read("Standup de lunes a viernes a las 9:30");
            equal(5, standup.Days.Count);
            assert(standup.Start == new DateTime(2026, 9, 10, 9, 30, 0), "standup: hoy ya pasó, empieza el jueves");

            var english = Read("Clase de inglés los martes y jueves de 7 a 8");
            assert(english.Title == "Clase de inglés" && english.Minutes == 60, "inglés: título y hora");
            assert(english.Start == new DateTime(2026, 9, 10, 7, 0, 0) && english.Days.Count == 2, "inglés: jueves 10 a las 7");

            var card = Read("Pagar tarjeta cada mes el día 5");
            assert(card.Repeat == RepeatKind.Monthly && card.Start == new DateTime(2026, 10, 5), "tarjeta: el 5 de cada mes, desde octubre");
            var rent = Read("Pagar renta el 1 de cada mes");
            assert(rent.Title == "Pagar renta" && rent.Repeat == RepeatKind.Monthly && rent.Start == new DateTime(2026, 10, 1), "renta: el 1 de cada mes");

            var run = Read("Correr cada 2 semanas el sábado a las 8");
            assert(run.Repeat == RepeatKind.Weekly && run.Interval == 2 && run.Start == new DateTime(2026, 9, 12, 8, 0, 0), "correr: cada dos sábados");

            var pill = Read("Pastilla todos los días a las 8 durante 10 días");
            assert(pill.Repeat == RepeatKind.Daily && pill.Start == new DateTime(2026, 9, 10, 8, 0, 0), "pastilla: diaria desde mañana");
            assert(pill.Until == new DateOnly(2026, 9, 19), $"pastilla: termina el 19, no el {pill.Until}");

            var course = Read("Curso los martes hasta el 20 de octubre");
            assert(course.Days.Single() == DayOfWeek.Tuesday && course.Until == new DateOnly(2026, 10, 20), "curso: martes hasta el 20 de octubre");

            var project = Read("Proyecto hasta octubre cada semana");
            assert(project.Repeat == RepeatKind.Weekly && project.Until == new DateOnly(2026, 10, 31), "proyecto: hasta fin de octubre");

            var therapy = Read("Terapia cada jueves 5 veces");
            assert(therapy.Count == 5 && therapy.Start == new DateTime(2026, 9, 10), "terapia: cinco jueves");

            var weekly = Read("Reunión semanal los lunes a las 10");
            assert(weekly.Title == "Reunión semanal" && weekly.Start == new DateTime(2026, 9, 14, 10, 0, 0), "reunión semanal: lunes 14");
        });

        test("quick add reads places, colours and reminders", () =>
        {
            var zoom = Read("Reunión en Zoom a las 5");
            assert(zoom.Location == "Zoom" && zoom.Title == "Reunión en Zoom", "zoom: el lugar se copia, el título queda entero");
            var dinner = Read("Cena en casa de Ana el viernes a las 9 de la noche");
            assert(dinner.Location == "casa de Ana" && dinner.Start == new DateTime(2026, 9, 11, 21, 0, 0), "cena: casa de Ana el viernes");
            // "Pensar en ideas" is not a place.
            assert(Read("Pensar en ideas").Location == "", "pensar en ideas: sin lugar");

            var birthday = Read("Cumpleaños de Ana @Casa_Ana #rojo el 3 de octubre");
            assert(birthday.Title == "Cumpleaños de Ana" && birthday.Location == "Casa Ana" && birthday.Color == "red", "cumpleaños: etiquetas");
            assert(birthday.Start == new DateTime(2026, 10, 3), "cumpleaños: 3 de octubre");

            var delivery = Read("Entrega el 30 de septiembre avísame 1 día antes");
            assert(delivery.Reminders.Single() == 1440 && delivery.Start == new DateTime(2026, 9, 30), "entrega: aviso un día antes");
            var mum = Read("Llamar a mamá sin aviso mañana a las 11");
            assert(mum.Reminders.Count == 0 && mum.Title == "Llamar a mamá" && mum.Start == new DateTime(2026, 9, 10, 11, 0, 0), "mamá: sin aviso");
        });

        test("natural commands stay out of titles and deadlines keep their meaning", () =>
        {
            Is("Recuérdame registrar materias antes del lunes", "Registrar materias", new(2026, 9, 13), true);
            Is("Oye, recuérdame que tengo que enviar la solicitud antes del viernes", "Enviar la solicitud", new(2026, 9, 10), true);
            Is("Avísame de llamar al dentista antes del lunes a las 5", "Llamar al dentista", new(2026, 9, 14, 17, 0, 0), false);
            Is("Anota un evento: revisión a más tardar el lunes", "Revisión", new(2026, 9, 14), true);
            Is("Crear tarea pagar colegiatura no después del viernes", "Pagar colegiatura", new(2026, 9, 11), true);
        });
    }
}
