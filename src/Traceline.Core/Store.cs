using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Traceline.Core;

/// <summary>
/// Everything that is saved lives in one SQLite file. A single connection guarded by a lock is plenty
/// for tens of targets and keeps the code simple.
/// </summary>
public sealed class Store : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SqliteConnection _db;
    private readonly object _lock = new();
    private readonly Dictionary<long, string?[]> _routeCache = [];

    public Store(string path)
    {
        if (path != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS targets(id INTEGER PRIMARY KEY AUTOINCREMENT, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS routes(id INTEGER PRIMARY KEY AUTOINCREMENT, target_id INTEGER NOT NULL, hops TEXT NOT NULL, UNIQUE(target_id, hops));
            CREATE TABLE IF NOT EXISTS samples(target_id INTEGER NOT NULL, ts INTEGER NOT NULL, route_id INTEGER NOT NULL, final_only INTEGER NOT NULL, rtts BLOB NOT NULL, PRIMARY KEY(target_id, ts)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS rollups(target_id INTEGER NOT NULL, ts INTEGER NOT NULL, route_id INTEGER NOT NULL, data BLOB NOT NULL, PRIMARY KEY(target_id, ts)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY AUTOINCREMENT, target_id INTEGER NOT NULL, ts INTEGER NOT NULL, kind TEXT NOT NULL, text TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS events_target ON events(target_id, ts);
            CREATE TABLE IF NOT EXISTS hosts(ip TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS shares(token TEXT PRIMARY KEY, json TEXT NOT NULL);
            """);
    }

    public void Dispose() { lock (_lock) _db.Dispose(); }

    private void Exec(string sql, params (string, object?)[] args)
    {
        lock (_lock) { using var c = Cmd(sql, args); c.ExecuteNonQuery(); }
    }

    private SqliteCommand Cmd(string sql, params (string Name, object? Value)[] args)
    {
        var c = _db.CreateCommand();
        c.CommandText = sql;
        foreach (var (name, value) in args) c.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return c;
    }

    /// <summary>Runs many writes as one transaction, which is far faster than one at a time.</summary>
    public void Batch(Action work)
    {
        lock (_lock)
        {
            Exec("BEGIN");
            try { work(); Exec("COMMIT"); }
            catch { Exec("ROLLBACK"); throw; }
        }
    }

    // ---------------------------------------------------------------- targets

    public List<Target> Targets()
    {
        lock (_lock)
        {
            var list = new List<Target>();
            using var c = Cmd("SELECT id, json FROM targets ORDER BY id");
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                var t = JsonSerializer.Deserialize<Target>(r.GetString(1), Json)!;
                t.Id = r.GetInt64(0);
                list.Add(t);
            }
            return list;
        }
    }

    public void SaveTarget(Target t)
    {
        lock (_lock)
        {
            if (t.Id == 0)
            {
                using var c = Cmd("INSERT INTO targets(json) VALUES($j); SELECT last_insert_rowid();", ("$j", JsonSerializer.Serialize(t, Json)));
                t.Id = (long)c.ExecuteScalar()!;
            }
            Exec("UPDATE targets SET json=$j WHERE id=$id", ("$j", JsonSerializer.Serialize(t, Json)), ("$id", t.Id));
        }
    }

    /// <summary>Removes a target and everything recorded for it.</summary>
    public void DeleteTarget(long id)
    {
        lock (_lock)
        {
            foreach (var table in new[] { "samples", "rollups", "events", "routes" })
                Exec($"DELETE FROM {table} WHERE target_id=$id", ("$id", id));
            Exec("DELETE FROM targets WHERE id=$id", ("$id", id));
        }
    }

    // ---------------------------------------------------------------- routes

    public long RouteId(long targetId, string?[] hops)
    {
        var json = JsonSerializer.Serialize(hops);
        lock (_lock)
        {
            Exec("INSERT OR IGNORE INTO routes(target_id, hops) VALUES($t, $h)", ("$t", targetId), ("$h", json));
            using var c = Cmd("SELECT id FROM routes WHERE target_id=$t AND hops=$h", ("$t", targetId), ("$h", json));
            var id = (long)c.ExecuteScalar()!;
            _routeCache[id] = hops.ToArray();
            return id;
        }
    }

    public string?[] Route(long routeId)
    {
        lock (_lock)
        {
            if (_routeCache.TryGetValue(routeId, out var cached)) return cached;
            using var c = Cmd("SELECT hops FROM routes WHERE id=$id", ("$id", routeId));
            var hops = c.ExecuteScalar() is string s ? JsonSerializer.Deserialize<string?[]>(s)! : [];
            return _routeCache[routeId] = hops;
        }
    }

    /// <summary>The route recorded with the most recent sample, so a restart remembers which address each hop had.</summary>
    public (long RouteId, string?[] Hops)? LastRoute(long targetId)
    {
        lock (_lock)
        {
            using var c = Cmd("SELECT route_id FROM samples WHERE target_id=$t ORDER BY ts DESC LIMIT 1", ("$t", targetId));
            return c.ExecuteScalar() is long id ? (id, Route(id)) : null;
        }
    }

    // ---------------------------------------------------------------- samples and roll-ups

    public void AddSample(long targetId, Sample s)
    {
        Exec("INSERT OR REPLACE INTO samples(target_id, ts, route_id, final_only, rtts) VALUES($t, $ts, $r, $f, $b)",
            ("$t", targetId), ("$ts", s.Ts), ("$r", s.RouteId), ("$f", s.FinalOnly ? 1 : 0), ("$b", MemoryMarshal.AsBytes<float>(s.Rtts).ToArray()));
    }

    public List<Sample> Samples(long targetId, long from, long to)
    {
        lock (_lock)
        {
            var list = new List<Sample>();
            using var c = Cmd("SELECT ts, route_id, final_only, rtts FROM samples WHERE target_id=$t AND ts>=$a AND ts<$b ORDER BY ts",
                ("$t", targetId), ("$a", from), ("$b", to));
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new Sample(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2) != 0, MemoryMarshal.Cast<byte, float>((byte[])r[3]).ToArray()));
            return list;
        }
    }

    /// <summary>Aggs[0] is the destination; Aggs[k] is hop k.</summary>
    public void SaveRollup(long targetId, long minuteTs, long routeId, Agg[] aggs)
    {
        var data = new float[aggs.Length * Agg.BlobFloats];
        for (var i = 0; i < aggs.Length; i++) aggs[i].Write(data.AsSpan(i * Agg.BlobFloats, Agg.BlobFloats));
        Exec("INSERT OR REPLACE INTO rollups(target_id, ts, route_id, data) VALUES($t, $ts, $r, $b)",
            ("$t", targetId), ("$ts", minuteTs), ("$r", routeId), ("$b", MemoryMarshal.AsBytes<float>(data).ToArray()));
    }

    public List<RollupRow> Rollups(long targetId, long from, long to)
    {
        lock (_lock)
        {
            var list = new List<RollupRow>();
            using var c = Cmd("SELECT ts, route_id, data FROM rollups WHERE target_id=$t AND ts>=$a AND ts<$b ORDER BY ts",
                ("$t", targetId), ("$a", from), ("$b", to));
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                var floats = MemoryMarshal.Cast<byte, float>((byte[])r[2]);
                var aggs = new Agg[floats.Length / Agg.BlobFloats];
                for (var i = 0; i < aggs.Length; i++) aggs[i] = Agg.Read(floats.Slice(i * Agg.BlobFloats, Agg.BlobFloats));
                list.Add(new RollupRow(r.GetInt64(0), r.GetInt64(1), aggs));
            }
            return list;
        }
    }

    /// <summary>First and last sample time and the number of samples, or null when nothing is stored.</summary>
    public (long First, long Last, long Count)? DataRange(long targetId)
    {
        lock (_lock)
        {
            using var c = Cmd("SELECT MIN(ts), MAX(ts), COUNT(*) FROM samples WHERE target_id=$t", ("$t", targetId));
            using var r = c.ExecuteReader();
            if (!r.Read() || r.IsDBNull(0)) return null;
            var first = r.GetInt64(0);
            using var c2 = Cmd("SELECT MIN(ts) FROM rollups WHERE target_id=$t", ("$t", targetId));
            if (c2.ExecuteScalar() is long rollupFirst && rollupFirst < first) first = rollupFirst;
            return (first, r.GetInt64(1), r.GetInt64(2));
        }
    }

    public void DeleteData(long targetId, long before)
    {
        Exec("DELETE FROM samples WHERE target_id=$t AND ts<$b", ("$t", targetId), ("$b", before));
        Exec("DELETE FROM rollups WHERE target_id=$t AND ts<$b", ("$t", targetId), ("$b", before));
        Exec("DELETE FROM events WHERE target_id=$t AND ts<$b", ("$t", targetId), ("$b", before));
    }

    public void Prune(long rawBefore, long rollupBefore)
    {
        Exec("DELETE FROM samples WHERE ts<$b", ("$b", rawBefore));
        Exec("DELETE FROM rollups WHERE ts<$b", ("$b", rollupBefore));
        Exec("DELETE FROM events WHERE ts<$b", ("$b", rollupBefore));
    }

    // ---------------------------------------------------------------- events (comments, route changes, alerts)

    public TimelineEvent AddEvent(long targetId, long ts, string kind, string text)
    {
        lock (_lock)
        {
            using var c = Cmd("INSERT INTO events(target_id, ts, kind, text) VALUES($t, $ts, $k, $x); SELECT last_insert_rowid();",
                ("$t", targetId), ("$ts", ts), ("$k", kind), ("$x", text));
            return new TimelineEvent((long)c.ExecuteScalar()!, targetId, ts, kind, text);
        }
    }

    /// <summary>The latest <paramref name="limit"/> events in the period, oldest first; of one kind only when <paramref name="kind"/> is given.</summary>
    public List<TimelineEvent> Events(long? targetId, long from, long to, int limit = 2000, string? kind = null)
    {
        lock (_lock)
        {
            var list = new List<TimelineEvent>();
            using var c = Cmd("SELECT id, target_id, ts, kind, text FROM events WHERE ($t IS NULL OR target_id=$t) AND ($k IS NULL OR kind=$k) AND ts>=$a AND ts<$b ORDER BY ts DESC LIMIT $n",
                ("$t", targetId), ("$k", kind), ("$a", from), ("$b", to), ("$n", limit));
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(new TimelineEvent(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetString(4)));
            list.Reverse();
            return list;
        }
    }

    public void UpdateEvent(long id, string text) => Exec("UPDATE events SET text=$x WHERE id=$id", ("$x", text), ("$id", id));
    public void DeleteEvent(long id) => Exec("DELETE FROM events WHERE id=$id", ("$id", id));

    // ---------------------------------------------------------------- hosts, settings, shares

    public HostInfo? Host(string ip)
    {
        lock (_lock)
        {
            using var c = Cmd("SELECT json FROM hosts WHERE ip=$ip", ("$ip", ip));
            return c.ExecuteScalar() is string s ? JsonSerializer.Deserialize<HostInfo>(s, Json) : null;
        }
    }

    public void SaveHost(HostInfo h) =>
        Exec("INSERT OR REPLACE INTO hosts(ip, json) VALUES($ip, $j)", ("$ip", h.Ip), ("$j", JsonSerializer.Serialize(h, Json)));

    public T Get<T>(string key) where T : new()
    {
        lock (_lock)
        {
            using var c = Cmd("SELECT value FROM settings WHERE key=$k", ("$k", key));
            return c.ExecuteScalar() is string s ? JsonSerializer.Deserialize<T>(s, Json) ?? new T() : new T();
        }
    }

    public void Set<T>(string key, T value) =>
        Exec("INSERT OR REPLACE INTO settings(key, value) VALUES($k, $v)", ("$k", key), ("$v", JsonSerializer.Serialize(value, Json)));

    public List<ShareLink> Shares()
    {
        lock (_lock)
        {
            var list = new List<ShareLink>();
            using var c = Cmd("SELECT json FROM shares");
            using var r = c.ExecuteReader();
            while (r.Read()) list.Add(JsonSerializer.Deserialize<ShareLink>(r.GetString(0), Json)!);
            return list.OrderBy(s => s.CreatedMs).ToList();
        }
    }

    public void SaveShare(ShareLink s) =>
        Exec("INSERT OR REPLACE INTO shares(token, json) VALUES($t, $j)", ("$t", s.Token), ("$j", JsonSerializer.Serialize(s, Json)));

    public void DeleteShare(string token) => Exec("DELETE FROM shares WHERE token=$t", ("$t", token));
}
