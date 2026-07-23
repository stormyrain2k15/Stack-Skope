namespace StackScope.Core.Models;

/// <summary>
/// The canonical model description used across StackScope. Every adapter
/// (safetensors, gguf, transformers, savedmodel) normalises into this.
/// Immutable.
/// </summary>
public sealed record UnifiedModelDescriptor(
    string ModelId,                     // stable identity (repo id or path hash)
    string DisplayName,
    string SourceKind,                  // "safetensors" | "gguf" | "hf_repo" | "savedmodel"
    string SourcePath,
    string ContentHashSha256,           // hash of the primary weight file(s)
    ArchitectureFamily Architecture,
    string ArchitectureName,            // raw arch string, e.g. "LlamaForCausalLM"
    LayerGraph Layers,
    IReadOnlyList<TensorInfo> Tensors,
    QuantizationInfo DominantQuantization,
    TokenizerInfo? Tokenizer,
    MemoryEstimate MemoryEstimate,
    IReadOnlyDictionary<string, string> RawConfig,
    IReadOnlyList<string> Files);
