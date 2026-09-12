using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using LLama;
using LLama.Common;
using LLama.Sampling;
using LLama.Transformers;
using PomoDock.Core;

namespace PomoDock.App;

internal interface IAgentConversation : IDisposable
{
    Task<string> AskAsync(string text, CancellationToken cancellationToken);
    Task<string> AskAsync(string text, IProgress<string>? tokens, CancellationToken cancellationToken);
}

/// <summary>
/// Owns PomoDock's local language model. The weights live beside the Whisper model in the
/// user's data directory; nothing is sent to a service and no Ollama installation is required.
/// </summary>
internal sealed class LocalAgentModel : IDisposable
{
    public const string FileName = LocalLlmFile.FileName;
    public const long ExpectedBytes = LocalLlmFile.ExpectedBytes;
    public const string Sha256 = LocalLlmFile.Sha256;
    private const string DownloadUrl = "https://huggingface.co/bartowski/Qwen_Qwen3-4B-Instruct-2507-GGUF/resolve/main/Qwen_Qwen3-4B-Instruct-2507-Q4_K_M.gguf?download=true";

    private static readonly HttpClient http = CreateDownloadClient();

    private LLamaWeights? weights;
    private ModelParams? parameters;
    private readonly object lifetimeGate = new();
    private int contextLeases;
    private bool disposed;
    public string DataPath { get; }
    public string ModelPath { get; private set; }
    public LocalLlmStatus FileStatus => LocalLlmFile.Inspect(DataPath);
    public bool IsDownloaded => RefreshReady();
    public bool IsLoaded => weights is not null;
    public string Backend { get; private set; } = "";

    public LocalAgentModel(string dataPath)
    {
        DataPath = dataPath;
        var status = LocalLlmFile.Inspect(dataPath);
        ModelPath = status.Path is { Length: > 0 } && status.Ready ? status.Path : LocalLlmFile.DefaultPath(dataPath);
    }

    private bool RefreshReady()
    {
        var status = LocalLlmFile.Inspect(DataPath);
        if (status.Ready && status.Path is { Length: > 0 }) ModelPath = status.Path;
        return status.Ready;
    }

