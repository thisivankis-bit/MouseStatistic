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
    lastSeen        = m.LastSeen,
    totalClicks     = m.TotalClicks,
    activeSeconds   = m.ActiveSeconds,
    inactiveSeconds = m.InactiveSeconds,
    appStats        = m.AppStats.Select(a => new { processName = a.ProcessName, seconds = a.Seconds })
}));

app.MapGet("/", () => Results.Content(Dashboard.Html, "text/html; charset=utf-8"));

app.Run();

namespace MouseClickServer
{
    public record AppStatPayload(string ProcessName, long Seconds);

    public record SyncPayload(
        string MachineId,
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
                    h1 { font-size: 1.1rem; font-weight: 600; color: #333; margin-bottom: 24px; }

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
                    .machine-name { font-size: 1rem; font-weight: 600; color: #222; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
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
                </style>
            </head>
            <body>
                <h1>Mouse Click Tracker — Сервер</h1>

                <div class="summary">
                    <div class="summary-card"><div class="s-label">Машин</div><div class="s-value" id="s-machines">—</div></div>
                    <div class="summary-card"><div class="s-label">Онлайн</div><div class="s-value" id="s-online">—</div></div>
                    <div class="summary-card"><div class="s-label">Кликов всего</div><div class="s-value" id="s-clicks">—</div></div>
                    <div class="summary-card"><div class="s-label">Активность всего</div><div class="s-value" id="s-active">—</div></div>
                </div>

                <div class="grid" id="grid"></div>
                <div class="updated" id="updated"></div>

                <script>
                    function fmt(s) {
                        const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
                        if (h > 0) return `${h}ч ${String(m).padStart(2,'0')}м`;
                        if (m > 0) return `${m}м ${String(sec).padStart(2,'0')}с`;
                        return `${sec}с`;
                    }
                    function statusClass(lastSeen) {
                        const age = (Date.now() - new Date(lastSeen)) / 1000;
                        return age < 90 ? 'status-online' : age < 600 ? 'status-away' : 'status-offline';
                    }
                    function relTime(lastSeen) {
                        const age = Math.floor((Date.now() - new Date(lastSeen)) / 1000);
                        if (age < 60)   return `${age}с назад`;
                        if (age < 3600) return `${Math.floor(age/60)}м назад`;
                        return `${Math.floor(age/3600)}ч назад`;
                    }
                    function renderCard(m) {
                        const top = m.appStats.slice(0, 5);
                        const maxSec = top[0]?.seconds || 1;
                        const bars = top.map(a => `
                            <div class="app-row">
                                <div class="app-row-header">
                                    <div class="app-row-name">${a.processName}</div>
                                    <div class="app-row-time">${fmt(a.seconds)}</div>
                                </div>
                                <div class="bar-bg"><div class="bar-fill" style="width:${(a.seconds/maxSec*100).toFixed(1)}%"></div></div>
                            </div>`).join('');
                        return `
                            <div class="machine-card">
                                <div class="machine-header">
                                    <div class="status-dot ${statusClass(m.lastSeen)}"></div>
                                    <div class="machine-name">${m.machineId}</div>
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
                    async function update() {
                        try {
                            const machines = await (await fetch('/api/machines')).json();
                            const online      = machines.filter(m => (Date.now() - new Date(m.lastSeen)) / 1000 < 90).length;
                            const totalClicks = machines.reduce((s, m) => s + m.totalClicks, 0);
                            const totalActive = machines.reduce((s, m) => s + m.activeSeconds, 0);
                            document.getElementById('s-machines').textContent = machines.length;
                            document.getElementById('s-online').textContent   = online;
                            document.getElementById('s-clicks').textContent   = totalClicks.toLocaleString('ru');
                            document.getElementById('s-active').textContent   = fmt(totalActive);
                            document.getElementById('grid').innerHTML = machines.length
                                ? machines.map(renderCard).join('')
                                : '<div class="empty">Нет данных — ждём первого клиента</div>';
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
