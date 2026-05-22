using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MouseClickTracker;

public sealed class SyncService : IDisposable
{
    private readonly DataStore _store;
    private readonly ActivityTracker _activity;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string _endpoint;
    private readonly string _configEndpoint;
    private readonly string _machineId;
    private readonly string _userName;
    private readonly Action<string, string, string>? _onConfig;
    private readonly Action<long, int, int>? _onWins;
    private readonly System.Threading.Timer _timer;

    private static readonly JsonSerializerOptions _jsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    public SyncService(DataStore store, ActivityTracker activity,
                       string serverUrl, string machineId, string userName,
                       Action<string, string, string>? onConfig = null,
                       Action<long, int, int>? onWins = null)
    {
        _store          = store;
        _activity       = activity;
        _endpoint       = serverUrl.TrimEnd('/') + "/api/sync";
        _configEndpoint = serverUrl.TrimEnd('/') + "/api/config";
        _machineId      = machineId;
        _userName       = userName;
        _onConfig       = onConfig;
        _onWins         = onWins;
        _timer = new System.Threading.Timer(Sync, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
    }

    private async void Sync(object? state)
    {
        try
        {
            var fraud = _store.LoadFraudCounters();
            var payload = new
            {
                machineId       = _machineId,
                userName        = _userName,
                totalClicks     = _store.Load(),
                totalKeys       = _store.LoadKeys(),
                syntheticClicks = fraud.SyntheticClicks,
                keyRepeats      = fraud.KeyRepeats,
                syntheticKeys   = fraud.SyntheticKeys,
                activeSeconds   = _activity.ActiveSeconds,
                inactiveSeconds = _activity.InactiveSeconds,
                appStats        = _store.LoadAppStats()
                    .Select(x => new { processName = x.Name, seconds = x.Seconds })
                    .ToArray(),
                hours           = _store.LoadRecentHours(daysBack: 1)
                    .Select(h => new {
                        day         = h.Day,
                        hour        = h.Hour,
                        clicks      = h.Clicks,
                        keys        = h.Keys,
                        activeSec   = h.ActiveSec,
                        inactiveSec = h.InactiveSec
                    })
                    .ToArray()
            };
            var json = JsonSerializer.Serialize(payload);
            var resp = await _http.PostAsync(_endpoint, new StringContent(json, Encoding.UTF8, "application/json"));
            if (resp.IsSuccessStatusCode && _onWins is not null)
            {
                var reply = await JsonSerializer.DeserializeAsync<SyncReply>(
                    await resp.Content.ReadAsStreamAsync(), _jsonOpts);
                if (reply is not null) _onWins(reply.Wins, reply.Rank, reply.Total);
            }
        }
        catch { }

        // Получаем расписание с сервера
        if (_onConfig is null) return;
        try
        {
            var resp = await _http.GetAsync(_configEndpoint);
            if (!resp.IsSuccessStatusCode) return;
            var cfg = await JsonSerializer.DeserializeAsync<ServerSchedule>(
                await resp.Content.ReadAsStreamAsync(), _jsonOpts);
            if (cfg is not null)
                _onConfig(cfg.WorkStart ?? "", cfg.WorkEnd ?? "", cfg.ResetTime ?? "");
        }
        catch { }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }

    private record ServerSchedule(
        [property: JsonPropertyName("workStart")] string? WorkStart,
        [property: JsonPropertyName("workEnd")]   string? WorkEnd,
        [property: JsonPropertyName("resetTime")] string? ResetTime);

    private record SyncReply(
        [property: JsonPropertyName("wins")]  long Wins,
        [property: JsonPropertyName("rank")]  int  Rank,
        [property: JsonPropertyName("total")] int  Total);
}
