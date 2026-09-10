using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PomoDock.Core;

public sealed record CsvImportResult(int Imported, int Skipped, int Invalid);

public static class PomofocusCsv
{
    private static readonly string[] DateFormats = ["yyyyMMdd", "yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy"];
    private static readonly string[] TimeFormats = ["HH:mm", "H:mm", "hh:mm tt", "h:mm tt"];

    public static CsvImportResult Import(Store store, Settings settings, string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(L.T("csv.missing"), path);
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException(L.T("csv.empty"));
        var delimiter = DetectDelimiter(text);
        var records = ParseRecords(text, delimiter).Where(r => r.Any(v => !string.IsNullOrWhiteSpace(v))).ToList();
        if (records.Count < 2) throw new InvalidDataException(L.T("csv.noSessions"));

        var header = records[0].Select(NormalizeHeader).ToArray();
        int dateIndex = RequiredColumn(header, "date");
        int projectIndex = RequiredColumn(header, "project");
        int taskIndex = RequiredColumn(header, "task");
        int hoursIndex = RequiredColumn(header, "hours");
        int startIndex = RequiredColumn(header, "starttime");
        int endIndex = RequiredColumn(header, "endtime");
        int maxIndex = new[] { dateIndex, projectIndex, taskIndex, hoursIndex, startIndex, endIndex }.Max();

        var known = store.Sessions().Select(s => s.Id).ToHashSet();
        int imported = 0, skipped = 0, invalid = 0;
        bool settingsChanged = false;
        for (int rowIndex = 1; rowIndex < records.Count; rowIndex++)
        {
            var row = records[rowIndex];
            if (row.Length <= maxIndex) { invalid++; continue; }
            try
            {
                var id = StableId(row, rowIndex);
                if (known.Contains(id)) { skipped++; continue; }

                var project = Clean(row[projectIndex], "Sin proyecto");
                var task = Clean(row[taskIndex], "Enfoque libre");
                if (!settings.Projects.Any(p => string.Equals(p, project, StringComparison.OrdinalIgnoreCase)))
                { settings.Projects.Add(project); settingsChanged = true; }

                Guid? taskId = null;
                if (!string.Equals(task, "Enfoque libre", StringComparison.OrdinalIgnoreCase))
                {
                    var existingTask = settings.Tasks.FirstOrDefault(t => !t.Template &&
                        string.Equals(t.Name, task, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(t.Project, project, StringComparison.OrdinalIgnoreCase));
                    if (existingTask is null)
                    {
                        existingTask = new WorkTask { Name = task, Project = project };
                        settings.Tasks.Add(existingTask); settingsChanged = true;
                    }
                    taskId = existingTask.Id;
                }

                if (!double.TryParse(row[hoursIndex].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) || !double.IsFinite(hours) || hours < 0 || hours > 24)
                    throw new InvalidDataException(L.T("csv.badHours"));
                if (!DateTime.TryParseExact(row[dateIndex].Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    throw new InvalidDataException(L.T("csv.badDate"));
                bool hasStart = DateTime.TryParseExact(row[startIndex].Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var startTime);
                bool hasEnd = DateTime.TryParseExact(row[endIndex].Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var endTime);
                var seconds = hours * 3600;
                DateTime startLocal, endLocal;
                bool inferredTime = false;
                if (hasStart && hasEnd)
                {
                    startLocal = date.Date.Add(startTime.TimeOfDay);
                    endLocal = date.Date.Add(endTime.TimeOfDay);
                    if (endLocal < startLocal || (endLocal == startLocal && hours > 0)) endLocal = endLocal.AddDays(1);
                }
                else if (hasStart)
                {
                    startLocal = date.Date.Add(startTime.TimeOfDay);
                    endLocal = startLocal.AddSeconds(seconds);
                    inferredTime = true;
                }
                else if (hasEnd)
                {
                    endLocal = date.Date.Add(endTime.TimeOfDay);
                    startLocal = endLocal.AddSeconds(-seconds);
                    inferredTime = true;
                }
                else
                {
                    startLocal = date.Date.AddHours(12);
                    endLocal = startLocal.AddSeconds(seconds);
                    inferredTime = true;
                }
                startLocal = DateTime.SpecifyKind(startLocal, DateTimeKind.Unspecified);
                endLocal = DateTime.SpecifyKind(endLocal, DateTimeKind.Unspecified);
                var started = ToLocalOffset(startLocal);
                var wallEnded = ToLocalOffset(endLocal);
                var productiveEnd = started.AddSeconds(seconds);
                var ended = productiveEnd > wallEnded ? productiveEnd : wallEnded;
                var segments = seconds > 0 ? [new FocusSegment(started, productiveEnd)] : new List<FocusSegment>();

                var session = new Session
                {
                    Id = id,
                    Started = started,
                    Ended = ended,
                    Phase = Phase.Focus,
                    Outcome = seconds > 0 ? Outcome.Completed : Outcome.Partial,
                    Project = project,
                    Task = task,
                    TaskId = taskId,
                    PlannedSeconds = Math.Max(1, seconds),
                    Segments = segments,
                    Note = inferredTime ? "Importado desde Pomofocus CSV. Horario inferido porque faltaba una marca de tiempo." : "Importado desde Pomofocus CSV."
                };
                store.Save(session); known.Add(id); imported++;
            }
            catch (FormatException) { invalid++; }
            catch (InvalidDataException) { invalid++; }
            catch (ArgumentException) { invalid++; }
        }
        if (settingsChanged) store.Write("settings", settings);
        return new CsvImportResult(imported, skipped, invalid);
    }

    private static DateTimeOffset ToLocalOffset(DateTime local)
    {
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.Local);
        return new DateTimeOffset(utc);
    }

    private static string Clean(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    private static int RequiredColumn(string[] header, string name)
    {
        var index = Array.IndexOf(header, name);
        if (index < 0) throw new InvalidDataException($"Falta la columna '{name}'.");
        return index;
    }
    private static string NormalizeHeader(string value) => value.Trim().Trim('\uFEFF').Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
    private static char DetectDelimiter(string text)
    {
        var firstLine = text.Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? "";
        return ParseRecord(firstLine.TrimEnd('\r'), '\t').Count >= 6 ? '\t' : ',';
    }
    private static Guid StableId(IReadOnlyList<string> row, int rowIndex)
    {
        var payload = row.Count + ":" + rowIndex + ":" + string.Join("\u001f", row);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return new Guid(bytes.AsSpan(0, 16));
    }
    private static List<string[]> ParseRecords(string text, char delimiter)
    {
        var records = new List<string[]>(); var fields = new List<string>(); var field = new StringBuilder(); bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == delimiter) { fields.Add(field.ToString()); field.Clear(); }
            else if (c == '\n') { fields.Add(field.ToString().TrimEnd('\r')); records.Add(fields.ToArray()); fields.Clear(); field.Clear(); }
            else field.Append(c);
        }
        if (field.Length > 0 || fields.Count > 0) { fields.Add(field.ToString().TrimEnd('\r')); records.Add(fields.ToArray()); }
        return records;
    }
    private static List<string> ParseRecord(string line, char delimiter) => ParseRecords(line, delimiter).FirstOrDefault()?.ToList() ?? [];
}