    public async Task DownloadAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (RefreshReady()) { progress?.Report(1); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        string partial = LocalLlmFile.PartialPath(DataPath);
        Exception? last = null;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            if (RefreshReady()) { progress?.Report(1); return; }
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(12, 2 * attempt)), cancellationToken);
            try
            {
                await DownloadOnceAsync(partial, progress, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && IsTransient(ex))
            {
                last = ex;
                if (attempt >= 5) break;
            }
        }
        throw Explain(last ?? new IOException("La descarga del modelo falló."), PartialBytes(partial));
    }

    private async Task DownloadOnceAsync(string partial, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        long existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (existing > ExpectedBytes || (existing > 0 && !LocalLlmFile.HasGgufMagic(partial)))
        {
            File.Delete(partial);
            existing = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, DownloadUrl)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await SendFollowingRedirects(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            if (File.Exists(partial)) File.Delete(partial);
            throw new HttpRequestException("El servidor rechazó el punto de reanudación.");
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType is { } media
            && media.Contains("html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Hugging Face devolvió una página en lugar del modelo. Revisa la conexión e inténtalo de nuevo.");

        long? contentLength = response.Content.Headers.ContentLength;
        bool remainder = existing > 0 && response.StatusCode == HttpStatusCode.OK && contentLength == ExpectedBytes - existing;
        bool resumed = existing > 0 && (response.StatusCode == HttpStatusCode.PartialContent || remainder);
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

        long length = new FileInfo(partial).Length;
        if (length >= 4 && !LocalLlmFile.HasGgufMagic(partial))
        {
            File.Delete(partial);
            throw new InvalidDataException("El archivo descargado no es un modelo GGUF. Se descartó para no reanudar basura.");
        }
        if (length != ExpectedBytes)
            throw new IOException($"El modelo llegó incompleto ({length:N0} de {ExpectedBytes:N0} bytes). La descarga se conservará para continuarla.");

        await using (var check = File.OpenRead(partial))
        {
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(check, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(digest, Sha256, StringComparison.Ordinal))
            {
                File.Delete(partial);
                throw new InvalidDataException("La firma del modelo no coincide. No se cargó el archivo por seguridad.");
            }
        }
        File.Move(partial, LocalLlmFile.DefaultPath(DataPath), true);
        ModelPath = LocalLlmFile.DefaultPath(DataPath);
        progress?.Report(1);
    }

    private static HttpClient CreateDownloadClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(30)
        };
        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "PomoDock/0.2 (Windows; local-agent)");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "identity");
        return client;
    }

    private static async Task<HttpResponseMessage> SendFollowingRedirects(HttpRequestMessage seed, CancellationToken cancellationToken)
    {
        Uri uri = seed.RequestUri ?? throw new InvalidOperationException("Falta la URL del modelo.");
        RangeHeaderValue? range = seed.Headers.Range;
        for (int hop = 0; hop < 8; hop++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, uri)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
            if (range is not null) request.Headers.Range = range;
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
                return response;
            Uri? next = response.Headers.Location;
            response.Dispose();
            if (next is null) throw new HttpRequestException("Hugging Face redirigió sin destino.");
            uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
        }
        throw new HttpRequestException("Demasiadas redirecciones al pedir el modelo.");
    }

    private static bool IsTransient(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is InvalidDataException) return false;
            if (current is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Gone })
                return false;
            if (current is CryptographicException or HttpRequestException or SocketException or IOException)
                return true;
        }
        return false;
    }

    private static IOException Explain(Exception ex, long copied)
    {
        string kept = copied > 0 ? " El archivo a medias se conservó para reanudar." : "";
        return new IOException("La conexión se cortó a mitad de la descarga." + kept + " " + Flatten(ex), ex);
    }

    internal static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message) && (parts.Count == 0 || parts[^1] != current.Message))
                parts.Add(current.Message);
        }
        return string.Join(" — ", parts);
    }

    private static long PartialBytes(string partial) =>
        File.Exists(partial) ? new FileInfo(partial).Length : 0;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (weights is not null) return;
        if (!IsDownloaded) throw new FileNotFoundException("No se encontró el modelo local completo.", ModelPath);
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            // CPU only. Vulkan crashed the whole process mid-inference on this hardware;
            // one backend, one model (Qwen3-4B), no GPU fallback path.
            var loadedParameters = Parameters();
            var loadedWeights = LLamaWeights.LoadFromFile(loadedParameters);
            lock (lifetimeGate)
            {
                if (disposed)
                {
                    loadedWeights.Dispose();
                    throw new ObjectDisposedException(nameof(LocalAgentModel));
                }
                parameters = loadedParameters;
                weights = loadedWeights;
                Backend = "CPU · Qwen3-4B";
            }
        }, cancellationToken);
    }

    private ModelParams Parameters() => new(ModelPath)
    {
        ContextSize = 4096,
        BatchSize = 128,
        UBatchSize = 64,
        Threads = Math.Max(4, Environment.ProcessorCount - 2),
        BatchThreads = Math.Max(4, Environment.ProcessorCount - 2),
        GpuLayerCount = 0,
        FlashAttention = false,
        UseMemorymap = true
    };

    public IAgentConversation Begin(string systemPrompt)
    {
        LLamaWeights activeWeights;
        ModelParams activeParameters;
        lock (lifetimeGate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(LocalAgentModel));
            activeWeights = weights ?? throw new InvalidOperationException("El modelo todavía no está cargado.");
            activeParameters = parameters ?? throw new InvalidOperationException("Faltan los parámetros del modelo.");
            contextLeases++;
        }
        try { return new Conversation(activeWeights, activeParameters, systemPrompt, ReleaseContext); }
        catch { ReleaseContext(); throw; }
    }

    public void Dispose()
    {
        LLamaWeights? release = null;
        lock (lifetimeGate)
        {
            disposed = true;
            if (contextLeases == 0)
            {
                release = weights;
                weights = null;
                parameters = null;
            }
        }
        release?.Dispose();
    }

    private void ReleaseContext()
    {
        LLamaWeights? release = null;
        lock (lifetimeGate)
        {
            if (contextLeases > 0) contextLeases--;
            if (disposed && contextLeases == 0)
            {
                release = weights;
                weights = null;
                parameters = null;
            }
        }
        release?.Dispose();
    }

    internal sealed class Conversation : IAgentConversation
    {
        private readonly LLamaContext context;
        private readonly ChatSession session;
        private readonly Action releaseContext;
        private readonly SemaphoreSlim turnGate = new(1, 1);
        private readonly object contextGate = new();
        private bool running;
        private bool disposeRequested;
        private bool contextDisposed;
        /// <summary>
        /// True after a soft-stop or mid-stream abort. The KV cache / sampler must not be reused.
        /// </summary>
        private bool corrupt;
        private readonly DefaultSamplingPipeline sampling = new()
        {
            Temperature = 0.3f,
            TopP = 0.8f,
            RepeatPenalty = 1.08f,
            Grammar = TryGrammar()
        };
        private readonly InferenceParams inference;
        private static readonly TimeSpan SoftDeadline = TimeSpan.FromSeconds(90);

        private static Grammar? TryGrammar()
        {
            try { return new Grammar(AgentGrammar.JsonTurn, "root"); }
            catch (Exception ex)
            {
                Debug.WriteLine("agent grammar skipped: " + ex.Message);
                return null;
            }
        }

        public Conversation(LLamaWeights weights, ModelParams parameters, string systemPrompt, Action releaseContext)
        {
            this.releaseContext = releaseContext;
            context = weights.CreateContext(parameters);
            var history = new ChatHistory();
            history.AddMessage(AuthorRole.System, systemPrompt);
            session = new ChatSession(new InteractiveExecutor(context), history);
            session.WithHistoryTransform(new PromptTemplateTransformer(weights, withAssistant: true));
            inference = new InferenceParams
            {
                MaxTokens = 220,
                SamplingPipeline = sampling
            };
        }

        public Task<string> AskAsync(string text, CancellationToken cancellationToken) =>
            AskAsync(text, null, cancellationToken);

        public async Task<string> AskAsync(string text, IProgress<string>? tokens, CancellationToken cancellationToken)
        {
            await turnGate.WaitAsync(cancellationToken);
            lock (contextGate)
            {
                if (disposeRequested || contextDisposed || corrupt)
                {
                    turnGate.Release();
                    throw new ObjectDisposedException(nameof(Conversation));
                }
                running = true;
            }
            // Never CancelAfter / break into LLamaSharp mid-stream — that native abort closed the whole app.
            // Bound wall-clock with WhenAny; on timeout abandon the turn and never touch the context again.
            var turn = Task.Run(() => ReadTurn(text, tokens, cancellationToken), CancellationToken.None);
            bool abandoned = false;
            try
            {
                var finished = await Task.WhenAny(turn, Task.Delay(SoftDeadline, CancellationToken.None));
                if (finished != turn)
                {
                    abandoned = true;
                    MarkCorrupt();
                    _ = turn.ContinueWith(AbandonedTurnCleanup, TaskScheduler.Default);
                    throw new OperationCanceledException();
                }
                string answer = await turn;
                return answer;
            }
            catch
            {
                MarkCorrupt();
                if (!abandoned && !turn.IsCompleted)
                {
                    abandoned = true;
                    _ = turn.ContinueWith(AbandonedTurnCleanup, TaskScheduler.Default);
                }
                throw;
            }
            finally
            {
                if (!abandoned)
                {
                    bool disposeNow;
                    lock (contextGate)
                    {
                        running = false;
                        disposeNow = (disposeRequested || corrupt) && !contextDisposed;
                    }
                    if (disposeNow) DisposeContext();
                    turnGate.Release();
                }
            }
        }

        private void AbandonedTurnCleanup(Task _)
        {
            bool disposeNow;
            lock (contextGate)
            {
                running = false;
                disposeNow = !contextDisposed;
                corrupt = true;
                disposeRequested = true;
            }
            if (disposeNow)
            {
                try { DisposeContext(); } catch { /* native may already be gone */ }
            }
            try { turnGate.Release(); } catch (SemaphoreFullException) { }
        }

        private string ReadTurn(string text, IProgress<string>? tokens, CancellationToken cancellationToken)
        {
            sampling.Reset();
            var output = new System.Text.StringBuilder();
            var visible = new System.Text.StringBuilder();
            int ticks = 0;
            bool jsonComplete = false;
            bool cancelSeen = false;
            // CancellationToken.None into ChatAsync: cancelling the native sampler mid-token crashed the process.
            foreach (var token in session.ChatAsync(
                new ChatHistory.Message(AuthorRole.User, text), inference, CancellationToken.None).ToBlockingEnumerable(CancellationToken.None))
            {
                if (cancellationToken.IsCancellationRequested) cancelSeen = true;
                output.Append(token);
                string stripped = AgentThink.Strip(output.ToString());
                if (cancelSeen) continue; // keep draining silently so the sampler stays consistent
                if (!jsonComplete)
                {
                    string live = AgentThink.LiveText(stripped);
                    if (live.Length > 0 && live != visible.ToString())
                    {
                        visible.Clear().Append(live);
                        tokens?.Report(live);
                    }
                    else if (live.Length == 0 && ++ticks % 4 == 0)
                        tokens?.Report("\u0001");
                    jsonComplete = ContainsCompleteJson(stripped);
                }
                // Always drain to EOS/MaxTokens before leaving this loop (safe multi-turn reuse).
            }
            string answer = AgentThink.Strip(output.ToString());
            Debug.WriteLine("LOCAL AGENT: " + answer);
            if (cancelSeen || !ContainsCompleteJson(answer))
                MarkCorrupt();
            if (cancelSeen)
                throw new OperationCanceledException(cancellationToken);
            return answer;
        }

        private void MarkCorrupt()
        {
            lock (contextGate) corrupt = true;
        }

        private static bool ContainsCompleteJson(string text)
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

        public void Dispose()
        {
            bool disposeNow;
            lock (contextGate)
            {
                disposeRequested = true;
                disposeNow = !running && !contextDisposed;
            }
            if (disposeNow) DisposeContext();
        }

        public bool IsCorrupt
        {
            get { lock (contextGate) return corrupt || contextDisposed; }
        }

        private void DisposeContext()
        {
            lock (contextGate)
            {
                if (contextDisposed) return;
                contextDisposed = true;
            }
            try { context.Dispose(); }
            finally { releaseContext(); }
        }
    }
}
