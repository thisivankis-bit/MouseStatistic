using System.Text.Json;

namespace MouseClickTracker;

public class AppConfig
{
    public string ServerUrl { get; set; } = "";
    public string MachineId { get; set; } = "";
    public string UserName  { get; set; } = "";

    public string ResolvedMachineId =>
        string.IsNullOrWhiteSpace(MachineId) ? Environment.MachineName : MachineId;

    private static readonly string ConfigPath =
        Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    private static readonly JsonSerializerOptions ReadOptions  = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        if (!File.Exists(ConfigPath)) return new();
        try
        {
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), ReadOptions) ?? new();
        }
        catch { return new(); }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, WriteOptions);
        File.WriteAllText(ConfigPath, json);
    }
}
