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
            <style>
                * { box-sizing: border-box; margin: 0; padding: 0; }
                body {
                    font-family: 'Segoe UI', sans-serif;
                    background: #f0f2f5;
                    padding: 40px 20px;
                    min-height: 100vh;
                }
                .layout {
                    display: flex;
                    gap: 24px;
                    max-width: 860px;
                    margin: 0 auto;
                    align-items: flex-start;
                }
                .card {
                    background: white;
                    border-radius: 20px;
                    padding: 40px 48px;
                    box-shadow: 0 4px 32px rgba(0,0,0,0.07);
                    flex-shrink: 0;
                    width: 300px;
                    text-align: center;
                }
                .section-label {
                    color: #aaa;
                    font-size: 0.75rem;
                    letter-spacing: 0.08em;
                    text-transform: uppercase;
                    margin-bottom: 8px;
                }
                .count {
                    font-size: 5rem;
                    font-weight: 700;
                    color: #111;
                    line-height: 1;
                    transition: color 0.15s;
                    margin-bottom: 32px;
                }
                .count.flash { color: #4f46e5; }
                .stats {
                    display: grid;
                    grid-template-columns: 1fr 1fr;
                    gap: 12px;
                    margin-bottom: 16px;
                }
                .stat {
                    background: #f8f9fa;
                    border-radius: 12px;
                    padding: 14px 12px;
                }
                .stat .stat-label {
                    font-size: 0.7rem;
                    color: #bbb;
                    text-transform: uppercase;
                    letter-spacing: 0.06em;
                    margin-bottom: 4px;
                }
                .stat .stat-value {
                    font-size: 1.1rem;
                    font-weight: 600;
                    font-variant-numeric: tabular-nums;
                }
                .active .stat-value  { color: #16a34a; }
                .inactive .stat-value { color: #dc2626; }
                .app-block {
                    background: #f8f9fa;
                    border-radius: 12px;
                    padding: 12px 14px;
                    text-align: left;
                }
                .app-name {
                    font-size: 0.95rem;
                    font-weight: 600;
                    color: #333;
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                }
                .app-title {
                    font-size: 0.78rem;
                    color: #999;
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                    margin-top: 2px;
                }

                /* app stats table */
                .apps-card {
                    background: white;
                    border-radius: 20px;
                    padding: 32px 36px;
                    box-shadow: 0 4px 32px rgba(0,0,0,0.07);
                    flex: 1;
                    min-width: 0;
                }
                .apps-card h2 {
                    font-size: 0.75rem;
                    color: #aaa;
                    letter-spacing: 0.08em;
                    text-transform: uppercase;
                    margin-bottom: 20px;
                }
                .app-row {
                    margin-bottom: 14px;
                }
                .app-row-header {
                    display: flex;
                    justify-content: space-between;
                    align-items: baseline;
                    margin-bottom: 4px;
                }
                .app-row-name {
                    font-size: 0.9rem;
                    font-weight: 500;
                    color: #333;
                    white-space: nowrap;
                    overflow: hidden;
                    text-overflow: ellipsis;
                    max-width: 70%;
                }
                .app-row-time {
                    font-size: 0.82rem;
                    color: #888;
                    font-variant-numeric: tabular-nums;
                    white-space: nowrap;
                    flex-shrink: 0;
                }
                .bar-bg {
                    height: 6px;
                    background: #f0f0f0;
                    border-radius: 3px;
                    overflow: hidden;
                }
                .bar-fill {
                    height: 100%;
                    background: #4f46e5;
                    border-radius: 3px;
                    transition: width 0.4s ease;
                }
                .empty-msg {
                    color: #ccc;
                    font-size: 0.9rem;
                    text-align: center;
                    padding: 20px 0;
                }
            </style>
        </head>
        <body>
            <div class="layout">
                <div class="card">
                    <div class="section-label">Кликов мышкой</div>
                    <div class="count" id="count">—</div>
                    <div class="stats">
                        <div class="stat active">
                            <div class="stat-label">Активность</div>
                            <div class="stat-value" id="active">—</div>
                        </div>
                        <div class="stat inactive">
                            <div class="stat-label">Неактивность</div>
                            <div class="stat-value" id="inactive">—</div>
                        </div>
                    </div>
                    <div class="app-block">
                        <div class="stat-label" style="font-size:.7rem;color:#bbb;text-transform:uppercase;letter-spacing:.06em;margin-bottom:4px">Сейчас</div>
                        <div class="app-name" id="app-name">—</div>
                        <div class="app-title" id="app-title"></div>
                    </div>
                </div>

                <div class="apps-card">
                    <h2>Время в приложениях</h2>
                    <div id="app-list"><div class="empty-msg">Нет данных</div></div>
                </div>
            </div>

            <script>
                let prevCount = null;
                const elCount    = document.getElementById('count');
                const elActive   = document.getElementById('active');
                const elInactive = document.getElementById('inactive');
                const elAppName  = document.getElementById('app-name');
                const elAppTitle = document.getElementById('app-title');
                const elAppList  = document.getElementById('app-list');

                function fmt(s) {
                    const h = Math.floor(s / 3600);
                    const m = Math.floor((s % 3600) / 60);
                    const sec = s % 60;
                    if (h > 0) return `${h}ч ${String(m).padStart(2,'0')}м ${String(sec).padStart(2,'0')}с`;
                    if (m > 0) return `${m}м ${String(sec).padStart(2,'0')}с`;
                    return `${sec}с`;
                }

                function renderApps(apps) {
                    if (!apps || apps.length === 0) {
                        elAppList.innerHTML = '<div class="empty-msg">Нет данных</div>';
                        return;
                    }
                    const max = apps[0].seconds;
                    elAppList.innerHTML = apps.map(a => {
                        const pct = max > 0 ? (a.seconds / max * 100).toFixed(1) : 0;
                        return `
                            <div class="app-row">
                                <div class="app-row-header">
                                    <div class="app-row-name">${a.name}</div>
                                    <div class="app-row-time">${fmt(a.seconds)}</div>
                                </div>
                                <div class="bar-bg">
                                    <div class="bar-fill" style="width:${pct}%"></div>
                                </div>
                            </div>`;
                    }).join('');
                }

                async function update() {
                    try {
                        const r = await fetch('/api/stats');
                        const d = await r.json();

                        elCount.textContent    = d.count.toLocaleString('ru');
                        elActive.textContent   = fmt(d.active_seconds);
                        elInactive.textContent = fmt(d.inactive_seconds);
                        elAppName.textContent  = d.app_process || '—';
                        elAppTitle.textContent = d.app_title || '';

                        if (prevCount !== null && d.count !== prevCount) {
                            elCount.classList.add('flash');
                            setTimeout(() => elCount.classList.remove('flash'), 150);
                        }
                        prevCount = d.count;

                        renderApps(d.app_stats);
                    } catch {}
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
