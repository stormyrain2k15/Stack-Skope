using System.Text.Json;
using StackScope.Core.Models;

namespace StackScope.Adapters.Architectures;

/// <summary>Llama / Llama-2 / Llama-3 / CodeLlama family adapter.</summary>
public sealed class LlamaAdapter : IArchitectureAdapter
{
    public ArchitectureFamily Family => ArchitectureFamily.Llama;

    public bool CanHandle(ArchitectureInput input)
    {
        if (input.Config.TryGetValue("model_type", out var mt) &&
            mt.ValueKind == JsonValueKind.String)
        {
            var s = mt.GetString();
            if (s == "llama" || s == "codellama") return true;
        }
        if (input.Config.TryGetValue("architectures", out var archs) &&
            archs.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in archs.EnumerateArray())
            {
                var s = a.GetString() ?? "";
                if (s.StartsWith("Llama", StringComparison.Ordinal) ||
                    s.StartsWith("CodeLlama", StringComparison.Ordinal))
                    return true;
            }
        }
        if (input.GgufMetadata.TryGetValue("general.architecture", out var g) &&
            g is string gs && gs.Equals("llama", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public UnifiedModelDescriptor Normalize(ArchitectureInput input)
    {
        int nLayers   = AdapterHelpers.Int(input.Config, "num_hidden_layers",
                          AdapterHelpers.IntFromGguf(input.GgufMetadata, "llama.block_count"));
        int nHeads    = AdapterHelpers.Int(input.Config, "num_attention_heads",
                          AdapterHelpers.IntFromGguf(input.GgufMetadata, "llama.attention.head_count"));
        int kvHeads   = AdapterHelpers.Int(input.Config, "num_key_value_heads",
                          AdapterHelpers.IntFromGguf(input.GgufMetadata, "llama.attention.head_count_kv", nHeads));
        int hidden    = AdapterHelpers.Int(input.Config, "hidden_size",
                          AdapterHelpers.IntFromGguf(input.GgufMetadata, "llama.embedding_length"));
        int headDim   = nHeads == 0 ? 0 : hidden / nHeads;

        var byLayer = GroupByLayer(input.Tensors, "model.layers.");

        var graph = AdapterHelpers.BuildDecoderGraph(
            nLayers, nHeads, hidden, headDim,
            i => byLayer.TryGetValue(i, out var arr) ? arr : Array.Empty<string>());

        var mem = AdapterHelpers.EstimateForDecoderOnly(
            input.Tensors, nLayers, nHeads, hidden, kvHeads);

        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in input.Config) raw[kv.Key] = kv.Value.ToString();

        return new UnifiedModelDescriptor(
            ModelId: input.ModelId,
            DisplayName: input.DisplayName,
            SourceKind: input.SourceKind,
            SourcePath: input.SourcePath,
            ContentHashSha256: input.ContentHash,
            Architecture: ArchitectureFamily.Llama,
            ArchitectureName: "LlamaForCausalLM",
            Layers: graph,
            Tensors: input.Tensors,
            DominantQuantization: AdapterHelpers.DominantQuant(input.Tensors),
            Tokenizer: input.Tokenizer,
            MemoryEstimate: mem,
            RawConfig: raw,
            Files: input.Files);
    }

    internal static Dictionary<int, string[]> GroupByLayer(
        IReadOnlyList<TensorInfo> tensors, string prefix)
    {
        var by = new Dictionary<int, List<string>>();
        foreach (var t in tensors)
        {
            if (!t.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = t.Name.AsSpan(prefix.Length);
            int dot = rest.IndexOf('.');
            if (dot <= 0) continue;
            if (!int.TryParse(rest[..dot], out int idx)) continue;
            if (!by.TryGetValue(idx, out var list)) by[idx] = list = new();
            list.Add(t.Name);
        }
        return by.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }
}
