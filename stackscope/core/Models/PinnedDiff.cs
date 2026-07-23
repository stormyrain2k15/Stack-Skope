using Microsoft.Data.Sqlite;

namespace StackScope.Core.Models;

/// <summary>
/// One saved (baseline ⇆ candidate) diff pin. Persisted in a
/// project-scoped SQLite database so head-attribution studies survive
/// across sessions. Anything the Compare view can display can be
/// pinned: seed, sigma, and a free-form note.
/// </summary>
public sealed record PinnedDiff(
    long   Id,
    string LeftTransactionId,
    string RightTransactionId,
    double SigmaThreshold,
    long   CreatedAtNs,
    string Note,
    string Tags);

/// <summary>
/// Storage for <see cref="PinnedDiff"/>. Kept in a dedicated sqlite
/// file (<c>pinned_diffs.sqlite</c>) under the project root — not per
/// capture — because the value of a diff is that it references
/// <b>two</b> captures. Pins survive individual capture deletion; a
/// dangling reference is surfaced by the WPF pane rather than swept
/// silently.
/// </summary>
public sealed class PinnedDiffStore : IDisposable
{
    private readonly SqliteConnection _conn;

    public PinnedDiffStore(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS pinned_diffs (
                id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                left_transaction_id   TEXT NOT NULL,
                right_transaction_id  TEXT NOT NULL,
                sigma_threshold       REAL NOT NULL DEFAULT 1.5,
                created_at_ns         INTEGER NOT NULL,
                note                  TEXT NOT NULL DEFAULT '',
                tags                  TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS pinned_diffs_left_idx  ON pinned_diffs(left_transaction_id);
            CREATE INDEX IF NOT EXISTS pinned_diffs_right_idx ON pinned_diffs(right_transaction_id);";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Add a pin. Returns the assigned row id. Rejects empty
    /// transaction ids so a hollow pin can't be persisted — the
    /// pane will not silently discard user input.
    /// </summary>
    public long Add(PinnedDiff p)
    {
        if (string.IsNullOrWhiteSpace(p.LeftTransactionId) ||
            string.IsNullOrWhiteSpace(p.RightTransactionId))
        {
            throw new ArgumentException(
                "PinnedDiff requires both LeftTransactionId and RightTransactionId to be non-empty.");
        }
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO pinned_diffs
                (left_transaction_id, right_transaction_id, sigma_threshold,
                 created_at_ns, note, tags)
            VALUES ($L, $R, $sigma, $ts, $note, $tags);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$L",    p.LeftTransactionId);
        cmd.Parameters.AddWithValue("$R",    p.RightTransactionId);
        cmd.Parameters.AddWithValue("$sigma", p.SigmaThreshold);
        cmd.Parameters.AddWithValue("$ts",   p.CreatedAtNs);
        cmd.Parameters.AddWithValue("$note", p.Note ?? "");
        cmd.Parameters.AddWithValue("$tags", p.Tags ?? "");
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>List every pin, newest first.</summary>
    public IReadOnlyList<PinnedDiff> List()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, left_transaction_id, right_transaction_id,
                   sigma_threshold, created_at_ns, note, tags
            FROM pinned_diffs
            ORDER BY created_at_ns DESC;";
        var result = new List<PinnedDiff>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new PinnedDiff(
                r.GetInt64(0), r.GetString(1), r.GetString(2),
                r.GetDouble(3), r.GetInt64(4),
                r.IsDBNull(5) ? "" : r.GetString(5),
                r.IsDBNull(6) ? "" : r.GetString(6)));
        }
        return result;
    }

    /// <summary>Delete a pin by id. Returns the number of rows removed.</summary>
    public int Delete(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM pinned_diffs WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Update the note/tags on an existing pin. Returns rows changed.</summary>
    public int UpdateNote(long id, string note, string tags)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE pinned_diffs SET note = $note, tags = $tags WHERE id = $id;";
        cmd.Parameters.AddWithValue("$note", note ?? "");
        cmd.Parameters.AddWithValue("$tags", tags ?? "");
        cmd.Parameters.AddWithValue("$id",   id);
        return cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
