using Microsoft.Data.Sqlite;

namespace StackScope.Core.Models;

/// <summary>
/// A saved ablation configuration — name + rectangular range + prompt +
/// sampling knobs. Users pick from the preset library and hit Load to
/// seed the AnalysisView / AblationSweep controls, so common
/// attribution studies are one click away instead of re-typed.
/// </summary>
public sealed record AblationPreset(
    long   Id,
    string Name,
    int    LayerStart,
    int    LayerEnd,
    int    HeadStart,
    int    HeadEnd,
    string Prompt,
    ulong  Seed,
    double SigmaThreshold,
    long   CreatedAtNs);

/// <summary>
/// Project-scoped SQLite storage for <see cref="AblationPreset"/>. Same
/// pattern as <see cref="PinnedDiffStore"/> — one <c>.sqlite</c> file at
/// the project root so presets travel with the project folder.
/// </summary>
public sealed class AblationPresetStore : IDisposable
{
    private readonly SqliteConnection _conn;

    public AblationPresetStore(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS ablation_presets (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                name            TEXT NOT NULL UNIQUE,
                layer_start     INTEGER NOT NULL,
                layer_end       INTEGER NOT NULL,
                head_start      INTEGER NOT NULL,
                head_end        INTEGER NOT NULL,
                prompt          TEXT NOT NULL DEFAULT '',
                seed            INTEGER NOT NULL DEFAULT 0,
                sigma_threshold REAL    NOT NULL DEFAULT 1.0,
                created_at_ns   INTEGER NOT NULL
            );";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Insert or replace by unique name. Returns the row id.</summary>
    public long Upsert(AblationPreset p)
    {
        if (string.IsNullOrWhiteSpace(p.Name))
            throw new ArgumentException("AblationPreset requires a non-empty Name.");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO ablation_presets
              (name, layer_start, layer_end, head_start, head_end,
               prompt, seed, sigma_threshold, created_at_ns)
            VALUES
              ($n, $ls, $le, $hs, $he, $prompt, $seed, $sigma, $ts)
            ON CONFLICT(name) DO UPDATE SET
              layer_start     = excluded.layer_start,
              layer_end       = excluded.layer_end,
              head_start      = excluded.head_start,
              head_end        = excluded.head_end,
              prompt          = excluded.prompt,
              seed            = excluded.seed,
              sigma_threshold = excluded.sigma_threshold,
              created_at_ns   = excluded.created_at_ns;
            SELECT id FROM ablation_presets WHERE name = $n;";
        cmd.Parameters.AddWithValue("$n",    p.Name);
        cmd.Parameters.AddWithValue("$ls",   p.LayerStart);
        cmd.Parameters.AddWithValue("$le",   p.LayerEnd);
        cmd.Parameters.AddWithValue("$hs",   p.HeadStart);
        cmd.Parameters.AddWithValue("$he",   p.HeadEnd);
        cmd.Parameters.AddWithValue("$prompt", p.Prompt ?? "");
        cmd.Parameters.AddWithValue("$seed",   (long)p.Seed);
        cmd.Parameters.AddWithValue("$sigma",  p.SigmaThreshold);
        cmd.Parameters.AddWithValue("$ts",     p.CreatedAtNs);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public IReadOnlyList<AblationPreset> List()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, name, layer_start, layer_end, head_start, head_end,
                   prompt, seed, sigma_threshold, created_at_ns
            FROM ablation_presets ORDER BY created_at_ns DESC;";
        var result = new List<AblationPreset>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new AblationPreset(
                r.GetInt64(0), r.GetString(1),
                r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5),
                r.IsDBNull(6) ? "" : r.GetString(6),
                (ulong)r.GetInt64(7),
                r.GetDouble(8),
                r.GetInt64(9)));
        }
        return result;
    }

    public int Delete(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ablation_presets WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
