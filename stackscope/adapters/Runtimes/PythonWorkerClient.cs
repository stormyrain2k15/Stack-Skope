using Grpc.Net.Client;
using StackScope.Core.Transactions;
using StackScope.Proto.V1;

namespace StackScope.Adapters.Runtimes;

/// <summary>
/// gRPC client for the Python inference worker (workers/inference_worker_py).
/// Wraps the generated <see cref="InferenceWorker.InferenceWorkerClient"/>
/// and translates protobuf events → domain <see cref="TransactionEvent"/>.
/// </summary>
public sealed class PythonWorkerClient : IRuntimeAdapter
{
    private GrpcChannel? _channel;
    private InferenceWorker.InferenceWorkerClient? _client;

    public string Kind => "pytorch";

    public async Task ConnectAsync(string endpoint, CancellationToken ct)
    {
        _channel = GrpcChannel.ForAddress(endpoint);
        _client = new InferenceWorker.InferenceWorkerClient(_channel);

        // Fail fast if the worker isn't answering.
        await _client.HeartbeatAsync(new HeartbeatRequest(), cancellationToken: ct);
    }

    private InferenceWorker.InferenceWorkerClient Client =>
        _client ?? throw new InvalidOperationException("PythonWorkerClient not connected.");

    public async Task<WorkerCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        var r = await Client.GetCapabilitiesAsync(new CapabilitiesRequest(), cancellationToken: ct);
        var details = r.DeviceInfo.Select(d => new DeviceInfo(
            d.Id, d.Kind, d.Name, d.TotalMemoryBytes, d.FreeMemoryBytes,
            d.ComputeCapability, d.DriverVersion, d.MultiProcessorCount,
            d.IsIntegrated, d.IsDefault)).ToArray();
        return new WorkerCapabilities(
            r.WorkerKind, r.Version,
            r.SupportedFormats.ToArray(),
            r.Devices.ToArray(),
            r.SupportsAttention, r.SupportsActivations, r.SupportsTensorReadback,
            details);
    }

    public async Task<LoadedModelInfo> LoadModelAsync(LoadModelArgs args, CancellationToken ct)
    {
        var reply = await Client.LoadModelAsync(new LoadModelRequest
        {
            ModelPath = args.ModelPath,
            Format    = args.Format,
            Device    = args.Device,
            TrustRemoteCode = args.TrustRemoteCode,
            NCtx      = args.NCtx
        }, cancellationToken: ct);
        return new LoadedModelInfo(
            reply.ModelHandle, reply.Architecture,
            reply.NLayers, reply.NHeads, reply.HiddenSize, reply.VocabSize,
            reply.ResolvedDevice ?? "",
            reply.ResolvedDeviceVerified);
    }

    public async Task UnloadModelAsync(string modelHandle, CancellationToken ct)
    {
        await Client.UnloadModelAsync(new UnloadModelRequest { ModelHandle = modelHandle },
            cancellationToken: ct);
    }

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
            CaptureLevel  = (Proto.V1.CaptureLevel)args.Level,
            AblateLayer   = args.AblateLayer,
            AblateHead    = args.AblateHead
        }, cancellationToken: ct);

        await foreach (var pe in call.ResponseStream.ReadAllAsync(ct))
            yield return Convert(pe);
    }

    public async Task<TensorReadback> ReadTensorAsync(
        string transactionId, ulong eventId, CancellationToken ct)
    {
        var r = await Client.ReadTensorAsync(new ReadTensorRequest
        {
            TransactionId = transactionId,
            EventId       = eventId
        }, cancellationToken: ct);
        return new TensorReadback(r.Data.ToByteArray(), r.Dtype, r.Shape.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.ShutdownAsync();
            _channel.Dispose();
        }
    }

    internal static TransactionEvent Convert(Proto.V1.Event pe)
    {
        var markers = new TraceMarker[pe.Markers.Count];
        for (int i = 0; i < pe.Markers.Count; i++)
        {
            var m = pe.Markers[i];
            markers[i] = new TraceMarker(
                m.Name, m.BeginNs, m.EndNs, m.ColorRgba,
                m.ThreadId, m.StreamId, m.CorrelationId);
        }
        return new TransactionEvent(
            EventId: pe.EventId,
            TransactionId: pe.TransactionId,
            TimestampNs: pe.TimestampNs,
            Kind: (Transactions.EventKind)(byte)pe.Kind,
            TokenIndex: pe.TokenIndex,
            LayerIndex: pe.LayerIndex,
            HeadIndex:  pe.HeadIndex,
            ThreadId:   pe.ThreadId,
            StreamId:   pe.StreamId,
            DeviceId:   pe.DeviceId,
            Payload:    pe.Payload.ToByteArray(),
            Markers:    markers);
    }
}
