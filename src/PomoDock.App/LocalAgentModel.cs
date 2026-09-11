using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using LLama;
using LLama.Common;
using LLama.Sampling;
using LLama.Transformers;

namespace PomoDock.App;

internal interface IAgentConversation : IDisposable
{
    Task<string> AskAsync(string text, CancellationToken cancellationToken);
}

/// <summary>
/// Owns PomoDock's local language model. The weights live beside the Whisper model in the
/// user's data directory; nothing is sent to a service and no Ollama installation is required.
/// </summary>
internal sealed class LocalAgentModel : IDisposable
{
    public const string FileName = "Qwen3-4B-Q4_K_M.gguf";
    public const long ExpectedBytes = 2_497_280_640;
    public const string Sha256 = "ab27b9bfa375a178d6cba48f3ad892b94b7739659dcc7aae8058ce0ffed6b328";
    private const string DownloadUrl = "https://huggingface.co/ggml-org/Qwen3-4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf";

    private static readonly HttpClient http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private LLamaWeights? weights;
    private ModelParams? parameters;
    private readonly object lifetimeGate = new();
    private bool disposed;
    public string ModelPath { get; }
    public bool IsDownloaded => File.Exists(ModelPath) && new FileInfo(ModelPath).Length == ExpectedBytes;
    public bool IsLoaded => weights is not null;
    public string Backend { get; private set; } = "";

    public LocalAgentModel(string dataPath)
    {
        ModelPath = Path.Combine(dataPath, "models", FileName);
    }

    public async Task DownloadAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        var partial = ModelPath + ".partial";
        long existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (existing > ExpectedBytes) { File.Delete(partial); existing = 0; }

        using var request = new HttpRequestMessage(HttpMethod.Get, DownloadUrl);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        bool resumed = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed) existing = 0;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(partial, resumed ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            long copied = existing;
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copied += read;
                progress?.Report(Math.Clamp((double)copied / ExpectedBytes, 0, 1));
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        await destination.FlushAsync(cancellationToken);

        if (new FileInfo(partial).Length != ExpectedBytes)
            throw new InvalidDataException($"El modelo llegó incompleto ({new FileInfo(partial).Length:N0} de {ExpectedBytes:N0} bytes). La descarga se conservará para continuarla.");
        await using (var check = File.OpenRead(partial))
        {
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(check, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(digest, Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("La firma del modelo no coincide. No se cargó el archivo por seguridad.");
        }
        File.Move(partial, ModelPath, true);
        progress?.Report(1);
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (weights is not null) return;
        if (!IsDownloaded) throw new FileNotFoundException("No se encontró el modelo local completo.", ModelPath);
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Qwen 3 4B Q4 fits inside this machine's 4 GB RX 580 together with a compact
            // 4K context. Vulkan can offload all layers; llama.cpp spills safely if needed.
            ModelParams loadedParameters = Parameters(99);
            LLamaWeights loadedWeights;
            string loadedBackend;
            try
            {
                loadedWeights = LLamaWeights.LoadFromFile(loadedParameters);
                loadedBackend = "VULKAN · RX 580";
            }
            catch (Exception gpuError)
            {
                Debug.WriteLine("Vulkan load failed; retrying on CPU: " + gpuError);
                loadedParameters = Parameters(0);
                loadedWeights = LLamaWeights.LoadFromFile(loadedParameters);
                loadedBackend = "CPU · RYZEN 5";
            }
            lock (lifetimeGate)
            {
                if (disposed)
                {
                    loadedWeights.Dispose();
                    throw new ObjectDisposedException(nameof(LocalAgentModel));
                }
                parameters = loadedParameters;
                weights = loadedWeights;
                Backend = loadedBackend;
            }
        }, cancellationToken);
    }

    private ModelParams Parameters(int gpuLayers) => new(ModelPath)
    {
        ContextSize = 4096,
        BatchSize = 256,
        UBatchSize = 128,
        Threads = 6,
        BatchThreads = 6,
        GpuLayerCount = gpuLayers,
        UseMemorymap = true
    };

    public IAgentConversation Begin(string systemPrompt)
    {
        if (weights is null || parameters is null) throw new InvalidOperationException("El modelo todavía no está cargado.");
        return new Conversation(weights, parameters, systemPrompt);
    }

    public void Dispose()
    {
        lock (lifetimeGate)
        {
            disposed = true;
            weights?.Dispose();
            weights = null;
        }
    }

    internal sealed class Conversation : IAgentConversation
    {
        private readonly LLamaContext context;
        private readonly ChatSession session;
        private readonly InferenceParams inference = new()
        {
            MaxTokens = 220,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0.1f,
                TopP = 0.82f,
                RepeatPenalty = 1.05f
            }
        };

        public Conversation(LLamaWeights weights, ModelParams parameters, string systemPrompt)
        {
            context = weights.CreateContext(parameters);
            var history = new ChatHistory();
            history.AddMessage(AuthorRole.System, systemPrompt);
            session = new ChatSession(new InteractiveExecutor(context), history);
            session.WithHistoryTransform(new PromptTemplateTransformer(weights, withAssistant: true));
        }

        public async Task<string> AskAsync(string text, CancellationToken cancellationToken)
        {
            var output = new System.Text.StringBuilder();
            await foreach (var token in session.ChatAsync(
                new ChatHistory.Message(AuthorRole.User, text), inference, cancellationToken))
            {
                output.Append(token);
                // Agent turns are exactly one JSON object. Stop native generation the instant
                // that object closes instead of paying for Qwen to continue with commentary.
                if (ContainsCompleteJson(output)) break;
            }
            Debug.WriteLine("LOCAL AGENT: " + output);
            return output.ToString().Trim();
        }

        private static bool ContainsCompleteJson(System.Text.StringBuilder text)
        {
            bool started = false, quoted = false, escaped = false;
            int depth = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (!started) { if (c == '{') { started = true; depth = 1; } continue; }
                if (escaped) { escaped = false; continue; }
                if (quoted && c == '\\') { escaped = true; continue; }
                if (c == '"') { quoted = !quoted; continue; }
                if (quoted) continue;
                if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return true;
            }
            return false;
        }

        public void Dispose() => context.Dispose();
    }
}
