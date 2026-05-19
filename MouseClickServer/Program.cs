using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using MouseClickServer;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "MouseClickServer");
var app = builder.Build();

var db = new ServerDb();

// Directory where the admin drops the latest client installer + latest.json manifest.
var downloadsPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "MouseClickServer", "downloads");
Directory.CreateDirectory(downloadsPath);

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider      = new PhysicalFileProvider(downloadsPath),
    RequestPath       = "/downloads",
    ServeUnknownFileTypes = true,
    DefaultContentType    = "application/octet-stream",
    OnPrepareResponse = ctx => ctx.Context.Response.Headers["Cache-Control"] = "no-cache"
});

app.MapPost("/api/sync", async (HttpContext ctx) =>
{
    var payload = await ctx.Request.ReadFromJsonAsync<SyncPayload>();
    if (payload is null || string.IsNullOrWhiteSpace(payload.MachineId))
        return Results.BadRequest();
    db.Upsert(payload);
    return Results.Ok();
});

app.MapGet("/api/machines", (HttpContext ctx) =>
{
    // X-Server-Time lets the dashboard compute clock skew vs the viewer's browser
    // and apply it when classifying machines as online/away/offline.
    ctx.Response.Headers["X-Server-Time"] = DateTime.UtcNow.ToString("o");
    return db.GetAll().Select(m => new
    {
        machineId       = m.MachineId,
        userName        = m.UserName,
        lastSeen        = m.LastSeen,
        totalClicks     = m.TotalClicks,
        totalKeys       = m.TotalKeys,
        activeSeconds   = m.ActiveSeconds,
        inactiveSeconds = m.InactiveSeconds,
        recentClicks    = m.RecentClicks,
        recentKeys      = m.RecentKeys,
        wins            = m.Wins,
        appStats        = m.AppStats.Select(a => new { processName = a.ProcessName, seconds = a.Seconds })
    });
});

app.MapGet("/api/config", () => new
{
    workStart = db.GetSetting("work_start"),
    workEnd   = db.GetSetting("work_end"),
    resetTime = db.GetSetting("reset_time")
});

app.MapPost("/api/config", async (HttpContext ctx) =>
{
    var payload = await ctx.Request.ReadFromJsonAsync<ConfigPayload>();
    if (payload is null) return Results.BadRequest();
    db.SetSetting("work_start", payload.WorkStart ?? "");
    db.SetSetting("work_end",   payload.WorkEnd   ?? "");
    db.SetSetting("reset_time", payload.ResetTime ?? "");
    return Results.Ok();
});

app.MapGet("/api/stats", (string? from, string? to) =>
{
    var today = DateTime.Now.ToString("yyyy-MM-dd");
    var f = string.IsNullOrWhiteSpace(from) ? today : from;
    var t = string.IsNullOrWhiteSpace(to)   ? today : to;
    return db.GetPeriodStats(f, t).Select(m => new
    {
        machineId        = m.MachineId,
        userName         = m.UserName,
        totalClicks      = m.TotalClicks,
        totalKeys        = m.TotalKeys,
        totalActiveSec   = m.TotalActiveSec,
        totalInactiveSec = m.TotalInactiveSec,
        days = m.Days.Select(d => new { day = d.Day, clicks = d.Clicks, keys = d.Keys, activeSec = d.ActiveSec, inactiveSec = d.InactiveSec })
    });
});

app.MapGet("/api/version", () =>
{
    var manifestPath = Path.Combine(downloadsPath, "latest.json");
    if (!File.Exists(manifestPath))
        return Results.Ok(new { version = "0.0.0", fileName = (string?)null });
    try
    {
        var json = File.ReadAllText(manifestPath);
        var info = JsonSerializer.Deserialize<VersionManifest>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return Results.Ok(new
        {
            version  = info?.Version  ?? "0.0.0",
            fileName = info?.FileName ?? "MouseClickTracker-Setup.exe"
        });
    }
    catch
    {
        return Results.Ok(new { version = "0.0.0", fileName = (string?)null });
    }
});

app.MapGet("/", () => Results.Content(Dashboard.Html, "text/html; charset=utf-8"));

app.Run();

namespace MouseClickServer
{
    public record AppStatPayload(string ProcessName, long Seconds);
    public record ConfigPayload(string? WorkStart, string? WorkEnd, string? ResetTime);
    public record VersionManifest(string? Version, string? FileName);

    public record SyncPayload(
        string MachineId,
        string? UserName,
        long TotalClicks,
        long TotalKeys,
        long ActiveSeconds,
        long InactiveSeconds,
        List<AppStatPayload> AppStats);

