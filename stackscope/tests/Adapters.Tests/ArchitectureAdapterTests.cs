using System.Text.Json;
using StackScope.Adapters.Architectures;
using StackScope.Core.Models;
using Xunit;

namespace StackScope.Adapters.Tests;

public class ArchitectureAdapterTests
{
    private static JsonElement E(object o)
        => JsonSerializer.SerializeToElement(o);

    [Fact]
    public void LlamaAdapter_Detects_And_Normalizes_ConfigJson()
    {
        var cfg = new Dictionary<string, JsonElement>
        {
            ["model_type"]           = E("llama"),
            ["architectures"]        = E(new[] { "LlamaForCausalLM" }),
            ["num_hidden_layers"]    = E(32),
            ["num_attention_heads"]  = E(32),
            ["num_key_value_heads"]  = E(8),
            ["hidden_size"]          = E(4096),
            ["vocab_size"]           = E(32000),
        };

        var input = new ArchitectureInput(
            "id-abc", "Llama-3-8B", "hf_repo", "/tmp/x", "hashhash",
            cfg,
            new Dictionary<string, object>(),
            Array.Empty<TensorInfo>(),
            null,
            new[] { "config.json" });

        var reg = new ArchitectureRegistry();
        var adapter = reg.Resolve(input);
        Assert.NotNull(adapter);
        Assert.Equal(ArchitectureFamily.Llama, adapter!.Family);

        var d = adapter.Normalize(input);
        Assert.Equal(ArchitectureFamily.Llama, d.Architecture);
        Assert.Equal(32, d.Layers.NumLayers);
        Assert.Equal(32, d.Layers.NumHeads);
        Assert.Equal(4096, d.Layers.HiddenSize);
        Assert.Equal(128, d.Layers.HeadDim);
    }

    [Fact]
    public void Registry_First_Match_Wins()
    {
        // A Qwen2 config should not be handled by the Llama adapter.
        var cfg = new Dictionary<string, JsonElement>
        {
            ["model_type"]           = E("qwen2"),
            ["architectures"]        = E(new[] { "Qwen2ForCausalLM" }),
            ["num_hidden_layers"]    = E(24),
            ["num_attention_heads"]  = E(14),
            ["hidden_size"]          = E(896),
            ["vocab_size"]           = E(151936),
        };

        var input = new ArchitectureInput(
            "id-q", "Qwen2", "hf_repo", "/tmp/q", "h",
            cfg, new Dictionary<string, object>(),
            Array.Empty<TensorInfo>(), null, Array.Empty<string>());
        var reg = new ArchitectureRegistry();
        var adapter = reg.Resolve(input);
        Assert.Equal(ArchitectureFamily.Qwen2, adapter?.Family);
    }

    [Fact]
    public void Gpt2Adapter_Uses_n_layer_And_n_head()
    {
        var cfg = new Dictionary<string, JsonElement>
        {
            ["model_type"] = E("gpt2"),
            ["architectures"] = E(new[] { "GPT2LMHeadModel" }),
            ["n_layer"] = E(12),
            ["n_head"] = E(12),
            ["n_embd"] = E(768),
            ["vocab_size"] = E(50257),
        };
        var input = new ArchitectureInput(
            "id-gpt", "gpt2", "hf_repo", "/tmp/gpt", "h",
            cfg, new Dictionary<string, object>(),
            Array.Empty<TensorInfo>(), null, Array.Empty<string>());
        var d = new ArchitectureRegistry().Resolve(input)!.Normalize(input);
        Assert.Equal(ArchitectureFamily.Gpt2, d.Architecture);
        Assert.Equal(12, d.Layers.NumLayers);
        Assert.Equal(12, d.Layers.NumHeads);
        Assert.Equal(768, d.Layers.HiddenSize);
        Assert.Equal(64, d.Layers.HeadDim);
    }
}
