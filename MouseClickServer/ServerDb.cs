using Microsoft.Data.Sqlite;

namespace MouseClickServer;

public record AppStat(string ProcessName, long Seconds);

public record MachineSnapshot(
    string MachineId,
    string UserName,
    DateTime LastSeen,
    long TotalClicks,
    long TotalKeys,
    long SyntheticClicks,
    long KeyRepeats,
    long SyntheticKeys,
    long ActiveSeconds,
    long InactiveSeconds,
    long RecentClicks,
    long RecentKeys,
    long Wins,
    List<AppStat> AppStats);

public record DailyEntry(string Day, long Clicks, long Keys, long ActiveSec, long InactiveSec);

public record PeriodMachineStat(
    string MachineId,
    string UserName,
    long TotalClicks,
    long TotalKeys,
    long TotalActiveSec,
    long TotalInactiveSec,
    List<DailyEntry> Days);

public sealed class ServerDb : IDisposable
{
    // Fallback combined clicks+keys threshold for "first to finish today" if the
    // settings table doesn't have a daily_target row yet. Should match the JS fallback.
    private const long DefaultDailyTarget = 5000;

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

            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS machines (
                machine_id        TEXT PRIMARY KEY,
                user_name         TEXT NOT NULL DEFAULT '',
                last_seen         TEXT NOT NULL,
                total_clicks      INTEGER NOT NULL DEFAULT 0,
                total_keys        INTEGER NOT NULL DEFAULT 0,
                synthetic_clicks  INTEGER NOT NULL DEFAULT 0,
                key_repeats       INTEGER NOT NULL DEFAULT 0,
                synthetic_keys    INTEGER NOT NULL DEFAULT 0,
                active_seconds    INTEGER NOT NULL DEFAULT 0,
                inactive_seconds  INTEGER NOT NULL DEFAULT 0,
                recent_clicks     INTEGER NOT NULL DEFAULT 0,
                recent_keys       INTEGER NOT NULL DEFAULT 0,
                wins              INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS daily_winner (
                day        TEXT PRIMARY KEY,
                machine_id TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS machine_app_stats (
                machine_id    TEXT NOT NULL,
                process_name  TEXT NOT NULL,
                seconds       INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (machine_id, process_name)
            );

            CREATE TABLE IF NOT EXISTS machine_daily (
                machine_id   TEXT NOT NULL,
                user_name    TEXT NOT NULL DEFAULT '',
                day          TEXT NOT NULL,
                clicks       INTEGER NOT NULL DEFAULT 0,
                keys         INTEGER NOT NULL DEFAULT 0,
                active_sec   INTEGER NOT NULL DEFAULT 0,
                inactive_sec INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (machine_id, day)
            );
            """;
        cmd.ExecuteNonQuery();

        Migrate();
    }

    private void Migrate()
    {
        foreach (var ddl in new[]
        {
            "ALTER TABLE machines      ADD COLUMN user_name     TEXT    NOT NULL DEFAULT ''",
            "ALTER TABLE machines      ADD COLUMN recent_clicks INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE machines      ADD COLUMN total_keys    INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE machines      ADD COLUMN recent_keys   INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE machines      ADD COLUMN wins             INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE machines      ADD COLUMN synthetic_clicks INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE machines      ADD COLUMN key_repeats      INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE machines      ADD COLUMN synthetic_keys   INTEGER NOT NULL DEFAULT 0",
            "ALTER TABLE machine_daily ADD COLUMN keys          INTEGER NOT NULL DEFAULT 0",
        })
        {
            try { using var c = _conn.CreateCommand(); c.CommandText = ddl; c.ExecuteNonQuery(); }
            catch (SqliteException) { }
        }
    }

    public long Upsert(SyncPayload payload)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();

            // Read previous snapshot to compute deltas for daily history
            long prevClicks = 0, prevKeys = 0, prevActive = 0, prevInactive = 0;
            using (var q = _conn.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT total_clicks, total_keys, active_seconds, inactive_seconds FROM machines WHERE machine_id = $id";
                q.Parameters.AddWithValue("$id", payload.MachineId);
                using var qr = q.ExecuteReader();
                if (qr.Read()) { prevClicks = qr.GetInt64(0); prevKeys = qr.GetInt64(1); prevActive = qr.GetInt64(2); prevInactive = qr.GetInt64(3); }
            }

            // If new value < prev the counter was reset; treat new value as the delta
            bool wasReset  = payload.TotalClicks < prevClicks;
            long dClicks   = payload.TotalClicks    >= prevClicks   ? payload.TotalClicks    - prevClicks   : payload.TotalClicks;
            long dKeys     = payload.TotalKeys      >= prevKeys     ? payload.TotalKeys      - prevKeys     : payload.TotalKeys;
            long dActive   = payload.ActiveSeconds   >= prevActive   ? payload.ActiveSeconds   - prevActive   : payload.ActiveSeconds;
            long dInactive = payload.InactiveSeconds >= prevInactive ? payload.InactiveSeconds - prevInactive : payload.InactiveSeconds;

            // On reset: wipe today's daily entry so Reports reflect only post-reset activity
            if (wasReset)
            {
                var resetDay = DateTime.Now.ToString("yyyy-MM-dd");
                var rz = _conn.CreateCommand();
                rz.Transaction = tx;
                rz.CommandText = "UPDATE machine_daily SET clicks = 0, keys = 0, active_sec = 0, inactive_sec = 0 WHERE machine_id = $id AND day = $day";
                rz.Parameters.AddWithValue("$id",  payload.MachineId);
                rz.Parameters.AddWithValue("$day", resetDay);
                rz.ExecuteNonQuery();
            }

            var m = _conn.CreateCommand();
            m.Transaction = tx;
            m.CommandText = """
                INSERT INTO machines (machine_id, user_name, last_seen, total_clicks, total_keys, synthetic_clicks, key_repeats, synthetic_keys, active_seconds, inactive_seconds, recent_clicks, recent_keys)
                VALUES ($id, $userName, $ts, $clicks, $keys, $sc, $kr, $sk, $active, $inactive, $rc, $rk)
                ON CONFLICT(machine_id) DO UPDATE SET
                    user_name        = $userName,
                    last_seen        = $ts,
                    total_clicks     = $clicks,
                    total_keys       = $keys,
                    synthetic_clicks = $sc,
                    key_repeats      = $kr,
                    synthetic_keys   = $sk,
                    active_seconds   = $active,
                    inactive_seconds = $inactive,
                    recent_clicks    = $rc,
                    recent_keys      = $rk
                """;
            m.Parameters.AddWithValue("$id",       payload.MachineId);
            m.Parameters.AddWithValue("$userName", payload.UserName ?? "");
            m.Parameters.AddWithValue("$ts",       DateTime.UtcNow.ToString("o"));
            m.Parameters.AddWithValue("$clicks",   payload.TotalClicks);
            m.Parameters.AddWithValue("$keys",     payload.TotalKeys);
            m.Parameters.AddWithValue("$sc",       payload.SyntheticClicks);
            m.Parameters.AddWithValue("$kr",       payload.KeyRepeats);
            m.Parameters.AddWithValue("$sk",       payload.SyntheticKeys);
            m.Parameters.AddWithValue("$active",   payload.ActiveSeconds);
            m.Parameters.AddWithValue("$inactive", payload.InactiveSeconds);
            m.Parameters.AddWithValue("$rc",       dClicks);
            m.Parameters.AddWithValue("$rk",       dKeys);
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

            // Accumulate daily activity
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (dClicks > 0 || dKeys > 0 || dActive > 0 || dInactive > 0)
            {
                var d = _conn.CreateCommand();
                d.Transaction = tx;
                d.CommandText = """
                    INSERT INTO machine_daily (machine_id, user_name, day, clicks, keys, active_sec, inactive_sec)
                    VALUES ($id, $user, $day, $dc, $dk, $da, $di)
                    ON CONFLICT(machine_id, day) DO UPDATE SET
                        user_name    = $user,
                        clicks       = clicks + $dc,
                        keys         = keys + $dk,
                        active_sec   = active_sec + $da,
                        inactive_sec = inactive_sec + $di
                    """;
                d.Parameters.AddWithValue("$id",   payload.MachineId);
                d.Parameters.AddWithValue("$user", payload.UserName ?? "");
                d.Parameters.AddWithValue("$day",  today);
                d.Parameters.AddWithValue("$dc",   dClicks);
                d.Parameters.AddWithValue("$dk",   dKeys);
                d.Parameters.AddWithValue("$da",   dActive);
                d.Parameters.AddWithValue("$di",   dInactive);
                d.ExecuteNonQuery();
            }

            // Daily winner: first machine whose today's clicks+keys cross the target gets +1 win.
            long todayClicks = 0, todayKeys = 0;
            using (var q = _conn.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT clicks, keys FROM machine_daily WHERE machine_id = $id AND day = $day";
                q.Parameters.AddWithValue("$id",  payload.MachineId);
                q.Parameters.AddWithValue("$day", today);
                using var qr = q.ExecuteReader();
                if (qr.Read()) { todayClicks = qr.GetInt64(0); todayKeys = qr.GetInt64(1); }
            }
            long dailyTarget = DefaultDailyTarget;
            using (var ts = _conn.CreateCommand())
            {
                ts.Transaction = tx;
                ts.CommandText = "SELECT value FROM settings WHERE key = 'daily_target'";
                var raw = ts.ExecuteScalar() as string;
                if (long.TryParse(raw, out var v) && v > 0) dailyTarget = v;
            }
            if (todayClicks + todayKeys >= dailyTarget)
            {
                var insertWinner = _conn.CreateCommand();
                insertWinner.Transaction = tx;
                insertWinner.CommandText = "INSERT INTO daily_winner (day, machine_id) VALUES ($day, $id) ON CONFLICT(day) DO NOTHING";
                insertWinner.Parameters.AddWithValue("$day", today);
                insertWinner.Parameters.AddWithValue("$id",  payload.MachineId);
                var inserted = insertWinner.ExecuteNonQuery();
                if (inserted > 0)
                {
                    var bump = _conn.CreateCommand();
                    bump.Transaction = tx;
                    bump.CommandText = "UPDATE machines SET wins = wins + 1 WHERE machine_id = $id";
                    bump.Parameters.AddWithValue("$id", payload.MachineId);
                    bump.ExecuteNonQuery();
                }
            }

            tx.Commit();

            long wins = 0;
            using (var q = _conn.CreateCommand())
            {
                q.CommandText = "SELECT wins FROM machines WHERE machine_id = $id";
                q.Parameters.AddWithValue("$id", payload.MachineId);
                var raw = q.ExecuteScalar();
                if (raw is long l) wins = l;
            }
            return wins;
        }
    }

    public List<MachineSnapshot> GetAll()
    {
        lock (_lock)
        {
            var machines = new List<MachineSnapshot>();

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT machine_id, user_name, last_seen, total_clicks, total_keys, synthetic_clicks, key_repeats, synthetic_keys, active_seconds, inactive_seconds, recent_clicks, recent_keys, wins FROM machines ORDER BY last_seen DESC";
            using var r = cmd.ExecuteReader();

            while (r.Read())
            {
                var id = r.GetString(0);
                var userName = r.GetString(1);
                var lastSeen = DateTime.Parse(r.GetString(2)).ToUniversalTime();
                var apps = GetAppStats(id);
                machines.Add(new MachineSnapshot(
                    id, userName, lastSeen,
                    r.GetInt64(3),  r.GetInt64(4),
                    r.GetInt64(5),  r.GetInt64(6),  r.GetInt64(7),
                    r.GetInt64(8),  r.GetInt64(9),
                    r.GetInt64(10), r.GetInt64(11),
                    r.GetInt64(12),
                    apps));
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

    public string GetSetting(string key)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string ?? "";
        }
    }

    public void SetSetting(string key, string value)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO settings (key, value) VALUES ($k, $v)
                ON CONFLICT(key) DO UPDATE SET value = $v
                """;
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    public List<PeriodMachineStat> GetPeriodStats(string from, string to)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT machine_id, user_name, day, clicks, keys, active_sec, inactive_sec
                FROM machine_daily
                WHERE day >= $from AND day <= $to
                ORDER BY machine_id, day
                """;
            cmd.Parameters.AddWithValue("$from", from);
            cmd.Parameters.AddWithValue("$to",   to);

            var days  = new Dictionary<string, List<DailyEntry>>();
            var names = new Dictionary<string, string>();

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetString(0);
                names[id] = r.GetString(1);
                if (!days.TryGetValue(id, out var list))
                    days[id] = list = new List<DailyEntry>();
                list.Add(new DailyEntry(r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6)));
            }

            return days.Select(kv => new PeriodMachineStat(
                kv.Key,
                names.GetValueOrDefault(kv.Key, ""),
                kv.Value.Sum(d => d.Clicks),
                kv.Value.Sum(d => d.Keys),
                kv.Value.Sum(d => d.ActiveSec),
                kv.Value.Sum(d => d.InactiveSec),
                kv.Value
            )).ToList();
        }
    }

    public void Dispose() => _conn.Dispose();
}
