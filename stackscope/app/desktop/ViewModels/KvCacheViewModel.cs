using System.Buffers.Binary;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StackScope.Core.Queries;
using StackScope.Core.Storage;
using StackScope.Core.Transactions;
using StackScope.Desktop.State;
using StackScope.Services;

namespace StackScope.Desktop.ViewModels;

public sealed partial class KvCacheViewModel : ObservableObject
{
    private readonly ProjectService _project;

    public ObservableCollection<LayerRow> LayerRows { get; } = new();
    [ObservableProperty] private long totalPeakBytes;
    [ObservableProperty] private int  layerCount;

    public KvCacheViewModel(ProjectService project)
    {
        _project = project;
        SelectionState.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SelectionState.Current.TransactionId))
                Refresh();
        };
    }

    [RelayCommand]
    public void Refresh()
    {
        LayerRows.Clear();
        TotalPeakBytes = 0;
        LayerCount = 0;

        var txid = SelectionState.Current.TransactionId
                   ?? WorkspaceState.Current.CurrentTransactionId;
        if (txid is null) return;

        using var store = new EventStore(txid, _project.CapturesDir);
        var qe = new QueryEngine(store);

        var perLayerLive  = new Dictionary<int, long>();
        var perLayerPeak  = new Dictionary<int, long>();
        var addressLayer  = new Dictionary<ulong, int>();

        foreach (var e in qe.Query(new EventQuery
        {
            Kinds = new[] { EventKind.Alloc, EventKind.Free },
            Limit = 1_000_000
        }))
        {
            if (e.Payload.Length < 16) continue;
            ulong addr = BinaryPrimitives.ReadUInt64LittleEndian(e.Payload.Span);
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(e.Payload.Span[8..]);
            int layer = e.LayerIndex >= 0 ? e.LayerIndex : 0;

            if (e.Kind == EventKind.Alloc)
            {
                addressLayer[addr] = layer;
                perLayerLive.TryGetValue(layer, out var cur);
                cur += (long)size;
                perLayerLive[layer] = cur;
                perLayerPeak.TryGetValue(layer, out var peak);
                if (cur > peak) perLayerPeak[layer] = cur;
            }
            else
            {
                if (!addressLayer.TryGetValue(addr, out var freedLayer)) freedLayer = layer;
                perLayerLive.TryGetValue(freedLayer, out var cur);
                cur -= (long)size;
                perLayerLive[freedLayer] = Math.Max(0, cur);
                addressLayer.Remove(addr);
            }
        }

        if (perLayerPeak.Count == 0) return;
        long maxPeak = perLayerPeak.Values.Max();
        TotalPeakBytes = perLayerPeak.Values.Sum();
        LayerCount = perLayerPeak.Count;

        foreach (var kv in perLayerPeak.OrderBy(k => k.Key))
        {
            double frac = maxPeak == 0 ? 0.0 : (double)kv.Value / maxPeak;
            LayerRows.Add(new LayerRow(
                Layer: kv.Key,
                LayerLabel: $"L{kv.Key}",
                PeakBytes: kv.Value,
                BarWidth: Math.Max(1, frac * 400.0),
                BytesLabel: $"{kv.Value:N0} B"));
        }
    }

    /// <summary>Click handler surface: called by the view when a layer
    /// bar is clicked. Updates SelectionState so all other views follow.</summary>
    public void PickLayer(LayerRow row)
    {
        SelectionState.Current.LayerIndex = row.Layer;
    }

    public sealed record LayerRow(
        int Layer, string LayerLabel, long PeakBytes,
        double BarWidth, string BytesLabel)
    {
        public string Tooltip =>
            $"Layer {Layer}\nPeak KV-cache: {PeakBytes:N0} bytes ({PeakBytes / 1024.0 / 1024.0:F2} MiB)";
    }
}
