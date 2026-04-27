using System.Text.Json;

namespace MouseClickTracker;

public class AppConfig
{
    public string ServerUrl { get; set; } = "";
    public string MachineId { get; set; } = "";

    public string ResolvedMachineId =>
        string.IsNullOrWhiteSpace(MachineId) ? Environment.MachineName : MachineId;

    private static readonly string ConfigPath =
        Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    public static AppConfig Load()
    {
        if (!File.Exists(ConfigPath)) return new();
        try
        {
            return JsonSerializer.Deserialize<AppConfig>(
                File.ReadAllText(ConfigPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            ) ?? new();
        }
        catch { return new(); }
    }
}
