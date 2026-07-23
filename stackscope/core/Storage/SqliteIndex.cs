using Microsoft.Data.Sqlite;

namespace StackScope.Core.Storage;

/// <summary>
/// Per-transaction SQLite index over the mmap event log. One row per event.
/// The event body itself lives in the mmap; SQLite only stores keys +
/// the byte offset into the log. This keeps queries O(log n) on the axes
/// the UI actually filters on: kind, token, layer, head, timestamp.
/// </summary>
public sealed class SqliteIndex : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqliteCommand _insert;
    private readonly SqliteCommand _upsertMarker;
    private readonly SqliteCommand _lookupMarker;
    private bool _disposed;

    public SqliteIndex(string path)
    {
        _conn = new SqliteConnection($"Data Source={path};Cache=Shared;Pooling=False");
        _conn.Open();

        using (var pragma = _conn.CreateCommand())
        {
            pragma.CommandText = @"
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA temp_store=MEMORY;
                PRAGMA mmap_size=268435456;
            ";
            pragma.ExecuteNonQuery();
        }

        using (var ddl = _conn.CreateCommand())
        {
            ddl.CommandText = @"
                CREATE TABLE IF NOT EXISTS events (
                    event_id     INTEGER PRIMARY KEY,
                    ts_ns        INTEGER NOT NULL,
                    kind         INTEGER NOT NULL,
                    token_index  INTEGER NOT NULL,
                    layer_index  INTEGER NOT NULL,
                    head_index   INTEGER NOT NULL,
                    stream_id    INTEGER NOT NULL,
                    thread_id    INTEGER NOT NULL,
                    device_id    INTEGER NOT NULL,
                    log_offset   INTEGER NOT NULL,
                    log_length   INTEGER NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_events_kind_time
                    ON events(kind, ts_ns);
                CREATE INDEX IF NOT EXISTS ix_events_token
                    ON events(token_index, kind, ts_ns);
                CREATE INDEX IF NOT EXISTS ix_events_layer
                    ON events(layer_index, kind, ts_ns);
                CREATE INDEX IF NOT EXISTS ix_events_head
                    ON events(head_index, kind, ts_ns);
                CREATE INDEX IF NOT EXISTS ix_events_stream_time
                    ON events(stream_id, ts_ns);
                CREATE INDEX IF NOT EXISTS ix_events_thread_time
                    ON events(thread_id, ts_ns);

                CREATE TABLE IF NOT EXISTS marker_names (
                    id   INTEGER PRIMARY KEY,
                    name TEXT NOT NULL UNIQUE
                );

                CREATE TABLE IF NOT EXISTS meta (
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
            ";
            ddl.ExecuteNonQuery();
        }

        _insert = _conn.CreateCommand();
        _insert.CommandText = @"
            INSERT INTO events (event_id, ts_ns, kind, token_index,
                layer_index, head_index, stream_id, thread_id, device_id,
                log_offset, log_length)
            VALUES ($id, $ts, $k, $tok, $lay, $hd, $st, $th, $dv, $off, $len);
        ";
        for (int i = 0; i < 11; i++) _insert.Parameters.Add(new SqliteParameter());
        _insert.Parameters[0].ParameterName = "$id";
        _insert.Parameters[1].ParameterName = "$ts";
        _insert.Parameters[2].ParameterName = "$k";
        _insert.Parameters[3].ParameterName = "$tok";
        _insert.Parameters[4].ParameterName = "$lay";
        _insert.Parameters[5].ParameterName = "$hd";
        _insert.Parameters[6].ParameterName = "$st";
        _insert.Parameters[7].ParameterName = "$th";
        _insert.Parameters[8].ParameterName = "$dv";
        _insert.Parameters[9].ParameterName = "$off";
        _insert.Parameters[10].ParameterName = "$len";

        _upsertMarker = _conn.CreateCommand();
        _upsertMarker.CommandText = @"
            INSERT INTO marker_names (name) VALUES ($n)
            ON CONFLICT(name) DO UPDATE SET name=excluded.name
            RETURNING id;
        ";
        _upsertMarker.Parameters.Add(new SqliteParameter("$n", ""));

        _lookupMarker = _conn.CreateCommand();
        _lookupMarker.CommandText = "SELECT name FROM marker_names WHERE id=$id;";
        _lookupMarker.Parameters.Add(new SqliteParameter("$id", 0L));
    }

    public SqliteConnection Connection => _conn;

    public void InsertEvent(
        ulong eventId, long tsNs, byte kind,
        int token, int layer, int head,
        int stream, int thread, int device,
        long logOffset, int logLength)
    {
        _insert.Parameters[0].Value = (long)eventId;
        _insert.Parameters[1].Value = tsNs;
        _insert.Parameters[2].Value = (int)kind;
        _insert.Parameters[3].Value = token;
        _insert.Parameters[4].Value = layer;
        _insert.Parameters[5].Value = head;
        _insert.Parameters[6].Value = stream;
        _insert.Parameters[7].Value = thread;
        _insert.Parameters[8].Value = device;
        _insert.Parameters[9].Value = logOffset;
        _insert.Parameters[10].Value = logLength;
        _insert.ExecuteNonQuery();
    }

    public uint InternMarkerName(string name)
    {
        _upsertMarker.Parameters[0].Value = name;
        var r = _upsertMarker.ExecuteScalar();
        return checked((uint)Convert.ToInt64(r));
    }

    public string LookupMarkerName(uint id)
    {
        _lookupMarker.Parameters[0].Value = (long)id;
        var r = _lookupMarker.ExecuteScalar();
        return r as string ?? throw new InvalidDataException($"Unknown marker id {id}.");
    }

    public SqliteTransaction BeginBatch() => _conn.BeginTransaction();

    public void SetMeta(string key, string value)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO meta(key,value) VALUES ($k,$v)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public string? GetMeta(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=$k;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _insert.Dispose();
        _upsertMarker.Dispose();
        _lookupMarker.Dispose();
        _conn.Close();
        _conn.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
