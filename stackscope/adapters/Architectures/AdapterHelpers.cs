using System.Text.Json;
using StackScope.Core.Models;

namespace StackScope.Adapters.Architectures;

/// <summary>
/// Small helpers shared by every architecture adapter for pulling shapes
/// and building the transformer block graph.
/// </summary>
internal static class AdapterHelpers
{
    public static int Int(IReadOnlyDictionary<string, JsonElement> cfg,
                          string key, int fallback = 0)
    {
        if (!cfg.TryGetValue(key, out var v)) return fallback;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i : fallback;
    }

    public static int IntFromGguf(IReadOnlyDictionary<string, object> md, string key,
                                  int fallback = 0)
    {
        if (!md.TryGetValue(key, out var v)) return fallback;
        try { return Convert.ToInt32(v); } catch { return fallback; }
    }

    public static QuantizationInfo DominantQuant(IReadOnlyList<TensorInfo> tensors)
    {
        // Prefer the most common non-None quantization. If everything is
        // fp32/fp16/bf16, prefer whichever appears most.
        var counts = new Dictionary<QuantizationScheme, int>();
        foreach (var t in tensors)
        {
            counts.TryGetValue(t.Quantization.Scheme, out var c);
            counts[t.Quantization.Scheme] = c + 1;
        }
        if (counts.Count == 0) return QuantizationInfo.Fp32;
        QuantizationScheme best = QuantizationScheme.None;
        int bestCount = -1;
        foreach (var (s, c) in counts)
        {
            if (s == QuantizationScheme.None) continue;
            if (c > bestCount) { best = s; bestCount = c; }
        }
        return best switch
        {
            QuantizationScheme.F32 => QuantizationInfo.Fp32,
            QuantizationScheme.F16 => QuantizationInfo.Fp16,
            QuantizationScheme.BF16 => QuantizationInfo.Bf16,
            _ => tensors.First(t => t.Quantization.Scheme == best).Quantization
        };
    }

    public static long BytesFromQuant(long elements, QuantizationInfo q)
        => q.BitsPerElement == 0 ? 0 : (elements * q.BitsPerElement + 7) / 8;

    public static MemoryEstimate EstimateForDecoderOnly(
        IReadOnlyList<TensorInfo> tensors,
        int nLayers, int nHeads, int hidden, int kvHeadCount)
    {
        long weights = 0;
        foreach (var t in tensors)
            weights += BytesFromQuant(t.ElementCount, t.Quantization);

        int headDim = nHeads == 0 ? 0 : hidden / nHeads;
        long kvPerTok = kvHeadCount > 0
            ? 2L * kvHeadCount * headDim * nLayers * 2     // K+V, fp16
            : 2L * nHeads * headDim * nLayers * 2;
        long actsPerTok = 4L * hidden * 2;
        long overhead = 128 * 1024 * 1024;

        return new MemoryEstimate(weights, kvPerTok, actsPerTok, overhead);
    }

    public static LayerGraph BuildDecoderGraph(
        int nLayers, int nHeads, int hidden, int headDim,
        Func<int, string[]> perLayerTensorNames,
        string blockNameSpace = "model.layers")
    {
        var blocks = new List<LayerNode>(nLayers);
        for (int i = 0; i < nLayers; i++)
        {
            string basePath = $"{blockNameSpace}.{i}";
            var attn = new LayerNode
            {
                Path = $"{basePath}.self_attn",
                Name = "self_attn",
                ModuleType = "SelfAttention",
                Depth = 2,
                HeadCount = nHeads,
                HiddenSize = hidden,
                Children = new List<LayerNode>
                {
                    new() { Path = $"{basePath}.self_attn.q_proj", Name="q_proj", ModuleType="Linear", Depth=3 },
                    new() { Path = $"{basePath}.self_attn.k_proj", Name="k_proj", ModuleType="Linear", Depth=3 },
                    new() { Path = $"{basePath}.self_attn.v_proj", Name="v_proj", ModuleType="Linear", Depth=3 },
                    new() { Path = $"{basePath}.self_attn.o_proj", Name="o_proj", ModuleType="Linear", Depth=3 },
                }
            };
            var mlp = new LayerNode
            {
                Path = $"{basePath}.mlp",
                Name = "mlp",
                ModuleType = "MLP",
                Depth = 2,
                HiddenSize = hidden,
                Children = new List<LayerNode>
                {
                    new() { Path = $"{basePath}.mlp.gate_proj", Name="gate_proj", ModuleType="Linear", Depth=3 },
                    new() { Path = $"{basePath}.mlp.up_proj",   Name="up_proj",   ModuleType="Linear", Depth=3 },
                    new() { Path = $"{basePath}.mlp.down_proj", Name="down_proj", ModuleType="Linear", Depth=3 },
                }
            };
            blocks.Add(new LayerNode
            {
                Path = basePath, Name = $"layers.{i}",
                ModuleType = "TransformerBlock",
                Depth = 1, LayerIndex = i,
                HeadCount = nHeads, HiddenSize = hidden,
                TensorNames = perLayerTensorNames(i),
                Children = new List<LayerNode> { attn, mlp }
            });
        }

        var root = new LayerNode
        {
            Path = "model",
            Name = "model",
            ModuleType = "TransformerModel",
            Depth = 0,
            HeadCount = nHeads,
            HiddenSize = hidden,
            Children = blocks
        };
        return new LayerGraph
        {
            Root = root,
            NumLayers = nLayers,
            NumHeads = nHeads,
            HiddenSize = hidden,
            HeadDim = headDim
        };
    }
}
