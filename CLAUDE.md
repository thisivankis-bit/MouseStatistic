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

KeyboardHook (WinAPI WH_KEYBOARD_LL)
  ├── Pressed  →  [IsInWorkHours check] → DataStore.IncrementKey()  (counts WM_KEYDOWN/WM_SYSKEYDOWN, including auto-repeat)
  └── Activity →  ActivityTracker.RegisterActivity()

ActivityTracker (1-second timer)
  ├── classifies each second as active/inactive (threshold: 5s since last activity)
  ├── calls ForegroundApp.Get() each tick to track per-app time
  └── flushes to DataStore every 10 seconds (batched writes)

DataStore (SQLite via Microsoft.Data.Sqlite)
  └── %LocalAppData%\MouseClickTracker\data.db
      Tables: counter(total, key_total, active_seconds, inactive_seconds, last_reset_date,
                      synthetic_clicks, key_repeat_total, synthetic_keys)
              app_stats(process_name, seconds)

WebServer (HttpListener on :5000)
  ├── GET /          — embedded HTML dashboard (updates every 1s)
  └── GET /api/stats — JSON snapshot from DataStore + ActivityTracker (includes `keys`)

SyncService (optional, 1-minute timer) — only created when ServerUrl is set
  ├── POST /api/sync → MouseClickServer  (sends clicks, keys, activity, app stats, userName)
  └── GET  /api/config ← MouseClickServer  (receives workStart, workEnd, resetTime)
       └── calls OnConfigReceived() callback → updates MainForm fields

UpdaterService (optional, fires once 30s after start, then every 24h) — only when ServerUrl is set
  ├── GET /api/version ← MouseClickServer  (compares server `Version` against Assembly.GetName().Version)
  └── if newer: downloads /downloads/<fileName>, launches it `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`,
       then Environment.Exit(0) so Inno can overwrite the running exe

ResetTimer (30-second timer, always active)
  └── CheckReset() — fires DataStore.Reset() + ActivityTracker.Reset() when
      today's resetTime moment has passed and last_reset_date < today
      (so a client booted at 10:30 still resets even if it missed the 09:00 tick)
