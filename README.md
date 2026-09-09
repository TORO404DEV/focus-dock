# FOCUS DOCK

FOCUS DOCK is a free, open source Windows desktop focus station for a vertical secondary monitor. It combines a Pomodoro timer, tasks and projects, local reports, and a dock for external application windows and web panels.

The main shell is native WPF. External windows are hosted as real Win32 HWNDs, so an already-running app keeps its own session and input behavior. Web panels are loaded on demand through the installed WebView2 runtime. The app has no account, ads, premium membership, or hosted backend.

## Current scope

- Pomodoro, short break, and long break phases with configurable durations.
- Monotonic-time accounting that pauses after sleep, clock jumps, or long scheduling gaps.
- Local SQLite history with crash checkpoint recovery.
- Tasks, projects, estimates, templates, and free-focus sessions.
- Report view with daily chart, project breakdown, completed/partial sessions, streaks, pauses, filters, CSV export, JSON backup and import.
- F11 or the fullscreen button fills the current monitor; Escape exits fullscreen.
- A canvas-style widget grid with free drag-and-drop placement and per-widget resizing. The Pomodoro surface stays fixed; every other widget can be moved, resized, collapsed, or removed.
- Widget layouts with notes, gamified focus stats, HTTPS web panels, and external app windows.
- External window widgets can be resized, collapsed, released, and cropped at the top or bottom while preserving the source app session.
- Generated button sounds, optional filtered white noise during focus, and configurable completion alarms. Sound is self-contained and requires no bundled audio files.
- Brutalist light/dark themes with editable focus, short-break, long-break, and accent colors. Settings, reports, task editing, and confirmations use borderless in-app modal surfaces.
- A guardian process records hosted window state before reparenting and restores it if FOCUS DOCK exits unexpectedly.

## External application windows

Open the target application first, then choose `+ INCRUSTAR UNA VENTANA` and select its top-level window. The external app remains the owner of its content and is returned to the desktop when the widget is removed or FOCUS DOCK closes.

The Windows API requires compatible DPI awareness modes for cross-process reparenting. If the app rejects the operation, FOCUS DOCK leaves the source untouched and reports the reason. Applications running elevated may also require FOCUS DOCK to run at the same integrity level. Telegram Portable and its Mini App windows are listed by their visible title; the exact behavior depends on that Telegram build and its DPI mode.

For a web panel, use `+ WIDGET` and an HTTPS URL. Web panels are isolated from the native host and can be suspended when collapsed. Use “Mantener activo” for music or a dashboard that must continue running while collapsed.

## Build

The repository targets .NET 8 on Windows. Install the .NET 8 SDK and build:

```powershell
dotnet build FocusDock.sln -c Release
dotnet run --project tests/FocusDock.Tests -c Release
```

Run the desktop app from `src/FocusDock.App/bin/Release/net8.0-windows/win-x64/FocusDock.exe`.

The app stores its data under `%LOCALAPPDATA%\FocusDock`. Export a JSON backup before moving machines or testing development builds.

## Native smoke test

The app includes a disposable integration fixture that opens a temporary Win32 window, embeds it into a WPF host, resizes it, crops it, releases it, and verifies restoration. It also renders screenshots for light mode, dark mode, widgets, and reports:

```powershell
FocusDock.exe --self-test C:\temp\focus-dock-native-test
```

The command writes `results.json` and PNG captures into the supplied directory. It never targets another user's application.

## License

MIT. See [LICENSE](LICENSE).
