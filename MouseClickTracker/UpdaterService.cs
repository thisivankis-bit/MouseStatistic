using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MouseClickTracker;

public sealed class UpdaterService : IDisposable
{
    public enum CheckResult { UpToDate, Updating, Error }

    private readonly string _serverUrl;
    private readonly System.Threading.Timer _timer;

    private static readonly JsonSerializerOptions _jsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    public UpdaterService(string serverUrl)
    {
        _serverUrl = serverUrl;
        // First check 30s after startup (let the main sync settle), then every 24h.
        _timer = new System.Threading.Timer(_ => RunBackground(), null, TimeSpan.FromSeconds(30), TimeSpan.FromHours(24));
    }

    private async void RunBackground()
    {
        var (result, _) = await CheckAsync(_serverUrl);
        if (result == CheckResult.Updating)
        {
            await Task.Delay(2000);
            Environment.Exit(0);
        }
    }

    /// <summary>
    /// Asks the server for the latest version and, if it's strictly newer than the running build,
    /// downloads /downloads/&lt;fileName&gt; and starts it with /VERYSILENT. The caller decides whether
    /// to exit the process (Environment.Exit) on Updating — that lets the installer overwrite files.
    /// </summary>
    public static async Task<(CheckResult result, string message)> CheckAsync(string serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
            return (CheckResult.Error, "Адрес сервера не указан");

        if (!serverUrl.StartsWith("http://",  StringComparison.OrdinalIgnoreCase) &&
            !serverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            serverUrl = "http://" + serverUrl;
        serverUrl = serverUrl.TrimEnd('/');

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        try
        {
            using var resp = await http.GetAsync(serverUrl + "/api/version");
            if (!resp.IsSuccessStatusCode)
                return (CheckResult.Error, $"Сервер вернул {(int)resp.StatusCode}");

            var info = await JsonSerializer.DeserializeAsync<VersionInfo>(
                await resp.Content.ReadAsStreamAsync(), _jsonOpts);
            if (info?.Version is null)
                return (CheckResult.Error, "Манифест пуст");
            if (!Version.TryParse(info.Version, out var serverVersion))
                return (CheckResult.Error, $"Некорректная версия '{info.Version}'");

            var localVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version();
            if (serverVersion <= localVersion)
                return (CheckResult.UpToDate, $"Установлена последняя версия (v{localVersion.ToString(3)})");

            var fileName = string.IsNullOrWhiteSpace(info.FileName)
                ? "MouseClickTracker-Setup.exe"
                : info.FileName;
            var url = serverUrl + "/downloads/" + fileName;
            var tmp = Path.Combine(Path.GetTempPath(), $"MouseClickTracker-Setup-{info.Version}.exe");

            using (var rs = await http.GetStreamAsync(url))
            using (var fs = File.Create(tmp))
                await rs.CopyToAsync(fs);

            Process.Start(new ProcessStartInfo
            {
                FileName        = tmp,
                Arguments       = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = true
            });

            return (CheckResult.Updating, $"Найдено обновление v{info.Version}, устанавливаю…");
        }
        catch (Exception ex)
        {
            return (CheckResult.Error, ex.Message);
        }
    }

    public void Dispose() => _timer.Dispose();

    private record VersionInfo(
        [property: JsonPropertyName("version")]  string? Version,
        [property: JsonPropertyName("fileName")] string? FileName);
}
