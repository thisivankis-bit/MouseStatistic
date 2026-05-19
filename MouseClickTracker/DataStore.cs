using Microsoft.Data.Sqlite;

namespace MouseClickTracker;

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

    public void IncrementKey(bool isRepeat = false, bool injected = false)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE counter SET
                    key_total        = key_total + 1,
                    key_repeat_total = key_repeat_total + $r,
                    synthetic_keys   = synthetic_keys + $i
                """;
            cmd.Parameters.AddWithValue("$r", isRepeat ? 1 : 0);
            cmd.Parameters.AddWithValue("$i", injected ? 1 : 0);
            cmd.ExecuteNonQuery();
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
                """;
            cmd.Parameters.AddWithValue("$today", DateTime.Now.ToString("yyyy-MM-dd"));
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() => _conn.Dispose();
}
