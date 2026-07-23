using StackScope.Core.Storage;

namespace StackScope.Services;

/// <summary>
/// Project = a StackScope working directory. Owns the on-disk layout,
/// the list of captured transactions, and per-transaction event stores.
/// </summary>
public sealed class ProjectService
{
    public string RootDir { get; }
    public string CapturesDir => Path.Combine(RootDir, "captures");
    public string ModelsCacheDir => Path.Combine(RootDir, "models");
    public string LayoutsDir => Path.Combine(RootDir, "layouts");

    private readonly Dictionary<string, (string Device, bool Verified)> _resolvedByHandle = new();

    public ProjectService(string rootDir)
    {
        RootDir = rootDir;
        Directory.CreateDirectory(CapturesDir);
        Directory.CreateDirectory(ModelsCacheDir);
        Directory.CreateDirectory(LayoutsDir);
    }

    /// <summary>
    /// Called by <see cref="CoordinatorService.LoadModel"/> so that
    /// <see cref="CoordinatorService.RunInference"/> can look up the
    /// worker's reported placement (and verified flag) when picking
    /// the driver-capture backend. Without this the RunInference path
    /// would have to re-query capabilities every time.
    /// </summary>
    public void RememberResolvedDevice(string modelHandle, string device, bool verified)
    {
        _resolvedByHandle[modelHandle] = (device, verified);
    }

    public string? LookupResolvedDevice(string modelHandle)
        => _resolvedByHandle.TryGetValue(modelHandle, out var r) ? r.Device : null;

    public bool LookupResolvedDeviceVerified(string modelHandle)
        => _resolvedByHandle.TryGetValue(modelHandle, out var r) && r.Verified;

    public EventStore OpenOrCreateStore(string transactionId)
        => new EventStore(transactionId, CapturesDir);

    /// <summary>
    /// List past transactions found in the captures directory.
    /// Complete-vs-partial is decided by presence of the .sqlite meta row
    /// <c>completed</c> = "true".
    /// </summary>
    public IReadOnlyList<TransactionMetadata> ListTransactions()
    {
        var list = new List<TransactionMetadata>();
        foreach (var mm in Directory.GetFiles(CapturesDir, "*.mmap"))
        {
            var txid = Path.GetFileNameWithoutExtension(mm);
            var sqlite = Path.Combine(CapturesDir, txid + ".sqlite");
            if (!File.Exists(sqlite)) continue;
            try
            {
                using var idx = new SqliteIndex(sqlite);
                bool completed = string.Equals(idx.GetMeta("completed"), "true",
                    StringComparison.OrdinalIgnoreCase);
                list.Add(new TransactionMetadata(
                    txid,
                    idx.GetMeta("model_handle") ?? "",
                    idx.GetMeta("architecture") ?? "",
                    long.TryParse(idx.GetMeta("started_ns"), out var s) ? s : 0,
                    long.TryParse(idx.GetMeta("ended_ns"), out var e) ? e : 0,
                    completed,
                    idx.GetMeta("error")));
            }
            catch
            {
                // Corrupt / partial index — surface as partial capture.
                list.Add(new TransactionMetadata(
                    txid, "", "", 0, 0, false, "index unreadable"));
            }
        }
        list.Sort((a, b) => b.StartedNs.CompareTo(a.StartedNs));
        return list;
    }
}

public sealed record TransactionMetadata(
    string TransactionId,
    string ModelHandle,
    string Architecture,
    long   StartedNs,
    long   EndedNs,
    bool   Completed,
    string? Error);
