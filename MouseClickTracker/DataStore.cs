using Microsoft.Data.Sqlite;

namespace MouseClickTracker;

public record HourBucket(string Day, int Hour, long Clicks, long Keys, long ActiveSec, long InactiveSec);

public sealed class DataStore : IDisposable
{
    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MouseClickTracker", "data.db");

    private readonly SqliteConnection _conn;
    private readonly object _lock = new();

    public DataStore()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        _conn = new SqliteConnection($"Data Source={DbPath}");
        _conn.Open();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS counter (
                total             INTEGER NOT NULL DEFAULT 0,
                active_seconds    INTEGER NOT NULL DEFAULT 0,
                inactive_seconds  INTEGER NOT NULL DEFAULT 0,
                key_total         INTEGER NOT NULL DEFAULT 0,
                last_reset_date   TEXT    NOT NULL DEFAULT '',
                synthetic_clicks  INTEGER NOT NULL DEFAULT 0,
                key_repeat_total  INTEGER NOT NULL DEFAULT 0,
                synthetic_keys    INTEGER NOT NULL DEFAULT 0
            );
            INSERT INTO counter (total) SELECT 0 WHERE NOT EXISTS (SELECT 1 FROM counter);
            CREATE TABLE IF NOT EXISTS app_stats (
                process_name TEXT PRIMARY KEY,
                seconds      INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS hour_stats (
                day          TEXT    NOT NULL,
                hour         INTEGER NOT NULL,
                clicks       INTEGER NOT NULL DEFAULT 0,
                keys         INTEGER NOT NULL DEFAULT 0,
                active_sec   INTEGER NOT NULL DEFAULT 0,
                inactive_sec INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (day, hour)
            );
            """;
        cmd.ExecuteNonQuery();

        Migrate();
    }

    private void Migrate()
    {
        foreach (var ddl in new[]
        {
            "ALTER TABLE counter ADD COLUMN active_seconds   INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE counter ADD COLUMN inactive_seconds INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE counter ADD COLUMN key_total        INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE counter ADD COLUMN last_reset_date  TEXT    NOT NULL DEFAULT ''",
            "ALTER TABLE counter ADD COLUMN synthetic_clicks INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE counter ADD COLUMN key_repeat_total INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE counter ADD COLUMN synthetic_keys   INTEGER NOT NULL DEFAULT 0",
        })
        {
            try { using var c = _conn.CreateCommand(); c.CommandText = ddl; c.ExecuteNonQuery(); }
            catch (SqliteException) { /* колонка уже есть */ }
        }
    }

    public int Load()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT total FROM counter LIMIT 1";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public (long Active, long Inactive) LoadActivity()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT active_seconds, inactive_seconds FROM counter LIMIT 1";
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return (0, 0);
            return (r.GetInt64(0), r.GetInt64(1));
        }
    }

    public void Increment(bool injected = false)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = injected
                ? "UPDATE counter SET total = total + 1, synthetic_clicks = synthetic_clicks + 1"
                : "UPDATE counter SET total = total + 1";
            cmd.ExecuteNonQuery();
            BumpHour(clicks: 1);
        }
    }

    public int LoadKeys()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT key_total FROM counter LIMIT 1";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void IncrementKey(bool injected = false)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE counter SET
                    key_total      = key_total + 1,
                    synthetic_keys = synthetic_keys + $i
                """;
            cmd.Parameters.AddWithValue("$i", injected ? 1 : 0);
            cmd.ExecuteNonQuery();
            BumpHour(keys: 1);
        }
    }

    public (long SyntheticClicks, long KeyRepeats, long SyntheticKeys) LoadFraudCounters()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT synthetic_clicks, key_repeat_total, synthetic_keys FROM counter LIMIT 1";
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return (0, 0, 0);
            return (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
        }
    }

    public void AddTime(int active, int inactive)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE counter
                SET active_seconds   = active_seconds   + $a,
                    inactive_seconds = inactive_seconds + $i
                """;
            cmd.Parameters.AddWithValue("$a", active);
            cmd.Parameters.AddWithValue("$i", inactive);
            cmd.ExecuteNonQuery();
            BumpHour(active: active, inactive: inactive);
        }
    }

    // Caller MUST hold _lock. Buckets into hour_stats by current local day + hour.
    private void BumpHour(int clicks = 0, int keys = 0, int active = 0, int inactive = 0)
    {
        if (clicks == 0 && keys == 0 && active == 0 && inactive == 0) return;
        var now = DateTime.Now;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO hour_stats (day, hour, clicks, keys, active_sec, inactive_sec)
            VALUES ($d, $h, $c, $k, $a, $i)
            ON CONFLICT(day, hour) DO UPDATE SET
                clicks       = clicks       + excluded.clicks,
                keys         = keys         + excluded.keys,
                active_sec   = active_sec   + excluded.active_sec,
                inactive_sec = inactive_sec + excluded.inactive_sec
            """;
        cmd.Parameters.AddWithValue("$d", now.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$h", now.Hour);
        cmd.Parameters.AddWithValue("$c", clicks);
        cmd.Parameters.AddWithValue("$k", keys);
        cmd.Parameters.AddWithValue("$a", active);
        cmd.Parameters.AddWithValue("$i", inactive);
        cmd.ExecuteNonQuery();
    }

    public List<HourBucket> LoadTodayHours()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT day, hour, clicks, keys, active_sec, inactive_sec FROM hour_stats WHERE day = $d ORDER BY hour";
            cmd.Parameters.AddWithValue("$d", today);
            using var r = cmd.ExecuteReader();
            var list = new List<HourBucket>();
            while (r.Read())
                list.Add(new HourBucket(r.GetString(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5)));
            return list;
        }
    }

    public List<HourBucket> LoadRecentHours(int daysBack)
    {
        var cutoff = DateTime.Now.Date.AddDays(-daysBack).ToString("yyyy-MM-dd");
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT day, hour, clicks, keys, active_sec, inactive_sec
                FROM hour_stats
                WHERE day >= $d
                ORDER BY day, hour
                """;
            cmd.Parameters.AddWithValue("$d", cutoff);
            using var r = cmd.ExecuteReader();
            var list = new List<HourBucket>();
            while (r.Read())
                list.Add(new HourBucket(
                    r.GetString(0), r.GetInt32(1),
                    r.GetInt64(2),  r.GetInt64(3),
                    r.GetInt64(4),  r.GetInt64(5)));
            return list;
        }
    }

    public void AddAppTime(Dictionary<string, int> pending)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            foreach (var (name, secs) in pending)
            {
                using var cmd = _conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO app_stats (process_name, seconds) VALUES ($name, $secs)
                    ON CONFLICT(process_name) DO UPDATE SET seconds = seconds + $secs
                    """;
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$secs", secs);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public List<(string Name, long Seconds)> LoadAppStats()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT process_name, seconds FROM app_stats ORDER BY seconds DESC";
            using var r = cmd.ExecuteReader();
            var result = new List<(string, long)>();
            while (r.Read())
                result.Add((r.GetString(0), r.GetInt64(1)));
            return result;
        }
    }

    public DateOnly? LoadLastResetDate()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT last_reset_date FROM counter LIMIT 1";
            var raw = cmd.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return DateOnly.TryParse(raw, out var d) ? d : null;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE counter SET total = 0, active_seconds = 0, inactive_seconds = 0, key_total = 0,
                    synthetic_clicks = 0, key_repeat_total = 0, synthetic_keys = 0,
                    last_reset_date = $today;
                DELETE FROM app_stats;
                DELETE FROM hour_stats WHERE day = $today;
                """;
            cmd.Parameters.AddWithValue("$today", DateTime.Now.ToString("yyyy-MM-dd"));
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() => _conn.Dispose();
}
