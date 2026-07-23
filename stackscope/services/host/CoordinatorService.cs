using Grpc.Core;
using StackScope.Adapters.Runtimes;
using StackScope.Core.Queries;
using StackScope.Core.Storage;
using StackScope.Core.Transactions;
using StackScope.Proto.V1;
using StackScope.Services;

namespace StackScope.Coordinator;

/// <summary>
/// Real Coordinator gRPC service. Sits between the WPF UI (or any
/// other client) and the workers. Owns the ProjectService + QueryService
/// so the UI process never touches capture files or worker processes
/// directly. This is the process-isolation boundary promised by the
/// original plan §9.
/// </summary>
public sealed class CoordinatorService : Coordinator.CoordinatorBase
{
    private readonly ProjectService _project;
    private readonly QueryService _query;
    private readonly ILogger<CoordinatorService> _log;

    // Registered workers by id. In this pass the coordinator manages
    // pre-configured worker endpoints; process-spawning is on the
    // next pass but the gRPC surface is complete.
    private readonly Dictionary<string, IRuntimeAdapter> _workers = new();
    private readonly Dictionary<string, string> _workerEndpoints = new();
    private readonly Dictionary<string, string> _workerKinds = new();
    private readonly Dictionary<string, System.Diagnostics.Process> _spawnedProcs = new();
    private readonly WorkerLauncher _launcher;
    private readonly object _lock = new();

    public CoordinatorService(ProjectService project, QueryService query,
                              WorkerLauncher launcher,
                              ILogger<CoordinatorService> log)
    {
        _project = project; _query = query; _launcher = launcher; _log = log;
    }

    public override async Task<ListWorkersReply> ListWorkers(
        ListWorkersRequest request, ServerCallContext context)
    {
        var reply = new ListWorkersReply();
        List<KeyValuePair<string, IRuntimeAdapter>> snapshot;
        lock (_lock) snapshot = _workers.ToList();

        foreach (var (id, adapter) in snapshot)
        {
            bool healthy = false;
            try
            {
                await adapter.GetCapabilitiesAsync(context.CancellationToken);
                healthy = true;
            }
            catch { healthy = false; }

            reply.Workers.Add(new WorkerInfo
            {
                WorkerId = id,
                Kind = _workerKinds.GetValueOrDefault(id, "unknown"),
                Endpoint = _workerEndpoints.GetValueOrDefault(id, ""),
                Pid = 0,
                Healthy = healthy,
            });
        }
        return reply;
    }

    public override async Task<StartWorkerReply> StartWorker(
        StartWorkerRequest request, ServerCallContext context)
    {
        var envKey = $"STACKSCOPE_{request.Kind.ToUpperInvariant()}_ENDPOINT";
        var endpoint = Environment.GetEnvironmentVariable(envKey);
        System.Diagnostics.Process? spawned = null;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            var res = await _launcher.SpawnAsync(request.Kind, context.CancellationToken);
            endpoint = res.Endpoint;
            spawned = res.Process;
        }

        IRuntimeAdapter adapter = request.Kind.ToLowerInvariant() switch
        {
            "pytorch"  => new PythonWorkerClient(),
            "llamacpp" => new LlamaCppWorkerClient(),
            _ => throw new RpcException(new Status(StatusCode.Unimplemented,
                                        $"Unknown worker kind: {request.Kind}"))
        };
        await adapter.ConnectAsync("http://" + endpoint, context.CancellationToken);

