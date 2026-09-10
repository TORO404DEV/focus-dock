using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace PomoDock.Core;

/// <summary>One language the interface can wear, named in its own tongue.</summary>
public sealed record Language(string Code, string Name, string Culture);

/// <summary>
/// Every word the interface says, in one place.
///
/// Only <c>es.json</c> is written by hand: it is the source, and the app falls back to it whenever
/// another language is missing a key, so a half-translated language still reads correctly instead
/// of showing raw keys. Adding a word is one line in <c>es.json</c> and one <see cref="T"/> call;
/// adding a language is one more file next to it, listed in <see cref="Catalog"/>.
///
/// Calls read <c>L.T("tasks.focus")</c>, or <c>L.T("tasks.count", 3)</c> when the sentence carries
/// a value: the placeholders are <c>{0}</c>, <c>{1}</c>… so a translator can move them where that
/// language needs them, which is exactly what string interpolation in the code cannot do.
/// </summary>
public static class Strings
{
    /// <summary>The languages that ship. Each needs a <c>Strings/&lt;code&gt;.json</c> beside it.</summary>
    public static readonly IReadOnlyList<Language> Catalog =
    [
        new("es", "Español", "es-ES"),
        new("en", "English", "en-US")
    ];

    public const string Fallback = "es";

    private static readonly Dictionary<string, Dictionary<string, string>> loaded = [];
    private static Dictionary<string, string> current = Load(Fallback);
    private static Dictionary<string, string> source = Load(Fallback);

    /// <summary>The language on screen right now.</summary>
    public static string Code { get; private set; } = Fallback;

    /// <summary>Month and weekday names, and how numbers and dates are written.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("es-ES");

    /// <summary>Raised after the language changes, so open windows can draw themselves again.</summary>
    public static event Action? Changed;

    /// <summary>
    /// Puts a language on. "system" (or anything unknown) follows the operating system, falling
    /// back to Spanish when the system speaks a language PomoDock does not have yet.
    /// </summary>
    public static void Use(string? code)
    {
        var chosen = Resolve(code);
        if (chosen == Code && loaded.Count > 0) return;
        Code = chosen;
        current = Load(chosen);
        source = Load(Fallback);
        Culture = CultureInfo.GetCultureInfo(Catalog.First(language => language.Code == chosen).Culture);
        Changed?.Invoke();
    }

    /// <summary>Which language a stored setting means, with "system" read from Windows.</summary>
    public static string Resolve(string? code)
    {
        if (!string.IsNullOrWhiteSpace(code) && Catalog.Any(language => language.Code == code)) return code;
        var system = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Catalog.Any(language => language.Code == system) ? system : Fallback;
    }

    /// <summary>
    /// The text for a key. An unknown key falls back to Spanish, and then to the key itself, so a
    /// missing word is visible in testing but never crashes the app in front of a user.
    /// </summary>
    public static string T(string key)
    {
        if (current.TryGetValue(key, out var text)) return text;
        return source.TryGetValue(key, out var original) ? original : key;
    }

    /// <summary>The text for a key with values dropped into its <c>{0}</c>, <c>{1}</c>… holes.</summary>
    public static string T(string key, params object?[] values)
    {
        var pattern = T(key);
        try { return string.Format(Culture, pattern, values); }
        catch (FormatException) { return pattern; }
    }

    /// <summary>Reads a language file, or an empty set when it is missing or damaged.</summary>
    private static Dictionary<string, string> Load(string code)
    {
        if (loaded.TryGetValue(code, out var cached)) return cached;
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        var assembly = Assembly.GetExecutingAssembly();
        var name = $"PomoDock.Core.Strings.{code}.json";
        try
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is not null)
            {
                var read = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
                if (read is not null) table = new Dictionary<string, string>(read, StringComparer.Ordinal);
            }
        }
        catch (JsonException) { }
        loaded[code] = table;
        return table;
    }
}

/// <summary>A short name for <see cref="Strings.T(string)"/>, because it appears on every line of UI.</summary>
public static class L
{
    public static string T(string key) => Strings.T(key);
    public static string T(string key, params object?[] values) => Strings.T(key, values);
}
