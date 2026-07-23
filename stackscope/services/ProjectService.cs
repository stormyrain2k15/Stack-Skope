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
    /// <summary>Path to the project-scoped SQLite file backing the
    /// diff pin board. Lives at the project root (not per-capture)
    /// because a pin references *two* captures.</summary>
    public string PinnedDiffsDbPath => Path.Combine(RootDir, "pinned_diffs.sqlite");

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
                int ablateLayer    = int.TryParse(idx.GetMeta("ablate_layer"),     out var al)  ? al  : -1;
                int ablateHead     = int.TryParse(idx.GetMeta("ablate_head"),      out var ah)  ? ah  : -1;
                int ablateLayerEnd = int.TryParse(idx.GetMeta("ablate_layer_end"), out var ale) ? ale : -1;
                int ablateHeadEnd  = int.TryParse(idx.GetMeta("ablate_head_end"),  out var ahe) ? ahe : -1;
                list.Add(new TransactionMetadata(
                    txid,
                    idx.GetMeta("model_handle") ?? "",
                    idx.GetMeta("architecture") ?? "",
                    long.TryParse(idx.GetMeta("started_ns"), out var s) ? s : 0,
                    long.TryParse(idx.GetMeta("ended_ns"), out var e) ? e : 0,
                    completed,
                    idx.GetMeta("error"),
                    idx.GetMeta("prompt") ?? "",
                    ablateLayer,
                    ablateHead,
                    idx.GetMeta("capture_ceiling"),
                    ablateLayerEnd,
                    ablateHeadEnd));
            }
            catch
            {
                // Corrupt / partial index — surface as partial capture.
                list.Add(new TransactionMetadata(
                    txid, "", "", 0, 0, false, "index unreadable", "", -1, -1, null, -1, -1));
            }
        }
        list.Sort((a, b) => b.StartedNs.CompareTo(a.StartedNs));
        return list;
    }

    /// <summary>
    /// Find the newest completed transaction that ran the same prompt as
    /// <paramref name="reference"/> but with <b>no</b> ablation set. Used
    /// by the WPF "auto-compare" flow: after an ablated capture, we
    /// pre-seed Diff Mode with the closest non-ablated baseline so the
    /// user sees the head's contribution in one click.
    /// Returns <c>null</c> if no baseline exists (e.g. user ran the
    /// ablated capture before ever running a plain one).
    /// </summary>
    public TransactionMetadata? FindLatestNonAblatedBaseline(TransactionMetadata reference)
    {
        if (reference is null || string.IsNullOrEmpty(reference.Prompt)) return null;
        foreach (var t in ListTransactions())      // already sorted newest → oldest
        {
            if (t.TransactionId == reference.TransactionId) continue;
            if (!t.Completed) continue;
            if (t.AblateLayer >= 0 || t.AblateHead >= 0) continue;
            if (!string.Equals(t.Prompt, reference.Prompt, StringComparison.Ordinal)) continue;
            // Prefer same model when the metadata is present on both sides;
            // when either side has no model handle recorded (older
            // captures), fall back to prompt-equality alone rather than
            // returning nothing.
            if (!string.IsNullOrEmpty(t.ModelHandle) &&
                !string.IsNullOrEmpty(reference.ModelHandle) &&
                t.ModelHandle != reference.ModelHandle) continue;
            return t;
        }
        return null;
    }
}

public sealed record TransactionMetadata(
    string TransactionId,
    string ModelHandle,
    string Architecture,
    long   StartedNs,
    long   EndedNs,
    bool   Completed,
    string? Error,
    string Prompt,
    int    AblateLayer,
    int    AblateHead,
    string? CaptureCeiling,
    int    AblateLayerEnd = -1,
    int    AblateHeadEnd  = -1)
{
    public bool WasAblated => AblateLayer >= 0 && AblateHead >= 0;
    /// <summary>True when the ablation covers more than one head.</summary>
    public bool IsAblationRange =>
        WasAblated && (AblateLayerEnd > AblateLayer || AblateHeadEnd > AblateHead);
    /// <summary>True when the worker emitted a capacity-ceiling marker
    /// (e.g. llama.cpp can't do advanced capture / ablation). The WPF
    /// status bar surfaces this so users see the fallback loudly.</summary>
    public bool HasCaptureCeiling => !string.IsNullOrEmpty(CaptureCeiling);
}
