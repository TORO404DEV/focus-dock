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
        Write(SessionsKey, all);
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
