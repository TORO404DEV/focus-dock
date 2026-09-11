namespace PomoDock.Core;

/// <summary>
/// Locates the local Qwen GGUF without confusing it with Whisper or voice packs.
/// A file that is already complete is reused; a sibling <c>.partial</c> is resumed or promoted.
/// </summary>
public readonly record struct LocalLlmStatus(string? Path, long Bytes, long Expected, bool Ready, bool Partial)
{
    public bool Missing => !Ready && !Partial;
    public double Progress => Expected <= 0 ? 0 : Math.Clamp(Bytes / (double)Expected, 0, 1);
}

public static class LocalLlmFile
{
    public const string FileName = "Qwen3-4B-Q4_K_M.gguf";
    public const long ExpectedBytes = 2_497_280_640;
    public const string Sha256 = "ab27b9bfa375a178d6cba48f3ad892b94b7739659dcc7aae8058ce0ffed6b328";
    public const long MinimumPlausibleBytes = 2_000_000_000;
    public const long MaximumPlausibleBytes = 3_200_000_000;

    public static string ModelsDirectory(string dataPath) => System.IO.Path.Combine(dataPath, "models");
    public static string DefaultPath(string dataPath) => System.IO.Path.Combine(ModelsDirectory(dataPath), FileName);
    public static string PartialPath(string dataPath) => DefaultPath(dataPath) + ".partial";

    public static LocalLlmStatus Inspect(string dataPath)
    {
        string directory = ModelsDirectory(dataPath);
        Directory.CreateDirectory(directory);
        string ready = DefaultPath(dataPath);
        string partial = PartialPath(dataPath);

        if (TryPromote(partial, ready))
            return new LocalLlmStatus(ready, new FileInfo(ready).Length, ExpectedBytes, true, false);

        if (IsUsableGguf(ready, out long readyBytes))
            return new LocalLlmStatus(ready, readyBytes, ExpectedBytes, true, false);

        foreach (var candidate in Directory.EnumerateFiles(directory, "Qwen3*.gguf"))
        {
            if (candidate.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsUsableGguf(candidate, out long bytes)) continue;
            if (!string.Equals(candidate, ready, StringComparison.OrdinalIgnoreCase))
            {
                bool emptyReady = !File.Exists(ready) || new FileInfo(ready).Length < MinimumPlausibleBytes;
                try
                {
                    if (emptyReady) File.Copy(candidate, ready, overwrite: true);
                    else return new LocalLlmStatus(candidate, bytes, ExpectedBytes, true, false);
                }
                catch (IOException) { return new LocalLlmStatus(candidate, bytes, ExpectedBytes, true, false); }
                return new LocalLlmStatus(ready, new FileInfo(ready).Length, ExpectedBytes, true, false);
            }
            return new LocalLlmStatus(candidate, bytes, ExpectedBytes, true, false);
        }

        if (File.Exists(partial))
        {
            long bytes = new FileInfo(partial).Length;
            if (bytes > ExpectedBytes)
            {
                try { File.Delete(partial); } catch { }
                bytes = 0;
            }
            if (bytes > 0)
                return new LocalLlmStatus(partial, bytes, ExpectedBytes, false, true);
        }

        return new LocalLlmStatus(null, 0, ExpectedBytes, false, false);
    }

    public static bool IsWhisperFile(string path)
    {
        string name = System.IO.Path.GetFileName(path);
        return name.StartsWith("ggml-", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
            || name.Contains("whisper", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsUsableGguf(string path, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || IsWhisperFile(path)) return false;
        if (!path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) return false;
        bytes = new FileInfo(path).Length;
        if (bytes == ExpectedBytes) return true;
        string name = System.IO.Path.GetFileName(path);
        bool qwen = name.StartsWith("Qwen3", StringComparison.OrdinalIgnoreCase);
        return qwen && bytes >= MinimumPlausibleBytes && bytes <= MaximumPlausibleBytes;
    }

    private static bool TryPromote(string partial, string ready)
    {
        if (!File.Exists(partial)) return false;
        long bytes = new FileInfo(partial).Length;
        if (bytes != ExpectedBytes && (bytes < MinimumPlausibleBytes || bytes > MaximumPlausibleBytes))
            return false;
        if (bytes != ExpectedBytes && bytes < MinimumPlausibleBytes) return false;
        if (File.Exists(ready) && IsUsableGguf(ready, out _)) return false;
        try
        {
            File.Move(partial, ready, overwrite: false);
            return File.Exists(ready);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
