<div align="center">

# PomoDock

### Your second monitor, turned into a focus workspace.

A free, open-source Pomodoro timer and widget dock for **vertical secondary monitors** on Windows.<br>
Timer, tasks, habits, notes, calendar and focus reports — side by side with the real apps you already use.

[![License: MIT](https://img.shields.io/github/license/TORO404DEV/focus-dock?color=171916)](LICENSE)
[![GitHub stars](https://img.shields.io/github/stars/TORO404DEV/focus-dock?style=flat&color=171916)](https://github.com/TORO404DEV/focus-dock/stargazers)
![Platform: Windows x64](https://img.shields.io/badge/platform-Windows%20x64-171916)
![.NET 8 · WPF](https://img.shields.io/badge/.NET%208-WPF-171916)
![Version 0.1.0](https://img.shields.io/badge/version-0.1.0-D7D9D1)

**[Get started](#installation)** · **[Features](#features)** · **[Import from Pomofocus](#pomodock--pomofocus)** · **[Contribute](#contributing)** · **[Report a bug](https://github.com/TORO404DEV/focus-dock/issues)**

<!-- TODO(support): add the official sponsorship link here once it exists, e.g. · **[Support](<URL>)** -->

</div>

<!--
TODO(screenshots): no screenshots are committed yet. Suggested hero:
<p align="center"><img src="docs/images/workspace-vertical.png" alt="PomoDock on a vertical monitor" width="420"></p>
Real captures can be generated with `PomoDock.exe --self-test <folder>` (see Contributing).
-->

> [!NOTE]
> **Early development.** PomoDock is at version 0.1.0, Windows-only, and built from source — there are no prebuilt releases yet. The interface is currently in **Spanish**; this README quotes on-screen labels as they appear, with their meaning in English.

---

## The problem

Most productivity software is designed for the main screen — the one that should belong to your actual work.

Meanwhile a lot of developers, writers, students and streamers have a second monitor, often turned vertical, that ends up holding a stray browser tab or nothing at all. It is always visible and almost never useful.

PomoDock is built for that screen.

## What is PomoDock?

PomoDock is **not just a Pomodoro timer**. It is a persistent workspace that fills your secondary monitor with widgets you arrange yourself: move them anywhere, resize them from any edge, and keep several pages of them.

Widgets come in two kinds:

- **Native widgets** — a Pomodoro timer, a to-do list, a habit tracker, rich sticky notes, a calendar with reminders, and a focus-metrics card.
- **Embedded widgets** — any open Windows application window, or any HTTPS web page, docked into the same canvas as a real, working widget.

The timer keeps time; everything around it keeps context. Every session is stored locally and feeds reports, streaks and a focus rank.

```text
  MAIN MONITOR                                     VERTICAL MONITOR · PomoDock
 ┌───────────────────────────────────────────┐    ┌──────────────────────────┐
 │                                           │    │ POMODORO           24:58 │
 │                                           │    │ [ START → ]   ↺   ↠      │
 │     your editor, document, course,        │    ├──────────────────────────┤
 │     design tool — the actual work         │    │ TO DO   01 / 05 HECHAS   │
 │                                           │    │ ○ Review pull request !  │
 │                                           │    ├──────────────────────────┤
 └───────────────────────────────────────────┘    │ AGENDA  hoy · 3 eventos  │
                                                  ├──────────────────────────┤
                                                  │ APP / Telegram           │
                                                  │ (a real, live window)    │
                                                  ├──────────────────────────┤
                                                  │     ‹   •  ○  ○   ›  ＋   │
                                                  └──────────────────────────┘
                                                   illustration, not a screenshot
```

---

## Features

### Focus

- **Pomodoro, short break and long break** phases with configurable durations and a configurable number of pomodoros per cycle.
- **Four named rhythms** to switch in one click — Classic 25·5·15, Deep 50·10·20, Sprint 15·3·10, Marathon 90·20·30 — plus fine-grained steppers.
- **Link a session to a task** or run *free focus* (`ENFOQUE LIBRE`); resetting or skipping saves the partial work instead of discarding it.
- **Honest timekeeping**: monotonic accounting that pauses on sleep, clock jumps or long scheduling gaps, so suspended time never counts as work.
- **Crash recovery**: a running session is checkpointed and offered back after a restart — never resumed behind your back.
- Optional auto-start for breaks and focus, a completion alarm, button sounds and filtered white noise — all generated in-app, no audio files.
- The timer's frame takes the colour of the active phase.

### Productivity widgets

| Widget | What it does |
|---|---|
| ☑ **To Do** | Priorities (`!` important, `!!` urgent), dates and times in plain Spanish (`mañana a las 5`, `el viernes 18`, `antes de 2pm`, `todos los martes`), overdue warnings, filters (all · pending · today · done), manual ordering, one-click urgency sort, inline rename. Tasks live in the database, so closing a card never loses one, and every dated task shows in the calendar and rings through its reminders — completing it on either side completes it on the other. |
| ↻ **Habits** | Daily, chosen-weekday or *N-times-per-week* cadences, per-day targets, cadence-aware streaks, experience, levels and awards, and a detail view with a heat map. Adapts from a single strip of squares to a full week grid. |
| ✎ **Notes** | Rich sticky notes: bold, italic, underline, strikethrough (`Ctrl+Shift+X`), bullet and numbered lists, checklists that cross out and fade finished lines (`Enter` continues the list), timestamps, undo/redo, and six post-it colours that re-skin the whole card. The `⋯` button opens a searchable **note history**: closing a note keeps its text there, ready to be copied or reopened. |
| ▦ **Calendar / Agenda** | Month, week (hour grid) and list views. Timed, all-day and multi-day events; daily/weekly/monthly/yearly repetition with an end date or occurrence count; colours, place and notes. Plain-language quick add: `Dentista mañana a las 17:30 durante 45m`. |
| ◒ **Focus metrics** | Hours focused today against your daily goal, the current streak, and this week at a glance. Adapts its layout to small and large cards. |
| ◷ **Timer** | The Pomodoro itself is a widget too: move it, resize it, remove it, or place it on any page (once per page). |

Plus a **Tasks & projects** window (`☷`) with pomodoro estimates, progress per task, projects and reusable task templates.

**Calendar reminders** ring with a chime and a notification card in the corner of the screen — with *snooze 5 / 15 min* and *done* — even when the calendar sits on another page or PomoDock is minimized. They run while PomoDock is open; a reminder that came due while the app was closed is only shown if its event hasn't passed yet.

### Anything with a window can become a widget

This is the core of PomoDock: the widgets you need are often other apps.

- **Embedded app windows** — pick any open top-level window (with search and quick filters for browsers, messaging and development tools) and it becomes a widget. It is hosted as a real Win32 window, so the app keeps its own session, login and input — Telegram, a browser or Spotify, for example. There is no app-specific integration — it works at the window level.
  - Crop the app's own title or bottom bars (0–200 px), release it back to the desktop, or connect a different window.
  - Windows are always returned to the desktop when the widget is removed or PomoDock closes. A separate guardian process restores them even if PomoDock exits unexpectedly.
- **Web panels** — any HTTPS page (YouTube, dashboards, documentation) in an isolated WebView2 panel with its own profile. Collapsed panels are suspended unless you mark them *keep alive* for music or live dashboards. Non-HTTPS navigation is blocked and pop-ups open in your default browser.

> [!IMPORTANT]
> Cross-process embedding depends on Windows. Some apps refuse to be re-parented because of their DPI mode, and apps running as administrator may require PomoDock to run at the same level. When an app refuses, PomoDock leaves it untouched and tells you why.

### Workspace

- **Free canvas**: drag widgets by their header, resize them from all four sides and corners, collapse, rename or remove them.
- **Workspace pages**: keep several canvases and move between them with a floating page dock, `Ctrl+←` / `Ctrl+→`, the mouse wheel over the dock, or by **dragging empty canvas sideways** like a carousel. Pages that have widgets keep them alive, embedded windows included.
- A new page opens past either end of the carousel whenever the page at that end has content, so you can keep one blank canvas on each side — never two in a row. New pages always start blank.
- **Fullscreen** on the current monitor (`F11`, which still works while an embedded app has focus), optional *always on top*, and a *timer at the bottom* placement.
- Everything persists instantly: layout, sizes, pages and widget content.

### Analytics

- **Focus report** (`▥`) by week, month or year, with navigable periods, a daily chart with hover details, and totals for hours focused, days recorded and current streak.
- **Session history** with project filter and search; edit a session's duration when the record is wrong.
- **Focus rank** in the control panel, earned only by hours of real focus (breaks never count), from *Primer paso* to *Leyenda*.
- Habit experience, levels and awards live in the habit widget.

### Your data

- **Local-first.** No account, no server. Everything is stored under `%LOCALAPPDATA%\PomoDock` in a SQLite database.
- **CSV export** of the visible report period — spreadsheet-safe (formula injection is neutralized).
- **JSON backup** of settings, focus sessions, habits, calendar events and the note history. **JSON import** merges them into what you already have — sessions and events by stable ID, habits day by day — without overwriting anything. Page layouts and card contents are saved in the backup but not restored by import.
- **Automatic safety copy**: before any import, the database is copied to `backups\`.
- **Pomofocus import** — see below.

### Appearance & control panel

- Brutalist light and dark themes, a colour palette for each phase and the accent (exact hex still available), and a *reduce motion* option.
- The control panel (`⚙`) groups settings into four rooms — *Rhythm*, *Sound*, *Appearance*, *Space* — applies every change live, previews sounds before you commit, and offers one-click **undo** for the whole visit.

---

## Why vertical?

A vertical monitor is not objectively better. But it is shaped for **stacks** — and a focus setup is a stack: the timer, today's tasks, what's next on the calendar, a chat you need to keep an eye on, a reference page.

On a horizontal main screen those things compete with your work for space. On a vertical second screen they can stay permanently visible without covering anything. PomoDock's default window (620 × 940) and its widget layouts are designed around that tall, narrow canvas — and it works just as well on any secondary display you want to dedicate to it.

Typical setups: coding, writing, studying, research, deep work, streaming, creative work, and anyone who wants a productivity dashboard they can see while working on the main display.

## Use cases

**Coding**
Main monitor: IDE and terminal.
Vertical monitor: Pomodoro + To Do for the current branch + a browser window on the pull request or CI page + a note for the running TODOs.

**Studying**
Main monitor: the course, book or lecture.
Vertical monitor: Pomodoro in the *Sprint* or *Classic* rhythm + notes with checklists + habits for daily practice + focus metrics against a daily goal.

**Deep work**
Main monitor: the project itself.
Vertical monitor: Pomodoro in the *Deep* rhythm + today's tasks + the calendar with reminders so the next meeting can't ambush you.

**Communication without distraction**
Main monitor: focused work.
Vertical monitor: the timer plus an embedded Telegram (or any chat app) window — visible, but out of the way.

---

## PomoDock + Pomofocus

Already have years of history in [Pomofocus](https://pomofocus.io)? Bring it with you.

PomoDock can import Pomofocus's report export and treat it like its own history: it appears in reports, counts toward streaks and totals, and feeds your focus rank. PomoDock's author moved roughly **three years** of Pomofocus history into it, which is why the report view opens on the yearly period and is built to handle long histories.

**How to import**

1. Download your report data from Pomofocus as a CSV file.
2. In PomoDock, open the report (`▥`) and choose **`⇩ IMPORTAR`** (*Import*).
3. Select the file. PomoDock first copies its database to `backups\`, then imports.
4. A status line reports how many sessions were imported, how many already existed, and how many rows were skipped.

**What the importer understands**

- Comma- or tab-separated files (`.csv` / `.tsv`); the delimiter is detected automatically.
- Required columns: `date`, `project`, `task`, `hours`, `startTime`, `endTime` (header matching ignores case and underscores).
- Dates as `yyyyMMdd`, `yyyy-MM-dd`, `dd/MM/yyyy` or `MM/dd/yyyy`; times as `HH:mm` or `h:mm tt`.
- Missing start or end times are inferred from the duration, and the session is marked as inferred.
- Projects and tasks are created in PomoDock as they are found.
- **Re-importing is safe**: every row gets a stable ID, so rows already imported are skipped instead of duplicated.

The same button also imports PomoDock's own JSON backups.

---

## Installation

PomoDock currently runs from source. There is no installer or published release yet.

### Requirements

- **Windows**, x64
- **[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)**
- **Microsoft Edge WebView2 Runtime** — only needed for web-page widgets

### Build and run

```powershell
git clone https://github.com/TORO404DEV/focus-dock.git
cd focus-dock
dotnet build PomoDock.sln -c Release
```

Then launch:

```powershell
.\src\PomoDock.App\bin\Release\net8.0-windows\win-x64\PomoDock.exe
```

### Optional: Start menu shortcut

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-start-menu.ps1
```

The shortcut points at your local Release build. It installs no service and runs nothing in the background.

> [!TIP]
> Data lives in `%LOCALAPPDATA%\PomoDock`. To move everything to another machine — layout and card contents included — copy that folder while PomoDock is closed. The JSON backup (`□ BACKUP` in the report) carries your history, habits and calendar, and can be merged into another installation. Data from the project's earlier name, *FocusDock*, is migrated automatically.

## Quick start

1. **Launch PomoDock** and drag its window to your secondary monitor. The first page, *INICIO*, opens with the Pomodoro timer.
2. **Add widgets** with **`+ WIDGET`** — or press `1`–`8` in the launcher. Start with **TO DO** and **CALENDARIO / AGENDA**.
3. **Arrange them**: drag a widget by its header, resize it from any edge or corner.
4. **Dock an app**: `+ WIDGET` → **VENTANA DE OTRA APP** (*another app's window*) and pick any open window. For a website, choose **PÁGINA WEB / YOUTUBE** and enter an HTTPS address.
5. **Pick a rhythm** in the control panel (`⚙` → *RITMO*).
6. **Focus**: press **`START →`** or `Space`. Link the session to a task from the line under the timer.
7. **Review** your week, month or year in the report (`▥`).

## Customization

| What | How |
|---|---|
| Widget position and size | Drag the header; resize from any side or corner. Saved instantly. |
| Widget options | `⋯` on a widget: rename; crop / release / reconnect (apps); change URL, reload, keep alive (web). |
| Collapse / remove | `−` and `×` on the widget header. Removing an app widget hands the window back to the desktop. |
| Pages | `＋` in the page dock (adds on the right), or navigate or drag the canvas past either end. |
| Rhythm, sound, colours, theme | Control panel (`⚙`). Changes apply live; *DESHACER* undoes the whole visit. |
| Window behaviour | *Always on top*, *timer at the bottom*, fullscreen (`F11`). |

### Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Space` | Start / pause the timer (when you're not typing) |
| `Ctrl` + `←` / `→` | Previous / next workspace page |
| `F11` | Fullscreen on the current monitor |
| `Esc` | Leave fullscreen · close the report |
| `1`–`8` | Choose a widget in the `+ WIDGET` launcher |
| `Ctrl` + `B` / `I` / `U` | Bold / italic / underline in notes |
| `Ctrl` + `Shift` + `C` | Toggle a checklist line in notes |
| `Ctrl` + `Z` / `Y` | Undo / redo in notes |
| `Enter` | Add from the To Do or calendar quick-add field |

---

## Under the hood

PomoDock is a native Windows desktop app.

| Layer | Technology |
|---|---|
| UI | WPF on .NET 8, per-monitor DPI aware |
| Embedded apps | Win32 window re-parenting with a crash-safe journal and guardian process |
| Web panels | Microsoft Edge WebView2 |
| Storage | SQLite (`Microsoft.Data.Sqlite`) with JSON payloads |

```text
src/
├── PomoDock.Core/   timer engine, sessions, reports, Pomofocus import,
│                    habits, agenda, to-do, focus rank, storage
└── PomoDock.App/    WPF shell, widget canvas and pages, widgets,
                     window hosting, dialogs, sound
tests/
└── PomoDock.Tests/  test runner for the core logic
scripts/             Start menu shortcut, icon generator
```

The core library has no UI dependency, so the rules that matter — timekeeping, streaks, recurrence, imports — are covered by plain tests.

---

## Open source

PomoDock is free and MIT-licensed. You can read every line, build it yourself, and change it.

It is a young project, so this is a good moment to shape it. Bug reports, feature ideas, and pull requests — new widgets, better embedding, fixes — are all welcome. If something doesn't work with a particular app you tried to dock, that is a useful report too.

## Known gaps and next steps

There is no official roadmap yet. These are gaps visible in the repository today:

- **Prebuilt releases** — the app is currently built from source; there is no packaging or release workflow.
- **Interface language** — the UI is Spanish-only; there is no localization layer yet.
- **Restoring a workspace** — importing a JSON backup merges history, habits and events, but does not restore page layouts or card contents.
- **Screenshots and documentation** — this README is the only documentation so far.
- **Platform** — PomoDock depends on WPF and Win32 and is Windows-only.

## Contributing

There is no `CONTRIBUTING.md` yet, so here is the workflow:

1. **Fork** the repository and create a branch: `git checkout -b feature/my-change`
2. **Build**: `dotnet build PomoDock.sln -c Release`
3. **Test** the core logic:
   ```powershell
   dotnet run --project tests/PomoDock.Tests -c Release
   ```
4. **Run the native smoke test** for UI-level changes:
   ```powershell
   .\src\PomoDock.App\bin\Release\net8.0-windows\win-x64\PomoDock.exe --self-test C:\temp\pomodock-test
   ```
   It opens a disposable window of its own, embeds it, resizes, crops and releases it, exercises the timer, pages, report, calendar, To Do and control panel, and writes `results.json` plus PNG captures to the folder. It never touches another application. Its timer-gesture checks use the real mouse, so leave the pointer alone while it runs.
5. **Open a pull request** describing what changed and how you verified it. GitHub Actions builds the solution and runs the core tests on every push and pull request.

## Support the project

PomoDock is and will stay free. If it's useful to you and you'd like to support its development, sponsorship options will be listed here.

<!-- TODO(support): add the official GitHub Sponsors / Ko-fi / Buy Me a Coffee link here and in the header, and create .github/FUNDING.yml. -->

Until then, the best support is a ⭐ on GitHub, a bug report, or a pull request.

## License

[MIT](LICENSE) © 2026 TORO404DEV

---

<div align="center">

**Built for people who refuse to let their second monitor go to waste.**

</div>
