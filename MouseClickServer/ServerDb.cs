using Microsoft.Data.Sqlite;

namespace MouseClickServer;

public record AppStat(string ProcessName, long Seconds);

public record MachineSnapshot(
    string MachineId,
    DateTime LastSeen,
    long TotalClicks,
    long ActiveSeconds,
    long InactiveSeconds,
    List<AppStat> AppStats);

public sealed class ServerDb : IDisposable
{
    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "MouseClickServer", "server.db");

    private readonly SqliteConnection _conn;
    private readonly object _lock = new();

    public ServerDb()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        _conn = new SqliteConnection($"Data Source={DbPath}");
        _conn.Open();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS machines (
                machine_id        TEXT PRIMARY KEY,
                last_seen         TEXT NOT NULL,
                total_clicks      INTEGER NOT NULL DEFAULT 0,
                active_seconds    INTEGER NOT NULL DEFAULT 0,
                inactive_seconds  INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS machine_app_stats (
                machine_id    TEXT NOT NULL,
                process_name  TEXT NOT NULL,
                seconds       INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (machine_id, process_name)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void Upsert(SyncPayload payload)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();

            var m = _conn.CreateCommand();
            m.Transaction = tx;
            m.CommandText = """
                INSERT INTO machines (machine_id, last_seen, total_clicks, active_seconds, inactive_seconds)
                VALUES ($id, $ts, $clicks, $active, $inactive)
                ON CONFLICT(machine_id) DO UPDATE SET
                    last_seen        = $ts,
                    total_clicks     = $clicks,
                    active_seconds   = $active,
                    inactive_seconds = $inactive
                """;
            m.Parameters.AddWithValue("$id",       payload.MachineId);
            m.Parameters.AddWithValue("$ts",       DateTime.UtcNow.ToString("o"));
            m.Parameters.AddWithValue("$clicks",   payload.TotalClicks);
            m.Parameters.AddWithValue("$active",   payload.ActiveSeconds);
            m.Parameters.AddWithValue("$inactive", payload.InactiveSeconds);
            m.ExecuteNonQuery();

            // replace app stats for this machine
            var del = _conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM machine_app_stats WHERE machine_id = $id";
            del.Parameters.AddWithValue("$id", payload.MachineId);
            del.ExecuteNonQuery();

            foreach (var app in payload.AppStats)
            {
                var a = _conn.CreateCommand();
                a.Transaction = tx;
                a.CommandText = """
                    INSERT INTO machine_app_stats (machine_id, process_name, seconds)
                    VALUES ($id, $name, $secs)
                    """;
                a.Parameters.AddWithValue("$id",   payload.MachineId);
                a.Parameters.AddWithValue("$name", app.ProcessName);
                a.Parameters.AddWithValue("$secs", app.Seconds);
                a.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public List<MachineSnapshot> GetAll()
    {
        lock (_lock)
        {
            var machines = new List<MachineSnapshot>();

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT machine_id, last_seen, total_clicks, active_seconds, inactive_seconds FROM machines ORDER BY last_seen DESC";
            using var r = cmd.ExecuteReader();

            while (r.Read())
            {
                var id = r.GetString(0);
                var lastSeen = DateTime.Parse(r.GetString(1)).ToUniversalTime();
                var apps = GetAppStats(id);
                machines.Add(new MachineSnapshot(id, lastSeen, r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), apps));
            }

            return machines;
        }
    }

    private List<AppStat> GetAppStats(string machineId)
    {
        var list = new List<AppStat>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT process_name, seconds FROM machine_app_stats WHERE machine_id = $id ORDER BY seconds DESC";
        cmd.Parameters.AddWithValue("$id", machineId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new AppStat(r.GetString(0), r.GetInt64(1)));
        return list;
    }

    public void Dispose() => _conn.Dispose();
}
