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

# Publish server (self-contained installer input)
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
  ├── Clicked  →  DataStore.Increment()        — writes to SQLite
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
  ├── GET /          — embedded HTML dashboard
  └── GET /api/stats — JSON snapshot read from DataStore + ActivityTracker

SyncService (optional, 1-minute timer)
  └── POST /api/sync → MouseClickServer   (only if ServerUrl set in appsettings.json)
```

**Thread safety**: `DataStore` uses a `lock` on all methods — it is accessed from the WinAPI hook callback thread, the `ActivityTracker` timer thread, and the `WebServer` request threads concurrently.

**Config** (`appsettings.json` next to exe):
```json
{ "ServerUrl": "http://<server-ip>:5001", "MachineId": "" }
```
`MachineId` defaults to `Environment.MachineName` if empty. `SyncService` is not created at all when `ServerUrl` is blank.

## Server Architecture (`MouseClickServer`)

Minimal ASP.NET Core API that runs as a Windows Service.

```
POST /api/sync   — receives SyncPayload from clients, upserts into ServerDb
GET  /api/machines — returns all machine snapshots ordered by last_seen DESC
GET  /           — embedded HTML dashboard (polls /api/machines every 30s)
```

**Database**: `%ProgramData%\MouseClickServer\server.db`
```
machines(machine_id PK, last_seen, total_clicks, active_seconds, inactive_seconds)
machine_app_stats(machine_id, process_name, seconds — composite PK)
```

Each sync **replaces** the machine's app_stats rows entirely (DELETE + INSERT in one transaction) — the server stores the latest snapshot, not a history.

**Online status logic** (dashboard only, not in DB): `last_seen < 90s` = online, `< 600s` = away, else offline.

## Installer Scripts

Both installers are Inno Setup 6 scripts in `<project>/installer/setup.iss`. Output goes to `<project>/installer/output/`.

- **Client installer**: prompts for `ServerUrl`, writes `appsettings.json` post-install; optionally adds to Windows startup via registry `HKCU\...\Run`.
- **Server installer**: prompts for port (default 5001), writes `appsettings.json` (`Urls`), registers a Windows Service via `sc.exe`, adds a Windows Firewall inbound rule via `netsh`, and auto-starts the service. Uninstaller reverses all of this.

## Key Patterns

- **No dependency injection** — components are instantiated directly in `MainForm` constructor and passed by reference.
- **Embedded HTML** — both the client `WebServer` and the server `Dashboard` class contain their full HTML/CSS/JS as `const string` literals. Edit these in-place.
- **Batched SQLite writes** — `ActivityTracker` accumulates pending counters in memory and flushes every 10 ticks to reduce write frequency. Always call `Flush()` before reading totals in tests or after forced stop.
- **WinAPI hook lifetime** — `MouseHook` holds a delegate reference (`_proc`) as a field to prevent GC collection while the hook is active.
