# POMODOCK

POMODOCK is a free, open source Windows desktop focus station for a vertical secondary monitor. It combines a Pomodoro timer, tasks and projects, local reports, and a dock for external application windows and web panels.

The main shell is native WPF. External windows are hosted as real Win32 HWNDs, so an already-running app keeps its own session and input behavior. Web panels are loaded on demand through the installed WebView2 runtime. The app has no account, ads, premium membership, or hosted backend.

## Current scope

- Pomodoro, short break, and long break phases with configurable durations.
- Monotonic-time accounting that pauses after sleep, clock jumps, or long scheduling gaps.
- Local SQLite history with crash checkpoint recovery.
- Tasks, projects, estimates, templates, and free-focus sessions.
- Report view with daily chart, project breakdown, completed/partial sessions, streaks, pauses, filters, CSV export, JSON backup and import.
- F11 or the fullscreen button fills the current monitor; Escape exits fullscreen.
- A canvas-style widget grid with free drag-and-drop placement and per-widget resizing from all four sides and corners. Every widget, including the Pomodoro timer, can be moved, resized, and removed; the timer can be added once per workspace page.
- Launcher-style workspace pages with instant persistence, a compact floating dock, keyboard shortcuts, and mouse swipe navigation that follows the pointer like a horizontal carousel. Swiping beyond either populated edge creates a completely blank canvas.
- The Pomodoro surface can stay above the workspace or move below it from Settings; its full frame follows the active phase color.
- Widget layouts with color-customizable rich post-it notes (formatting, lists and interactive checklists), To Do lists with priorities, due dates, filters and manual order, a gamified habit tracker, focus stats, HTTPS web panels, and external app windows. Notes and To Do data live inside their card; habits and events live in the PomoDock store and are shared by every widget that shows them.
- A calendar and agenda widget with month, week, and list views over one shared event book: timed, all-day, and multi-day entries, daily/weekly/monthly/yearly repetition with an end date or a fixed number of occurrences, per-event color, place and notes, plain-language quick add, and reminders that raise a notification card with sound even while POMODOCK is minimized.
- External window widgets can be resized, collapsed, released, and cropped at the top or bottom while preserving the source app session. Cross-DPI Windows content, including Telegram Mini App windows, uses mixed hosting when the OS permits it, and connection work runs off the UI thread.
- Generated button sounds, optional filtered white noise during focus, and configurable completion alarms. Sound is self-contained and requires no bundled audio files.
- Brutalist light/dark themes with editable focus, short-break, long-break, and accent colors. Settings, reports, task editing, and confirmations use borderless in-app modal surfaces.
- A control panel grouped into rhythm, sound, appearance, and space, with named pomodoro rhythms, live changes, one-click undo, sound previews, a color palette instead of hexadecimal fields, and a focus rank earned by hours of real work.
- A guardian process records hosted window state before reparenting and restores it if POMODOCK exits unexpectedly.

## External application windows

Open the target application first, then choose `+ INCRUSTAR UNA VENTANA` and select its top-level window. The external app remains the owner of its content and is returned to the desktop when the widget is removed or POMODOCK closes.

The Windows API requires compatible DPI awareness modes for cross-process reparenting. If the app rejects the operation, POMODOCK leaves the source untouched and reports the reason. Applications running elevated may also require POMODOCK to run at the same integrity level. Telegram Portable and its Mini App windows are listed by their visible title; the exact behavior depends on that Telegram build and its DPI mode.

## Control panel

The gear opens a panel divided into four rooms — `RITMO`, `SONIDO`, `ASPECTO`, `ESPACIO` — instead of one long list of fields. Every change lands the moment it is made: colors, theme and window behaviour are visible behind the panel while it is still open, and `DESHACER` puts every setting back to the state it was in when the panel was opened.

`RITMO` offers four named rhythms — Clásico 25·5·15, Profundo 50·10·20, Sprint 15·3·10, Maratón 90·20·30 — that set the three durations and the cycle in one click, with steppers underneath to fine-tune each number. `SONIDO` reveals its options progressively behind the master switch and can play the alarm, a button click, or three seconds of white noise before committing. `ASPECTO` replaces the hexadecimal fields with a palette of tones (an exact code is still available under `OTRO…`). `ESPACIO` covers window placement and opens the local data folder.

The header shows a rank earned by hours of real focus — breaks never count — from `PRIMER PASO` to `LEYENDA`, with the hours still missing before the next one.

## To Do

