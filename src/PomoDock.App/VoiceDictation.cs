using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;
using PomoDock.Core;
using Whisper.net;

namespace PomoDock.App;

/// <summary>
/// A local Whisper microphone for whichever PomoDock editor owns the caret. The language is the
/// effective language selected in Settings, including the resolved value of “same as the system”.
/// </summary>
internal sealed class VoiceDictation : IDisposable
{
    private const int SampleRate = 16_000;
    private const int MaximumSeconds = 60;
    private const long MinimumWaveBytes = 8_000;
    private const long ModelBytes = 190_085_487;
    private const string ModelFile = "ggml-small-q5_1.bin";
    private const string ModelSha256 = "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb";
    private const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/c521a4b02f422512d734391fdf08bb08c0862f68/ggml-small-q5_1.bin";
    private static readonly SemaphoreSlim ModelGate = new(1, 1);
    private static readonly HttpClient ModelClient = CreateModelClient();
    private static VoiceDictation? current;
    private static bool handlersRegistered;

    private readonly MainWindow owner;
    private readonly Popup popup;
    private readonly Grid popupSurface;
    private readonly Button microphone;
    private readonly Border feedback;
    private readonly TextBlock feedbackText;
    private readonly DispatcherTimer recordingClock;
    private readonly DispatcherTimer feedbackHideClock;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim inferenceGate = new(1, 1);
    private readonly object audioGate = new();
    private CancellationTokenSource? operation;
    private CancellationTokenSource? previewCancellation;
    private WhisperFactory? whisperFactory;
    private string? loadedModel;
    private TextBoxBase? target;
    private TextBox? provisionalTarget;
    private int provisionalStart;
    private int provisionalOriginalLength;
    private int provisionalLength;
    private string provisionalPrefix = "";
    private bool provisionalActive;
    private Thickness originalPadding;
    private WaveInEvent? capture;
    private WaveFileWriter? writer;
    private string? recordingPath;
    private MemoryStream? liveAudio;
    private long recordedBytes;
    private long nextPreviewBytes;
    private DateTime recordingStartedUtc;
    private double inputPeak;
    private bool heardSignal;
    private string livePreview = "";
    private int previewBusy;
    private Exception? runtimeFailure;
    private bool paddingReserved;
    private bool recording;
    private bool busy;
    private bool stopRequested;
    private bool disposed;

    private VoiceDictation(MainWindow owner)
    {
        this.owner = owner;
        microphone = new Button
        {
            Width = 29, Height = 29, Padding = new Thickness(5), Margin = new Thickness(0),
            Focusable = false, IsTabStop = false, BorderThickness = new Thickness(1.25),
            Content = MicrophoneIcon()
        };
        microphone.SetResourceReference(Control.BackgroundProperty, "Raised");
        microphone.SetResourceReference(Control.BorderBrushProperty, "Edge");
        microphone.Click += Toggle;

        feedbackText = new TextBlock
        {
            FontSize = 10, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0)
        };
        feedbackText.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        feedback = new Border
        {
            Height = 28, Margin = new Thickness(0, 34, 0, 0), BorderThickness = new Thickness(1.25),
            VerticalAlignment = VerticalAlignment.Top, Child = feedbackText, Visibility = Visibility.Collapsed
        };
        feedback.SetResourceReference(Border.BackgroundProperty, "Raised");
        feedback.SetResourceReference(Border.BorderBrushProperty, "Edge");
        popupSurface = new Grid { Width = 320, Height = 30 };
        popupSurface.Children.Add(feedback);
        popupSurface.Children.Add(microphone);
        microphone.HorizontalAlignment = HorizontalAlignment.Right;
        microphone.VerticalAlignment = VerticalAlignment.Top;

