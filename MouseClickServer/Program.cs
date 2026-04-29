using MouseClickServer;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "MouseClickServer");
var app = builder.Build();

var db = new ServerDb();

app.MapPost("/api/sync", async (HttpContext ctx) =>
{
    var payload = await ctx.Request.ReadFromJsonAsync<SyncPayload>();
    if (payload is null || string.IsNullOrWhiteSpace(payload.MachineId))
        return Results.BadRequest();
    db.Upsert(payload);
    return Results.Ok();
});

app.MapGet("/api/machines", () => db.GetAll().Select(m => new
{
    machineId       = m.MachineId,
    userName        = m.UserName,
    lastSeen        = m.LastSeen,
    totalClicks     = m.TotalClicks,
    activeSeconds   = m.ActiveSeconds,
    inactiveSeconds = m.InactiveSeconds,
    recentClicks    = m.RecentClicks,
    appStats        = m.AppStats.Select(a => new { processName = a.ProcessName, seconds = a.Seconds })
}));

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
        totalActiveSec   = m.TotalActiveSec,
        totalInactiveSec = m.TotalInactiveSec,
        days = m.Days.Select(d => new { day = d.Day, clicks = d.Clicks, activeSec = d.ActiveSec, inactiveSec = d.InactiveSec })
    });
});

app.MapGet("/", () => Results.Content(Dashboard.Html, "text/html; charset=utf-8"));

app.Run();

namespace MouseClickServer
{
    public record AppStatPayload(string ProcessName, long Seconds);
    public record ConfigPayload(string? WorkStart, string? WorkEnd, string? ResetTime);

