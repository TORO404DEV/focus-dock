namespace PomoDock.Core;

/// <summary>
/// Locates the local Qwen3 Instruct GGUF without confusing it with Whisper, voice packs or leftover Gemma files.
/// A file that is already complete is reused; a sibling <c>.partial</c> is resumed or promoted.
/// </summary>
public readonly record struct LocalLlmStatus(string? Path, long Bytes, long Expected, bool Ready, bool Partial)
{
    public bool Missing => !Ready && !Partial;
    public double Progress => Expected <= 0 ? 0 : Math.Clamp(Bytes / (double)Expected, 0, 1);
}

public static class LocalLlmFile
{
    public const string FileName = "Qwen_Qwen3-4B-Instruct-2507-Q4_K_M.gguf";
    public const long ExpectedBytes = 2_497_280_736;
    public const string Sha256 = "2fde00ce69dd4899c70d020845e2638353015bba0fdf161b3eb965f2bca4464e";
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

        DemoteIncomplete(ready, partial);

        if (TryPromote(partial, ready))
            return new LocalLlmStatus(ready, new FileInfo(ready).Length, ExpectedBytes, true, false);

        if (IsUsableGguf(ready, out long readyBytes))
            return new LocalLlmStatus(ready, readyBytes, ExpectedBytes, true, false);

        foreach (var candidate in Directory.EnumerateFiles(directory, "Qwen*4B*Instruct*.gguf"))
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

    public static bool HasGgufMagic(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> magic = stackalloc byte[4];
            return stream.Read(magic) == 4
                && magic[0] == (byte)'G' && magic[1] == (byte)'G'
                && magic[2] == (byte)'U' && magic[3] == (byte)'F';
        }
        catch (IOException)
        {
            return File.Exists(path) && new FileInfo(path).Length >= 4;
        }
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
        return bytes == ExpectedBytes;
    }

    private static void DemoteIncomplete(string ready, string partial)
    {
        if (!File.Exists(ready)) return;
        long bytes = new FileInfo(ready).Length;
        if (bytes == ExpectedBytes) return;
        try
        {
            if (bytes <= 0)
            {
                File.Delete(ready);
                return;
            }
            if (File.Exists(partial))
            {
                if (new FileInfo(partial).Length >= bytes)
                {
                    File.Delete(ready);
                    return;
                }
                File.Delete(partial);
            }
            File.Move(ready, partial);
        }
        catch (IOException) { }
    }

    private static bool TryPromote(string partial, string ready)
    {
        if (!File.Exists(partial)) return false;
        long bytes = new FileInfo(partial).Length;
        if (bytes != ExpectedBytes) return false;
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
