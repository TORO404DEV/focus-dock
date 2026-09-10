using System.Text.Json;
using PomoDock.Core;

/// <summary>Note history rules: closed notes keep their words, empty notes vanish, backups only add.</summary>
internal static class NoteArchiveTests
{
    private static readonly DateTime Morning = new(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);

    public static void Run(Action<string, Action> test, Action<double, double> equal, Action<bool, string> assert)
    {
        test("a closed note stays in the history with its last words", () =>
        {
            var archive = new NoteArchive();
            var id = Guid.NewGuid();
            archive.Track(id, "Lista de la compra\nPan", "yellow", "{uno}", Morning);
            archive.Track(id, "Lista de la compra\nPan\nLeche", "yellow", "{dos}", Morning.AddMinutes(5));
            assert(archive.Close(id, Morning.AddMinutes(6)), "closing a tracked note is recorded");
            var note = archive.Find(id)!;
            assert(!note.IsOpen && note.Text.EndsWith("Leche") && note.Payload == "{dos}", "the last version is the one kept");
            assert(note.Title == "Lista de la compra" && note.Body == "Pan  ·  Leche", "the first line titles the note");
            equal(1, archive.Notes.Count);
        });

        test("an empty note leaves no trace and a reopened note is open again", () =>
        {
            var archive = new NoteArchive();
            archive.Track(Guid.NewGuid(), "   ", "paper", "", Morning);
            equal(0, archive.Notes.Count);
            var id = Guid.NewGuid();
            archive.Track(id, "Idea", "paper", "p", Morning);
            archive.Close(id, Morning.AddHours(1));
            archive.Track(id, "Idea", "paper", "p", Morning.AddHours(2));
            assert(archive.Find(id)!.IsOpen, "a note back on a page is open again");
            archive.Track(id, "", "paper", "", Morning.AddHours(3));
            assert(archive.Find(id) is null, "wiping a note removes it from the history");
        });

        test("the history lists open notes first and searches without caring for accents", () =>
        {
            var archive = new NoteArchive();
            var closed = Guid.NewGuid();
            var open = Guid.NewGuid();
            archive.Track(closed, "Canción para el viernes", "mint", "", Morning);
            archive.Close(closed, Morning.AddHours(1));
            archive.Track(open, "Reunión de equipo", "blue", "", Morning.AddMinutes(30));
            assert(archive.Ordered()[0].Id == open, "notes on a page come first");
            assert(archive.Search("cancion").Single().Id == closed, "search ignores accents");
            assert(archive.Search("REUNION equipo").Single().Id == open, "every word must match, in any case");
            equal(2, archive.Search("").Count);
        });

        test("notes no page holds are treated as closed, and a backup only adds", () =>
        {
            var archive = new NoteArchive();
            var lost = Guid.NewGuid();
            var kept = Guid.NewGuid();
            archive.Track(lost, "Perdida", "paper", "", Morning);
            archive.Track(kept, "En pantalla", "paper", "", Morning);
            equal(1, archive.Reconcile(new HashSet<Guid> { kept }, Morning.AddDays(1)));
            assert(!archive.Find(lost)!.IsOpen && archive.Find(kept)!.IsOpen, "only the orphan is closed");

            var backup = JsonSerializer.Deserialize<NoteArchive>(JsonSerializer.Serialize(archive))!;
            backup.Find(kept)!.Text = "Cambiada en la copia";
            var extra = Guid.NewGuid();
            backup.Track(extra, "Solo en la copia", "paper", "", Morning);
            equal(1, archive.MergeFrom(backup));
            assert(archive.Find(kept)!.Text == "En pantalla", "a note already here keeps its own version");
            assert(!archive.Find(extra)!.IsOpen, "a restored note arrives closed, ready to reopen");
            equal(0, archive.MergeFrom(backup));
        });
    }
}
