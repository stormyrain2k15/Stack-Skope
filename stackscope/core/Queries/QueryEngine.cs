using System.Text;
using StackScope.Core.Storage;
using StackScope.Core.Transactions;

namespace StackScope.Core.Queries;

/// <summary>
/// Query engine over the event store. Everything the UI shows goes through
/// this. Uses the SQLite index to locate matching events, then reads their
/// bodies from the mmap log. Supports virtualised paging.
/// </summary>
public sealed class QueryEngine
{
    private readonly EventStore _store;

    public QueryEngine(EventStore store) { _store = store; }

    public long Count(EventQuery q)
    {
        var (sql, parms) = BuildSql(q, countOnly: true);
        using var cmd = _store.Index.Connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in parms) cmd.Parameters.AddWithValue(n, v);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public IEnumerable<TransactionEvent> Query(EventQuery q)
    {
        var (sql, parms) = BuildSql(q, countOnly: false);
        using var cmd = _store.Index.Connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in parms) cmd.Parameters.AddWithValue(n, v);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            long offset = r.GetInt64(0);
            yield return _store.ReadAt(offset);
        }
    }

    private static (string sql, List<(string, object)>) BuildSql(EventQuery q, bool countOnly)
    {
        var where = new StringBuilder();
        var parms = new List<(string, object)>();

        void And(string clause) { if (where.Length > 0) where.Append(" AND "); where.Append(clause); }

        if (q.Kinds.Count > 0)
        {
            var placeholders = string.Join(",", q.Kinds.Select((_, i) => "$k" + i));
            And($"kind IN ({placeholders})");
            for (int i = 0; i < q.Kinds.Count; i++)
                parms.Add(("$k" + i, (int)(byte)q.Kinds[i]));
        }
        if (q.TokenIndex.From != -1) { And("token_index >= $tokfrom"); parms.Add(("$tokfrom", q.TokenIndex.From)); }
        if (q.TokenIndex.To   != -1) { And("token_index <= $tokto");   parms.Add(("$tokto",   q.TokenIndex.To)); }
        if (q.LayerIndex.From != -1) { And("layer_index >= $layfrom"); parms.Add(("$layfrom", q.LayerIndex.From)); }
        if (q.LayerIndex.To   != -1) { And("layer_index <= $layto");   parms.Add(("$layto",   q.LayerIndex.To)); }
        if (q.HeadIndex.From  != -1) { And("head_index  >= $hdfrom");  parms.Add(("$hdfrom",  q.HeadIndex.From)); }
        if (q.HeadIndex.To    != -1) { And("head_index  <= $hdto");    parms.Add(("$hdto",    q.HeadIndex.To)); }
        if (q.TimeFromNs > 0)               { And("ts_ns >= $tfrom"); parms.Add(("$tfrom", q.TimeFromNs)); }
        if (q.TimeToNs   < long.MaxValue)   { And("ts_ns <= $tto");   parms.Add(("$tto",   q.TimeToNs)); }

        string whereClause = where.Length > 0 ? " WHERE " + where : "";

        if (countOnly)
            return ($"SELECT COUNT(*) FROM events{whereClause};", parms);

        return (
            $"SELECT log_offset FROM events{whereClause} " +
            "ORDER BY event_id " +
            "LIMIT $lim OFFSET $off;",
            parms.Concat(new (string, object)[] { ("$lim", q.Limit), ("$off", q.Offset) }).ToList());
    }
}