        var id = $"{request.Kind}-{Guid.NewGuid():N}"[..12];
        lock (_lock)
        {
            _workers[id] = adapter;
            _workerEndpoints[id] = endpoint;
            _workerKinds[id] = request.Kind;
            if (spawned is not null) _spawnedProcs[id] = spawned;
        }
        return new StartWorkerReply { Worker = new WorkerInfo
        {
            WorkerId = id, Kind = request.Kind, Endpoint = endpoint,
            Pid = spawned?.Id ?? 0, Healthy = true,
        }};
    }

    public override async Task<StopWorkerReply> StopWorker(
        StopWorkerRequest request, ServerCallContext context)
    {
        IRuntimeAdapter? adapter;
        System.Diagnostics.Process? proc;
        lock (_lock)
        {
            _workers.Remove(request.WorkerId, out adapter);
            _workerEndpoints.Remove(request.WorkerId);
            _workerKinds.Remove(request.WorkerId);
            _spawnedProcs.Remove(request.WorkerId, out proc);
        }
        if (adapter is not null) await adapter.DisposeAsync();
        if (proc is not null)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        }
        return new StopWorkerReply();
    }

    public override async Task<ListDevicesReply> ListDevices(
        ListDevicesRequest request, ServerCallContext context)
    {
        var adapter = ResolveWorker(request.WorkerId);
        var caps = await adapter.GetCapabilitiesAsync(context.CancellationToken);
        var reply = new ListDevicesReply();
        foreach (var d in caps.DeviceDetails)
        {
            reply.Devices.Add(new DeviceInfo
            {
                Id = d.Id, Kind = d.Kind, Name = d.Name,
                TotalMemoryBytes = d.TotalMemoryBytes,
                FreeMemoryBytes = d.FreeMemoryBytes,
                ComputeCapability = d.ComputeCapability,
                DriverVersion = d.DriverVersion,
                MultiProcessorCount = d.MultiProcessorCount,
                IsIntegrated = d.IsIntegrated,
                IsDefault = d.IsDefault,
            });
        }
        return reply;
    }


    public override async Task<CoordLoadModelReply> LoadModel(
        CoordLoadModelRequest request, ServerCallContext context)
    {
        var adapter = ResolveWorker(request.WorkerId);
        var info = await adapter.LoadModelAsync(new LoadModelArgs(
            request.ModelPath, request.Format, request.Device,
            request.TrustRemoteCode, request.NCtx), context.CancellationToken);

        return new CoordLoadModelReply
        {
            ModelHandle = info.ModelHandle,
            Architecture = info.Architecture,
            NLayers = info.NLayers,
            NHeads = info.NHeads,
            HiddenSize = info.HiddenSize,
            VocabSize = info.VocabSize,
            TransactionScopeId = Ulid.NewUlid(),
            ResolvedDevice = info.ResolvedDevice ?? "",
            ResolvedDeviceVerified = info.ResolvedDeviceVerified,
        };
    }

    public override async Task RunInference(
        CoordRunRequest request, IServerStreamWriter<RunProgress> responseStream,
        ServerCallContext context)
    {
        var adapter = ResolveWorker(request.WorkerId);
        var txid = Ulid.NewUlid();
        using var store = new EventStore(txid, _project.CapturesDir);
        store.Index.SetMeta("transaction_id", txid);
        store.Index.SetMeta("model_handle", request.ModelHandle);
        store.Index.SetMeta("started_ns",
            System.Diagnostics.Stopwatch.GetTimestamp().ToString());
        store.Index.SetMeta("completed", "false");

        var args = new RunInferenceArgs(
            txid, request.ModelHandle, request.Prompt,
            request.MaxNewTokens, request.Temperature, request.TopP,
            request.TopK, request.Seed,
            (CaptureLevel)(int)request.CaptureLevel,
            request.AblateLayer, request.AblateHead);

        ulong count = 0;
        int tokens = 0;
        string? err = null;
        try
        {
            await foreach (var e in adapter.RunInferenceAsync(args, context.CancellationToken))
            {
                store.Append(e);
                count++;
                if (e.Kind == EventKind.TokenEnd) tokens++;
                if ((count % 128) == 0)
                {
                    await responseStream.WriteAsync(new RunProgress
                    {
                        TransactionId = txid,
                        EventsCommitted = count,
                        TokensEmitted = tokens,
                        Finished = false,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            err = ex.Message;
            _log.LogError(ex, "RunInference failed for txn {Txid}", txid);
        }
        finally
        {
            store.Flush();
            store.Index.SetMeta("completed", err is null ? "true" : "false");
            if (err is not null) store.Index.SetMeta("error", err);

            await responseStream.WriteAsync(new RunProgress
            {
                TransactionId = txid,
                EventsCommitted = count,
                TokensEmitted = tokens,
                Finished = true,
                Error = err ?? "",
            });
        }
    }

    public override Task<CancelReply> CancelInference(
        CancelRequest request, ServerCallContext context)
    {
        // Streaming cancellation happens via context.CancellationToken
        // from the client side. This RPC exists so a *different* client
        // can request cancellation — not implemented yet; declared here
        // to keep the surface truthful.
        throw new RpcException(new Status(StatusCode.Unimplemented,
            "Out-of-band cancel is not implemented; cancel the RunInference stream directly."));
    }

    public override async Task QueryEvents(
        QueryEventsRequest request, IServerStreamWriter<Event> responseStream,
        ServerCallContext context)
    {
        var q = BuildQuery(request);
        var results = _query.Query(request.TransactionId, q);
        foreach (var e in results)
        {
            var pe = ToProto(e);
            await responseStream.WriteAsync(pe);
            if (context.CancellationToken.IsCancellationRequested) break;
        }
    }

    public override Task<CountReply> CountEvents(
        QueryEventsRequest request, ServerCallContext context)
    {
        var q = BuildQuery(request);
        return Task.FromResult(new CountReply
        {
            Count = (ulong)_query.Count(request.TransactionId, q)
        });
    }

    public override Task<TransactionSummary> GetTransaction(
        GetTransactionRequest request, ServerCallContext context)
    {
        var meta = _project.ListTransactions()
            .FirstOrDefault(t => t.TransactionId == request.TransactionId);
        if (meta is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound,
                $"Transaction not found: {request.TransactionId}"));
        }
        var count = _query.Count(request.TransactionId, new EventQuery());
        return Task.FromResult(new TransactionSummary
        {
            TransactionId = meta.TransactionId,
            ModelHandle = meta.ModelHandle,
            Architecture = meta.Architecture,
            StartedNs = meta.StartedNs,
            EndedNs = meta.EndedNs,
            EventCount = (ulong)count,
            Completed = meta.Completed,
            Error = meta.Error ?? "",
        });
    }

    public override Task<ListTransactionsReply> ListTransactions(
        ListTransactionsRequest request, ServerCallContext context)
    {
        var reply = new ListTransactionsReply();
        foreach (var t in _project.ListTransactions())
        {
            reply.Transactions.Add(new TransactionSummary
            {
                TransactionId = t.TransactionId,
                ModelHandle = t.ModelHandle,
                Architecture = t.Architecture,
                StartedNs = t.StartedNs,
                EndedNs = t.EndedNs,
                Completed = t.Completed,
                Error = t.Error ?? "",
            });
        }
        return Task.FromResult(reply);
    }

    public override async Task<ReadTensorReply> ReadTensor(
        CoordReadTensorRequest request, ServerCallContext context)
    {
        // Forward tensor readback to whichever worker owns the arena.
        // We iterate registered workers and return the first successful
        // one — arena ownership is 1:1 with (worker, txn) in this pass.
        List<IRuntimeAdapter> snapshot;
        lock (_lock) snapshot = _workers.Values.ToList();
        foreach (var w in snapshot)
        {
            try
            {
                var t = await w.ReadTensorAsync(
                    request.TransactionId, request.EventId, context.CancellationToken);
                var reply = new ReadTensorReply { Data = Google.Protobuf.ByteString.CopyFrom(t.Data), Dtype = t.DType };
                foreach (var d in t.Shape) reply.Shape.Add(d);
                return reply;
            }
            catch { /* try next worker */ }
        }
        throw new RpcException(new Status(StatusCode.NotFound,
            "No worker has an arena entry for that (transaction, event)."));
    }

    // ---- helpers ---------------------------------------------------------

    private IRuntimeAdapter ResolveWorker(string workerId)
    {
        lock (_lock)
        {
            if (_workers.TryGetValue(workerId, out var w)) return w;
        }
        throw new RpcException(new Status(StatusCode.NotFound,
            $"Unknown worker id: {workerId}"));
    }

    private static EventQuery BuildQuery(QueryEventsRequest r)
        => new()
        {
            Kinds = r.Kinds.Select(k => (EventKind)(byte)k).ToArray(),
            TokenIndex = new IntRange(r.TokenIndexFrom, r.TokenIndexTo),
            LayerIndex = new IntRange(r.LayerIndexFrom, r.LayerIndexTo),
            HeadIndex  = new IntRange(r.HeadIndexFrom,  r.HeadIndexTo),
            TimeFromNs = r.TimeFromNs,
            TimeToNs   = r.TimeToNs > 0 ? r.TimeToNs : long.MaxValue,
            Offset     = (long)r.Offset,
            Limit      = r.Limit > 0 ? (int)r.Limit : 512,
        };

    private static Event ToProto(TransactionEvent e)
    {
        var m = new Event
        {
            EventId = e.EventId,
            TransactionId = e.TransactionId,
            TimestampNs = e.TimestampNs,
            Kind = (Proto.V1.EventKind)(byte)e.Kind,
            TokenIndex = e.TokenIndex,
            LayerIndex = e.LayerIndex,
            HeadIndex  = e.HeadIndex,
            ThreadId   = e.ThreadId,
            StreamId   = e.StreamId,
            DeviceId   = e.DeviceId,
            Payload    = Google.Protobuf.ByteString.CopyFrom(e.Payload.ToArray()),
        };
        foreach (var mk in e.Markers)
        {
            m.Markers.Add(new TraceMarker
            {
                Name = mk.Name,
                BeginNs = mk.BeginNs, EndNs = mk.EndNs,
                ColorRgba = mk.ColorRgba,
                ThreadId  = mk.ThreadId, StreamId  = mk.StreamId,
                CorrelationId = mk.CorrelationId,
            });
        }
        return m;
    }
}
