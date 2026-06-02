using System.Net;
using System.Text;
using System.Text.Json;

namespace MouseClickTracker;

public sealed class WebServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly DataStore _store;
    private readonly ActivityTracker _activity;
    private bool _running;

    public WebServer(DataStore store, ActivityTracker activity, int port = 5000)
    {
        _store = store;
        _activity = activity;
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        _running = true;
        new Thread(Listen) { IsBackground = true }.Start();
    }

    private void Listen()
    {
        while (_running)
        {
            try
            {
                var ctx = _listener.GetContext();
                Task.Run(() => Handle(ctx));
            }
            catch (HttpListenerException) { break; }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";

            if (path == "/api/stats")
            {
                var (procName, title) = ForegroundApp.Get();
                var appStats = _store.LoadAppStats()
                    .Select(x => new { name = x.Name, seconds = x.Seconds })
                    .ToArray();

                var json = JsonSerializer.Serialize(new
                {
                    count            = _store.Load(),
                    keys             = _store.LoadKeys(),
                    active_seconds   = _activity.ActiveSeconds,
                    inactive_seconds = _activity.InactiveSeconds,
                    app_process      = procName,
                    app_title        = title,
                    app_stats        = appStats
                });
                Respond(ctx, json, "application/json");
            }
            else
            {
                Respond(ctx, Html, "text/html; charset=utf-8");
            }
        }
        catch
        {
            // Even on failure, close the response so the underlying socket and buffers are released.
            try { ctx.Response.Close(); } catch { }
        }
    }

    private static void Respond(HttpListenerContext ctx, string body, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    private const string Html = """
        <!DOCTYPE html>
        <html lang="ru">
        <head>
            <meta charset="UTF-8">
            <title>Mouse Click Tracker</title>
            <link rel="icon" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'%3E%3Ccircle cx='50' cy='50' r='50' fill='%238BC34A'/%3E%3C/svg%3E">
            <link href="https://fonts.googleapis.com/css2?family=Press+Start+2P&display=swap" rel="stylesheet">
            <style>
                * { box-sizing: border-box; margin: 0; padding: 0; }
                body {
                    font-family: 'Press Start 2P', monospace;
                    background: #0d0d1a;
                    color: #e0e0ff;
                    min-height: 100vh;
                    padding: 32px 20px;
                }
                body::after {
                    content: '';
                    position: fixed;
                    top: 0; left: 0; right: 0; bottom: 0;
                    background: repeating-linear-gradient(
                        0deg, transparent, transparent 2px,
                        rgba(0,0,0,0.06) 2px, rgba(0,0,0,0.06) 4px
                    );
                    pointer-events: none;
                    z-index: 999;
                }
                .layout {
                    display: flex;
                    gap: 20px;
                    max-width: 860px;
                    margin: 0 auto;
                    align-items: flex-start;
                }
                .panel {
                    background: #13132a;
                    border: 3px solid #3730a3;
                    box-shadow: 4px 4px 0 #3730a3;
                    padding: 28px 20px;
                    width: 272px;
                    flex-shrink: 0;
                    text-align: center;
                }
                .panel-title {
                    font-size: 0.45rem;
                    color: #818cf8;
                    letter-spacing: 0.12em;
                    text-transform: uppercase;
                    margin-bottom: 20px;
                }
                #cv {
                    display: block;
                    margin: 0 auto 16px;
                    image-rendering: pixelated;
                    image-rendering: crisp-edges;
                }
                .count {
                    font-size: 1.5rem;
                    color: #ffffff;
                    line-height: 1;
                    margin-bottom: 4px;
                    transition: color 0.1s;
                }
                .count.flash { color: #818cf8; }
                .count-sub {
                    font-size: 0.32rem;
                    color: #4f46e5;
                    letter-spacing: 0.12em;
                    margin-bottom: 12px;
                }
                .counts {
                    display: grid;
                    grid-template-columns: 1fr 1fr;
                    gap: 10px;
                    margin-bottom: 18px;
                }
                .count-cell { text-align: center; }
                .stats {
                    display: grid;
                    grid-template-columns: 1fr 1fr;
                    gap: 6px;
                    margin-bottom: 6px;
                }
                .stat {
                    background: #0d0d1a;
                    border: 2px solid #1e1e4a;
                    padding: 10px 6px;
                }
                .stat-label {
                    font-size: 0.32rem;
                    letter-spacing: 0.06em;
                    text-transform: uppercase;
                    margin-bottom: 6px;
                    color: #44447a;
                }
                .stat-value { font-size: 0.52rem; }
                .active .stat-value  { color: #4ade80; }
                .inactive .stat-value { color: #f87171; }
                .app-block {
                    background: #0d0d1a;
                    border: 2px solid #1e1e4a;
                    padding: 10px;
                    text-align: left;
                }
                .app-label {
                    font-size: 0.3rem;
                    color: #44447a;
                    text-transform: uppercase;
                    letter-spacing: 0.1em;
                    margin-bottom: 6px;
                }
                .app-name {
                    font-size: 0.42rem;
                    color: #c7c7ff;
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                }
                .app-title {
                    font-size: 0.32rem;
                    color: #44447a;
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                    margin-top: 4px;
                }
                .apps-panel {
                    background: #13132a;
                    border: 3px solid #1e1e4a;
                    box-shadow: 4px 4px 0 #1e1e4a;
                    padding: 24px;
                    flex: 1;
                    min-width: 0;
                }
                .apps-panel h2 {
                    font-size: 0.4rem;
                    color: #4f46e5;
                    letter-spacing: 0.1em;
                    text-transform: uppercase;
                    margin-bottom: 20px;
                }
                .app-row { margin-bottom: 14px; }
                .app-row-header {
                    display: flex;
                    justify-content: space-between;
                    align-items: baseline;
                    margin-bottom: 5px;
                }
                .app-row-name {
                    font-size: 0.38rem;
                    color: #a5a5cc;
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                    max-width: 65%;
                }
                .app-row-time {
                    font-size: 0.35rem;
                    color: #44447a;
                    white-space: nowrap;
                    flex-shrink: 0;
                }
                .bar-bg {
                    height: 5px;
                    background: #0d0d1a;
                    border: 1px solid #1e1e4a;
                }
                .bar-fill {
                    height: 100%;
                    background: #4f46e5;
                    transition: width 0.4s;
                }
                .empty-msg {
                    font-size: 0.4rem;
                    color: #333366;
                    text-align: center;
                    padding: 24px 0;
                }
            </style>
        </head>
        <body>
        <div class="layout">
            <div class="panel">
                <div class="panel-title">Mouse Tracker</div>
                <canvas id="cv"></canvas>
                <div class="counts">
                    <div class="count-cell">
                        <div class="count" id="count">—</div>
                        <div class="count-sub">кликов</div>
                    </div>
                    <div class="count-cell">
                        <div class="count" id="keys">—</div>
                        <div class="count-sub">клавиш</div>
                    </div>
                </div>
                <div class="stats">
                    <div class="stat active">
                        <div class="stat-label">Активно</div>
                        <div class="stat-value" id="active">—</div>
                    </div>
                    <div class="stat inactive">
                        <div class="stat-label">Неактивно</div>
                        <div class="stat-value" id="inactive">—</div>
                    </div>
                </div>
                <div class="app-block">
                    <div class="app-label">Сейчас</div>
                    <div class="app-name" id="app-name">—</div>
                    <div class="app-title" id="app-title"></div>
                </div>
            </div>
            <div class="apps-panel">
                <h2>Время в приложениях</h2>
                <div id="app-list"><div class="empty-msg">Нет данных</div></div>
            </div>
        </div>
        <script>
            // --- 8-bit pixel character ---
            const S = 8;
            const cv = document.getElementById('cv');
            cv.width  = 12 * S;
            cv.height = 16 * S;
            const cx = cv.getContext('2d');
            cx.imageSmoothingEnabled = false;

            const _ = null;
            const K = '#FDB97D'; // skin
            const H = '#3D1F08'; // hair
            const B = '#2B4BCC'; // shirt
            const P = '#15297A'; // pants
            const O = '#111111'; // shoe
            const E = '#220800'; // eyes/mouth

            // idle: arms out to both sides
            const idle = [
                [_,_,_,H,H,H,H,H,_,_,_,_],
                [_,_,H,H,H,H,H,H,H,_,_,_],
                [_,H,H,K,K,K,K,K,K,H,_,_],
                [_,_,K,K,E,K,K,E,K,K,_,_],
                [_,_,K,K,K,K,K,K,K,K,_,_],
                [_,_,K,K,K,E,E,K,K,K,_,_],
                [_,_,K,K,K,K,K,K,K,K,_,_],
                [B,B,B,B,B,B,B,B,B,B,B,B],
                [B,B,B,B,B,B,B,B,B,B,B,B],
                [_,B,B,B,B,B,B,B,B,B,B,_],
                [_,_,P,P,P,_,_,P,P,P,_,_],
                [_,_,P,P,P,_,_,P,P,P,_,_],
                [_,_,P,P,P,_,_,P,P,P,_,_],
                [_,_,P,P,P,_,_,P,P,P,_,_],
                [_,O,O,O,O,_,_,O,O,O,O,_],
                [O,O,O,O,_,_,_,_,O,O,O,O],
            ];

            // click: right arm raised up
            const cf = [
                [_,_,_,H,H,H,H,H,_,B,B,_],
                [_,_,H,H,H,H,H,H,H,B,_,_],
                [_,H,H,K,K,K,K,K,K,B,_,_],
                [_,_,K,K,E,K,K,E,K,B,_,_],
                [_,_,K,K,K,K,K,K,K,B,_,_],
                [_,_,K,K,K,E,E,K,K,B,_,_],
                [_,_,K,K,K,K,K,K,K,B,_,_],
                [B,B,B,B,B,B,B,B,B,B,_,_],
                [B,B,B,B,B,B,B,B,B,B,_,_],
                [_,B,B,B,B,B,B,B,B,B,_,_],
                [_,_,P,P,P,_,_,P,P,P,_,_],
                [_,_,P,P,P,_,_,P,P,P,_,_],
                [_,_,P,P,P,_,_,P,P,P,_,_],
                [_,_,P,P,P,_,_,P,P,P,_,_],
                [_,O,O,O,O,_,_,O,O,O,O,_],
                [O,O,O,O,_,_,_,_,O,O,O,O],
            ];

            function drawFrame(frame) {
                cx.clearRect(0, 0, cv.width, cv.height);
                for (let y = 0; y < frame.length; y++) {
                    for (let x = 0; x < frame[y].length; x++) {
                        if (!frame[y][x]) continue;
                        cx.fillStyle = frame[y][x];
                        cx.fillRect(x * S, y * S, S, S);
                    }
                }
            }

            drawFrame(idle);

            function triggerClick() {
                drawFrame(cf);
                setTimeout(() => drawFrame(idle), 130);
            }

            // --- Stats ---
            let prevCount = null;
            let prevKeys  = null;
            const elCount    = document.getElementById('count');
            const elKeys     = document.getElementById('keys');
            const elActive   = document.getElementById('active');
            const elInactive = document.getElementById('inactive');
            const elAppName  = document.getElementById('app-name');
            const elAppTitle = document.getElementById('app-title');
            const elAppList  = document.getElementById('app-list');

            function fmt(s) {
                const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
                if (h > 0) return h + 'ч ' + String(m).padStart(2,'0') + 'м';
                if (m > 0) return m + 'м ' + String(sec).padStart(2,'0') + 'с';
                return sec + 'с';
            }

            function esc(s) {
                return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
            }

            function renderApps(apps) {
                if (!apps || apps.length === 0) {
                    elAppList.innerHTML = '<div class="empty-msg">Нет данных</div>';
                    return;
                }
                const max = apps[0].seconds;
                elAppList.innerHTML = apps.map(a => {
                    const pct = max > 0 ? (a.seconds / max * 100).toFixed(1) : 0;
                    return '<div class="app-row">' +
                        '<div class="app-row-header">' +
                        '<div class="app-row-name">' + esc(a.name) + '</div>' +
                        '<div class="app-row-time">' + fmt(a.seconds) + '</div>' +
                        '</div><div class="bar-bg"><div class="bar-fill" style="width:' + pct + '%"></div></div></div>';
                }).join('');
            }

            async function update() {
                try {
                    const r = await fetch('/api/stats');
                    const d = await r.json();
                    elCount.textContent    = d.count.toLocaleString('ru');
                    elKeys.textContent     = (d.keys ?? 0).toLocaleString('ru');
                    elActive.textContent   = fmt(d.active_seconds);
                    elInactive.textContent = fmt(d.inactive_seconds);
                    elAppName.textContent  = d.app_process || '—';
                    elAppTitle.textContent = d.app_title || '';
                    if (prevCount !== null && d.count !== prevCount) {
                        elCount.classList.add('flash');
                        setTimeout(() => elCount.classList.remove('flash'), 130);
                        triggerClick();
                    }
                    if (prevKeys !== null && d.keys !== prevKeys) {
                        elKeys.classList.add('flash');
                        setTimeout(() => elKeys.classList.remove('flash'), 130);
                    }
                    prevCount = d.count;
                    prevKeys  = d.keys;
                    renderApps(d.app_stats);
                } catch(e) {}
            }

            update();
            setInterval(update, 1000);
        </script>
        </body>
        </html>
        """;

    public void Dispose()
    {
        _running = false;
        _listener.Stop();
    }
}
