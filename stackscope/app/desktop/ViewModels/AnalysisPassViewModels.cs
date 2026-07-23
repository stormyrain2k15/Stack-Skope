using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StackScope.Core.Analysis;
using StackScope.Core.Models;
using StackScope.Core.Storage;
using StackScope.Services;

namespace StackScope.Desktop.ViewModels;

// ---------- Numerical Health Dashboard --------------------------------

public sealed partial class HealthDashboardViewModel : ObservableObject
{
    private readonly ProjectService _project;

    [ObservableProperty] private string _transactionId = "";
    [ObservableProperty] private int _totalEvents;
    [ObservableProperty] private int _totalAnomalies;
    [ObservableProperty] private string _status = "";

    public ObservableCollection<NumericalHealth.LayerHealth> Layers { get; } = new();
    public ObservableCollection<string> Findings { get; } = new();

    public HealthDashboardViewModel(ProjectService project) { _project = project; }

    public IRelayCommand ComputeCommand => new RelayCommand(Compute);

    private void Compute()
    {
        Layers.Clear(); Findings.Clear(); Status = "";
        if (string.IsNullOrWhiteSpace(TransactionId)) { Status = "Provide a transaction id."; return; }
        try
        {
            using var store = new EventStore(TransactionId, _project.CapturesDir);
            var report = new NumericalHealth(store).Compute();
            TotalEvents    = report.TotalEvents;
            TotalAnomalies = report.TotalAnomalies;
            foreach (var l in report.Layers) Layers.Add(l);
            foreach (var f in report.Findings) Findings.Add(f);
            Status = $"OK — {report.TotalEvents} events, {report.TotalAnomalies} anomalies.";
        }
        catch (Exception ex) { Status = "Failed: " + ex.Message; }
    }
}

// ---------- Quantization Diff -----------------------------------------

public sealed partial class QuantizationDiffViewModel : ObservableObject
{
    private readonly ProjectService _project;

    [ObservableProperty] private string _referenceTx = "";
    [ObservableProperty] private string _candidateTx = "";
    [ObservableProperty] private string _referenceQuant = "f16";
    [ObservableProperty] private string _candidateQuant = "q4_k_m";
    [ObservableProperty] private int _firstDivergentToken = -1;
    [ObservableProperty] private int _firstDivergentLayer = -1;
    [ObservableProperty] private double _totalEnergyDelta;
    [ObservableProperty] private string _status = "";

    public ObservableCollection<QuantizationDiff.LayerShift> Rows { get; } = new();

    public QuantizationDiffViewModel(ProjectService project) { _project = project; }

    public IRelayCommand CompareCommand => new RelayCommand(Compare);

    private void Compare()
    {
        Rows.Clear(); Status = "";
        try
        {
            using var refStore = new EventStore(ReferenceTx, _project.CapturesDir);
            using var candStore = new EventStore(CandidateTx, _project.CapturesDir);
            var r = new QuantizationDiff(refStore, candStore, ReferenceQuant, CandidateQuant).Compute();
            FirstDivergentToken = r.FirstDivergentToken;
            FirstDivergentLayer = r.FirstDivergentLayer;
            TotalEnergyDelta    = r.TotalEnergyDelta;
            foreach (var row in r.PerLayer) Rows.Add(row);
            Status = "Done.";
        }
        catch (Exception ex) { Status = "Failed: " + ex.Message; }
    }
}

// ---------- Determinism Auditor ---------------------------------------

public sealed partial class DeterminismAuditorViewModel : ObservableObject
{
    private readonly ProjectService _project;

    [ObservableProperty] private string _runA = "";
    [ObservableProperty] private string _runB = "";
    [ObservableProperty] private bool _deterministic;
    [ObservableProperty] private string _status = "";

    public ObservableCollection<DeterminismAuditor.Finding> Findings { get; } = new();

    public DeterminismAuditorViewModel(ProjectService project) { _project = project; }

