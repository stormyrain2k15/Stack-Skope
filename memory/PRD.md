# StackScope — PRD

## Original problem statement (verbatim)

Standalone transformer inspection, tracing, profiling, and debugging application. Native Windows desktop.

Input → tokenization → layers → attention heads → activations → tensors → runtime operators → driver calls → kernels → memory transactions → logits → output token. Principle: capture everything, reveal progressively.

## User decisions (this pass)

- Environment fit: **1(c)** — write source tree only, execute on the user's Windows 10 Pro box.
- Scope for this pass: **2(c)** — main agent chose the subset (see "In scope").
- CUDA/CUPTI target: **CUDA 12.x + ROCm 6.x + Vulkan** on Windows 10 Pro, `net8.0-windows` TFM.
- llama.cpp: **4(a)** — git submodule + build script; not built here.
- Delivery: brand-new GitHub repo pushed via "Save to GitHub".

## Architecture (delivered)

```
stackscope/
├── proto/                     gRPC contracts (events, worker, coordinator)
├── core/                      .NET 8 lib (Linux-testable)
│   ├── Models/                UnifiedModelDescriptor, LayerGraph, TensorInfo, ...
│   ├── Transactions/          InferenceTransaction, EventKind, TraceMarker, Ulid
│   ├── Storage/               MmapEventLog, SqliteIndex, EventStore, EventRecord
│   ├── Queries/               EventQuery + QueryEngine
│   ├── Correlation/           CorrelationEngine + CorrelationConfidence
│   ├── Capture/               ICaptureBackend + CapturePipeline
│   └── Comparison/            TransactionComparer
├── adapters/
│   ├── Formats/{SafeTensors,Gguf,Transformers,TensorFlow}   real parsers, no stubs
│   ├── Architectures/         Llama, Gemma, Qwen2, Mistral, GPT-2 adapters
│   ├── Runtimes/              gRPC clients for PyTorch + llama.cpp workers
│   └── Drivers/{Cuda,Rocm,Vulkan,Cpu}
│                              CUPTI, rocprofiler, Vulkan bridge, CPU counters
├── services/                  ModelIntrospection, Project, Capture, Query
├── workers/
│   ├── inference_worker_py/   Python 3.11 + Transformers + PyTorch hooks + gRPC
│   └── llamacpp_worker/       C/C++ harness + CMake, llama.cpp submodule
├── app/desktop/               WPF (net8.0-windows) shell, 12 real views, dark theme
├── tests/{Core.Tests, Adapters.Tests, python_worker_tests, integration}
└── packaging/wix/             (scaffold README — MSI pipeline deferred honestly)
```

## In scope this pass (real, no stubs)

- **Format adapters**: SafeTensors (header JSON + tensor offset/shape/dtype +
  quantization + SHA-256 + validation), GGUF (v2/v3, all Q4/Q5/Q6/Q8/IQ block
  layouts + KV metadata parse), Transformers repo (config.json + tokenizer.json
  + shard index), TF SavedModel (real proto wire-format reader).
- **UnifiedModelDescriptor** + 5 architecture adapters (Llama, Gemma, Qwen2,
  Mistral/Mixtral, GPT-2), first-match registry.
- **Event store**: memory-mapped append-only log + per-transaction SQLite
  index (WAL) with axis indexes on kind/token/layer/head/stream/thread/time.
- **QueryEngine** with paging + typed filters + count.
- **CorrelationEngine** with 6-level confidence enum (Direct, RuntimeCorrelated,
  AddressCorrelated, MarkerCorrelated, TimeCorrelated, Inferred) — every
  correlation labelled.
- **gRPC contracts** (proto/*.proto) — worker + coordinator + events.
- **PyTorch worker**: forward hooks over every `nn.Module`, layer-index inference
  from HF naming conventions, attention Q/K/V/O capture, logits (top-8), token
  events, NVTX/rocTX range markers with correlation IDs.
- **llama.cpp worker**: real C harness against llama.cpp's C API, gRPC C++
  glue via `grpcpp`, per-token markers.
- **CUDA driver capture** — CUPTI activity + callback API via P/Invoke, real
  kernel/memcpy/memory record parsing.
- **ROCm driver capture** — rocprofiler/roctracer via P/Invoke.
- **Vulkan driver capture** — vulkan-1.dll P/Invoke + out-of-band shared-memory
  bridge for GPU timestamp queries + `VK_EXT_debug_utils` labels.
- **CPU driver capture** — sampling backend using `Process.TotalProcessorTime`.
- **WPF app** — dark charcoal theme, 12 real views (Overview, Tokens, Layers,
  Attention, Activations, Tensors, Driver, Kernels, Memory, Timeline, Compare,
  Capture Library), AvalonDock workspace, cross-view selection sync, selection
  history (Alt+Left/Right), command palette (Ctrl+Shift+P), progressive
  disclosure Simple/Advanced/Forensic (F1/F2/F3), full keyboard operation,
  AutomationProperties on every interactive/critical control, software-render
  toggle.
- **Tests**: xUnit — SafeTensors parser, GGUF parser + block-quant math,
  architecture adapter registry, event store round-trip, query engine filters
  + paging, correlation engine confidence tiers, ULID monotonicity. pytest —
  hook capture on synthetic module tree + tiny-gpt2 integration.
- **Build scripts**: `build_linux.sh`, `build_windows.ps1`, `scripts/gen_proto.sh`.

## Deferred honestly absent (not stubbed)

- DirectML backend, Metal (Apple), Unreal client, code-signing pipeline, MSI
  installer pipeline (WiX scaffold README only), tensor readback session cache,
  in-process ETW instrumentation agent (CPU counters are still real — from the
  coordinator's own process).

## Files delivered

- 136 files under `/app/stackscope/`
- .NET code: `core/`, `adapters/`, `services/`, `app/desktop/`
- Python worker: `workers/inference_worker_py/`
- Native worker: `workers/llamacpp_worker/`
- Proto contracts: `proto/`
- Tests: `tests/{Core,Adapters}.Tests + python_worker_tests + integration`
- Docs: `README.md`, `docs/DEFERRED.md`, per-subsystem READMEs.

## What's been implemented (dated)

- **2026-01** — Full source tree delivered. Python worker lint-clean;
  `stackscope_worker.markers` verified executable (NVTX/rocTX no-op
  fallback path works). Everything else compiles-on-target-only per user's
  1(c) decision.

## Next action items

- User: `git init && git add . && git commit && ` push via "Save to GitHub".
- User: on Windows box, `git submodule update --init --recursive` then run
  `.\build_windows.ps1` (needs .NET 8 SDK, VS 2022 C++ build tools, CUDA
  Toolkit 12.x with CUPTI, ROCm 6.x SDK, Vulkan SDK, and vcpkg with
  `grpc protobuf` for the llama.cpp worker).
- User: on any Linux machine, `./build_linux.sh` to run the non-WPF portions
  through `dotnet test` + `pytest`.

## Backlog / roadmap

- **P0** (next pass): Tensor readback forensic path (worker-side arena cache
  + gRPC ReadTensor), Attention head-level split events (currently emitted at
  the projection layer without per-head disaggregation), Coordinator gRPC
  server front (UI currently owns services in-process — coordinator process
  boundary is defined by proto but the standalone service host isn't shipped).
- **P1**: DirectML backend, code-signed MSI installer via WiX, HuggingFace
  Hub download UI, native ETW instrumentation agent.
- **P2**: Unreal visualization client, GPU-side flame graph rendering,
  time-travel debugging (rewind to a token, resume with different sampler).
