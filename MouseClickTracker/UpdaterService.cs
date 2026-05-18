using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MouseClickTracker;

public sealed class UpdaterService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly string _serverUrl;
    private readonly System.Threading.Timer _timer;

    private static readonly JsonSerializerOptions _jsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    public UpdaterService(string serverUrl)
    {
        _serverUrl = serverUrl.TrimEnd('/');
        // First check 30s after startup (let the main sync settle), then every 24h.
        _timer = new System.Threading.Timer(Check, null, TimeSpan.FromSeconds(30), TimeSpan.FromHours(24));
    }

    private async void Check(object? state)
    {
        try
        {
            using var resp = await _http.GetAsync(_serverUrl + "/api/version");
            if (!resp.IsSuccessStatusCode) return;

            var info = await JsonSerializer.DeserializeAsync<VersionInfo>(
                await resp.Content.ReadAsStreamAsync(), _jsonOpts);
            if (info?.Version is null) return;
            if (!Version.TryParse(info.Version, out var serverVersion)) return;

            var localVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version();
            if (serverVersion <= localVersion) return;

            var fileName = string.IsNullOrWhiteSpace(info.FileName)
                ? "MouseClickTracker-Setup.exe"
                : info.FileName;
            var url = _serverUrl + "/downloads/" + fileName;
            var tmp = Path.Combine(Path.GetTempPath(), $"MouseClickTracker-Setup-{info.Version}.exe");

            using (var rs = await _http.GetStreamAsync(url))
            using (var fs = File.Create(tmp))
                await rs.CopyToAsync(fs);

            Process.Start(new ProcessStartInfo
            {
                FileName        = tmp,
                Arguments       = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = true
            });

            // Give the installer a moment to start, then bow out so it can overwrite our files.
            await Task.Delay(2000);
            Environment.Exit(0);
        }
        catch { }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }

    private record VersionInfo(
        [property: JsonPropertyName("version")]  string? Version,
        [property: JsonPropertyName("fileName")] string? FileName);
}