    public IRelayCommand AuditCommand => new RelayCommand(Audit);

    private void Audit()
    {
        Findings.Clear(); Status = "";
        try
        {
            using var a = new EventStore(RunA, _project.CapturesDir);
            using var b = new EventStore(RunB, _project.CapturesDir);
            var r = new DeterminismAuditor(a, b).Audit();
            Deterministic = r.Deterministic;
            foreach (var f in r.Findings) Findings.Add(f);
            Status = r.Deterministic ? "Deterministic — no findings." : $"{r.Findings.Count} finding(s).";
        }
        catch (Exception ex) { Status = "Failed: " + ex.Message; }
    }
}

// ---------- Attribution Graph ----------------------------------------

public sealed partial class AttributionGraphViewModel : ObservableObject
{
    private readonly ProjectService _project;

    [ObservableProperty] private string _transactionId = "";
    [ObservableProperty] private int _targetToken;
    [ObservableProperty] private int _maxPerLayer = 8;
    [ObservableProperty] private int _maxLayers   = 32;
    [ObservableProperty] private string _status = "";

    public ObservableCollection<AttributionGraph.Node> Nodes { get; } = new();
    public ObservableCollection<AttributionGraph.Edge> Edges { get; } = new();

    public AttributionGraphViewModel(ProjectService project) { _project = project; }

    public IRelayCommand BuildCommand => new RelayCommand(Build);

    private void Build()
    {
        Nodes.Clear(); Edges.Clear(); Status = "";
        try
        {
            using var store = new EventStore(TransactionId, _project.CapturesDir);
            var g = new AttributionGraph(store).Build(TargetToken, MaxPerLayer, MaxLayers);
            foreach (var n in g.Nodes) Nodes.Add(n);
            foreach (var e in g.Edges) Edges.Add(e);
            Status = $"{g.Nodes.Count} nodes / {g.Edges.Count} edges.";
        }
        catch (Exception ex) { Status = "Failed: " + ex.Message; }
    }
}

// ---------- Annotations Pane -----------------------------------------

public sealed partial class AnnotationsViewModel : ObservableObject
{
    private readonly ProjectService _project;
    private AnnotationStore? _store;

    [ObservableProperty] private string _transactionId = "";
    [ObservableProperty] private string _newText = "";
    [ObservableProperty] private string _tags = "";
    [ObservableProperty] private int _layer = -1;
    [ObservableProperty] private int _head  = -1;
    [ObservableProperty] private int _token = -1;
    [ObservableProperty] private string _status = "";

    public ObservableCollection<SnapshotAnnotation> Items { get; } = new();

    public AnnotationsViewModel(ProjectService project) { _project = project; }

    private AnnotationStore GetStore()
    {
        if (_store is not null) return _store;
        var path = Path.Combine(_project.CapturesDir, "annotations.sqlite");
        _store = new AnnotationStore(path);
        return _store;
    }

    public IRelayCommand RefreshCommand => new RelayCommand(Refresh);
    public IRelayCommand AddCommand => new RelayCommand(Add);
    public IRelayCommand ExportMarkdownCommand => new RelayCommand(ExportMarkdown);

    private void Refresh()
    {
        Items.Clear();
        foreach (var a in GetStore().List(string.IsNullOrWhiteSpace(TransactionId) ? null : TransactionId))
            Items.Add(a);
        Status = $"{Items.Count} note(s).";
    }

    private void Add()
    {
        if (string.IsNullOrWhiteSpace(NewText)) return;
        var author = Environment.UserName ?? "user";
        GetStore().Add(new SnapshotAnnotation(
            Id: 0,
            TransactionId: TransactionId ?? "",
            EventId: null,
            Layer: Layer < 0 ? null : Layer,
            Head:  Head  < 0 ? null : Head,
            Token: Token < 0 ? null : Token,
            CreatedAtNs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L,
            Author: author,
            Text: NewText,
            Tags: Tags ?? ""));
        NewText = "";
        Refresh();
    }

