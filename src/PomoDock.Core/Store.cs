using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PomoDock.Core;

public sealed class Store : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SqliteConnection db;
    public string DirectoryPath { get; }
    public Store(string directory)
    {
        DirectoryPath = directory;
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "pomo-dock.db");
        var legacyDatabase = Path.Combine(directory, "focus-dock.db");
        if (!File.Exists(database) && File.Exists(legacyDatabase))
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var oldFile = legacyDatabase + suffix; var newFile = database + suffix;
                if (!File.Exists(oldFile)) continue;
                try { File.Move(oldFile, newFile); } catch { }
            }
        }
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, payload TEXT NOT NULL); CREATE TABLE IF NOT EXISTS state(key TEXT PRIMARY KEY, payload TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
    }
    public void Save(Session session)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO sessions VALUES($id,$payload) ON CONFLICT(id) DO UPDATE SET payload=$payload";
        cmd.Parameters.AddWithValue("$id", session.Id.ToString());
        cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(session, JsonOptions));
        cmd.ExecuteNonQuery();
    }
    public List<Session> Sessions()
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT payload FROM sessions";
        using var reader = cmd.ExecuteReader();
        var list = new List<Session>();
        while (reader.Read()) { var session = JsonSerializer.Deserialize<Session>(reader.GetString(0)); if (session is not null) list.Add(session); }
        return list.OrderByDescending(s => s.Started).ToList();
    }
    public T? Read<T>(string key)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT payload FROM state WHERE key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json) : default;
    }
    public void Write<T>(string key, T value)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO state VALUES($key,$payload) ON CONFLICT(key) DO UPDATE SET payload=$payload";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(value, JsonOptions));
        cmd.ExecuteNonQuery();
    }
    // The checkpoint is removed in the same transaction as the completed session is inserted.
    public void Complete(Session session)
    {
        using var transaction = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO sessions VALUES($id,$payload) ON CONFLICT(id) DO UPDATE SET payload=$payload; DELETE FROM state WHERE key='checkpoint';";
        cmd.Parameters.AddWithValue("$id", session.Id.ToString());
        cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(session, JsonOptions));
        cmd.ExecuteNonQuery();
        transaction.Commit();
    }
    public void Backup(string destination)
    {
        using var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination }.ToString());
        copy.Open(); db.BackupDatabase(copy);
    }
    /// <summary>State rows shared with the app layer, which keeps a live copy of each book.</summary>
    public const string HabitsKey = "habits";
    public const string AgendaKey = "agenda";
    public const string NotesKey = "notes-archive";

    /// <summary>Everything a person would miss: settings, focus history, habits and calendar.</summary>
    public void ExportJson(string path) => File.WriteAllText(path, JsonSerializer.Serialize(new BackupData
    {
        Settings = Read<Settings>("settings") ?? new(),
        Sessions = Sessions(),
        Habits = TryRead<HabitBook>(HabitsKey),
        Agenda = TryRead<AgendaBook>(AgendaKey),
        Notes = TryRead<NoteArchive>(NotesKey)
    }, JsonOptions));

    /// <summary>A damaged row must not stop the rest of the backup from being written.</summary>
    private T? TryRead<T>(string key) where T : class
    {
        try { return Read<T>(key); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Reads and validates a backup. Files written before habits and events were exported
    /// still load; they simply carry no habits or calendar.
    /// </summary>
    public static BackupData ReadBackup(string path)
    {
        var data = JsonSerializer.Deserialize<BackupData>(File.ReadAllText(path)) ?? throw new InvalidDataException(L.T("backup.badFile"));
        data.Sessions ??= [];
        if (data.Version != 1 || data.Sessions.Any(s => s.PlannedSeconds <= 0 || s.Segments.Any(x => x.End < x.Start))) throw new InvalidDataException(L.T("backup.badSessions"));
        return data;
    }

    public int ImportJson(string path) => ImportSessions(ReadBackup(path));

    public int ImportSessions(BackupData data)
    {
        // Merge by stable ID; never replace the user's settings or overwrite existing corrections.
        var known = Sessions().Select(s => s.Id).ToHashSet();
        int count = 0;
        foreach (var session in data.Sessions.Where(s => !known.Contains(s.Id))) { Save(session); known.Add(session.Id); count++; }
        return count;
    }
    public static void ExportCsv(string path, IEnumerable<Session> sessions)
    {
        static string Cell(string s)
        {
            // Prevent spreadsheet formula execution in user-supplied names.
            if (s.TrimStart().StartsWith('=') || s.TrimStart().StartsWith('+') || s.TrimStart().StartsWith('-') || s.TrimStart().StartsWith('@')) s = "'" + s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
        var lines = new List<string> { "id,start_utc,end_utc,phase,outcome,project,task,minutes,planned_minutes,pauses,note" };
        lines.AddRange(sessions.Select(s => string.Join(",", s.Id, s.Started.ToString("O"), s.Ended.ToString("O"), s.Phase, s.Outcome, Cell(s.Project), Cell(s.Task), (s.Seconds / 60).ToString("F3", CultureInfo.InvariantCulture), (s.PlannedSeconds / 60).ToString("F2", CultureInfo.InvariantCulture), s.Pauses, Cell(s.Note))));
        File.WriteAllLines(path, lines, new UTF8Encoding(true));
    }
    public void Dispose() => db.Dispose();
}
public sealed class BackupData
{
    public int Version { get; set; } = 1;
    public Settings Settings { get; set; } = new();
    public List<Session> Sessions { get; set; } = [];
    /// <summary>Absent in backups written before habits were exported.</summary>
    public HabitBook? Habits { get; set; }
    /// <summary>Absent in backups written before calendar events were exported.</summary>
    public AgendaBook? Agenda { get; set; }
    /// <summary>Absent in backups written before the note history existed.</summary>
    public NoteArchive? Notes { get; set; }
}
