namespace StackScope.Core.Models;

/// <summary>
/// A node in the model's layer graph. Nodes are hierarchical:
/// <c>model → layers[N] → self_attn → q_proj</c> etc.
/// </summary>
public sealed class LayerNode
{
    public required string Path { get; init; }         // "model.layers.0.self_attn.q_proj"
    public required string Name { get; init; }         // "q_proj"
    public required string ModuleType { get; init; }   // "Linear", "LlamaAttention", ...
    public int Depth { get; init; }
    public int? LayerIndex { get; init; }              // if this node is a transformer block
    public int? HeadCount { get; init; }
    public int? HiddenSize { get; init; }
    public IReadOnlyList<LayerNode> Children { get; init; } = Array.Empty<LayerNode>();
    public IReadOnlyList<string> TensorNames { get; init; } = Array.Empty<string>();

    public IEnumerable<LayerNode> Walk()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.Walk())
                yield return d;
    }
}

/// <summary>
/// The whole layer tree for a model, with fast per-layer/per-head lookup.
/// </summary>
public sealed class LayerGraph
{
    public required LayerNode Root { get; init; }
    public required int NumLayers { get; init; }
    public required int NumHeads { get; init; }
    public required int HiddenSize { get; init; }
    public required int HeadDim { get; init; }

    private readonly Lazy<Dictionary<string, LayerNode>> _byPath;
    private readonly Lazy<Dictionary<int, LayerNode>> _byLayerIndex;

    public LayerGraph()
    {
        _byPath = new(BuildByPath);
        _byLayerIndex = new(BuildByLayerIndex);
    }

    public LayerNode? FindByPath(string path)
        => _byPath.Value.TryGetValue(path, out var n) ? n : null;

    public LayerNode? FindByLayerIndex(int layerIndex)
        => _byLayerIndex.Value.TryGetValue(layerIndex, out var n) ? n : null;

    private Dictionary<string, LayerNode> BuildByPath()
    {
        var d = new Dictionary<string, LayerNode>(StringComparer.Ordinal);
        foreach (var n in Root.Walk()) d[n.Path] = n;
        return d;
    }

    private Dictionary<int, LayerNode> BuildByLayerIndex()
    {
        var d = new Dictionary<int, LayerNode>();
        foreach (var n in Root.Walk())
            if (n.LayerIndex is int li) d[li] = n;
        return d;
    }
}