    private void ExportMarkdown()
    {
        var md = GetStore().ExportMarkdown(string.IsNullOrWhiteSpace(TransactionId) ? null : TransactionId);
        var out_ = Path.Combine(_project.CapturesDir,
            $"annotations-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        File.WriteAllText(out_, md);
        Status = $"Exported to {out_}";
    }
}

// ---------- Natural Language Query bar -------------------------------

/// <summary>
/// One-line natural-language query. Uses a rule-based translator by
/// default (kind names, layer/head/token numbers, "nan"/"anomaly"
/// keywords). If an LLM key is configured, the same query can be
/// forwarded to an LLM which returns structured MCP tool calls.
/// The rule-based path guarantees the feature works offline.
/// </summary>
public sealed partial class NaturalQueryViewModel : ObservableObject
{
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _translated = "";
    [ObservableProperty] private string _hint = "";

    public NaturalQueryViewModel()
    {
        Hint = "Try: 'attention layer 5 head 3', 'anomalies', 'logits token 12', 'kernel launches on layer 0'.";
    }

    public IRelayCommand TranslateCommand => new RelayCommand(Translate);

    private void Translate()
    {
        Translated = NaturalQueryTranslator.Translate(Query ?? "");
    }
}

/// <summary>
/// Rule-based translator: natural language → structured query hint that
/// the UI can either execute directly or hand to an LLM. Uses simple
/// keyword patterns so it works with zero external dependencies.
/// </summary>
public static class NaturalQueryTranslator
{
    private static readonly (string Kw, string Kind)[] KindKeywords =
    {
        ("attention", "ATTENTION_SCORES"),
        ("qkv",       "ATTENTION_QKV"),
        ("output",    "ATTENTION_OUTPUT"),
        ("layer",     "LAYER_BEGIN,LAYER_END"),
        ("kernel",    "KERNEL_LAUNCH,KERNEL_END"),
        ("memcpy",    "MEMCPY"),
        ("alloc",     "ALLOC,FREE"),
        ("logit",     "LOGITS"),
        ("sample",    "SAMPLE"),
        ("token",     "TOKEN_BEGIN,TOKEN_END"),
        ("activation","ACTIVATION"),
        ("marker",    "MARKER"),
        ("anomaly",   "MARKER"),   // anomalies are markers with a specific name
    };

    public static string Translate(string q)
    {
        q = q.ToLowerInvariant();
        var kinds = new List<string>();
        int? layer = null, head = null, token = null;

        foreach (var (kw, kind) in KindKeywords)
            if (q.Contains(kw)) foreach (var k in kind.Split(',')) if (!kinds.Contains(k)) kinds.Add(k);

        layer = ExtractInt(q, "layer");
        head  = ExtractInt(q, "head");
        token = ExtractInt(q, "token");

        var parts = new List<string>();
        if (kinds.Count > 0) parts.Add("kinds=" + string.Join("+", kinds));
        if (layer.HasValue) parts.Add($"layer={layer}");
        if (head.HasValue)  parts.Add($"head={head}");
        if (token.HasValue) parts.Add($"token={token}");
        if (q.Contains("anomaly") || q.Contains("nan") || q.Contains("inf"))
            parts.Add("marker_name=stackscope.anomaly");

        return parts.Count == 0 ? "(no filters matched — please rephrase)" : string.Join("  ", parts);
    }

    private static int? ExtractInt(string text, string label)
    {
        var idx = text.IndexOf(label, StringComparison.Ordinal);
        if (idx < 0) return null;
        var after = text.AsSpan(idx + label.Length);
        int start = 0;
        while (start < after.Length && !char.IsDigit(after[start])) start++;
        int end = start;
        while (end < after.Length && char.IsDigit(after[end])) end++;
        if (end == start) return null;
        return int.TryParse(after[start..end], out var n) ? n : null;
    }
}
