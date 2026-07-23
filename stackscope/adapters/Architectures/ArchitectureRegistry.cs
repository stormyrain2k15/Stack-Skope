namespace StackScope.Adapters.Architectures;

/// <summary>
/// Ordered registry of architecture adapters. First match wins. Order
/// matters — put more specific ("Mixtral" vs "Mistral") first if the
/// distinction is meaningful. The current adapters happen to be
/// order-independent, but the registry preserves order to keep future
/// changes explicit.
/// </summary>
public sealed class ArchitectureRegistry
{
    private readonly List<IArchitectureAdapter> _adapters;

    public ArchitectureRegistry(IEnumerable<IArchitectureAdapter>? overrides = null)
    {
        _adapters = overrides?.ToList() ?? new List<IArchitectureAdapter>
        {
            new LlamaAdapter(),
            new GemmaAdapter(),
            new Qwen2Adapter(),
            new MistralAdapter(),
            new Gpt2Adapter()
        };
    }

    public IArchitectureAdapter? Resolve(ArchitectureInput input)
    {
        foreach (var a in _adapters)
            if (a.CanHandle(input)) return a;
        return null;
    }

    public IReadOnlyList<IArchitectureAdapter> All => _adapters;
}
