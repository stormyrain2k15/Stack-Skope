using System.Text.Json;
using StackScope.Core.Models;

namespace StackScope.Adapters.Architectures;

/// <summary>Gemma / Gemma-2 adapter.</summary>
public sealed class GemmaAdapter : IArchitectureAdapter
{
    public ArchitectureFamily Family => ArchitectureFamily.Gemma;

    public bool CanHandle(ArchitectureInput i) => Match(i, "gemma", "Gemma");

    public UnifiedModelDescriptor Normalize(ArchitectureInput input)
    {
        int nLayers = AdapterHelpers.Int(input.Config, "num_hidden_layers",
                        AdapterHelpers.IntFromGguf(input.GgufMetadata, "gemma.block_count"));
        int nHeads  = AdapterHelpers.Int(input.Config, "num_attention_heads",
                        AdapterHelpers.IntFromGguf(input.GgufMetadata, "gemma.attention.head_count"));
        int kvHeads = AdapterHelpers.Int(input.Config, "num_key_value_heads",
                        AdapterHelpers.IntFromGguf(input.GgufMetadata, "gemma.attention.head_count_kv", nHeads));
        int hidden  = AdapterHelpers.Int(input.Config, "hidden_size",
                        AdapterHelpers.IntFromGguf(input.GgufMetadata, "gemma.embedding_length"));
        int headDim = nHeads == 0 ? 0 : hidden / nHeads;

        var byLayer = LlamaAdapter.GroupByLayer(input.Tensors, "model.layers.");
        var graph = AdapterHelpers.BuildDecoderGraph(nLayers, nHeads, hidden, headDim,
            i => byLayer.TryGetValue(i, out var arr) ? arr : Array.Empty<string>());
        var mem = AdapterHelpers.EstimateForDecoderOnly(input.Tensors, nLayers, nHeads, hidden, kvHeads);

        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in input.Config) raw[kv.Key] = kv.Value.ToString();

        return new UnifiedModelDescriptor(
            input.ModelId, input.DisplayName, input.SourceKind, input.SourcePath,
            input.ContentHash, ArchitectureFamily.Gemma, "GemmaForCausalLM",
            graph, input.Tensors,
            AdapterHelpers.DominantQuant(input.Tensors),
            input.Tokenizer, mem, raw, input.Files);
    }

    internal static bool Match(ArchitectureInput i, string ggufArch, string prefix)
    {
        if (i.Config.TryGetValue("model_type", out var mt) &&
            mt.ValueKind == JsonValueKind.String &&
            string.Equals(mt.GetString(), ggufArch, StringComparison.OrdinalIgnoreCase))
            return true;
        if (i.Config.TryGetValue("architectures", out var archs) &&
            archs.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in archs.EnumerateArray())
                if ((a.GetString() ?? "").StartsWith(prefix, StringComparison.Ordinal))
                    return true;
        }
        if (i.GgufMetadata.TryGetValue("general.architecture", out var g) &&
            g is string gs && gs.Equals(ggufArch, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }
}

/// <summary>Qwen2 / Qwen2.5 adapter.</summary>
public sealed class Qwen2Adapter : IArchitectureAdapter
{
    public ArchitectureFamily Family => ArchitectureFamily.Qwen2;
    public bool CanHandle(ArchitectureInput i) => GemmaAdapter.Match(i, "qwen2", "Qwen2");

    public UnifiedModelDescriptor Normalize(ArchitectureInput input)
    {
        int nLayers = AdapterHelpers.Int(input.Config, "num_hidden_layers",
                        AdapterHelpers.IntFromGguf(input.GgufMetadata, "qwen2.block_count"));
        int nHeads  = AdapterHelpers.Int(input.Config, "num_attention_heads",
                        AdapterHelpers.IntFromGguf(input.GgufMetadata, "qwen2.attention.head_count"));
        int kvHeads = AdapterHelpers.Int(input.Config, "num_key_value_heads",
                        AdapterHelpers.IntFromGguf(input.GgufMetadata, "qwen2.attention.head_count_kv", nHeads));
        int hidden  = AdapterHelpers.Int(input.Config, "hidden_size",
                        AdapterHelpers.IntFromGguf(input.GgufMetadata, "qwen2.embedding_length"));
        int headDim = nHeads == 0 ? 0 : hidden / nHeads;

        var byLayer = LlamaAdapter.GroupByLayer(input.Tensors, "model.layers.");
        var graph = AdapterHelpers.BuildDecoderGraph(nLayers, nHeads, hidden, headDim,
            i => byLayer.TryGetValue(i, out var arr) ? arr : Array.Empty<string>());
        var mem = AdapterHelpers.EstimateForDecoderOnly(input.Tensors, nLayers, nHeads, hidden, kvHeads);

        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in input.Config) raw[kv.Key] = kv.Value.ToString();

