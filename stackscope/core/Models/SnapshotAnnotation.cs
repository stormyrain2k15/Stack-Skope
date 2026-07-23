using Microsoft.Data.Sqlite;

namespace StackScope.Core.Models;

/// <summary>
/// Snapshot annotation — a persistent research note pinned to
/// (transaction_id, event_id?, layer?, head?, token?). Persisted
/// alongside the capture's SQLite index as a separate table.
/// </summary>
public sealed record SnapshotAnnotation(
    long Id,
    string TransactionId,
    ulong? EventId,
    int? Layer,
    int? Head,
    int? Token,
    long CreatedAtNs,
    string Author,
    string Text,
    string Tags);

/// <summary>
/// Storage for <see cref="SnapshotAnnotation"/>. Uses the same SQLite
/// database as the capture index so annotations round-trip with the
/// bundle and export cleanly as markdown.
/// </summary>
public sealed class AnnotationStore : IDisposable
{
    private readonly SqliteConnection _conn;

    public AnnotationStore(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS annotations (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                transaction_id TEXT NOT NULL,
                event_id       INTEGER,
                layer          INTEGER,
                head           INTEGER,
                token          INTEGER,
                created_at_ns  INTEGER NOT NULL,
                author         TEXT NOT NULL,
                text           TEXT NOT NULL,
                tags           TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS annotations_tx_idx ON annotations(transaction_id);
            CREATE INDEX IF NOT EXISTS annotations_layer_idx ON annotations(layer);";
        cmd.ExecuteNonQuery();
    }

    public long Add(SnapshotAnnotation a)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO annotations (transaction_id, event_id, layer, head, token,
                                      created_at_ns, author, text, tags)
            VALUES ($tx, $eid, $L, $H, $T, $ts, $author, $text, $tags);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$tx",     a.TransactionId);
        cmd.Parameters.AddWithValue("$eid",    (object?)a.EventId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$L",      (object?)a.Layer   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$H",      (object?)a.Head    ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$T",      (object?)a.Token   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts",     a.CreatedAtNs);
        cmd.Parameters.AddWithValue("$author", a.Author);
        cmd.Parameters.AddWithValue("$text",   a.Text);
        cmd.Parameters.AddWithValue("$tags",   a.Tags ?? "");
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public IReadOnlyList<SnapshotAnnotation> List(string? txId = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = txId is null
            ? "SELECT id, transaction_id, event_id, layer, head, token, created_at_ns, author, text, tags FROM annotations ORDER BY created_at_ns DESC;"
            : "SELECT id, transaction_id, event_id, layer, head, token, created_at_ns, author, text, tags FROM annotations WHERE transaction_id = $tx ORDER BY created_at_ns DESC;";
        if (txId is not null) cmd.Parameters.AddWithValue("$tx", txId);
        var result = new List<SnapshotAnnotation>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new SnapshotAnnotation(
                r.GetInt64(0), r.GetString(1),
                r.IsDBNull(2) ? null : (ulong?)r.GetInt64(2),
                r.IsDBNull(3) ? null : r.GetInt32(3),
                r.IsDBNull(4) ? null : r.GetInt32(4),
                r.IsDBNull(5) ? null : r.GetInt32(5),
                r.GetInt64(6), r.GetString(7), r.GetString(8),
                r.IsDBNull(9) ? "" : r.GetString(9)));
        }
        return result;
    }

    public string ExportMarkdown(string? txId = null)
    {
        var items = List(txId);
        var buf = new System.Text.StringBuilder();
        buf.AppendLine("# StackScope research notes");
        buf.AppendLine();
        foreach (var a in items)
        {
            buf.AppendLine($"## Note {a.Id}");
            buf.AppendLine();
            buf.Append("- **Location**: ");
            buf.Append(a.EventId is not null ? $"event {a.EventId} " : "");
            buf.Append(a.Layer   is not null ? $"L{a.Layer} " : "");
            buf.Append(a.Head    is not null ? $"H{a.Head} " : "");
            buf.Append(a.Token   is not null ? $"tok{a.Token}" : "");
            buf.AppendLine();
            if (!string.IsNullOrEmpty(a.Tags))
                buf.AppendLine($"- **Tags**: {a.Tags}");
            buf.AppendLine($"- **Author**: {a.Author}");
            buf.AppendLine();
            buf.AppendLine(a.Text);
            buf.AppendLine();
        }
        return buf.ToString();
    }

    public void Dispose() => _conn.Dispose();
}