    public record SyncPayload(
        string MachineId,
        string? UserName,
        long TotalClicks,
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

                    .metrics { display: grid; grid-template-columns: 1fr 1fr 1fr; gap: 10px; margin-bottom: 16px; }
                    .metric { background: #f8f9fa; border-radius: 10px; padding: 10px 12px; }
                    .m-label { font-size: 0.65rem; color: #bbb; text-transform: uppercase; letter-spacing: 0.06em; margin-bottom: 3px; }
                    .m-value { font-size: 1rem; font-weight: 600; color: #333; font-variant-numeric: tabular-nums; }
                    .metric.clicks .m-value   { color: #4f46e5; }
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
                                ['rep-s-clicks','rep-s-active','rep-s-pct'].forEach(id => document.getElementById(id).textContent = '—');
                                return;
                            }
                            data.sort((a, b) => b.totalClicks - a.totalClicks);
                            const totC = data.reduce((s, m) => s + m.totalClicks, 0);
                            const totA = data.reduce((s, m) => s + m.totalActiveSec, 0);
                            const totI = data.reduce((s, m) => s + m.totalInactiveSec, 0);
                            const pct  = totA + totI > 0 ? Math.round(totA / (totA + totI) * 100) : 0;
                            document.getElementById('rep-s-clicks').textContent = totC.toLocaleString('ru');
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
                                    <th class="num">Активно</th>
                                    <th class="num">Неактивно</th>
                                    <th class="num">% акт.</th>
                                </tr></thead>
                                <tbody>${rows}
                                <tr class="rep-total">
                                    <td>Итого <span style="font-weight:400;color:#aaa;font-size:0.8rem">${n} клиент${s}</span></td>
                                    <td class="num">${totC.toLocaleString('ru')}</td>
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
                    function getStatus(lastSeen) {
                        const age = (Date.now() - new Date(lastSeen)) / 1000;
                        return age < 90 ? 'online' : age < 600 ? 'away' : 'offline';
                    }
                    function relTime(lastSeen) {
                        const age = Math.floor((Date.now() - new Date(lastSeen)) / 1000);
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
                        return `
                            <div class="machine-card">
                                <div class="machine-header">
                                    <div class="status-dot status-${st}"></div>
                                    <div class="machine-name-block">
                                        <div class="machine-name">${displayName}</div>
                                        ${subtitle}
                                    </div>
                                    <div class="last-seen">${relTime(m.lastSeen)}</div>
                                </div>
                                <div class="metrics">
                                    <div class="metric clicks"><div class="m-label">Кликов</div><div class="m-value">${m.totalClicks.toLocaleString('ru')}</div></div>
                                    <div class="metric active"><div class="m-label">Актив.</div><div class="m-value">${fmt(m.activeSeconds)}</div></div>
                                    <div class="metric inactive"><div class="m-label">Неактив.</div><div class="m-value">${fmt(m.inactiveSeconds)}</div></div>
                                </div>
                                ${top.length ? `<div class="apps-label">Приложения</div>${bars}` : ''}
                            </div>`;
                    }

                    // ── Office tab — animated characters ──────────────────────
                    function _charPalette(i) {
                        const S = ['#4870c8','#c84848','#48c870','#9848c8','#c8a048','#48c8c0','#c84890','#78c848'];
                        const P = ['#283870','#702828','#287040','#582870','#705830','#287068','#702858','#407028'];
                        const H = ['#2c1a08','#1a1a1a','#8c6814','#5a3010','#a07828','#401808','#c89838','#3a3a50'];
                        return { shirt: S[i%S.length], pant: P[i%P.length], hair: H[i%H.length] };
                    }

                    function _drawDancer(ctx, cx, cy, pal, tick) {
                        const t   = tick * 0.12;
                        const bob = Math.sin(t) * 9;
                        const y   = cy + bob;

                        // Ground shadow
                        ctx.fillStyle = 'rgba(0,0,0,0.09)';
                        ctx.beginPath(); ctx.ellipse(cx, cy+64, 24, 7, 0, 0, Math.PI*2); ctx.fill();

                        // Legs (dancing spread)
                        const ls = Math.sin(t) * 13;
                        ctx.strokeStyle = pal.pant; ctx.lineWidth = 12; ctx.lineCap = 'round';
                        ctx.beginPath(); ctx.moveTo(cx, y+8); ctx.lineTo(cx-18+ls, y+52); ctx.stroke();
                        ctx.beginPath(); ctx.moveTo(cx, y+8); ctx.lineTo(cx+18-ls, y+52); ctx.stroke();
                        ctx.fillStyle = '#281a0a';
                        ctx.beginPath(); ctx.ellipse(cx-18+ls, y+57, 10, 5, -0.2, 0, Math.PI*2); ctx.fill();
                        ctx.beginPath(); ctx.ellipse(cx+18-ls, y+57, 10, 5,  0.2, 0, Math.PI*2); ctx.fill();

                        // Body
                        ctx.fillStyle = pal.shirt;
                        ctx.beginPath(); ctx.ellipse(cx, y-5, 18, 24, 0, 0, Math.PI*2); ctx.fill();

                        // Arms waving (opposite phase)
                        const aw = Math.sin(t + Math.PI) * 22;
                        ctx.strokeStyle = pal.shirt; ctx.lineWidth = 10; ctx.lineCap = 'round';
                        ctx.beginPath(); ctx.moveTo(cx-15, y-14); ctx.lineTo(cx-36, y-30+aw);  ctx.stroke();
                        ctx.beginPath(); ctx.moveTo(cx+15, y-14); ctx.lineTo(cx+36, y-30-aw);  ctx.stroke();

                        // Neck + head
                        ctx.fillStyle = '#f5c9a0';
                        ctx.beginPath(); ctx.ellipse(cx, y-27, 6, 8, 0, 0, Math.PI*2); ctx.fill();
                        ctx.beginPath(); ctx.arc(cx, y-46, 21, 0, Math.PI*2); ctx.fill();
                        ctx.fillStyle = pal.hair;
                        ctx.beginPath(); ctx.arc(cx, y-48, 20, Math.PI+0.25, -0.25); ctx.fill();

                        // Happy face
                        ctx.fillStyle = '#3a2510';
                        ctx.beginPath(); ctx.arc(cx-7, y-47, 3,   0, Math.PI*2); ctx.fill();
                        ctx.beginPath(); ctx.arc(cx+7, y-47, 3,   0, Math.PI*2); ctx.fill();
                        ctx.fillStyle = '#fff';
                        ctx.beginPath(); ctx.arc(cx-6, y-49, 1.3, 0, Math.PI*2); ctx.fill();
                        ctx.beginPath(); ctx.arc(cx+8, y-49, 1.3, 0, Math.PI*2); ctx.fill();
                        ctx.strokeStyle = '#3a2510'; ctx.lineWidth = 2.2; ctx.lineCap = 'round';
                        ctx.beginPath(); ctx.arc(cx, y-41, 8, 0.2, Math.PI-0.2); ctx.stroke();

                        // Floating music notes
                        ctx.textAlign = 'center';
                        for (let n = 0; n < 2; n++) {
                            const ph = (tick*0.02 + n*0.5) % 1;
                            const nx = cx + (n===0 ? 34 : -32);
                            const ny = y - 48 - ph*42;
                            const na = ph < 0.6 ? 0.85 : (1-ph)/0.4*0.85;
                            ctx.fillStyle = `rgba(${n===0?'80,60,220':'220,60,120'},${na})`;
                            ctx.font = `bold ${13+n*2}px sans-serif`;
                            ctx.fillText(n===0 ? '♪' : '♫', nx, ny);
                        }
                        ctx.textAlign = 'left';
                    }

                    function _drawSleeper(ctx, cx, cy, pal, tick) {
                        const breathe = Math.sin(tick * 0.04) * 1.5;

                        // Ground shadow
                        ctx.fillStyle = 'rgba(0,0,0,0.09)';
                        ctx.beginPath(); ctx.ellipse(cx, cy+64, 22, 7, 0, 0, Math.PI*2); ctx.fill();

                        // Legs (seated)
                        ctx.strokeStyle = pal.pant; ctx.lineWidth = 12; ctx.lineCap = 'round';
                        ctx.beginPath(); ctx.moveTo(cx-5, cy+12); ctx.lineTo(cx-22, cy+55); ctx.stroke();
                        ctx.beginPath(); ctx.moveTo(cx+5, cy+12); ctx.lineTo(cx+22, cy+55); ctx.stroke();
                        ctx.fillStyle = '#281a0a';
                        ctx.beginPath(); ctx.ellipse(cx-22, cy+59, 9, 5, -0.2, 0, Math.PI*2); ctx.fill();
                        ctx.beginPath(); ctx.ellipse(cx+22, cy+59, 9, 5,  0.2, 0, Math.PI*2); ctx.fill();

                        // Body (slight forward lean)
                        ctx.fillStyle = pal.shirt;
                        ctx.beginPath(); ctx.ellipse(cx+4, cy-4+breathe, 17, 22, 0.18, 0, Math.PI*2); ctx.fill();

                        // Arms hanging at sides
                        ctx.strokeStyle = pal.shirt; ctx.lineWidth = 10; ctx.lineCap = 'round';
                        ctx.beginPath(); ctx.moveTo(cx-10, cy-14); ctx.lineTo(cx-20, cy+20+breathe); ctx.stroke();
                        ctx.beginPath(); ctx.moveTo(cx+18, cy-14); ctx.lineTo(cx+26, cy+20+breathe); ctx.stroke();

                        // Neck + head (drooped)
                        ctx.fillStyle = '#f5c9a0';
                        ctx.beginPath(); ctx.ellipse(cx+6, cy-24+breathe, 6, 7, 0.18, 0, Math.PI*2); ctx.fill();
                        ctx.beginPath(); ctx.arc(cx+9, cy-42, 21, 0, Math.PI*2); ctx.fill();
                        ctx.fillStyle = pal.hair;
                        ctx.beginPath(); ctx.arc(cx+9, cy-44, 20, Math.PI+0.5, 0.15); ctx.fill();

                        // Sleeping face
                        ctx.strokeStyle = '#3a2510'; ctx.lineWidth = 2.2; ctx.lineCap = 'round';
                        ctx.beginPath(); ctx.moveTo(cx-1,  cy-44); ctx.lineTo(cx+10, cy-44); ctx.stroke();
                        ctx.beginPath(); ctx.moveTo(cx+14, cy-44); ctx.lineTo(cx+20, cy-44); ctx.stroke();
                        ctx.lineWidth = 2;
                        ctx.beginPath(); ctx.arc(cx+10, cy-35, 4, 0.3, Math.PI-0.3); ctx.stroke();

                        // ZZZ floating up (staggered)
                        ctx.textAlign = 'center';
                        for (let i = 0; i < 3; i++) {
                            const ph = (tick*0.022 + i*0.33) % 1;
                            const alpha = ph < 0.65 ? 0.9 : (1-ph)/0.35*0.9;
                            ctx.fillStyle = `rgba(100,120,210,${alpha})`;
                            ctx.font = `bold ${11+i*4}px "Segoe UI",sans-serif`;
                            ctx.fillText('z', cx+32+i*7, cy-48 - ph*45);
                        }
                        ctx.textAlign = 'left';
                    }

                    function _drawGhost(ctx, cx, cy, tick) {
                        const float = Math.sin(tick*0.07) * 9;
                        const gy    = cy - 8 + float;
                        const R     = 32;

                        // Shadow (shrinks as ghost floats up)
                        ctx.globalAlpha = Math.max(0.04, 0.11 - float*0.004);
                        ctx.fillStyle = '#666';
                        ctx.beginPath(); ctx.ellipse(cx, cy+62, 19-float*0.3, 5, 0, 0, Math.PI*2); ctx.fill();
                        ctx.globalAlpha = 0.80 + Math.sin(tick*0.05)*0.08;

                        // Ghost body: dome top + wavy skirt
                        ctx.fillStyle = '#dae6f8';
                        ctx.beginPath();
                        ctx.arc(cx, gy-R*0.2, R, Math.PI, 0);
                        ctx.lineTo(cx+R, gy+R);
                        ctx.quadraticCurveTo(cx+R*0.67, gy+R*0.55, cx+R*0.33, gy+R);
                        ctx.quadraticCurveTo(cx,         gy+R*0.55, cx-R*0.33, gy+R);
                        ctx.quadraticCurveTo(cx-R*0.67,  gy+R*0.55, cx-R,      gy+R);
                        ctx.closePath(); ctx.fill();

                        // Eyes
                        ctx.globalAlpha = 1;
                        ctx.fillStyle = '#6278c0';
                        ctx.beginPath(); ctx.ellipse(cx-R*0.38, gy-R*0.10, R*0.19, R*0.26, -0.18, 0, Math.PI*2); ctx.fill();
                        ctx.beginPath(); ctx.ellipse(cx+R*0.38, gy-R*0.10, R*0.19, R*0.26,  0.18, 0, Math.PI*2); ctx.fill();
                        ctx.fillStyle = '#dae6f8';
                        ctx.beginPath(); ctx.ellipse(cx-R*0.35, gy-R*0.07, R*0.09, R*0.13, -0.18, 0, Math.PI*2); ctx.fill();
                        ctx.beginPath(); ctx.ellipse(cx+R*0.41, gy-R*0.07, R*0.09, R*0.13,  0.18, 0, Math.PI*2); ctx.fill();

                        // Wavy worried mouth
                        ctx.strokeStyle = '#6278c0'; ctx.lineWidth = 2.4; ctx.lineCap = 'round';
                        ctx.beginPath();
                        ctx.moveTo(cx-R*0.28, gy+R*0.40);
                        ctx.quadraticCurveTo(cx-R*0.10, gy+R*0.52, cx,        gy+R*0.40);
                        ctx.quadraticCurveTo(cx+R*0.10, gy+R*0.28, cx+R*0.28, gy+R*0.40);
                        ctx.stroke();

                        // Little hands
                        ctx.fillStyle = '#dae6f8';
                        ctx.beginPath(); ctx.ellipse(cx-R-7, gy+R*0.35, 10, 7, -0.35, 0, Math.PI*2); ctx.fill();
                        ctx.beginPath(); ctx.ellipse(cx+R+7, gy+R*0.35, 10, 7,  0.35, 0, Math.PI*2); ctx.fill();

                        ctx.globalAlpha = 1;

                        // Pulsing "?" above
                        const qa = (Math.sin(tick*0.06)+1)*0.32;
                        ctx.fillStyle = `rgba(98,120,192,${qa})`;
                        ctx.font = 'bold 14px "Segoe UI",sans-serif'; ctx.textAlign = 'center';
                        ctx.fillText('? ? ?', cx, gy - R*1.38 - Math.sin(tick*0.08)*4);
                        ctx.textAlign = 'left';
                    }

                    // online = кликает (recentClicks > 0 в последнем синке)
                    // away   = подключён, но не кликает
                    // offline = давно не выходил на связь
                    function getActivityStatus(m) {
                        const age = (Date.now() - new Date(m.lastSeen)) / 1000;
                        if (age >= 600) return 'offline';
                        if (m.recentClicks > 0 && age < 150) return 'online';
                        return 'away';
                    }

                    function _drawCharSlot(ctx, cx, cy, machine, idx, tick) {
                        const st  = machine ? getActivityStatus(machine) : 'offline';
                        const pal = _charPalette(idx);
                        ctx.save();
                        if      (st === 'offline') _drawGhost(ctx, cx, cy, tick);
                        else if (st === 'online')  _drawDancer(ctx, cx, cy, pal, tick);
                        else                       _drawSleeper(ctx, cx, cy, pal, tick);
                        ctx.restore();

                        if (machine) {
                            const nm = (machine.userName || machine.machineId).substring(0, 16);
                            ctx.textAlign = 'center';
                            ctx.font = 'bold 13px "Segoe UI",sans-serif';
                            ctx.fillStyle = st==='online' ? '#3d4faa' : st==='away' ? '#b05e10' : '#8090c8';
                            ctx.fillText(nm, cx, cy+80);
                            ctx.font = '11px "Segoe UI",sans-serif';
                            ctx.fillStyle = st==='online' ? '#16a34a' : st==='away' ? '#d97706' : '#9ca3af';
                            ctx.fillText(st==='online' ? '● онлайн' : st==='away' ? '● отошёл' : '○ офлайн', cx, cy+95);
                            ctx.textAlign = 'left';
                        }
                    }

                    function _drawOfficeChars(ctx, machines, tick) {
                        const cv = ctx.canvas, W = cv.width, n = machines.length;
                        if (n === 0) {
                            if (cv.height !== 200) cv.height = 200;
                            ctx.fillStyle = '#f8f9ff'; ctx.fillRect(0, 0, W, 200);
                            ctx.fillStyle = '#ccc'; ctx.font = '15px "Segoe UI",sans-serif'; ctx.textAlign = 'center';
                            ctx.fillText('Нет подключённых клиентов', W/2, 100);
                            ctx.textAlign = 'left'; return;
                        }
                        const cols  = Math.min(n, 5);
                        const cellW = Math.floor(W / cols);
                        const cellH = 215;
                        const rows  = Math.ceil(n / cols);
                        const needH = rows * cellH + 16;
                        if (cv.height !== needH) cv.height = needH;
                        ctx.fillStyle = '#f8f9ff'; ctx.fillRect(0, 0, W, cv.height);
                        for (let i = 0; i < n; i++) {
                            const col = i % cols, row = Math.floor(i / cols);
                            _drawCharSlot(ctx, col*cellW + cellW/2, row*cellH + cellH*0.46, machines[i], i, tick);
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
                            const machines = await (await fetch('/api/machines')).json();
                            const online      = machines.filter(m => getStatus(m.lastSeen) === 'online').length;
                            const totalClicks = machines.reduce((s, m) => s + m.totalClicks, 0);
                            const totalActive = machines.reduce((s, m) => s + m.activeSeconds, 0);
                            document.getElementById('s-machines').textContent = machines.length;
                            document.getElementById('s-online').textContent   = online;
                            document.getElementById('s-clicks').textContent   = totalClicks.toLocaleString('ru');
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
