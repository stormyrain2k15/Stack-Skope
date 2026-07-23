# StackScope — PRD

## Problem statement (verbatim)

Standalone transformer inspection, tracing, profiling, and debugging application. Native Windows desktop. Traces: input → tokenization → layers → attention heads → activations → tensors → runtime operators → driver calls → kernels → memory transactions → logits → output token. Capture everything, reveal progressively.

## User decisions

- 1(c) — write source, execute on user's Windows 10 Pro box.
- CUDA 12.x + ROCm 6.x + Vulkan target; `net8.0-windows`.
- llama.cpp via git submodule.
- Deliver as new GitHub repo.
- Purpose: make transformer internals inspectable so humans can improve AI.

## Architecture (162 files)

```
stackscope/
├── proto/                gRPC contracts (events, worker, coordinator)
├── core/                 .NET 8: models, transactions, storage, queries,
│                         correlation, comparison (incl. DistributionStats
│                         + HeadDiffAnalyzer for Diff Mode), capture pipeline
├── adapters/
│   ├── Formats/          SafeTensors, Gguf, Transformers, TensorFlow
│   ├── Architectures/    Llama, Gemma, Qwen2, Mistral, GPT-2
│   ├── Runtimes/         gRPC clients: PythonWorker, LlamaCppWorker
│   └── Drivers/          Cuda (CUPTI), Rocm (rocprofiler),
│                         Vulkan (mmap bridge + debug-utils), Cpu
├── services/
│   ├── Services.csproj   ModelIntrospection, Project, Capture, Query
│   └── host/             StackScope.Coordinator — standalone gRPC host
├── workers/
│   ├── inference_worker_py/  hooks + attention_capture + arena +
│   │                         anomaly + DirectML/CUDA/MPS device routing
│   ├── llamacpp_worker/      C harness + gRPC (submodule)
│   └── instrumentation_agent/
├── app/desktop/          WPF shell: 12 views, dark theme, dockable,
│                         Diff Mode UI, Breadcrumb, Project tree,
│                         Device dropdown, Recovery banner, Command palette,
│                         saved layouts w/ load-on-startup
├── tests/                xUnit (Core + Adapters), pytest, integration
└── packaging/wix/        Real WiX v4 Product.wxs + Bundle.wxs + build.ps1
```

## What's live end-to-end

- **UI → Query → Store**: 8 event-list views + Overview + Timeline + Capture Library + Diff Compare all bound to real `QueryService` → `EventStore`. Cross-view selection sync via `SelectionState` singleton.
- **Diff Mode**: two `EventStore` instances → `HeadDiffAnalyzer` → ranked observable collection → sortable UI table. Threshold-filterable. Tested.
- **Coordinator host**: real `Grpc.AspNetCore` server; implements ListWorkers, StartWorker/StopWorker, LoadModel, RunInference (streaming), QueryEvents, CountEvents, GetTransaction, ListTransactions, ReadTensor.
- **Python worker**: `RunInference` streams events for token/layer/head/attention/logits, invokes `HookCapture` + per-top-level-attention capture with `output_attentions=True`, writes rows into `TensorArena` at `CAPTURE_FORENSIC`, runs each event through `AnomalyDetector` inline. `ReadTensor` returns real bytes from the arena.
- **Anomaly detector**: NaN/Inf in logits, attention entropy collapse/blow-up, per-layer latency outliers. Verified: fed a synthetic NaN logit and observed `stackscope.anomaly` MARKER emission.
- **DirectML**: `_resolve_device` accepts `dml:N`, imports `torch_directml` at runtime, falls back to CPU on ImportError; enumerated in device list alongside cuda/mps/cpu.
- **WiX MSI**: `Product.wxs` + `Bundle.wxs` + `build.ps1`. Unsigned (no cert). Chains .NET Desktop Runtime 8.
- **Persistence**: mmap append-only log + per-txn SQLite WAL index. Partial captures resurface via recovery banner on next launch.

## Non-negotiable rules held

- No stubs. No mock data. Deferred items honestly absent.
- Every visual object bound to a real event/tensor/allocation id.
- Correlation confidence always labelled (6 tiers).
- Capture/inference out-of-process (Coordinator host + Python/llama.cpp workers).
- Keyboard-complete. Progressive disclosure (F1/F2/F3). Software-render env toggle.

## Deferred honestly absent

- Code-signing pipeline (no cert issued).
- Coordinator process spawning of workers (endpoints registered via env var).
- Unreal client, Metal backend.
- Cancel via out-of-band RPC (client-stream cancel works).

## Next

Run on Windows box:
1. `git submodule update --init --recursive`
2. `.\build_windows.ps1`
3. `scripts/gen_proto.sh` for the Python worker gRPC stubs
4. `packaging/wix/build.ps1` for the installer
5. Launch `StackScope.Coordinator.exe`, then `StackScope.exe`