        return new UnifiedModelDescriptor(
            input.ModelId, input.DisplayName, input.SourceKind, input.SourcePath,
            input.ContentHash, ArchitectureFamily.Qwen2, "Qwen2ForCausalLM",
            graph, input.Tensors,
            AdapterHelpers.DominantQuant(input.Tensors),
            input.Tokenizer, mem, raw, input.Files);
    }
}

/// <summary>Mistral / Mixtral adapter (MoE-aware to the extent the config exposes it).</summary>
public sealed class MistralAdapter : IArchitectureAdapter
{
    public ArchitectureFamily Family => ArchitectureFamily.Mistral;
    public bool CanHandle(ArchitectureInput i)
        => GemmaAdapter.Match(i, "mistral", "Mistral") ||
           GemmaAdapter.Match(i, "mixtral", "Mixtral");

    public UnifiedModelDescriptor Normalize(ArchitectureInput input)
    {
        int nLayers = AdapterHelpers.Int(input.Config, "num_hidden_layers",
                        AdapterHelpers.IntFromGguf(input.GgufMetadata, "mistral.block_count"));
        int nHeads  = AdapterHelpers.Int(input.Config, "num_attention_heads",
                        AdapterHelpers.IntFromGguf(input.GgufMetadata, "mistral.attention.head_count"));
        int kvHeads = AdapterHelpers.Int(input.Config, "num_key_value_heads",
                        AdapterHelpers.IntFromGguf(input.GgufMetadata, "mistral.attention.head_count_kv", nHeads));
        int hidden  = AdapterHelpers.Int(input.Config, "hidden_size",
                        AdapterHelpers.IntFromGguf(input.GgufMetadata, "mistral.embedding_length"));
        int headDim = nHeads == 0 ? 0 : hidden / nHeads;

        var byLayer = LlamaAdapter.GroupByLayer(input.Tensors, "model.layers.");
        var graph = AdapterHelpers.BuildDecoderGraph(nLayers, nHeads, hidden, headDim,
            i => byLayer.TryGetValue(i, out var arr) ? arr : Array.Empty<string>());
        var mem = AdapterHelpers.EstimateForDecoderOnly(input.Tensors, nLayers, nHeads, hidden, kvHeads);

        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in input.Config) raw[kv.Key] = kv.Value.ToString();

        string archName = AdapterHelpers.Int(input.Config, "num_local_experts", 0) > 0
            ? "MixtralForCausalLM"
            : "MistralForCausalLM";

        return new UnifiedModelDescriptor(
            input.ModelId, input.DisplayName, input.SourceKind, input.SourcePath,
            input.ContentHash, ArchitectureFamily.Mistral, archName,
            graph, input.Tensors,
            AdapterHelpers.DominantQuant(input.Tensors),
            input.Tokenizer, mem, raw, input.Files);
    }
}

/// <summary>GPT-2 family adapter (n_layer / n_head / n_embd naming).</summary>
public sealed class Gpt2Adapter : IArchitectureAdapter
{
    public ArchitectureFamily Family => ArchitectureFamily.Gpt2;

    public bool CanHandle(ArchitectureInput i)
    {
        if (i.Config.TryGetValue("model_type", out var mt) &&
            mt.ValueKind == JsonValueKind.String &&
            (mt.GetString()?.StartsWith("gpt2", StringComparison.OrdinalIgnoreCase) ?? false))
            return true;
        if (i.Config.TryGetValue("architectures", out var archs) &&
            archs.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in archs.EnumerateArray())
                if ((a.GetString() ?? "").Contains("GPT2", StringComparison.Ordinal))
                    return true;
        }
        return false;
    }

    public UnifiedModelDescriptor Normalize(ArchitectureInput input)
    {
        int nLayers = AdapterHelpers.Int(input.Config, "n_layer");
        int nHeads  = AdapterHelpers.Int(input.Config, "n_head");
        int hidden  = AdapterHelpers.Int(input.Config, "n_embd");
        int headDim = nHeads == 0 ? 0 : hidden / nHeads;

        var byLayer = LlamaAdapter.GroupByLayer(input.Tensors, "transformer.h.");
        var graph = AdapterHelpers.BuildDecoderGraph(nLayers, nHeads, hidden, headDim,
            i => byLayer.TryGetValue(i, out var arr) ? arr : Array.Empty<string>(),
            blockNameSpace: "transformer.h");
        var mem = AdapterHelpers.EstimateForDecoderOnly(input.Tensors, nLayers, nHeads, hidden, nHeads);

        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in input.Config) raw[kv.Key] = kv.Value.ToString();

        return new UnifiedModelDescriptor(
            input.ModelId, input.DisplayName, input.SourceKind, input.SourcePath,
            input.ContentHash, ArchitectureFamily.Gpt2, "GPT2LMHeadModel",
            graph, input.Tensors,
            AdapterHelpers.DominantQuant(input.Tensors),
            input.Tokenizer, mem, raw, input.Files);
    }
}
