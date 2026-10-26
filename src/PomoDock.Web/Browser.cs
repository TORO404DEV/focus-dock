using Microsoft.JSInterop;

namespace PomoDock.Web;

/// <summary>
/// Everything PomoDock asks of the browser, in one place: storage, the second tick, sound, the
/// pointer gestures and the keyboard. It is the web's stand-in for Win32 and WPF; the rest of the
/// app talks to it the way the desktop talks to <c>Native</c>.
///
/// Calls are synchronous on purpose. WebAssembly runs on the page's own thread, so an in-process
/// call is cheap, and keeping them synchronous keeps the code shaped like the desktop's.
/// </summary>
public sealed class Browser
{
    private readonly IJSInProcessRuntime js;

    public Browser(IJSRuntime runtime) => js = (IJSInProcessRuntime)runtime;

    public string? StorageGet(string key) => js.Invoke<string?>("pd.store.get", key);
    public bool StorageSet(string key, string value) => js.Invoke<bool>("pd.store.set", key, value);
    public void StorageRemove(string key) => js.InvokeVoid("pd.store.remove", key);

    /// <summary>Wires the page's events to the window: ticks, gestures, swipes, keys and closing.</summary>
    public void Attach<T>(DotNetObjectReference<T> window) where T : class => js.InvokeVoid("pd.attach", window);

    public bool AudioHas(string key) => js.Invoke<bool>("pd.audio.has", key);
    public void AudioPlay(string key, byte[]? wave, int volume, bool alarm) => js.InvokeVoid("pd.audio.play", key, wave, volume, alarm);
    public void AudioStopAlarms() => js.InvokeVoid("pd.audio.stopAlarms");
    public void AudioLoop(string key, byte[]? wave, int volume) => js.InvokeVoid("pd.audio.loop", key, wave, volume);
    public void AudioStopLoop() => js.InvokeVoid("pd.audio.stopLoop");

    public void CarouselAnimate(int dx, int dy, bool commit) => js.InvokeVoid("pd.carousel.animate", dx, dy, commit);
    public void CarouselReset() => js.InvokeVoid("pd.carousel.reset");
    public double[] CanvasSize() => js.Invoke<double[]>("pd.canvasSize");

    /// <summary>Puts the skin on the page root, where every custom property of the palette is read.</summary>
    public void ApplyTheme(bool dark, bool reduceMotion, string accent, string accentInk, string language) =>
        js.InvokeVoid("pd.theme", dark, reduceMotion, accent, accentInk, language);

    public void ToggleFullscreen() => js.InvokeVoid("pd.fullscreen");
    public void SetTitle(string title) => js.InvokeVoid("pd.title", title);
    public void Notify(string title, string body) => js.InvokeVoid("pd.notify", title, body);
    public void AskNotifications() => js.InvokeVoid("pd.askNotifications");
    public bool PageHidden() => js.Invoke<bool>("pd.hidden");
    public void Focus(string selector) => js.InvokeVoid("pd.focus", selector);
    public void Download(string name, string text) => js.InvokeVoid("pd.download", name, text);
    public void Open(string url) => js.InvokeVoid("pd.open", url);
}
