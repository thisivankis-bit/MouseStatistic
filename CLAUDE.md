# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Projects

Two independent .NET 8 projects:

| Project | Type | Description |
|---|---|---|
| `MouseClickTracker/` | WinForms (`net8.0-windows`) | Client agent — runs on each monitored PC |
| `MouseClickServer/` | ASP.NET Core (`net8.0`) | Central server — aggregates data from all clients |

## Build & Run

```powershell
# Client — debug run (requires Windows)
dotnet run --project MouseClickTracker

# Server — debug run
dotnet run --project MouseClickServer

# Build both
dotnet build MouseClickTracker
dotnet build MouseClickServer

# Publish client (self-contained installer input)
dotnet publish MouseClickTracker -r win-x64 --self-contained true -c Release -o MouseClickTracker/publish

# Publish server — always delete publish/ first to avoid nested-folder bug
Remove-Item -Recurse -Force MouseClickServer/publish -ErrorAction SilentlyContinue
dotnet publish MouseClickServer -r win-x64 --self-contained true -c Release -o MouseClickServer/publish

# Compile installers (requires Inno Setup at default path)
& "C:\Users\User\AppData\Local\Programs\Inno Setup 6\ISCC.exe" MouseClickTracker/installer/setup.iss
& "C:\Users\User\AppData\Local\Programs\Inno Setup 6\ISCC.exe" MouseClickServer/installer/setup.iss
```

No tests exist in this project.

## Client Architecture (`MouseClickTracker`)

The app is a WinForms process that wires together several independent components in `MainForm`:

```
MouseHook (WinAPI WH_MOUSE_LL)
  ├── Clicked  →  [IsInWorkHours check] → DataStore.Increment()
  └── Activity →  ActivityTracker.RegisterActivity()

ActivityTracker (1-second timer)
  ├── classifies each second as active/inactive (threshold: 5s since last activity)
  ├── calls ForegroundApp.Get() each tick to track per-app time
  └── flushes to DataStore every 10 seconds (batched writes)

DataStore (SQLite via Microsoft.Data.Sqlite)
  └── %LocalAppData%\MouseClickTracker\data.db
      Tables: counter(total, active_seconds, inactive_seconds)
              app_stats(process_name, seconds)

WebServer (HttpListener on :5000)
  ├── GET /          — embedded HTML dashboard (updates every 1s)
  └── GET /api/stats — JSON snapshot from DataStore + ActivityTracker

SyncService (optional, 1-minute timer) — only created when ServerUrl is set
  ├── POST /api/sync → MouseClickServer  (sends clicks, activity, app stats, userName)
  └── GET  /api/config ← MouseClickServer  (receives workStart, workEnd, resetTime)
       └── calls OnConfigReceived() callback → updates MainForm fields

ResetTimer (30-second timer, always active)
  └── CheckReset() — fires DataStore.Reset() + ActivityTracker.Reset() when time matches resetTime
```

**Thread safety**: `DataStore` uses a `lock` on all methods. `ActivityTracker.Reset()` acquires `_flushLock` but `Tick()` does not — there is a known minor race on reset.

**Config** (`appsettings.json` next to exe):
```json
{
  "ServerUrl": "http://<server-ip>:5001",
  "MachineId": "",
  "UserName": "Иван"
}
```
`MachineId` defaults to `Environment.MachineName` if empty. Work hours and reset time are **not** stored locally — they come from the server via `/api/config`.

**Schedule (server-driven)**: `_workStart`, `_workEnd`, `_resetTime` are in-memory fields in `MainForm`, set by the `SyncService` callback. `_labelSchedule` shows the current received schedule (blue = active, gray = not configured).

**Reset behaviour**: `DataStore.Reset()` zeroes `total`, `active_seconds`, `inactive_seconds`, and deletes all `app_stats` rows. `ActivityTracker.Reset()` clears in-memory accumulators. Both are called together everywhere.

## Server Architecture (`MouseClickServer`)

Minimal ASP.NET Core API that runs as a Windows Service.

```
POST /api/sync     — upserts machine snapshot; accumulates daily delta in machine_daily
GET  /api/machines — all machine snapshots ordered by last_seen DESC
GET  /api/config   — returns work_start, work_end, reset_time from settings table
POST /api/config   — saves work_start, work_end, reset_time to settings table
GET  /api/stats    — period statistics: ?from=YYYY-MM-DD&to=YYYY-MM-DD
GET  /             — embedded HTML dashboard (4 tabs, 30s poll)
```