    public static class Dashboard
    {
        public const string Html = """
            <!DOCTYPE html>
            <html lang="ru">
            <head>
                <meta charset="UTF-8">
                <title>Mouse Click Tracker — Сервер</title>
                <style>
                    * { box-sizing: border-box; margin: 0; padding: 0; }
                    body { font-family: 'Segoe UI', sans-serif; background: #f0f2f5; padding: 32px 24px; }
                    h1 { font-size: 1.1rem; font-weight: 600; color: #333; margin-bottom: 20px; }

                    .tabs { display: flex; gap: 2px; margin-bottom: 24px; border-bottom: 2px solid #e0e0e0; }
                    .tab-btn {
                        padding: 8px 22px; font-size: 0.85rem; font-family: inherit; border: none;
                        background: none; cursor: pointer; color: #999; border-bottom: 2px solid transparent;
                        margin-bottom: -2px; transition: color 0.15s;
                    }
                    .tab-btn.active { color: #4f46e5; border-bottom-color: #4f46e5; font-weight: 600; }

                    /* ── Schedule tab ── */
                    #tab-schedule { max-width: 480px; }
                    .sch-card { background: white; border-radius: 16px; padding: 28px 32px; box-shadow: 0 2px 16px rgba(0,0,0,0.06); }
                    .sch-card h2 { font-size: 0.95rem; font-weight: 600; color: #333; margin-bottom: 24px; }
                    .sch-group { margin-bottom: 22px; }
                    .sch-label { font-size: 0.8rem; font-weight: 600; color: #444; margin-bottom: 8px; }
                    .sch-row { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
                    .sch-row span { font-size: 0.85rem; color: #666; }
                    .sch-time {
                        font-size: 1rem; padding: 7px 10px; border: 1.5px solid #e0e0e0;
                        border-radius: 8px; font-family: inherit; color: #333; outline: none;
                    }
                    .sch-time:focus { border-color: #4f46e5; }
                    .sch-hint { font-size: 0.7rem; color: #aaa; margin-top: 5px; }
                    .sch-save { background: #4f46e5; color: white; border: none; padding: 9px 26px; border-radius: 8px; font-size: 0.88rem; font-family: inherit; cursor: pointer; }
                    .sch-save:hover { background: #4338ca; }
                    .sch-msg { font-size: 0.78rem; margin-left: 12px; color: #16a34a; }

                    /* ── Office tab ── */
                    #tab-people { background: white; border-radius: 16px; padding: 24px; box-shadow: 0 2px 16px rgba(0,0,0,0.06); }
                    #office-cv  { width: 100%; height: auto; display: block; }

                    .summary { display: flex; gap: 16px; margin-bottom: 28px; flex-wrap: wrap; }
                    .summary-card {
                        background: white; border-radius: 14px; padding: 18px 28px;
                        box-shadow: 0 2px 16px rgba(0,0,0,0.06); min-width: 160px;
                    }
                    .s-label { font-size: 0.7rem; color: #aaa; text-transform: uppercase; letter-spacing: 0.07em; margin-bottom: 6px; }
                    .s-value { font-size: 1.8rem; font-weight: 700; color: #111; font-variant-numeric: tabular-nums; }

                    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(300px, 1fr)); gap: 20px; }
                    .machine-card {
                        background: white; border-radius: 16px; padding: 24px 28px;
                        box-shadow: 0 2px 16px rgba(0,0,0,0.06);
                    }
                    .machine-header { display: flex; align-items: center; gap: 10px; margin-bottom: 16px; }
                    .status-dot { width: 10px; height: 10px; border-radius: 50%; flex-shrink: 0; }
                    .status-online  { background: #16a34a; }
                    .status-away    { background: #d97706; }
                    .status-offline { background: #dc2626; }
                    .machine-name-block { overflow: hidden; min-width: 0; }
                    .machine-name { font-size: 1rem; font-weight: 600; color: #222; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
                    .machine-id   { font-size: 0.7rem; color: #bbb; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; margin-top: 1px; }
                    .last-seen { font-size: 0.75rem; color: #bbb; margin-left: auto; flex-shrink: 0; }

                    .wins-badge {
                        display: inline-flex; align-items: center; gap: 5px;
                        background: radial-gradient(circle at 30% 30%, #FFEE88 0%, #F8C828 55%, #C89008 100%);
                        color: #5C2808; font-size: 0.7rem; font-weight: 700; font-variant-numeric: tabular-nums;
                        padding: 2px 9px 2px 6px; margin-left: 8px;
                        border: 1.5px solid #A07800; border-radius: 12px;
                        box-shadow: inset 0 0 0 1px rgba(255,238,136,0.5);
                        vertical-align: middle;
                    }
                    .wins-badge::before {
                        content: "★"; color: #A07800; font-size: 0.8rem; line-height: 1;
                    }
                    .wins-badge.empty {
                        background: radial-gradient(circle at 30% 30%, #E8E8E8 0%, #BBB 55%, #888 100%);
                        color: #555; border-color: #888;
                        box-shadow: inset 0 0 0 1px rgba(255,255,255,0.4);
                        opacity: 0.7;
                    }
                    .wins-badge.empty::before { color: #777; }

                    .metrics { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; margin-bottom: 16px; }
                    .metric { background: #f8f9fa; border-radius: 10px; padding: 10px 12px; }
                    .m-label { font-size: 0.65rem; color: #bbb; text-transform: uppercase; letter-spacing: 0.06em; margin-bottom: 3px; }
                    .m-value { font-size: 1rem; font-weight: 600; color: #333; font-variant-numeric: tabular-nums; }
                    .metric.clicks .m-value   { color: #4f46e5; }
                    .metric.keys .m-value     { color: #0891b2; }
                    .metric.active .m-value   { color: #16a34a; }
                    .metric.inactive .m-value { color: #dc2626; }

                    .apps-label { font-size: 0.68rem; color: #bbb; text-transform: uppercase; letter-spacing: 0.07em; margin-bottom: 8px; }
                    .app-row { margin-bottom: 8px; }
                    .app-row-header { display: flex; justify-content: space-between; margin-bottom: 3px; }
                    .app-row-name { font-size: 0.82rem; color: #444; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; max-width: 70%; }
                    .app-row-time { font-size: 0.78rem; color: #999; font-variant-numeric: tabular-nums; }
                    .bar-bg  { height: 4px; background: #f0f0f0; border-radius: 2px; }
                    .bar-fill { height: 100%; background: #4f46e5; border-radius: 2px; }

                    .empty   { color: #ccc; font-size: 0.85rem; text-align: center; padding: 40px 0; }
                    .updated { font-size: 0.72rem; color: #ccc; text-align: right; margin-top: 20px; }

                    /* ── Reports tab ── */
                    .rep-controls { display: flex; flex-wrap: wrap; align-items: center; gap: 16px; margin-bottom: 22px; }
                    .rep-quick    { display: flex; gap: 8px; flex-wrap: wrap; }
                    .rep-custom   { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
                    .rep-btn {
                        padding: 7px 16px; border: 1.5px solid #e0e0f0; border-radius: 8px; background: white;
                        font-size: 0.82rem; font-family: inherit; color: #666; cursor: pointer; transition: all 0.15s;
                    }
                    .rep-btn:hover { border-color: #4f46e5; color: #4f46e5; }
                    .rep-btn.active { background: #4f46e5; color: white; border-color: #4f46e5; font-weight: 600; }
                    .rep-date {
                        padding: 6px 10px; border: 1.5px solid #e0e0f0; border-radius: 8px;
                        font-family: inherit; font-size: 0.84rem; color: #333; outline: none;
                    }
                    .rep-date:focus { border-color: #4f46e5; }
                    .rep-submit {
                        background: #4f46e5; color: white; border: none; padding: 7px 20px;
                        border-radius: 8px; font-size: 0.84rem; font-family: inherit; cursor: pointer;
                    }
                    .rep-submit:hover { background: #4338ca; }
                    .rep-card { background: white; border-radius: 16px; box-shadow: 0 2px 16px rgba(0,0,0,0.06); overflow: hidden; margin-bottom: 20px; }
                    .rep-table { width: 100%; border-collapse: collapse; }
                    .rep-table th {
                        text-align: left; font-size: 0.7rem; color: #bbb; text-transform: uppercase;
                        letter-spacing: 0.07em; padding: 14px 20px 10px; border-bottom: 1px solid #f0f0f0; font-weight: 600;
                    }
                    .rep-table th.num { text-align: right; }
                    .rep-table td { padding: 11px 20px; border-bottom: 1px solid #f8f8f8; font-size: 0.88rem; vertical-align: middle; }
                    .rep-table tbody tr:last-child td { border-bottom: none; }
                    .rep-table tbody tr:hover td { background: #fafafe; }
                    .rep-table td.num { text-align: right; font-variant-numeric: tabular-nums; color: #555; }
                    .rep-name { font-weight: 600; color: #222; }
                    .rep-sub  { font-size: 0.7rem; color: #bbb; margin-top: 1px; }
                    .rep-bar  { height: 3px; background: #f0f0f8; border-radius: 2px; margin-top: 5px; }
                    .rep-bar-fill { height: 100%; background: #4f46e5; border-radius: 2px; }
                    .rep-total td { background: #f5f5fc !important; font-weight: 600; color: #333; border-bottom: none; }
                    .rep-empty { text-align: center; padding: 48px 0; color: #ccc; font-size: 0.88rem; }

                </style>
            </head>
            <body>
                <h1>Mouse Click Tracker — Сервер</h1>

                <div class="tabs">
                    <button class="tab-btn active" data-tab="stats">Статистика</button>
                    <button class="tab-btn"        data-tab="people">Офис</button>
                    <button class="tab-btn"        data-tab="reports">Отчёты</button>
                    <button class="tab-btn"        data-tab="schedule">Расписание</button>
                </div>

                <div id="tab-stats">
                    <div class="summary">
                        <div class="summary-card"><div class="s-label">Машин</div><div class="s-value" id="s-machines">—</div></div>
                        <div class="summary-card"><div class="s-label">Онлайн</div><div class="s-value" id="s-online">—</div></div>
                        <div class="summary-card"><div class="s-label">Кликов всего</div><div class="s-value" id="s-clicks">—</div></div>
                        <div class="summary-card"><div class="s-label">Клавиш всего</div><div class="s-value" id="s-keys">—</div></div>
                        <div class="summary-card"><div class="s-label">Активность всего</div><div class="s-value" id="s-active">—</div></div>
                    </div>
                    <div class="grid" id="grid"></div>
                </div>

                <div id="tab-people" style="display:none">
                    <canvas id="office-cv" width="960" height="420"></canvas>
                </div>

                <div id="tab-reports" style="display:none">
                    <div class="rep-controls">
                        <div class="rep-quick">
                            <button class="rep-btn active" data-range="today">Сегодня</button>
                            <button class="rep-btn" data-range="yesterday">Вчера</button>
                            <button class="rep-btn" data-range="week">7 дней</button>
                            <button class="rep-btn" data-range="month">30 дней</button>
                        </div>
                        <div class="rep-custom">
                            <input type="date" id="rep-from" class="rep-date">
                            <span style="color:#bbb">—</span>
                            <input type="date" id="rep-to" class="rep-date">
                            <button id="rep-load" class="rep-submit">Показать</button>
                        </div>
                    </div>
                    <div class="summary" id="rep-summary">
                        <div class="summary-card"><div class="s-label">Кликов всего</div><div class="s-value" id="rep-s-clicks">—</div></div>
                        <div class="summary-card"><div class="s-label">Клавиш всего</div><div class="s-value" id="rep-s-keys">—</div></div>
                        <div class="summary-card"><div class="s-label">Активно</div><div class="s-value" id="rep-s-active">—</div></div>
                        <div class="summary-card"><div class="s-label">% активности</div><div class="s-value" id="rep-s-pct">—</div></div>
                    </div>
                    <div id="rep-table-wrap"></div>
                </div>

                <div id="tab-schedule" style="display:none">
                    <div class="sch-card">
                        <h2>Расписание клиентов</h2>
                        <div class="sch-group">
                            <div class="sch-label">Считать клики</div>
                            <div class="sch-row">
                                <span>с</span>
                                <input type="time" id="sch-start" class="sch-time">
                                <span>до</span>
                                <input type="time" id="sch-end" class="sch-time">
                            </div>
                            <div class="sch-hint">Оставьте пустым — считать весь день</div>
                        </div>
                        <div class="sch-group">
                            <div class="sch-label">Авто-сброс кликов в</div>
                            <div class="sch-row">
                                <input type="time" id="sch-reset" class="sch-time">
                            </div>
                            <div class="sch-hint">Оставьте пустым — не сбрасывать</div>
                        </div>
                        <div class="sch-row">
                            <button class="sch-save" id="sch-save">Сохранить</button>
                            <span class="sch-msg" id="sch-msg"></span>
                        </div>
                    </div>
                </div>

                <div class="updated" id="updated"></div>

                <script>
                    // ── Tab switching ──────────────────────────────────────────
                    document.querySelectorAll('.tab-btn').forEach(btn => {
                        btn.addEventListener('click', () => {
                            document.querySelectorAll('.tab-btn').forEach(b => b.classList.remove('active'));
                            btn.classList.add('active');
                            document.getElementById('tab-stats').style.display    = btn.dataset.tab === 'stats'    ? '' : 'none';
                            document.getElementById('tab-people').style.display   = btn.dataset.tab === 'people'   ? '' : 'none';
                            document.getElementById('tab-reports').style.display  = btn.dataset.tab === 'reports'  ? '' : 'none';
                            document.getElementById('tab-schedule').style.display = btn.dataset.tab === 'schedule' ? '' : 'none';
                            if (btn.dataset.tab === 'schedule') loadSchedule();
                            if (btn.dataset.tab === 'reports')  loadReport();
                        });
                    });

                    // ── Reports tab ──────────────────────────────────────────
                    (function() {
                        const t = new Date();
                        const iso = d => d.toISOString().slice(0, 10);
                        document.getElementById('rep-from').value = iso(t);
                        document.getElementById('rep-to').value   = iso(t);
                    })();

                    document.querySelectorAll('.rep-btn').forEach(btn => {
                        btn.addEventListener('click', () => {
                            document.querySelectorAll('.rep-btn').forEach(b => b.classList.remove('active'));
                            btn.classList.add('active');
                            const t = new Date(), y = new Date(t);
                            const iso = d => d.toISOString().slice(0, 10);
                            if      (btn.dataset.range === 'yesterday') { y.setDate(t.getDate()-1);  document.getElementById('rep-from').value = iso(y); document.getElementById('rep-to').value = iso(y); }
                            else if (btn.dataset.range === 'week')      { y.setDate(t.getDate()-6);  document.getElementById('rep-from').value = iso(y); document.getElementById('rep-to').value = iso(t); }
                            else if (btn.dataset.range === 'month')     { y.setDate(t.getDate()-29); document.getElementById('rep-from').value = iso(y); document.getElementById('rep-to').value = iso(t); }
                            else { document.getElementById('rep-from').value = iso(t); document.getElementById('rep-to').value = iso(t); }
                            loadReport();
                        });
                    });

                    document.getElementById('rep-load').addEventListener('click', () => {
                        document.querySelectorAll('.rep-btn').forEach(b => b.classList.remove('active'));
                        loadReport();
                    });

                    async function loadReport() {
                        const from = document.getElementById('rep-from').value;
                        const to   = document.getElementById('rep-to').value;
                        if (!from || !to) return;
                        const wrap = document.getElementById('rep-table-wrap');
                        wrap.innerHTML = '<div class="rep-card"><div class="rep-empty">Загрузка…</div></div>';
                        try {
                            const data = await (await fetch(`/api/stats?from=${from}&to=${to}`)).json();
                            if (!data.length) {
                                wrap.innerHTML = '<div class="rep-card"><div class="rep-empty">Нет данных за выбранный период</div></div>';
                                ['rep-s-clicks','rep-s-keys','rep-s-active','rep-s-pct'].forEach(id => document.getElementById(id).textContent = '—');
                                return;
                            }
                            data.sort((a, b) => b.totalClicks - a.totalClicks);
                            const totC = data.reduce((s, m) => s + m.totalClicks, 0);
                            const totK = data.reduce((s, m) => s + (m.totalKeys || 0), 0);
                            const totA = data.reduce((s, m) => s + m.totalActiveSec, 0);
                            const totI = data.reduce((s, m) => s + m.totalInactiveSec, 0);
                            const pct  = totA + totI > 0 ? Math.round(totA / (totA + totI) * 100) : 0;
                            document.getElementById('rep-s-clicks').textContent = totC.toLocaleString('ru');
                            document.getElementById('rep-s-keys').textContent   = totK.toLocaleString('ru');
                            document.getElementById('rep-s-active').textContent = fmt(totA);
                            document.getElementById('rep-s-pct').textContent    = pct + '%';
                            const maxC = data[0].totalClicks || 1;
                            const rows = data.map(m => {
                                const mp = m.totalActiveSec + m.totalInactiveSec > 0
                                    ? Math.round(m.totalActiveSec / (m.totalActiveSec + m.totalInactiveSec) * 100) : 0;
                                const nm = m.userName
                                    ? `<div class="rep-name">${esc(m.userName)}</div><div class="rep-sub">${esc(m.machineId)}</div>`
                                    : `<div class="rep-name">${esc(m.machineId)}</div>`;
                                return `<tr>
                                    <td>${nm}<div class="rep-bar"><div class="rep-bar-fill" style="width:${(m.totalClicks/maxC*100).toFixed(1)}%"></div></div></td>
                                    <td class="num">${m.totalClicks.toLocaleString('ru')}</td>
                                    <td class="num">${(m.totalKeys || 0).toLocaleString('ru')}</td>
                                    <td class="num">${fmt(m.totalActiveSec)}</td>
                                    <td class="num">${fmt(m.totalInactiveSec)}</td>
                                    <td class="num">${mp}%</td>
                                </tr>`;
                            }).join('');
                            const n = data.length, s = n===1?'':'а';
                            wrap.innerHTML = `<div class="rep-card"><table class="rep-table">
                                <thead><tr>
                                    <th>Сотрудник</th>
                                    <th class="num">Кликов</th>
                                    <th class="num">Клавиш</th>
                                    <th class="num">Активно</th>
                                    <th class="num">Неактивно</th>
                                    <th class="num">% акт.</th>
                                </tr></thead>
                                <tbody>${rows}
                                <tr class="rep-total">
                                    <td>Итого <span style="font-weight:400;color:#aaa;font-size:0.8rem">${n} клиент${s}</span></td>
                                    <td class="num">${totC.toLocaleString('ru')}</td>
                                    <td class="num">${totK.toLocaleString('ru')}</td>
                                    <td class="num">${fmt(totA)}</td>
                                    <td class="num">${fmt(totI)}</td>
                                    <td class="num">${pct}%</td>
                                </tr></tbody>
                            </table></div>`;
                        } catch { wrap.innerHTML = '<div class="rep-card"><div class="rep-empty">Ошибка загрузки</div></div>'; }
                    }

                    // ── Schedule tab ──────────────────────────────────────────
                    async function loadSchedule() {
                        try {
                            const c = await (await fetch('/api/config')).json();
                            document.getElementById('sch-start').value = c.workStart || '';
                            document.getElementById('sch-end').value   = c.workEnd   || '';
                            document.getElementById('sch-reset').value = c.resetTime || '';
                        } catch {}
                    }

                    document.getElementById('sch-save').addEventListener('click', async () => {
                        const payload = {
                            workStart: document.getElementById('sch-start').value,
                            workEnd:   document.getElementById('sch-end').value,
                            resetTime: document.getElementById('sch-reset').value
                        };
                        const msg = document.getElementById('sch-msg');
                        try {
                            const r = await fetch('/api/config', {
                                method: 'POST',
                                headers: { 'Content-Type': 'application/json' },
                                body: JSON.stringify(payload)
                            });
                            msg.textContent = r.ok ? 'Сохранено ✓' : 'Ошибка';
                        } catch { msg.textContent = 'Ошибка соединения'; }
                        setTimeout(() => msg.textContent = '', 3000);
                    });

                    // ── Helpers ───────────────────────────────────────────────
                    function fmt(s) {
                        const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
                        if (h > 0) return `${h}ч ${String(m).padStart(2,'0')}м`;
                        if (m > 0) return `${m}м ${String(sec).padStart(2,'0')}с`;
                        return `${sec}с`;
                    }
                    function esc(s) {
                        return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
                    }
                    // Clock skew between server and the viewer's browser, updated on every /api/machines fetch.
                    let _skew = 0;
                    function _ageSec(lastSeen) {
                        return (Date.now() + _skew - new Date(lastSeen).getTime()) / 1000;
                    }
                    function getStatus(lastSeen) {
                        const age = _ageSec(lastSeen);
                        return age < 90 ? 'online' : age < 600 ? 'away' : 'offline';
                    }
                    function relTime(lastSeen) {
                        const age = Math.floor(_ageSec(lastSeen));
                        if (age < 60)   return `${age}с назад`;
                        if (age < 3600) return `${Math.floor(age/60)}м назад`;
                        return `${Math.floor(age/3600)}ч назад`;
                    }

                    // ── Stats tab ─────────────────────────────────────────────
                    function renderCard(m) {
                        const st  = getStatus(m.lastSeen);
                        const top = m.appStats.slice(0, 5);
                        const maxSec = top[0]?.seconds || 1;
                        const bars = top.map(a => `
                            <div class="app-row">
                                <div class="app-row-header">
                                    <div class="app-row-name">${esc(a.processName)}</div>
                                    <div class="app-row-time">${fmt(a.seconds)}</div>
                                </div>
                                <div class="bar-bg"><div class="bar-fill" style="width:${(a.seconds/maxSec*100).toFixed(1)}%"></div></div>
                            </div>`).join('');
                        const displayName = m.userName ? esc(m.userName) : esc(m.machineId);
                        const subtitle    = m.userName ? `<div class="machine-id">${esc(m.machineId)}</div>` : '';
                        const winsCnt     = m.wins || 0;
                        const wins        = `<span class="wins-badge${winsCnt > 0 ? '' : ' empty'}" title="Побед в дневном марафоне">${winsCnt}</span>`;
                        return `
                            <div class="machine-card">
                                <div class="machine-header">
                                    <div class="status-dot status-${st}"></div>
                                    <div class="machine-name-block">
                                        <div class="machine-name">${displayName}${wins}</div>
                                        ${subtitle}
                                    </div>
                                    <div class="last-seen">${relTime(m.lastSeen)}</div>
                                </div>
                                <div class="metrics">
                                    <div class="metric clicks"><div class="m-label">Кликов</div><div class="m-value">${m.totalClicks.toLocaleString('ru')}</div></div>
                                    <div class="metric keys"><div class="m-label">Клавиш</div><div class="m-value">${(m.totalKeys || 0).toLocaleString('ru')}</div></div>
                                    <div class="metric active"><div class="m-label">Актив.</div><div class="m-value">${fmt(m.activeSeconds)}</div></div>
                                    <div class="metric inactive"><div class="m-label">Неактив.</div><div class="m-value">${fmt(m.inactiveSeconds)}</div></div>
                                </div>
                                ${top.length ? `<div class="apps-label">Приложения</div>${bars}` : ''}
                            </div>`;
                    }

                    // ── Office tab — Mario pixel-art characters ───────────────
                    // Pixel colors: r=red(hat/shirt) s=skin b=brown(hair/boots) u=blue(overalls)
                    const _mCol = { r:'#D83010', s:'#F8A070', b:'#5C2808', u:'#2858C8', '0':null };

                    function _sprite(ctx, ox, oy, rows, sc, pal) {
                        rows.forEach((row, ry) => {
                            for (let rx = 0; rx < row.length; rx++) {
                                const c = pal[row[rx]]; if (!c) continue;
                                ctx.fillStyle = c;
                                ctx.fillRect(ox + rx*sc, oy + ry*sc, sc, sc);
                            }
                        });
                    }

                    const _SP = {
                        // 16-wide, 14-tall — standing
                        stand: [
                            '0000rrrrrr000000',
                            '000rrrrrrrrrr000',
                            '00bbbbssssbbbb00',
                            '00bbsssssssbb000',
                            '0bbssssssssssb00',
                            '00bsssssssssb000',
                            '000bbbbbbbbb0000',
                            '00srrrrrrrrrs000',
                            '0uurrrrrrrruuuu0',
                            '0uuuuuuuuuuuuuu0',
                            '00uuuuuuuuuuuu00',
                            '000uuuu00uuuu000',
                            '000bbbb00bbbbb00',
                            '00bbbbb00bbbb000',
                        ],
                        // 16-wide, 14-tall — run frame A (left arm fwd, right leg fwd)
                        runA: [
                            '0000rrrrrr000000',
                            '000rrrrrrrrrr000',
                            '00bbbbssssbbbb00',
                            '00bbsssssssbb000',
                            '0bbssssssssssb00',
                            '00bsssssssssb000',
                            '000bbbbbbbbb0000',
                            'ssssrrrrrrrss000',
                            '0uurrrrrrrruuuu0',
                            '0uuuuuuuuuuuuu00',
                            '000uuuuuuuuuu000',
                            '000buuuu0uuub000',
                            '0bbbbuuu00uubb00',
                            '0bbbb000000bbb00',
                        ],
                        // 16-wide, 14-tall — run frame B (right arm fwd, left leg fwd)
                        runB: [
                            '0000rrrrrr000000',
                            '000rrrrrrrrrr000',
                            '00bbbbssssbbbb00',
                            '00bbsssssssbb000',
                            '0bbssssssssssb00',
                            '00bsssssssssb000',
                            '000bbbbbbbbb0000',
                            '000ssrrrrrrrssss',
                            '0uurrrrrrrruuuu0',
                            '0uuuuuuuuuuuuu00',
                            '000uuuuuuuuuu000',
                            '000buuu0uuuub000',
                            '00bbuu00uuubbb00',
                            '000bb000000bbbb0',
                        ],
                    };

                    // online = activity within last 150s; away = connected, no recent input; offline = >10min
                    function getActivityStatus(m) {
                        const age = _ageSec(m.lastSeen);
                        if (age >= 600) return 'offline';
                        const recent = (m.recentClicks || 0) + (m.recentKeys || 0);
                        if (recent > 0 && age < 150) return 'online';
                        return 'away';
                    }

                    // ── Marathon scene helpers ───────────────────────────────
                    const MARATHON_TARGET = 5000;        // combined clicks+keys to reach the flagpole
                    const _marathonPos = new Map();      // machineId → smoothed x (lerps toward target)

                    function _drawSky(ctx, W, H) {
                        const g = ctx.createLinearGradient(0, 0, 0, H);
                        g.addColorStop(0,    '#5C94FC');
                        g.addColorStop(0.75, '#9CB4FC');
                        ctx.fillStyle = g;
                        ctx.fillRect(0, 0, W, H);
                    }

                    function _drawCloud(ctx, x, y) {
                        ctx.fillStyle = '#FFFFFF';
                        ctx.beginPath();
                        ctx.arc(x,      y,     14, 0, Math.PI * 2);
                        ctx.arc(x + 16, y - 4, 18, 0, Math.PI * 2);
                        ctx.arc(x + 36, y,     14, 0, Math.PI * 2);
                        ctx.arc(x + 22, y + 8, 14, 0, Math.PI * 2);
                        ctx.fill();
                    }

                    function _drawClouds(ctx, W, tick) {
                        for (let i = 0; i < 4; i++) {
                            const cx = ((tick * 0.18 + i * 280) % (W + 200)) - 100;
                            const cy = 24 + (i % 2) * 22;
                            _drawCloud(ctx, cx, cy);
                        }
                    }

                    function _drawHills(ctx, W, groundY) {
                        ctx.fillStyle = '#3FA34D';
                        for (let i = 0; i < 4; i++) {
                            const hx = 80 + i * 280;
                            ctx.beginPath();
                            ctx.arc(hx, groundY, 56, Math.PI, 0);
                            ctx.fill();
                        }
                    }

                    function _drawGround(ctx, W, groundY, H) {
                        const tileW = 16;
                        for (let y = groundY; y < H; y += tileW) {
                            const offset = (Math.floor((y - groundY) / tileW) % 2) * 8;
                            for (let x = -offset; x < W; x += tileW) {
                                ctx.fillStyle = '#C46038';
                                ctx.fillRect(x, y, tileW - 2, tileW - 2);
                                ctx.fillStyle = '#7A2810';
                                ctx.fillRect(x + tileW - 4, y, 2, tileW - 2);
                                ctx.fillRect(x, y + tileW - 4, tileW - 2, 2);
                                ctx.fillStyle = '#FFAA70';
                                ctx.fillRect(x, y, 3, 3);
                            }
                        }
                    }

                    function _drawQuestionBlock(ctx, x, y, tick) {
                        const sz = 24;
                        const pulse = Math.floor(tick / 20) % 4 === 0 ? '#FFD060' : '#F8B038';
                        ctx.fillStyle = pulse;
                        ctx.fillRect(x, y, sz, sz);
                        ctx.fillStyle = '#A06800';
                        ctx.fillRect(x + sz - 3, y, 3, sz);
                        ctx.fillRect(x, y + sz - 3, sz, 3);
                        ctx.fillStyle = '#FFFFFF';
                        ctx.font = 'bold 14px "Segoe UI",sans-serif';
                        ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
                        ctx.fillText('?', x + sz / 2, y + sz / 2 + 1);
                        ctx.textAlign = 'left'; ctx.textBaseline = 'alphabetic';
                    }

                    function _drawPipe(ctx, x, groundY, h) {
                        ctx.fillStyle = '#2EC04F';
                        ctx.fillRect(x + 4, groundY - h, 24, h);
                        ctx.fillRect(x,     groundY - h, 32, 12);
                        ctx.fillStyle = '#84E090';
                        ctx.fillRect(x + 4, groundY - h + 2, 4, h - 2);
                        ctx.fillRect(x + 2, groundY - h + 2, 26, 4);
                        ctx.fillStyle = '#1A8030';
                        ctx.fillRect(x + 24, groundY - h + 2, 4, h - 2);
                        ctx.fillRect(x + 26, groundY - h, 6, 12);
                    }

                    function _drawFlagpole(ctx, x, groundY) {
                        const top = groundY - 150;
                        ctx.fillStyle = '#FFFFFF';
                        ctx.fillRect(x - 2, top + 6, 4, groundY - top - 6);
                        ctx.fillStyle = '#202020';
                        ctx.beginPath(); ctx.arc(x, top + 4, 5, 0, Math.PI * 2); ctx.fill();
                        ctx.fillStyle = '#F8F0D8';
                        ctx.beginPath();
                        ctx.moveTo(x + 3, top + 14);
                        ctx.lineTo(x + 30, top + 22);
                        ctx.lineTo(x + 3, top + 30);
                        ctx.closePath();
                        ctx.fill();
                        ctx.strokeStyle = '#1A1A4A'; ctx.lineWidth = 1.2; ctx.stroke();
                    }

                    function _drawCastle(ctx, x, groundY) {
                        const w = 84, h = 64;
                        const cy = groundY - h;
                        ctx.fillStyle = '#A8A8A8';
                        ctx.fillRect(x, cy, w, h);
                        for (let i = 0; i < 5; i++) {
                            if (i % 2 === 0) ctx.fillRect(x + i * 16, cy - 8, 12, 10);
                        }
                        ctx.fillStyle = '#808080';
                        ctx.fillRect(x + w * 0.30, cy - 22, w * 0.40, 18);
                        for (let i = 0; i < 3; i++) {
                            ctx.fillRect(x + w * 0.30 + i * (w * 0.13), cy - 30, w * 0.10, 8);
                        }
                        ctx.fillStyle = '#202020';
                        ctx.fillRect(x + w * 0.40, cy + h * 0.35, w * 0.20, h * 0.65);
                        ctx.beginPath();
                        ctx.arc(x + w * 0.50, cy + h * 0.35, w * 0.10, Math.PI, 0);
                        ctx.fill();
                    }

                    function _drawCoin(ctx, x, y, tick) {
                        const pulse = 1 + 0.12 * Math.sin(tick * 0.12);
                        ctx.fillStyle = '#F8C828';
                        ctx.beginPath(); ctx.arc(x, y, 5 * pulse, 0, Math.PI * 2); ctx.fill();
                        ctx.fillStyle = '#FFEE88';
                        ctx.beginPath(); ctx.arc(x - 1, y - 1, 2 * pulse, 0, Math.PI * 2); ctx.fill();
                        ctx.strokeStyle = '#A07800'; ctx.lineWidth = 1;
                        ctx.beginPath(); ctx.arc(x, y, 5 * pulse, 0, Math.PI * 2); ctx.stroke();
                    }

                    function _drawScoreBadge(ctx, cx, cy, score, status) {
                        const text = score.toLocaleString('ru');
                        ctx.font = 'bold 13px "Segoe UI",sans-serif';
                        const tw = ctx.measureText(text).width;
                        const padL = 24, padR = 12, h = 20;
                        const w = padL + tw + padR;
                        const x = Math.round(cx - w / 2);
                        const y = Math.round(cy - h / 2);
                        const bg = status === 'offline' ? 'rgba(70,70,90,0.92)'
                                 : status === 'online'  ? 'rgba(20,90,40,0.92)'
                                                        : 'rgba(120,72,24,0.92)';
                        ctx.fillStyle = bg;
                        if (ctx.roundRect) {
                            ctx.beginPath(); ctx.roundRect(x, y, w, h, h / 2); ctx.fill();
                        } else {
                            ctx.fillRect(x, y, w, h);
                        }
                        ctx.strokeStyle = 'rgba(255,255,255,0.35)'; ctx.lineWidth = 1;
                        if (ctx.roundRect) {
                            ctx.beginPath(); ctx.roundRect(x + 0.5, y + 0.5, w - 1, h - 1, h / 2); ctx.stroke();
                        }
                        const coinX = x + 12, coinY = y + h / 2;
                        ctx.fillStyle = '#F8C828';
                        ctx.beginPath(); ctx.arc(coinX, coinY, 6, 0, Math.PI * 2); ctx.fill();
                        ctx.fillStyle = '#FFEE88';
                        ctx.beginPath(); ctx.arc(coinX - 1.5, coinY - 1.5, 2, 0, Math.PI * 2); ctx.fill();
                        ctx.strokeStyle = '#A07800'; ctx.lineWidth = 1;
                        ctx.beginPath(); ctx.arc(coinX, coinY, 6, 0, Math.PI * 2); ctx.stroke();
                        ctx.fillStyle = '#FFFFFF';
                        ctx.textAlign = 'left'; ctx.textBaseline = 'middle';
                        ctx.fillText(text, x + padL, y + h / 2 + 1);
                        ctx.textBaseline = 'alphabetic';
                    }

                    function _drawMarathonRunner(ctx, cx, groundY, tick, idle) {
                        const sc = 3;
                        const fr = idle
                            ? _SP.stand
                            : (Math.floor(tick / 6) % 2 === 0 ? _SP.runA : _SP.runB);
                        const bob = idle ? 0 : Math.sin(tick * 0.20) * 3;
                        const W   = fr[0].length * sc, H = fr.length * sc;
                        const ox  = Math.round(cx - W / 2);
                        const oy  = Math.round(groundY - H + bob);
                        ctx.fillStyle = 'rgba(0,0,0,0.18)';
                        ctx.beginPath(); ctx.ellipse(cx, groundY - 1, W * 0.42, 4, 0, 0, Math.PI * 2); ctx.fill();
                        _sprite(ctx, ox, oy, fr, sc, _mCol);
                    }

                    function _drawOfficeChars(ctx, machines, tick) {
                        const cv = ctx.canvas, W = cv.width, H = 300;
                        if (cv.height !== H) cv.height = H;
                        const groundY    = 240;
                        const trackLeft  = 50;
                        const trackRight = W - 110;
                        const trackWidth = trackRight - trackLeft;

                        _drawSky(ctx, W, H);
                        _drawClouds(ctx, W, tick);
                        _drawHills(ctx, W, groundY);

                        // milestones (decorative): ? blocks and coin rows at 25/50/75%
                        for (const p of [0.25, 0.50, 0.75]) {
                            const x = trackLeft + p * trackWidth;
                            _drawQuestionBlock(ctx, x - 12, groundY - 92, tick);
                            for (let i = 0; i < 3; i++) _drawCoin(ctx, x - 16 + i * 16, groundY - 124, tick);
                        }

                        _drawPipe(ctx, trackLeft + 0.40 * trackWidth - 16, groundY, 30);
                        _drawPipe(ctx, trackLeft + 0.68 * trackWidth - 16, groundY, 46);

                        _drawGround(ctx, W, groundY, H);

                        _drawFlagpole(ctx, trackRight, groundY);
                        _drawCastle(ctx, trackRight + 28, groundY);

                        // start line
                        ctx.fillStyle = '#FFFFFF';
                        ctx.fillRect(trackLeft - 2, groundY - 12, 2, 12);
                        ctx.font = 'bold 10px "Segoe UI",sans-serif';
                        ctx.fillStyle = '#0a0a3a';
                        ctx.fillText('START', trackLeft - 22, groundY - 16);

                        // banner with day target
                        ctx.fillStyle = 'rgba(0,0,0,0.55)';
                        ctx.fillRect(8, 8, 220, 24);
                        ctx.fillStyle = '#FFFFFF';
                        ctx.font = 'bold 11px "Segoe UI",sans-serif';
                        ctx.fillText(`Цель дня: ${MARATHON_TARGET.toLocaleString('ru')} событий`, 16, 24);

                        // offline counter (top-right)
                        const offlineCount = machines.filter(m => getActivityStatus(m) === 'offline').length;
                        if (offlineCount > 0) {
                            ctx.fillStyle = 'rgba(0,0,0,0.55)';
                            ctx.fillRect(W - 138, 8, 130, 24);
                            ctx.fillStyle = '#FFFFFF';
                            ctx.fillText(`Не на связи: ${offlineCount}`, W - 130, 24);
                        }

                        if (machines.length === 0) {
                            ctx.fillStyle = '#FFFFFF';
                            ctx.font = 'bold 15px "Segoe UI",sans-serif';
                            ctx.textAlign = 'center';
                            ctx.fillText('Нет подключённых клиентов', W / 2, H / 2);
                            ctx.textAlign = 'left';
                            return;
                        }

                        // runners: offline are faded but still on the track; leaders draw on top
                        const ranked = machines
                            .map(m => ({
                                m,
                                progress: Math.min(1, ((m.totalClicks || 0) + (m.totalKeys || 0)) / MARATHON_TARGET),
                                status:   getActivityStatus(m)
                            }))
                            .sort((a, b) => a.progress - b.progress);

                        for (const r of ranked) {
                            const id = r.m.machineId;
                            const targetX = trackLeft + r.progress * trackWidth;
                            const cur = _marathonPos.get(id);
                            const next = cur === undefined ? targetX : cur + (targetX - cur) * 0.06;
                            _marathonPos.set(id, next);

                            const offline = r.status === 'offline';
                            ctx.save();
                            if (offline) ctx.globalAlpha = 0.4;
                            _drawMarathonRunner(ctx, next, groundY, tick, r.status !== 'online');

                            const todayScore = (r.m.totalClicks || 0) + (r.m.totalKeys || 0);
                            const nm = (r.m.userName || r.m.machineId).substring(0, 16);

                            ctx.textAlign = 'center';
                            ctx.font = 'bold 11px "Segoe UI",sans-serif';
                            ctx.fillStyle = offline ? '#404060' : '#0a0a3a';
                            ctx.fillText(nm, next, groundY - 74);
                            ctx.textAlign = 'left';
                            _drawScoreBadge(ctx, next, groundY - 58, todayScore, r.status);
                            ctx.restore();
                        }
                    }

                    let _officeMachines = [], _officeRaf = null, _officeTick = 0;

                    function renderPeople(machines) {
                        _officeMachines = machines;
                        const cv = document.getElementById('office-cv');
                        if (!cv) return;
                        if (_officeRaf) return;
                        const ctx = cv.getContext('2d');
                        (function frame() {
                            _officeTick++;
                            _drawOfficeChars(ctx, _officeMachines, _officeTick);
                            _officeRaf = requestAnimationFrame(frame);
                        })();
                    }

                    // ── Main update loop ──────────────────────────────────────
                    async function update() {
                        try {
                            const r = await fetch('/api/machines');
                            const st = r.headers.get('X-Server-Time');
                            if (st) _skew = new Date(st).getTime() - Date.now();
                            const machines = await r.json();
                            const online      = machines.filter(m => getStatus(m.lastSeen) === 'online').length;
                            const totalClicks = machines.reduce((s, m) => s + m.totalClicks, 0);
                            const totalKeys   = machines.reduce((s, m) => s + (m.totalKeys || 0), 0);
                            const totalActive = machines.reduce((s, m) => s + m.activeSeconds, 0);
                            document.getElementById('s-machines').textContent = machines.length;
                            document.getElementById('s-online').textContent   = online;
                            document.getElementById('s-clicks').textContent   = totalClicks.toLocaleString('ru');
                            document.getElementById('s-keys').textContent     = totalKeys.toLocaleString('ru');
                            document.getElementById('s-active').textContent   = fmt(totalActive);
                            document.getElementById('grid').innerHTML = machines.length
                                ? machines.map(renderCard).join('')
                                : '<div class="empty">Нет данных — ждём первого клиента</div>';
                            renderPeople(machines);
                            document.getElementById('updated').textContent = 'Обновлено: ' + new Date().toLocaleTimeString('ru');
                        } catch {}
                    }
                    update();
                    setInterval(update, 30_000);
                </script>
            </body>
            </html>
            """;
    }
}
