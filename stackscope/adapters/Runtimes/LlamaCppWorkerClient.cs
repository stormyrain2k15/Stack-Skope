using Grpc.Net.Client;
using StackScope.Core.Transactions;
using StackScope.Proto.V1;

namespace StackScope.Adapters.Runtimes;

/// <summary>
/// gRPC client for the native llama.cpp worker (workers/llamacpp_worker).
/// The wire protocol is identical to the Python worker's — both implement
/// <see cref="InferenceWorker"/>. Only the process on the other end changes.
/// </summary>
public sealed class LlamaCppWorkerClient : IRuntimeAdapter
{
    private GrpcChannel? _channel;
    private InferenceWorker.InferenceWorkerClient? _client;

    public string Kind => "llamacpp";

    public async Task ConnectAsync(string endpoint, CancellationToken ct)
    {
        _channel = GrpcChannel.ForAddress(endpoint);
        _client = new InferenceWorker.InferenceWorkerClient(_channel);
        await _client.HeartbeatAsync(new HeartbeatRequest(), cancellationToken: ct);
    }

    private InferenceWorker.InferenceWorkerClient Client =>
        _client ?? throw new InvalidOperationException("LlamaCppWorkerClient not connected.");

    public async Task<WorkerCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        var r = await Client.GetCapabilitiesAsync(new CapabilitiesRequest(), cancellationToken: ct);
        return new WorkerCapabilities(r.WorkerKind, r.Version,
            r.SupportedFormats.ToArray(), r.Devices.ToArray(),
            r.SupportsAttention, r.SupportsActivations, r.SupportsTensorReadback);
    }

    public async Task<LoadedModelInfo> LoadModelAsync(LoadModelArgs args, CancellationToken ct)
    {
        var reply = await Client.LoadModelAsync(new LoadModelRequest
        {
            ModelPath = args.ModelPath,
            Format    = args.Format,
            Device    = args.Device,
            NCtx      = args.NCtx
        }, cancellationToken: ct);
        return new LoadedModelInfo(reply.ModelHandle, reply.Architecture,
            reply.NLayers, reply.NHeads, reply.HiddenSize, reply.VocabSize);
    }

    public async Task UnloadModelAsync(string modelHandle, CancellationToken ct)
        => await Client.UnloadModelAsync(new UnloadModelRequest { ModelHandle = modelHandle },
                cancellationToken: ct);

    public async IAsyncEnumerable<TransactionEvent> RunInferenceAsync(
        RunInferenceArgs args,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var call = Client.RunInference(new RunInferenceRequest
        {
            TransactionId = args.TransactionId,
            ModelHandle   = args.ModelHandle,
            Prompt        = args.Prompt,
            MaxNewTokens  = args.MaxNewTokens,
            Temperature   = args.Temperature,
            TopP          = args.TopP,
            TopK          = args.TopK,
            Seed          = args.Seed,
            CaptureLevel  = (Proto.V1.CaptureLevel)args.Level
        }, cancellationToken: ct);
        await foreach (var pe in call.ResponseStream.ReadAllAsync(ct))
            yield return PythonWorkerClient.Convert(pe);
    }

    public async Task<TensorReadback> ReadTensorAsync(
        string transactionId, ulong eventId, CancellationToken ct)
    {
        var r = await Client.ReadTensorAsync(new ReadTensorRequest
        {
            TransactionId = transactionId, EventId = eventId
        }, cancellationToken: ct);
        return new TensorReadback(r.Data.ToByteArray(), r.Dtype, r.Shape.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null) { await _channel.ShutdownAsync(); _channel.Dispose(); }
    }
}
