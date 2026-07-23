using StackScope.Core.Transactions;

namespace StackScope.Adapters.Runtimes;

/// <summary>
/// Any external worker (PyTorch, llama.cpp, TF) is fronted by a gRPC
/// client that implements this interface. The coordinator only ever
/// talks to workers through this contract.
/// </summary>
public interface IRuntimeAdapter : IAsyncDisposable
{
    string Kind { get; }
    Task ConnectAsync(string endpoint, CancellationToken ct);
    Task<WorkerCapabilities> GetCapabilitiesAsync(CancellationToken ct);
    Task<LoadedModelInfo> LoadModelAsync(LoadModelArgs args, CancellationToken ct);
    Task UnloadModelAsync(string modelHandle, CancellationToken ct);
    IAsyncEnumerable<TransactionEvent> RunInferenceAsync(
        RunInferenceArgs args, CancellationToken ct);
    Task<TensorReadback> ReadTensorAsync(
        string transactionId, ulong eventId, CancellationToken ct);
}

public sealed record WorkerCapabilities(
    string WorkerKind,
    string Version,
    IReadOnlyList<string> SupportedFormats,
    IReadOnlyList<string> Devices,
    bool SupportsAttention,
    bool SupportsActivations,
    bool SupportsTensorReadback);

public sealed record LoadModelArgs(
    string ModelPath,
    string Format,
    string Device,
    bool TrustRemoteCode,
    int NCtx);

public sealed record LoadedModelInfo(
    string ModelHandle,
    string Architecture,
    int NLayers,
    int NHeads,
    int HiddenSize,
    int VocabSize);

public sealed record RunInferenceArgs(
    string TransactionId,
    string ModelHandle,
    string Prompt,
    int MaxNewTokens,
    float Temperature,
    float TopP,
    int TopK,
    ulong Seed,
    CaptureLevel Level);

public enum CaptureLevel { Simple = 0, Advanced = 1, Forensic = 2 }

public sealed record TensorReadback(
    byte[] Data,
    string DType,
    IReadOnlyList<long> Shape);
