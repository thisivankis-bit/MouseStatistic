using System.Text;
using System.Text.Json;

namespace MouseClickTracker;

public sealed class SyncService : IDisposable
{
    private readonly DataStore _store;
    private readonly ActivityTracker _activity;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string _endpoint;
    private readonly string _machineId;
    private readonly System.Threading.Timer _timer;

    public SyncService(DataStore store, ActivityTracker activity, string serverUrl, string machineId)
    {
        _store    = store;
        _activity = activity;
        _endpoint = serverUrl.TrimEnd('/') + "/api/sync";
        _machineId = machineId;
        // первый синк сразу при старте, потом каждую минуту
        _timer = new System.Threading.Timer(Sync, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    private async void Sync(object? state)
    {
        try
        {
            var payload = new
            {
                machineId       = _machineId,
                totalClicks     = _store.Load(),
                activeSeconds   = _activity.ActiveSeconds,
                inactiveSeconds = _activity.InactiveSeconds,
                appStats        = _store.LoadAppStats()
                    .Select(x => new { processName = x.Name, seconds = x.Seconds })
                    .ToArray()
            };
            var json = JsonSerializer.Serialize(payload);
            await _http.PostAsync(_endpoint, new StringContent(json, Encoding.UTF8, "application/json"));
        }
        catch { /* сеть недоступна — попробуем на следующем тике */ }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }
}