        recordingClock = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        recordingClock.Tick += (_, _) => RenderRecordingFeedback();
        feedbackHideClock = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        feedbackHideClock.Tick += (_, _) => { feedbackHideClock.Stop(); HideFeedback(); };
        popup = new Popup
        {
            Child = popupSurface, AllowsTransparency = true, StaysOpen = true, Focusable = false,
            Placement = PlacementMode.Relative, PopupAnimation = PopupAnimation.Fade
        };
        RefreshLanguage();
        WarmModelInBackground();
    }

    public static VoiceDictation Attach(MainWindow owner)
    {
        current?.Dispose();
        current = new VoiceDictation(owner);
        if (!handlersRegistered)
        {
            EventManager.RegisterClassHandler(typeof(TextBoxBase), Keyboard.GotKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler(InputFocused), true);
            EventManager.RegisterClassHandler(typeof(TextBoxBase), Keyboard.LostKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler(InputBlurred), true);
            handlersRegistered = true;
        }
        return current;
    }

    private static void InputFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBoxBase input) current?.Show(input);
    }

    private static void InputBlurred(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBoxBase input || current?.target != input) return;
        input.Dispatcher.BeginInvoke(() =>
        {
            if (current?.target == input && !input.IsKeyboardFocusWithin && !current.IsWorking) current.Hide();
        }, DispatcherPriority.Input);
    }

    private bool IsWorking => recording || busy;

    private void Show(TextBoxBase input)
    {
        if (disposed || input.IsReadOnly || !input.IsEnabled || input.ActualWidth < 72 || input.ActualHeight < 25)
        {
            if (!IsWorking) Hide();
            return;
        }
        if (IsWorking && target != input) return;
        if (target != input)
        {
            Hide();
            target = input;
            originalPadding = input.Padding;
            input.Padding = new Thickness(originalPadding.Left, originalPadding.Top,
                originalPadding.Right + 35, originalPadding.Bottom);
            paddingReserved = true;
            input.SizeChanged += TargetSizeChanged;
            input.IsVisibleChanged += TargetVisibilityChanged;
        }
        RefreshLanguage();
        popupSurface.Width = Math.Max(58, Math.Min(360, input.ActualWidth - 7));
        Position();
        popup.IsOpen = true;
    }

    private void TargetSizeChanged(object sender, SizeChangedEventArgs e) => Position();
    private void TargetVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (target is null || target.IsVisible) return;
        CancelWork();
        Hide(true);
    }

    private void Position()
    {
        if (target is null) return;
        popup.PlacementTarget = target;
        popupSurface.Width = Math.Max(58, Math.Min(360, target.ActualWidth - 7));
        popup.HorizontalOffset = Math.Max(2, target.ActualWidth - popupSurface.Width - 5);
        popup.VerticalOffset = Math.Max(2, Math.Min(5, (target.ActualHeight - microphone.Height) / 2));
        if (popup.IsOpen)
        {
            double x = popup.HorizontalOffset;
            popup.HorizontalOffset = x + .01;
            popup.HorizontalOffset = x;
        }
    }

    private void Toggle(object sender, RoutedEventArgs e)
    {
        if (recording) RequestStop();
        else if (!busy) StartRecording();
        e.Handled = true;
    }

    private void StartRecording()
    {
        if (target is null || disposed) return;
        try
        {
            if (WaveInEvent.DeviceCount == 0) throw new InvalidOperationException(L.T("dictation.noMicrophone"));
            target.Focus();
            Keyboard.Focus(target);
            BeginProvisionalText();
            recordingPath = Path.Combine(Path.GetTempPath(), $"pomodock-dictation-{Guid.NewGuid():N}.wav");
            operation?.Dispose();
            operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            previewCancellation?.Cancel();
            previewCancellation?.Dispose();
            previewCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            recordedBytes = 0;
            nextPreviewBytes = SampleRate * 2L * 2;
            recordingStartedUtc = DateTime.UtcNow;
            inputPeak = 0;
            heardSignal = false;
            livePreview = "";
            liveAudio = new MemoryStream(SampleRate * 2 * 12);
            stopRequested = false;
            capture = new WaveInEvent
            {
                DeviceNumber = -1, BufferMilliseconds = 80, NumberOfBuffers = 3,
                WaveFormat = new WaveFormat(SampleRate, 16, 1)
            };
            writer = new WaveFileWriter(recordingPath, capture.WaveFormat);
            capture.DataAvailable += AudioAvailable;
            capture.RecordingStopped += RecordingStopped;
            recording = true;
            SetRecordingVisual();
            recordingClock.Start();
            RenderRecordingFeedback();
            capture.StartRecording();
            owner.Status(L.T("dictation.listening", LanguageName()));
        }
        catch (Exception ex)
        {
            DisposeCapture();
            DisposeLiveAudio();
            operation?.Dispose();
            operation = null;
            recording = false;
            SetIdleVisual();
            var message = L.T("dictation.captureError", Friendly(ex));
            owner.Status(message);
            ShowTransientFeedback(message, 6);
            LogFailure(ex);
            Dialogs.Alert(owner, L.T("dictation.errorTitle"), message);
        }
    }

    private void AudioAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            writer?.Write(e.Buffer, 0, e.BytesRecorded);
            lock (audioGate) liveAudio?.Write(e.Buffer, 0, e.BytesRecorded);
            recordedBytes += e.BytesRecorded;
            double peak = 0;
            for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(e.Buffer, i)) / 32768d);
            inputPeak = Math.Max(peak, inputPeak * .72);
            if (peak >= .012) heardSignal = true;

            if (!stopRequested && recordedBytes >= nextPreviewBytes &&
                Interlocked.CompareExchange(ref previewBusy, 1, 0) == 0 && previewCancellation is { } preview)
            {
                nextPreviewBytes = recordedBytes + SampleRate * 2L * 2;
                byte[] pcm;
                lock (audioGate) pcm = liveAudio?.ToArray() ?? [];
                _ = PreviewAsync(pcm, Strings.Code, preview);
            }
            if (!stopRequested && recordedBytes >= SampleRate * 2L * MaximumSeconds)
                owner.Dispatcher.BeginInvoke(RequestStop);
        }
        catch { owner.Dispatcher.BeginInvoke(RequestStop); }
    }

    private void RequestStop()
    {
        if (!recording || stopRequested) return;
        stopRequested = true;
        previewCancellation?.Cancel();
        recordingClock.Stop();
        microphone.IsEnabled = false;
        owner.Status(L.T("dictation.preparing"));
        ShowFeedback(L.T("dictation.preparing"));
        try { capture?.StopRecording(); }
        catch (Exception ex) { RecordingStopped(capture, new StoppedEventArgs(ex)); }
    }

    private void RecordingStopped(object? sender, StoppedEventArgs e)
    {
        var path = recordingPath;
        recordingPath = null;
        recording = false;
        recordingClock.Stop();
        try { writer?.Dispose(); } catch { }
        writer = null;
        if (capture is { } stopped)
        {
            stopped.DataAvailable -= AudioAvailable;
            stopped.RecordingStopped -= RecordingStopped;
            stopped.Dispose();
        }
        capture = null;
        DisposeLiveAudio();
        _ = owner.Dispatcher.InvokeAsync(async () => await FinishRecordingAsync(path, e.Exception));
    }

    private async Task FinishRecordingAsync(string? path, Exception? captureError)
    {
        if (disposed) { DeleteRecording(path); return; }
        busy = true;
        SetBusyVisual();
        try
        {
            if (captureError is not null) throw captureError;
            if (path is null || !File.Exists(path) || new FileInfo(path).Length < MinimumWaveBytes)
            {
                var shortMessage = L.T("dictation.tooShort");
                owner.Status(shortMessage);
                ShowTransientFeedback(shortMessage);
                return;
            }

            var language = Strings.Code;
            var cancellationToken = operation?.Token ?? lifetime.Token;
            owner.Status(L.T(IsModelReady(ModelPath()) ? "dictation.transcribing" : "dictation.downloading", LanguageName()));
            ShowFeedback(L.T(IsModelReady(ModelPath()) ? "dictation.transcribing" : "dictation.downloading", LanguageName()));
            var model = await EnsureModelAsync(cancellationToken);
            owner.Status(L.T("dictation.transcribing", LanguageName()));
            ShowFeedback(L.T("dictation.transcribing", LanguageName()));
            var text = await Task.Run(() => TranscribeAsync(model, path, language, cancellationToken), cancellationToken);
            if (disposed) return;
            text = Clean(text);
            if (text.Length == 0)
            {
                var emptyMessage = L.T("dictation.nothingHeard");
                owner.Status(emptyMessage);
                ShowTransientFeedback(emptyMessage);
                return;
            }
            InsertAtCaret(text);
            owner.Status(L.T("dictation.inserted", LanguageName()));
            ShowTransientFeedback(L.T("dictation.result", text), 5);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var message = L.T("dictation.error", Friendly(ex));
            owner.Status(message);
            ShowTransientFeedback(message, 7);
            LogFailure(ex);
            Dialogs.Alert(owner, L.T("dictation.errorTitle"), message);
        }
        finally
        {
            DeleteRecording(path);
            busy = false;
            stopRequested = false;
            operation?.Dispose();
            operation = null;
            previewCancellation?.Dispose();
            previewCancellation = null;
            if (!disposed) SetIdleVisual();
        }
    }

    private async Task<string> EnsureModelAsync(CancellationToken cancellationToken)
    {
        var model = ModelPath();
        if (IsModelReady(model)) return model;
        await ModelGate.WaitAsync(cancellationToken);
        try
        {
            if (IsModelReady(model)) return model;
            Directory.CreateDirectory(Path.GetDirectoryName(model)!);
            var partial = model + ".download";
            try
            {
                using var response = await ModelClient.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var destination = new FileStream(partial, FileMode.Create, FileAccess.Write,
                    FileShare.None, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
                destination.Close();
                string actualHash;
                await using (var verify = File.OpenRead(partial))
                    actualHash = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken)).ToLowerInvariant();
                if (!string.Equals(actualHash, ModelSha256, StringComparison.Ordinal))
                    throw new InvalidDataException("Whisper model checksum mismatch.");
                File.Move(partial, model, true);
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
            return model;
        }
        finally { ModelGate.Release(); }
    }

    private async Task PreviewAsync(byte[] pcm, string language, CancellationTokenSource session)
    {
        try
        {
            if (pcm.Length < MinimumWaveBytes || !IsModelReady(ModelPath()) || session.IsCancellationRequested) return;
            byte[] waveBytes;
            using (var buffer = new MemoryStream(pcm.Length + 64))
            {
                using (var wave = new WaveFileWriter(buffer, new WaveFormat(SampleRate, 16, 1)))
                    wave.Write(pcm, 0, pcm.Length);
                waveBytes = buffer.ToArray();
            }
            using var audio = new MemoryStream(waveBytes, writable: false);
            var text = Clean(await TranscribeStreamAsync(ModelPath(), audio, language, session.Token));
            if (text.Length == 0 || session.IsCancellationRequested) return;
            await owner.Dispatcher.InvokeAsync(() =>
            {
                if (!recording || !ReferenceEquals(previewCancellation, session)) return;
                livePreview = text;
                UpdateProvisionalText(text);
                RenderRecordingFeedback();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsNativeRuntimeFailure(ex)) runtimeFailure = ex;
            previewCancellation?.Cancel();
            LogFailure(ex, "live preview");
            await owner.Dispatcher.InvokeAsync(() =>
            {
                if (!recording) return;
                var message = L.T("dictation.error", Friendly(ex));
                owner.Status(message);
                ShowTransientFeedback(message, 7);
            });
        }
        finally { Interlocked.Exchange(ref previewBusy, 0); }
    }

    private async Task<string> TranscribeAsync(string model, string wave, string language, CancellationToken cancellationToken)
    {
        using var audio = File.OpenRead(wave);
        return await TranscribeStreamAsync(model, audio, language, cancellationToken);
    }

    private async Task<string> TranscribeStreamAsync(string model, Stream audio, string language, CancellationToken cancellationToken)
    {
        await inferenceGate.WaitAsync(cancellationToken);
        try
        {
            var factory = FactoryFor(model);
            await using var processor = factory.CreateBuilder().WithLanguage(language).Build();
            var text = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(audio, cancellationToken)) text.Append(segment.Text);
            return text.ToString();
        }
        finally { inferenceGate.Release(); }
    }

    /// <summary>
    /// Loading the model while PomoDock is idle removes roughly half a second from the first
    /// result. Later dictations reuse this factory; only their lightweight processor is rebuilt so
    /// changing Settings between Spanish and English still takes effect immediately.
    /// </summary>
    private void WarmModelInBackground()
    {
        var model = ModelPath();
        if (!IsModelReady(model)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await inferenceGate.WaitAsync(lifetime.Token);
                try { FactoryFor(model); }
                finally { inferenceGate.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (IsNativeRuntimeFailure(ex)) runtimeFailure = ex;
                LogFailure(ex, "runtime warm-up");
            }
        });
    }

    private WhisperFactory FactoryFor(string model)
    {
        if (runtimeFailure is not null) throw new InvalidOperationException(Friendly(runtimeFailure), runtimeFailure);
        if (whisperFactory is not null && string.Equals(loadedModel, model, StringComparison.OrdinalIgnoreCase))
            return whisperFactory;
        whisperFactory?.Dispose();
        whisperFactory = WhisperFactory.FromPath(model);
        loadedModel = model;
        return whisperFactory;
    }

    private void RenderRecordingFeedback()
    {
        if (!recording) return;
        if (livePreview.Length > 0)
        {
            ShowFeedback(L.T("dictation.livePreview", livePreview));
            return;
        }
        var elapsed = DateTime.UtcNow - recordingStartedUtc;
        string level = !heardSignal && inputPeak < .012 && elapsed.TotalSeconds > 1.2 ? L.T("dictation.noSignalShort") : LevelBars(inputPeak);
        ShowFeedback(L.T("dictation.live", $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}", level));
        inputPeak *= .82;
    }

    private static string LevelBars(double peak)
    {
        int lit = Math.Clamp((int)Math.Ceiling(peak * 12), 1, 6);
        return new string('▮', lit) + new string('·', 6 - lit);
    }

    private void ShowFeedback(string text)
    {
        if (disposed) return;
        feedbackHideClock.Stop();
        feedbackText.Text = text;
        feedback.Visibility = Visibility.Visible;
        popupSurface.Height = 64;
        Position();
    }

    private void ShowTransientFeedback(string text, int seconds = 4)
    {
        ShowFeedback(text);
        feedbackHideClock.Interval = TimeSpan.FromSeconds(seconds);
        feedbackHideClock.Start();
    }

    private void HideFeedback()
    {
        feedback.Visibility = Visibility.Collapsed;
        popupSurface.Height = 30;
        Position();
    }

    private void BeginProvisionalText()
    {
        provisionalTarget = target as TextBox;
        provisionalActive = false;
        provisionalLength = 0;
        if (provisionalTarget is null) return;
        provisionalStart = provisionalTarget.SelectionStart;
        provisionalOriginalLength = provisionalTarget.SelectionLength;
        provisionalPrefix = provisionalStart > 0 && !char.IsWhiteSpace(provisionalTarget.Text[provisionalStart - 1]) ? " " : "";
    }

    /// <summary>
    /// Calendar and To Do inputs receive a replaceable draft while the user talks. Replacing the
    /// same span avoids duplicate words, and the latest useful draft remains if final recognition
    /// cannot improve it.
    /// </summary>
    private void UpdateProvisionalText(string text)
    {
        if (provisionalTarget is not { IsVisible: true } plain || !ReferenceEquals(plain, target)) return;
        int replaced = provisionalActive ? provisionalLength : provisionalOriginalLength;
        plain.Select(provisionalStart, Math.Min(replaced, Math.Max(0, plain.Text.Length - provisionalStart)));
        string insertion = provisionalPrefix + text;
        plain.SelectedText = insertion;
        provisionalLength = insertion.Length;
        provisionalActive = true;
        plain.SelectionStart = provisionalStart + provisionalLength;
        plain.SelectionLength = 0;
    }

    private void InsertAtCaret(string text)
    {
        if (target is null || !target.IsVisible) return;
        target.Focus();
        Keyboard.Focus(target);
        if (target is TextBox plain)
        {
            if (provisionalActive && ReferenceEquals(plain, provisionalTarget))
            {
                plain.Select(provisionalStart, Math.Min(provisionalLength, Math.Max(0, plain.Text.Length - provisionalStart)));
                var finalInsertion = provisionalPrefix + text;
                plain.SelectedText = finalInsertion;
                plain.SelectionStart = provisionalStart + finalInsertion.Length;
                plain.SelectionLength = 0;
                provisionalLength = finalInsertion.Length;
                return;
            }
            var insertionStart = plain.SelectionStart;
            var prefix = plain.SelectionStart > 0 && !char.IsWhiteSpace(plain.Text[plain.SelectionStart - 1]) ? " " : "";
            var insertion = prefix + text;
            plain.SelectedText = insertion;
            plain.SelectionStart = insertionStart + insertion.Length;
            plain.SelectionLength = 0;
        }
        else if (target is RichTextBox rich)
        {
            var previous = rich.Selection.Start.GetTextInRun(LogicalDirection.Backward);
            var prefix = previous.Length > 0 && !char.IsWhiteSpace(previous[^1]) ? " " : "";
            rich.Selection.Text = prefix + text;
            rich.CaretPosition = rich.Selection.End;
        }
    }

    public void RefreshLanguage()
    {
        microphone.ToolTip = recording ? L.T("dictation.stop", LanguageName())
            : busy ? L.T("dictation.working") : L.T("dictation.start", LanguageName());
        microphone.SetValue(AutomationProperties.NameProperty, microphone.ToolTip);
    }

    private static string LanguageName() => Strings.Catalog.First(language => language.Code == Strings.Code).Name;
    private string ModelPath() => Path.Combine(owner.Store.DirectoryPath, "models", ModelFile);
    private static bool IsModelReady(string path) => File.Exists(path) && new FileInfo(path).Length == ModelBytes;

    private static HttpClient CreateModelClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PomoDock/0.1 WhisperModel");
        return client;
    }

    private void SetRecordingVisual()
    {
        microphone.Content = StopIcon();
        microphone.SetResourceReference(Control.BackgroundProperty, "Accent");
        microphone.SetResourceReference(Control.ForegroundProperty, "AccentInk");
        microphone.IsEnabled = true;
        RefreshLanguage();
    }

    private void SetBusyVisual()
    {
        microphone.Content = new TextBlock { Text = "···", FontWeight = FontWeights.Black, FontSize = 11 };
        microphone.SetResourceReference(Control.BackgroundProperty, "Raised");
        microphone.IsEnabled = false;
        RefreshLanguage();
    }

    private void SetIdleVisual()
    {
        microphone.Content = MicrophoneIcon();
        microphone.SetResourceReference(Control.BackgroundProperty, "Raised");
        microphone.IsEnabled = true;
        RefreshLanguage();
    }

    private void Hide(bool force = false)
    {
        if (IsWorking && !force) return;
        recordingClock.Stop();
        feedbackHideClock.Stop();
        HideFeedback();
        popup.IsOpen = false;
        if (target is not null)
        {
            target.SizeChanged -= TargetSizeChanged;
            target.IsVisibleChanged -= TargetVisibilityChanged;
            if (paddingReserved) target.Padding = originalPadding;
        }
        paddingReserved = false;
        target = null;
    }

    private void CancelWork()
    {
        operation?.Cancel();
        previewCancellation?.Cancel();
        if (recording) RequestStop();
    }

    private void DisposeCapture()
    {
        recordingClock.Stop();
        try { writer?.Dispose(); } catch { }
        writer = null;
        if (capture is { } active)
        {
            active.DataAvailable -= AudioAvailable;
            active.RecordingStopped -= RecordingStopped;
            try { active.Dispose(); } catch { }
        }
        capture = null;
        DisposeLiveAudio();
        DeleteRecording(recordingPath);
        recordingPath = null;
    }

    private void DisposeLiveAudio()
    {
        lock (audioGate)
        {
            liveAudio?.Dispose();
            liveAudio = null;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        operation?.Cancel();
        previewCancellation?.Cancel();
        recordingClock.Stop();
        feedbackHideClock.Stop();
        if (recording) { try { capture?.StopRecording(); } catch { } }
        DisposeCapture();
        operation?.Dispose();
        operation = null;
        previewCancellation?.Dispose();
        previewCancellation = null;
        Hide(true);
        // Do not tear a native context out from underneath an inference that is observing the
        // cancellation. If idle, release it now; otherwise the process owns the final cleanup.
        if (inferenceGate.Wait(0))
        {
            try { whisperFactory?.Dispose(); }
            finally
            {
                inferenceGate.Release();
                inferenceGate.Dispose();
            }
        }
        whisperFactory = null;
        loadedModel = null;
        lifetime.Dispose();
        if (ReferenceEquals(current, this)) current = null;
    }

    private static void DeleteRecording(string? path)
    {
        if (path is null) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string Clean(string value)
    {
        var text = Regex.Replace(value, @"\s+", " ").Trim();
        return text is "[BLANK_AUDIO]" or "[MUSIC]" or "[Música]" or "(silencio)" ? "" : text;
    }

    private static string Friendly(Exception error) => error.InnerException?.Message ?? error.Message;

    private static bool IsNativeRuntimeFailure(Exception error)
    {
        for (Exception? currentError = error; currentError is not null; currentError = currentError.InnerException)
            if (currentError is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException) return true;
        return false;
    }

    private void LogFailure(Exception error, string stage = "final transcription")
    {
        try
        {
            File.AppendAllText(Path.Combine(owner.Store.DirectoryPath, "dictation-errors.log"),
                $"{DateTimeOffset.Now:O} [{stage}] {error}\n");
        }
        catch { }
    }

    private static Viewbox MicrophoneIcon()
    {
        var canvas = new Canvas { Width = 18, Height = 18 };
        var capsule = new Border
        {
            Width = 7, Height = 11, CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1.8), Background = Brushes.Transparent
        };
        capsule.SetResourceReference(Border.BorderBrushProperty, "Ink");
        Canvas.SetLeft(capsule, 5.5); Canvas.SetTop(capsule, 1);
        canvas.Children.Add(capsule);
        var stem = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M3.5,8.5 C3.5,13 6,15 9,15 C12,15 14.5,13 14.5,8.5 M9,15 L9,17 M6,17 L12,17"),
            StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Square, StrokeEndLineCap = PenLineCap.Square
        };
        stem.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Ink");
        canvas.Children.Add(stem);
        return new Viewbox { Width = 16, Height = 16, Child = canvas, Stretch = Stretch.Uniform };
    }

    private static Border StopIcon()
    {
        var stop = new Border { Width = 9, Height = 9, BorderThickness = new Thickness(1.5) };
        stop.SetResourceReference(Border.BackgroundProperty, "AccentInk");
        stop.SetResourceReference(Border.BorderBrushProperty, "AccentInk");
        return stop;
    }

    internal bool IsVisible => popup.IsOpen;
    internal FrameworkElement Surface => microphone;
    internal bool ReservesTextSpace => target is not null && target.Padding.Right >= originalPadding.Right + 35;
    internal bool FeedbackIsVisible => feedback.Visibility == Visibility.Visible;
    internal string FeedbackCopy => feedbackText.Text;
    internal void ShowFeedbackForDiagnostics() => ShowFeedback(L.T("dictation.live", "00:03", "▮▮▮···"));
    internal void BeginProvisionalForDiagnostics(string text) { BeginProvisionalText(); UpdateProvisionalText(text); }
    internal void UpdateProvisionalForDiagnostics(string text) => UpdateProvisionalText(text);
}
