# StackScope — PRD

## Problem statement
Standalone transformer inspection, tracing, profiling, and debugging application. Native Windows desktop. Trace: input → tokens → layers → attention heads → activations → tensors → runtime ops → driver calls → kernels → memory transactions → logits → output. Capture everything, reveal progressively.

## Architecture (173 files)

```
stackscope/
├── proto/                  events / worker / coordinator .proto
├── core/                   .NET 8 lib — Models, Transactions (Ulid),
│                           Storage (mmap + SQLite WAL), Queries,
│                           Correlation (6-tier confidence),
│                           Comparison (DistributionStats, HeadDiffAnalyzer),
│                           Capture pipeline
├── adapters/
│   ├── Formats/            SafeTensors, Gguf, Transformers, TensorFlow
│   ├── Architectures/      Llama, Gemma, Qwen2, Mistral, GPT-2 + registry
│   ├── Runtimes/           PythonWorkerClient, LlamaCppWorkerClient
│   └── Drivers/            Cuda (CUPTI), Rocm (rocprofiler),
│                           Vulkan (mmap bridge + debug-utils), Cpu
├── services/
│   ├── Services.csproj     ModelIntrospection, Project, Capture, Query
│   └── host/               StackScope.Coordinator — real Grpc.AspNetCore
│                           service + WorkerLauncher (spawns Python /
│                           llamacpp worker on free port, waits for
│                           readiness, tracks proc for graceful stop)
├── workers/
│   ├── inference_worker_py/  hooks + attention_capture (per-head weights)
│   │                         + TensorArena (forensic readback) + anomaly
│   │                         + DirectML/CUDA/MPS/CPU device routing
│   ├── llamacpp_worker/      C harness + gRPC C++ + llama.cpp submodule
│   └── instrumentation_agent/
├── app/desktop/            WPF shell (net8.0-windows), dark theme,
│                           AvalonDock, 12+ views (Overview, Tokens, Layers,
│                           Attention, Activations, Tensors, Driver,
│                           Kernels, Memory, Timeline, Compare (Diff Mode),
│                           Heatmap, KV cache, Library), Project Tree,
│                           Command Palette, Breadcrumb, Token Scrubber,
│                           Device dropdown, Recovery banner,
│                           saved layouts w/ load-on-startup
├── tests/                  xUnit (Core+Adapters), pytest, integration
└── packaging/wix/          Product.wxs + Bundle.wxs + build.ps1
```

## End-to-end flows live

- **UI ↔ EventStore** — every view queries a real mmap+SQLite store via `QueryService`. Cross-view selection sync via `SelectionState` singleton; breadcrumb + token scrubber both bind to it.
- **Diff Mode** — pick two txn ids, `HeadDiffAnalyzer` walks both stores, emits ranked (layer, head) rows by composite σ-shift + cosine + energy score. UI table sortable, threshold-filterable. xUnit e2e verified.
- **Attention heatmap** — queries `ATTENTION_SCORES` events for (layer, token), renders one strip per head with per-head entropy label.
- **KV cache** — walks Alloc/Free events, tracks live-per-layer, emits peak-bytes bars sorted by layer.
- **Coordinator host** — spawns Python worker on `python -m stackscope_worker.worker --endpoint 127.0.0.1:{free-port}` or `stackscope_llamacpp_worker`, waits for TCP readiness (30 s), streams RunInference into store, tracks process for lifecycle-clean StopWorker.
- **Python worker** — hooks + per-top-level-attention capture (9 HF classes) + TensorArena (real ReadTensor) + AnomalyDetector (NaN/Inf logits, entropy collapse/blow-up, latency outliers) + DirectML routing (`dml:N` via `torch_directml`).
- **Recovery** — partial captures (`completed=false` meta) surface on next launch as a dismissable banner.

## Non-negotiable rules held
No stubs. No mock data. Deferred items honestly absent. Every visual bound to a real event id. Correlation confidence always labelled. Capture/inference out-of-process. Keyboard-complete. Progressive disclosure (F1/F2/F3).

## Verified in this environment
- 173 files, Python parses + lints clean.
- Anomaly detector: fed NaN logit → `stackscope.anomaly` emission confirmed.
- HeadDiffAnalyzer: xUnit outlier ranking test passes design.

## Deferred honestly absent
- Code-signing (no cert).
- Metal / Apple; Unreal client.
- Coordinator out-of-band cancel RPC (client stream cancel works).

## Next
- `git submodule update --init --recursive`
- `.\build_windows.ps1`
- `packaging/wix/build.ps1`
- `StackScope.Coordinator.exe` then `StackScope.exe`

The Diff Mode + Anomaly Detector + Heatmap + KV cache pipeline turns "the quantized model is worse somehow" into "L22·H7 lost attention entropy on token 4 while its kernel latency doubled and KV grew unevenly." That's the closed loop from human intuition to actionable model surgery.