```

**System tray**: closing the window hides it to tray (`OnFormClosing` cancels the event and calls `Hide()`). The `NotifyIcon` has a context menu with "Открыть" (shows window) and "Выход" (sets `_realClose = true` then closes). Double-click on the tray icon also restores the window. The app only truly exits via the tray menu.

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

**Reset behaviour**: `DataStore.Reset()` zeroes `total`, `key_total`, `active_seconds`, `inactive_seconds`, deletes all `app_stats` rows, and stamps `last_reset_date = today` in the same statement. `ActivityTracker.Reset()` clears in-memory accumulators. Both are called together. There is no manual reset in the UI — reset only happens via the server-driven schedule (`CheckReset`).

**Missed reset recovery**: `last_reset_date` is persisted so that a client booted after `resetTime` still performs the reset for that day, and a restart later the same day doesn't reset twice. The `CheckReset` predicate is: `now ≥ today+resetTime AND last_reset_date < today`.

## Server Architecture (`MouseClickServer`)

Minimal ASP.NET Core API that runs as a Windows Service.

```
POST /api/sync     — upserts machine snapshot; accumulates daily delta in machine_daily
GET  /api/machines — all machine snapshots ordered by last_seen DESC; X-Server-Time header
GET  /api/config   — returns work_start, work_end, reset_time, daily_target from settings table
POST /api/config   — saves work_start, work_end, reset_time, daily_target to settings table
GET  /api/stats    — period statistics: ?from=YYYY-MM-DD&to=YYYY-MM-DD
GET  /api/version  — reads %ProgramData%\MouseClickServer\downloads\latest.json, returns { version, fileName }
GET  /downloads/*  — static files from %ProgramData%\MouseClickServer\downloads (auto-update installer)
GET  /             — embedded HTML dashboard (4 tabs, 30s poll)
```

**Database**: `%ProgramData%\MouseClickServer\server.db`
```
machines(machine_id PK, user_name, last_seen, total_clicks, total_keys, synthetic_clicks, key_repeats, synthetic_keys, active_seconds, inactive_seconds, recent_clicks, recent_keys, wins)
machine_app_stats(machine_id, process_name, seconds — composite PK)
machine_daily(machine_id, day YYYY-MM-DD, clicks, keys, active_sec, inactive_sec — composite PK)
daily_winner(day PK, machine_id)   ← one row per day, set on first machine to cross DailyWinTarget
settings(key PK, value)   ← stores work_start / work_end / reset_time
```

**`recent_clicks` / `recent_keys`**: filled on every sync with the delta since the previous sync (`new - prev`, or `new` if a reset occurred). The office tab considers a client "online" (clicking/typing) when `recent_clicks + recent_keys > 0` within the last 150s, otherwise "away".

**Daily history**: on each sync, `Upsert()` reads the previous snapshot, computes click/time deltas, and upserts into `machine_daily` (local server date). When a reset is detected (`new < prev`), today's `machine_daily` row is zeroed before accumulating new deltas — this keeps the Reports tab consistent with the Statistics tab (both reflect only post-reset activity).

**Fraud signals (MVP)**: the low-level hooks expose two flags via their event args. `MouseHook` marshals `MSLLHOOKSTRUCT.flags` and surfaces `IsInjected` (set when `LLMHF_INJECTED` or `LLMHF_LOWER_IL_INJECTED` is on); `KeyboardHook` marshals `KBDLLHOOKSTRUCT.flags` for `IsInjected` AND tracks held `vkCode`s in a `HashSet` to derive `IsRepeat` (since `WH_KEYBOARD_LL` doesn't expose the standard auto-repeat bit). Counters `synthetic_clicks`, `key_repeat_total`, `synthetic_keys` accumulate locally, ride along in `/api/sync`, and are persisted on the server (`machines.synthetic_clicks/key_repeats/synthetic_keys`). The dashboard's Statistics tab computes the suspicious flag in JS: `totalEvents ≥ 100 AND (synthShare > 0.30 OR repeatShare > 0.50)` and shows a red flag ⚑ next to the name with a tooltip listing the ratios. These are advisories, not proof — a CS-go macro or holding `↓` to scroll a long page will trigger the flag.

**Daily winner**: after the daily upsert, if today's `machine_daily.clicks + keys` for this machine is at least the daily target, the server does `INSERT INTO daily_winner ... ON CONFLICT(day) DO NOTHING`. If the insert actually happened (this machine was the first today), `machines.wins` is incremented by 1. The Statistics tab renders a gold star next to the user/machine name showing the lifetime win count (grey star with `0` when wins are zero). The target lives in `settings.daily_target` (admin sets it via the Schedule tab); both server-side winner detection and the dashboard's marathon X-axis read it from `/api/config`, falling back to 5000 when unset.

Each sync **replaces** the machine's app_stats rows entirely (DELETE + INSERT in one transaction). Both `ServerDb` and `DataStore` run a `Migrate()` on startup to `ALTER TABLE ADD COLUMN` for backward compatibility with older databases.

## Dashboard (server-side, `Program.cs → Dashboard.Html`)

Four tabs rendered as a single `const string` HTML page:

- **Статистика** — card grid per machine: clicks, keys, active/inactive time, top-5 apps, status dot
- **Офис** — animated daily marathon canvas (`<canvas id="office-cv">`): all Marios on one shared World 1-1 track, X-position = today's `totalClicks + totalKeys` normalized to `MARATHON_TARGET`, flagpole + castle at the finish line, RAF loop
- **Отчёты** — period statistics: date-range picker with quick buttons, summary cards, per-client table with inline bars
- **Расписание** — `<input type="time">` fields for work hours and auto-reset, saved via `POST /api/config`

**Marathon runner state** (function `getActivityStatus(m)`, separate from `getStatus()`):
- **online** → 2-frame running Mario (`_SP.runA`/`runB`, scale 3), bobbing
- **away**   → standing Mario (`_SP.stand`, scale 3), no animation
- **offline** → hidden from the track; only counted in the top-right "Не на связи" badge

Conditions: `online` = `(recentClicks + recentKeys) > 0 && age < 150s`; `away` = connected but no input (`age < 600s`); `offline` = `age ≥ 600s`.

**Clock skew compensation**: `age` is computed via `_ageSec(lastSeen) = (Date.now() + _skew - lastSeen) / 1000`. `_skew` is refreshed on every `/api/machines` fetch from the `X-Server-Time` response header, so a dashboard viewer whose browser clock drifts from the server (no NTP, VM time drift, etc.) still classifies machines correctly. Both `getActivityStatus` and the stats-tab `getStatus` use the same helper.

**Position smoothing**: `_marathonPos: Map<machineId, x>` keeps the current visual X per runner and lerps toward `targetX` (`trackLeft + progress * trackWidth`) at factor `0.06` each RAF frame, so the 30-s poll cycle doesn't cause Mario teleports.

**Scene primitives** (all in `Dashboard.Html`): `_drawSky`, `_drawClouds`, `_drawHills`, `_drawGround`, `_drawQuestionBlock`, `_drawPipe`, `_drawFlagpole`, `_drawCastle`, `_drawCoin`, `_drawMarathonRunner`. The marathon X-axis target is `_marathonTarget`, refreshed from `/api/config` on each poll (falls back to 5000 if the server hasn't returned a value yet); admins edit it via the Schedule tab.

**Pixel sprite system**: sprites are defined in `const _SP` as arrays of 16-char strings; chars map to colors via `const _mCol` (`r`=red, `s`=skin, `b`=brown, `u`=blue, `0`=transparent). Drawn by `_sprite(ctx, ox, oy, rows, sc, pal)` using `fillRect` per pixel.

`getStatus()` (used for stats tab dots) uses only `lastSeen`: online < 90s, away < 600s, offline otherwise.

## Installer Scripts

Both installers are Inno Setup 6 scripts in `<project>/installer/setup.iss`. Output goes to `<project>/installer/output/`.

- **Client installer**: per-user, `PrivilegesRequired=lowest`, installs to `{localappdata}\Programs\MouseClickTracker`. On first install prompts for `ServerUrl` and `UserName` and writes `appsettings.json`; on auto-update reinstalls (silent), the existing `appsettings.json` is **preserved** (the post-install only seeds the file when it doesn't exist yet). `CloseApplications=yes` lets Inno gracefully shut down the running exe during overwrite. Registers Windows startup via `HKCU\...\Run`.
- **Server installer**: prompts for port (default 5001), writes `appsettings.json` (`Urls`), registers a Windows Service via `sc.exe`, adds a Windows Firewall inbound rule via `netsh`, and auto-starts the service. Uninstaller reverses all of this.

## Releasing a new client version

1. Bump `<Version>` in `MouseClickTracker/MouseClickTracker.csproj` (must be strictly greater than what currently-installed clients report).
2. Also bump `AppVersion` in `MouseClickTracker/installer/setup.iss` to the same value (cosmetic; controls Add/Remove Programs text).
3. `dotnet publish` + Inno Setup to produce `MouseClickTracker/installer/output/MouseClickTracker-Setup.exe`.
4. Copy that `.exe` to `%ProgramData%\MouseClickServer\downloads\` on the server.
5. Update (or create) `%ProgramData%\MouseClickServer\downloads\latest.json`:
   ```json
   { "version": "1.2.0", "fileName": "MouseClickTracker-Setup.exe" }
   ```
6. Within ~24h every running client will hit `/api/version`, see the higher number, fetch the installer, and silently self-replace (no UAC because installs are per-user). At startup the check fires after 30 seconds, so the first wave usually rolls out within minutes.

**Migrating from the legacy admin install** (`C:\Program Files\MouseClickTracker\`): the new installer uses a different `AppId` and a per-user install path, so it will install in parallel — one of the two will need to be uninstalled manually via Add/Remove Programs once the per-user version is verified to work. After that, all future updates are silent.

## Key Patterns

- **No dependency injection** — components are instantiated directly in `MainForm` constructor and passed by reference.
- **Embedded HTML** — both the client `WebServer.Html` and the server `Dashboard.Html` are `const string` literals. Edit them in-place; there are no separate asset files.
- **Server-driven client config** — work hours and reset time live only in the server DB (`settings` table). Clients receive them via `GET /api/config` inside `SyncService.Sync()` and apply them in-memory. If `ServerUrl` is blank, these features are inactive.
- **Batched SQLite writes** — `ActivityTracker` accumulates pending counters in memory and flushes every 10 ticks. Call `Flush()` before reading totals after a forced stop.
- **WinAPI hook lifetime** — `MouseHook` holds a delegate reference (`_proc`) as a field to prevent GC collection while the hook is active.
- **`SettingsForm`** — runtime dialog for editing `ServerUrl` and `UserName`. On save, disposes and recreates `SyncService` without restarting the app.
- **Server publish gotcha** — `dotnet publish` into an existing `publish/` folder that already contains a `publish/` subfolder creates a nested structure and fails on second run. Always delete `MouseClickServer/publish/` before publishing.
