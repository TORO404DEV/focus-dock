using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using NAudio.Wave;
using SharpCompress.Readers;
using SherpaOnnx;

namespace PomoDock.App;

/// <summary>Offline neural speech with a Mexican Spanish voice and an American English voice.</summary>
internal sealed class LocalAgentVoice : IDisposable
{
    private sealed record VoiceSpec(string Folder, string Model, string Url, long Bytes, string Sha256);
    private static readonly VoiceSpec Spanish = new(
        "vits-piper-es_MX-claude-high", "es_MX-claude-high.onnx",
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-es_MX-claude-high.tar.bz2",
        67_207_890, "ec33fb689c248fe64810aab564cba97babf0f506672cfd404928d46e751a4721");
    private static readonly VoiceSpec English = new(
        "vits-piper-en_US-lessac-medium", "en_US-lessac-medium.onnx",
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-en_US-lessac-medium.tar.bz2",
        67_230_653, "9e3febfacf0abf4270172d2958bcec246032b7e88efc2720840cc80c93de334e");
    private static readonly HttpClient http = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = Timeout.InfiniteTimeSpan };

    private readonly string root;
    private OfflineTts? tts;
    private string loadedLanguage = "";
    private WaveOutEvent? output;
    private AudioFileReader? reader;
    private CancellationTokenSource? speech;
    private readonly SemaphoreSlim voiceGate = new(1, 1);

    public LocalAgentVoice(string dataPath) => root = Path.Combine(dataPath, "models", "voice");

    public async Task SpeakAsync(string text, string language, double speed, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Stop();
        var activeSpeech = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        speech = activeSpeech;
        string cache = Path.Combine(root, "cache"); Directory.CreateDirectory(cache);
        await voiceGate.WaitAsync(activeSpeech.Token);
        try
        {
            var spec = language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? English : Spanish;
            await EnsureVoiceAsync(spec, tts is null ? progress : null, activeSpeech.Token);
            if (tts is null || loadedLanguage != spec.Folder)
            {
                tts?.Dispose();
                tts = await Task.Run(() => Build(spec), activeSpeech.Token);
                loadedLanguage = spec.Folder;
            }
            progress?.Report(.6);
            var parts = SpokenParts(text);
            if (parts.Count == 0) return;
            async Task<string> Make(string sentence)
            {
                string wave = Path.Combine(cache, $"agent-{Guid.NewGuid():N}.wav");
                await RenderReadyAsync(sentence, speed, wave, activeSpeech.Token);
                return wave;
            }
            string current = await Make(parts[0]);
            progress?.Report(.99);
            for (int i = 0; i < parts.Count; i++)
            {
                activeSpeech.Token.ThrowIfCancellationRequested();
                var next = i + 1 < parts.Count ? Make(parts[i + 1]) : null;
                await PlayWaveAsync(current, activeSpeech);
                try { if (File.Exists(current)) File.Delete(current); } catch { }
                if (next is not null) current = await next;
            }
        }
        finally
        {
            output?.Dispose(); output = null; reader?.Dispose(); reader = null;
            if (ReferenceEquals(speech, activeSpeech)) speech = null;
            voiceGate.Release();
            activeSpeech.Dispose();
        }
    }

    internal async Task RenderToWaveAsync(string text, string language, double speed, string wave, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var spec = language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? English : Spanish;
        await EnsureVoiceAsync(spec, progress, cancellationToken);
        if (tts is null || loadedLanguage != spec.Folder)
        {
            tts?.Dispose();
            tts = await Task.Run(() => Build(spec), cancellationToken);
            loadedLanguage = spec.Folder;
        }
        progress?.Report(.6);
        await RenderReadyAsync(text, speed, wave, cancellationToken);
        progress?.Report(1);
    }

    private async Task RenderReadyAsync(string text, double speed, string wave, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            var config = new OfflineTtsGenerationConfig { Sid = 0, Speed = (float)Math.Clamp(speed, 0.75, 1.4), SilenceScale = 0.18f };
            string spoken = text.Length > 220 ? text[..220] : text;
            var audio = tts!.GenerateWithConfig(spoken, config, null);
            cancellationToken.ThrowIfCancellationRequested();
            if (!audio.SaveToWaveFile(wave)) throw new InvalidOperationException("No se pudo crear el audio sintetizado.");
        }, cancellationToken);
    }

    private async Task PlayWaveAsync(string wave, CancellationTokenSource activeSpeech)
    {
        reader = new AudioFileReader(wave);
        output = new WaveOutEvent(); output.Init(reader);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, e) => { if (e.Exception is null) finished.TrySetResult(); else finished.TrySetException(e.Exception); };
        using var registration = activeSpeech.Token.Register(() => { try { output?.Stop(); } catch { } });
        output.Play();
        await finished.Task;
        output?.Dispose(); output = null; reader?.Dispose(); reader = null;
    }

    private static List<string> SpokenParts(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return [];
        if (text.Length <= 140) return [text];
        var parts = new List<string>();
        var buffer = new System.Text.StringBuilder();
        foreach (char c in text)
        {
            buffer.Append(c);
            if (c is '.' or '!' or '?' or '…' && buffer.Length >= 32)
            {
                parts.Add(buffer.ToString().Trim());
                buffer.Clear();
                if (parts.Count >= 4) return parts;
            }
        }
        if (buffer.Length > 0 && parts.Count < 4) parts.Add(buffer.ToString().Trim());
        return parts.Where(part => part.Length > 0).ToList();
    }

    public async Task WarmAsync(string language, CancellationToken cancellationToken)
    {
        await voiceGate.WaitAsync(cancellationToken);
        try
        {
            var spec = language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? English : Spanish;
            await EnsureVoiceAsync(spec, null, cancellationToken);
            if (tts is not null && loadedLanguage == spec.Folder) return;
            tts?.Dispose();
            tts = await Task.Run(() => Build(spec), cancellationToken);
            loadedLanguage = spec.Folder;
        }
        finally { voiceGate.Release(); }
    }

    public void Stop()
    {
        speech?.Cancel();
        try { output?.Stop(); } catch { }
    }

    private OfflineTts Build(VoiceSpec spec)
    {
        string directory = Path.Combine(root, spec.Folder);
        var config = new OfflineTtsConfig();
        config.Model.Vits.Model = Path.Combine(directory, spec.Model);
        config.Model.Vits.Tokens = Path.Combine(directory, "tokens.txt");
        config.Model.Vits.DataDir = Path.Combine(directory, "espeak-ng-data");
        config.Model.Vits.NoiseScale = 0.667f;
        config.Model.Vits.NoiseScaleW = 0.8f;
        config.Model.Vits.LengthScale = 1;
        config.Model.NumThreads = Math.Max(4, Environment.ProcessorCount - 2); config.Model.Provider = "cpu"; config.MaxNumSentences = 1;
        return new OfflineTts(config);
    }

    private async Task EnsureVoiceAsync(VoiceSpec spec, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        string destination = Path.Combine(root, spec.Folder);
        if (File.Exists(Path.Combine(destination, spec.Model)) && File.Exists(Path.Combine(destination, "tokens.txt")))
        {
            progress?.Report(.35);
            return;
        }
        Directory.CreateDirectory(root);
        string archive = Path.Combine(root, spec.Folder + ".tar.bz2");
        await DownloadAsync(spec, archive, progress, cancellationToken);
        string staging = Path.Combine(root, ".extract-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        try
        {
            await Task.Run(() => ExtractSafe(archive, staging, cancellationToken), cancellationToken);
            string extracted = Path.Combine(staging, spec.Folder);
            if (!File.Exists(Path.Combine(extracted, spec.Model))) throw new InvalidDataException("El paquete de voz no contiene el modelo esperado.");
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            Directory.Move(extracted, destination);
            File.Delete(archive);
        }
        finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { } }
    }

    private static async Task DownloadAsync(VoiceSpec spec, string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && new FileInfo(path).Length != spec.Bytes) File.Delete(path);
        if (!File.Exists(path))
        {
            using var response = await http.GetAsync(spec.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken); response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 256 * 1024, true);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024); long readTotal = 0;
            try
            {
                while (true)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken); if (read == 0) break;
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken); readTotal += read; progress?.Report((double)readTotal / spec.Bytes);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        if (new FileInfo(path).Length != spec.Bytes) throw new InvalidDataException("La voz local llegó incompleta.");
        await using var check = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(check, cancellationToken)).ToLowerInvariant();
        if (hash != spec.Sha256) throw new InvalidDataException("La firma de la voz local no coincide.");
    }

    private static void ExtractSafe(string archive, string destination, CancellationToken cancellationToken)
    {
        string safeRoot = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var file = File.OpenRead(archive); using var reader = ReaderFactory.OpenReader(file);
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested(); var entry = reader.Entry;
            if (entry.IsDirectory) continue;
            string relative = (entry.Key ?? throw new InvalidDataException("Entrada de voz sin nombre.")).Replace('/', Path.DirectorySeparatorChar);
            string target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Ruta insegura dentro del paquete de voz.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = reader.OpenEntryStream(); using var output = File.Create(target); source.CopyTo(output);
        }
    }

    public void Dispose() { Stop(); tts?.Dispose(); tts = null; }
}
