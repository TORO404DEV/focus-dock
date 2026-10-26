using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PomoDock.App;

/// <summary>
/// Home workout catalog: dumbbells + flat bench only (no incline/decline backrest).
/// Videos stream from the upstream MIT CDN; metadata ships as an embedded JSON subset.
/// </summary>
internal static class WorkoutCatalog
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly Lazy<IReadOnlyList<Exercise>> All = new(Load);
    private static readonly Regex NeedsGear = new(
        @"incline|decline|stability ball|swiss|bosu|preacher|roman chair|smith|cable|machine|treadmill|ez.?bar|kettlebell|back support|adjustable bench",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal sealed class Exercise
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string BodyPart { get; set; } = "";
        public string Target { get; set; } = "";
        public List<string> SecondaryMuscles { get; set; } = [];
        public string Difficulty { get; set; } = "beginner";
        public bool Compound { get; set; }
        public bool Unilateral { get; set; }
        public string ShortDescription { get; set; } = "";
        public List<string> Steps { get; set; } = [];
        public List<string> FormCues { get; set; } = [];
        public List<string> CommonMistakes { get; set; } = [];
        public MediaPair Videos { get; set; } = new();
        public MediaPair Thumbnails { get; set; } = new();

        public string VideoUrl(bool female) =>
            female && !string.IsNullOrWhiteSpace(Videos.Female) ? Videos.Female!
            : !string.IsNullOrWhiteSpace(Videos.Male) ? Videos.Male!
            : Videos.Female ?? "";

        public string ThumbnailUrl(bool female) =>
            female && !string.IsNullOrWhiteSpace(Thumbnails.Female) ? Thumbnails.Female!
            : !string.IsNullOrWhiteSpace(Thumbnails.Male) ? Thumbnails.Male!
            : Thumbnails.Female ?? "";
    }

    internal sealed class MediaPair
    {
        public string? Male { get; set; }
        public string? Female { get; set; }
    }

    private sealed class FileRoot
    {
        public string Source { get; set; } = "";
        public string License { get; set; } = "";
        public string Note { get; set; } = "";
        public List<Exercise> Exercises { get; set; } = [];
    }

    internal static string Attribution =>
        "Demos · free-exercise-db-with-videos (MIT) · mancuernas + banca plana";

    internal static IReadOnlyList<Exercise> Exercises => All.Value;

    internal static IReadOnlyList<string> BodyParts =>
        Exercises.Select(e => e.BodyPart).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

    internal static Exercise? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null
        : Exercises.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    internal static IEnumerable<Exercise> Filter(string? bodyPart)
    {
        if (string.IsNullOrWhiteSpace(bodyPart) || bodyPart is "all" or "*")
            return Exercises;
        return Exercises.Where(e => e.BodyPart.Equals(bodyPart, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<Exercise> Load()
    {
        try
        {
            var assembly = typeof(WorkoutCatalog).Assembly;
            string? resource = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith("exercises.json", StringComparison.OrdinalIgnoreCase));
            if (resource is null) return Array.Empty<Exercise>();
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null) return Array.Empty<Exercise>();
            using var reader = new StreamReader(stream);
            var root = JsonSerializer.Deserialize<FileRoot>(reader.ReadToEnd(), Json);
            if (root?.Exercises is null) return Array.Empty<Exercise>();
            return root.Exercises
                .Where(IsHomeFriendly)
                .Where(e => !string.IsNullOrWhiteSpace(e.Id) && !string.IsNullOrWhiteSpace(e.Name))
                .Where(e => !string.IsNullOrWhiteSpace(e.Videos.Male) || !string.IsNullOrWhiteSpace(e.Videos.Female))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<Exercise>();
        }
    }

    private static bool IsHomeFriendly(Exercise e)
    {
        // Seated Arnold on a flat bench (no backrest) is in the home split.
        if (e.Id == "2137") return true;
        string blob = string.Join(' ', e.Name, e.ShortDescription, string.Join(' ', e.Steps));
        if (NeedsGear.IsMatch(e.Name)) return false;
        if (NeedsGear.IsMatch(blob) && !Regex.IsMatch(e.Name, @"\b(lying|supine|floor|flat)\b", RegexOptions.IgnoreCase))
            return false;
        return true;
    }
}
