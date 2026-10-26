using System.Globalization;
using System.Text.Json;
using PomoDock.Core;

namespace PomoDock.Web;

/// <summary>
/// The desktop's <see cref="Store"/>, kept in the browser instead of SQLite. Same keys, same JSON
/// rows, same rules: a completed session and the end of its checkpoint land together, and a
/// backup exported here is the file the desktop imports.
///
/// Rows live in localStorage under <c>pomodock:</c>. It is synchronous, which is what lets every
/// caller keep the desktop's simple "read, change, write" shape instead of growing async plumbing.
/// </summary>
public sealed class WebStore
{
    private const string SessionsKey = "sessions";
    private static readonly JsonSerializerOptions Compact = new();
    private readonly Browser browser;
    private List<Session>? sessions;

    public WebStore(Browser browser) => this.browser = browser;

    /// <summary>Shown where the desktop shows its data folder.</summary>
    public string DirectoryPath => "localStorage · pomodock:*";

    /// <summary>False when the browser refused the last write: private mode or a full quota.</summary>
    public bool LastWriteSucceeded { get; private set; } = true;

    public T? Read<T>(string key)
    {
        var json = browser.StorageGet(key);
        return json is null ? default : JsonSerializer.Deserialize<T>(json);
    }

    public void Write<T>(string key, T value)
    {
        LastWriteSucceeded = browser.StorageSet(key, JsonSerializer.Serialize(value, Compact));
    }

    public void Remove(string key) => browser.StorageRemove(key);

    public void Save(Session session)
    {
        var all = LoadSessions();
        int index = all.FindIndex(item => item.Id == session.Id);
        if (index >= 0) all[index] = session; else all.Add(session);
        if (holding > 0) { pending = true; return; }
        Write(SessionsKey, all);
    }

    private int holding;
    private bool pending;

    /// <summary>
    /// Runs a bulk change — an import of a few thousand sessions — and writes the sessions once at
    /// the end. The desktop's database takes one row at a time; one JSON row cannot.
    /// </summary>
    public T Hold<T>(Func<T> change)
    {
        holding++;
        try { return change(); }
        finally
        {
            if (--holding == 0 && pending)
            {
                pending = false;
                Write(SessionsKey, LoadSessions());
            }
        }
    }

    public List<Session> Sessions() => LoadSessions().OrderByDescending(s => s.Started).ToList();

    // The checkpoint is removed in the same step the completed session is written.
    public void Complete(Session session)
    {
        Save(session);
        Remove("checkpoint");
    }

    private List<Session> LoadSessions()
    {
        if (sessions is not null) return sessions;
        try { sessions = Read<List<Session>>(SessionsKey) ?? []; }
        catch (JsonException) { sessions = []; }
        return sessions;
    }

    /// <summary>State rows shared with the widgets, which keep a live copy of each book.</summary>
    public const string HabitsKey = Store.HabitsKey;
    public const string AgendaKey = Store.AgendaKey;
    public const string NotesKey = Store.NotesKey;
    public const string FinanceKey = Store.FinanceKey;

    /// <summary>Everything a person would miss, in the desktop's backup format.</summary>
    public string ExportJson(Settings settings) => JsonSerializer.Serialize(new BackupData
    {
        Settings = settings,
        Sessions = Sessions(),
        Habits = TryRead<HabitBook>(HabitsKey),
        Agenda = TryRead<AgendaBook>(AgendaKey),
        Notes = TryRead<NoteArchive>(NotesKey),
        Finance = TryRead<FinanceBook>(FinanceKey)
    }, Store.JsonOptions);

    /// <summary>A damaged row must not stop the rest of the backup from being written.</summary>
    private T? TryRead<T>(string key) where T : class
    {
        try { return Read<T>(key); }
        catch (JsonException) { return null; }
    }

    /// <summary>The sessions as a spreadsheet, with the desktop's columns and formula guard.</summary>
    public static string ExportCsv(IEnumerable<Session> sessions)
    {
        static string Cell(string s)
        {
            // Prevent spreadsheet formula execution in user-supplied names.
            if (s.TrimStart().StartsWith('=') || s.TrimStart().StartsWith('+') || s.TrimStart().StartsWith('-') || s.TrimStart().StartsWith('@')) s = "'" + s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
        var lines = new List<string> { "id,start_utc,end_utc,phase,outcome,project,task,minutes,planned_minutes,pauses,note,apps" };
        lines.AddRange(sessions.Select(s => string.Join(",", s.Id, s.Started.ToString("O"), s.Ended.ToString("O"), s.Phase, s.Outcome, Cell(s.Project), Cell(s.Task), (s.Seconds / 60).ToString("F3", CultureInfo.InvariantCulture), (s.PlannedSeconds / 60).ToString("F2", CultureInfo.InvariantCulture), s.Pauses, Cell(s.Note), Cell(AppUsage.Summarize(s.Apps ?? [], 8)))));
        // A byte-order mark so a spreadsheet opens the accents right, as the desktop file does.
        const string Bom = "\uFEFF", Line = "\r\n";
        return Bom + string.Join(Line, lines) + Line;
    }

    public static BackupData ReadBackup(string json)
    {
        var data = JsonSerializer.Deserialize<BackupData>(json) ?? throw new InvalidDataException(L.T("backup.badFile"));
        data.Sessions ??= [];
        if (data.Version != 1 || data.Sessions.Any(s => s.PlannedSeconds <= 0 || s.Segments.Any(x => x.End < x.Start))) throw new InvalidDataException(L.T("backup.badSessions"));
        return data;
    }

    public int ImportSessions(BackupData data)
    {
        // Merge by stable ID; never replace the user's settings or overwrite existing corrections.
        var all = LoadSessions();
        var known = all.Select(s => s.Id).ToHashSet();
        int count = 0;
        foreach (var session in data.Sessions.Where(s => !known.Contains(s.Id))) { all.Add(session); known.Add(session.Id); count++; }
        if (count > 0) Write(SessionsKey, all);
        return count;
    }
}