**Database**: `%ProgramData%\MouseClickServer\server.db`
```
machines(machine_id PK, user_name, last_seen, total_clicks, active_seconds, inactive_seconds, recent_clicks)
machine_app_stats(machine_id, process_name, seconds — composite PK)
machine_daily(machine_id, day YYYY-MM-DD, clicks, active_sec, inactive_sec — composite PK)
settings(key PK, value)   ← stores work_start / work_end / reset_time
```

**`recent_clicks`**: filled on every sync with the delta since the previous sync (`new - prev`, or `new` if a reset occurred). Used by the dashboard office tab to decide whether to show a character as dancing (clicking) vs sleeping (idle).

**Daily history**: on each sync, `Upsert()` reads the previous snapshot, computes click/time deltas, and upserts into `machine_daily` (local server date). Handles counter resets: if `new < prev`, the full new value is the delta.

Each sync **replaces** the machine's app_stats rows entirely (DELETE + INSERT in one transaction). Both `ServerDb` and `DataStore` run a `Migrate()` on startup to `ALTER TABLE ADD COLUMN` for backward compatibility with older databases.

## Dashboard (server-side, `Program.cs → Dashboard.Html`)

Four tabs rendered as a single `const string` HTML page:

- **Статистика** — card grid per machine: clicks, active/inactive time, top-5 apps, status dot
- **Офис** — animated character canvas (`<canvas id="office-cv">`), one character per connected client, RAF loop
- **Отчёты** — period statistics: date-range picker with quick buttons, summary cards, per-client table with inline bars
- **Расписание** — `<input type="time">` fields for work hours and auto-reset, saved via `POST /api/config`

**Office character states** (function `getActivityStatus(m)`, separate from `getStatus()`):
- **online** → `_drawMarioActive` — 2-frame running Mario (16×14 px sprites, scale 4), floating coins
- **away** → `_drawMarioIdle` — crouching Mario (16×12 px sprites, scale 4), rising Zzz bubbles
- **offline** → `_drawBoo` — procedural Boo ghost (Mario universe), angry brows, fangs, stubby arms

Conditions: `online` = `recentClicks > 0 && age < 150s`; `away` = connected but no clicks (`age < 600s`); `offline` = `age ≥ 600s`.

**Pixel sprite system**: sprites are defined in `const _SP` as arrays of 16-char strings; chars map to colors via `const _mCol` (`r`=red, `s`=skin, `b`=brown, `u`=blue, `0`=transparent). Drawn by `_sprite(ctx, ox, oy, rows, sc, pal)` using `fillRect` per pixel.

`getStatus()` (used for stats tab dots) uses only `lastSeen`: online < 90s, away < 600s, offline otherwise.

## Installer Scripts

Both installers are Inno Setup 6 scripts in `<project>/installer/setup.iss`. Output goes to `<project>/installer/output/`.

- **Client installer**: prompts for `ServerUrl` and `UserName`, writes `appsettings.json` post-install; optionally adds to Windows startup via registry `HKCU\...\Run`.
- **Server installer**: prompts for port (default 5001), writes `appsettings.json` (`Urls`), registers a Windows Service via `sc.exe`, adds a Windows Firewall inbound rule via `netsh`, and auto-starts the service. Uninstaller reverses all of this.

## Key Patterns

- **No dependency injection** — components are instantiated directly in `MainForm` constructor and passed by reference.
- **Embedded HTML** — both the client `WebServer.Html` and the server `Dashboard.Html` are `const string` literals. Edit them in-place; there are no separate asset files.
- **Server-driven client config** — work hours and reset time live only in the server DB (`settings` table). Clients receive them via `GET /api/config` inside `SyncService.Sync()` and apply them in-memory. If `ServerUrl` is blank, these features are inactive.
- **Batched SQLite writes** — `ActivityTracker` accumulates pending counters in memory and flushes every 10 ticks. Call `Flush()` before reading totals after a forced stop.
- **WinAPI hook lifetime** — `MouseHook` holds a delegate reference (`_proc`) as a field to prevent GC collection while the hook is active.
- **`SettingsForm`** — runtime dialog for editing `ServerUrl` and `UserName`. On save, disposes and recreates `SyncService` without restarting the app.
- **Server publish gotcha** — `dotnet publish` into an existing `publish/` folder that already contains a `publish/` subfolder creates a nested structure and fails on second run. Always delete `MouseClickServer/publish/` before publishing.
