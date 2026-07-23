using System.Text.Json;
using StackScope.Core.Models;

namespace StackScope.Adapters.Architectures;

/// <summary>
/// Input to an architecture adapter. All the raw parsed material an
/// adapter needs to normalise a model into a <see cref="UnifiedModelDescriptor"/>.
/// </summary>
public sealed record ArchitectureInput(
    string ModelId,
    string DisplayName,
    string SourceKind,
    string SourcePath,
    string ContentHash,
    IReadOnlyDictionary<string, JsonElement> Config,     // hf config.json flattened
    IReadOnlyDictionary<string, object> GgufMetadata,    // gguf KV, if any
    IReadOnlyList<TensorInfo> Tensors,
    TokenizerInfo? Tokenizer,
    IReadOnlyList<string> Files);

/// <summary>
/// Contract for an architecture adapter.
/// </summary>
public interface IArchitectureAdapter
{
    ArchitectureFamily Family { get; }
    bool CanHandle(ArchitectureInput input);
    UnifiedModelDescriptor Normalize(ArchitectureInput input);
}