Add it with `+ WIDGET` → `TO DO`. Unlike habits and events, tasks stay inside their own card, so two To Do widgets are two independent lists — one per page, one per project. A list written by an earlier version is read as it is and simply gains the new fields.

Typing accepts a little shorthand: `!` marks a task as important and `!!` as urgent, while `hoy`, `mañana` or `12/09` give it a date. Everything the reader does not consume stays in the title. On each row, `!` cycles the priority and the date button cycles between no date, today and tomorrow; a double click renames in place, and `⋯` opens the rest — an exact date, moving the task up or down, and deleting it.

The header counts what is done and calls out what is overdue. `TODAS`, `PENDIENTES`, `HOY` and `HECHAS` filter the list, `⇅` reorders once by what is late and closest, and `LIMPIAR` removes everything already finished.

## Habits

Add it with `+ WIDGET` → `HÁBITOS`. Habits live in the PomoDock database instead of inside the card, so every habit widget on every workspace page shows the same book and closing a card never loses a day. Habits saved by older versions inside a widget are absorbed into that book the first time the card is opened.

The week runs Monday to Sunday with today highlighted. A click marks a day, another click clears it, and a right-click steps back one repetition. Past days can be corrected at any time and `‹ ›` walks through earlier weeks.

Each habit carries its own cadence — every day, chosen weekdays, or a number of free days per week — plus a daily target for practices counted more than once. Streaks only judge the days a habit is actually due, a day still in progress never breaks one, and a habit measured by week counts weeks instead of days. `⋯` renames, changes cadence and target, reorders, archives, or deletes; the habit name opens a detail window with its numbers and a clickable heat map of the last months.

Marked days earn experience — ten points plus a bonus that grows with the streak — which becomes levels and awards. The header shows today's progress, the level, and the run of days where everything due was done.

The widget relays itself out at every size it can be dragged to: a single strip of squares for today when it is tiny, then the week grid, streaks, week navigation, the 30-day rate, and a consistency map of the last weeks as it grows.

## Calendar and reminders

Add it with `+ WIDGET` → `CALENDARIO / AGENDA`. Events live in the PomoDock database instead of inside the card, so every calendar widget on every workspace page shows the same agenda and closing a card never loses an appointment.

Type an entry in plain language and press Enter: `Dentista mañana a las 17:30 durante 45m`, `Gimnasio todos los martes a las 7`, `Pagar alquiler el 1 de octubre`. Whatever the reader cannot interpret stays in the title, and `⋯` opens the full form with repetition, end date, reminders, color, place, and notes. An entry without a time becomes an all-day event.

The card scales with the room it is given: type, rows, hour grid and day panel all grow when the widget is dragged out to fill a monitor, and the month cells switch between coloured dots, one chip, or several as the height allows. The toolbar grows more slowly than the content, so the month name keeps its place.

Reminders run on their own clock while POMODOCK is open, so they ring when the calendar is on another workspace page or the window is minimized. Each one raises a card in the corner of the screen with a chime, `+5 MIN`, `+15 MIN`, and `✓ LISTO`. A reminder is never delivered twice, and one that came due while the app was closed only appears if its event has not already passed.

For a web panel, use `+ WIDGET` and an HTTPS URL. Web panels are isolated from the native host and can be suspended when collapsed. Use “Mantener activo” for music or a dashboard that must continue running while collapsed.

## Build

The repository targets .NET 8 on Windows. Install the .NET 8 SDK and build:

```powershell
dotnet build PomoDock.sln -c Release
dotnet run --project tests/PomoDock.Tests -c Release
```

Run the desktop app from `src/PomoDock.App/bin/Release/net8.0-windows/win-x64/PomoDock.exe`. To add it to the current user's Windows Start menu, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-start-menu.ps1
```

The shortcut points to the local Release build and does not install a service or run anything in the background.

The app stores its data under `%LOCALAPPDATA%\PomoDock`. Export a JSON backup before moving machines or testing development builds.

## Native smoke test

The app includes a disposable integration fixture that opens a temporary Win32 window, embeds it into a WPF host, resizes it, crops it, releases it, and verifies restoration. It also drives the calendar through its three views and rings a real reminder, and renders screenshots for light mode, dark mode, widgets, reports, and the calendar:

```powershell
PomoDock.exe --self-test C:\temp\pomo-dock-native-test
```

The command writes `results.json` and PNG captures into the supplied directory. It never targets another user's application.

## License

MIT. See [LICENSE](LICENSE).
