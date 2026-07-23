# StackScope — PRD

## Problem statement
Standalone transformer inspection, tracing, profiling, and debugging application. Native Windows desktop. Trace: input → tokens → layers → attention heads → activations → tensors → runtime ops → driver calls → kernels → memory transactions → logits → output. Capture everything, reveal progressively.

## Architecture (190 files)

```
stackscope/
├── proto/                    events / worker / coordinator .proto
├── core/                     .NET 8 lib
│   ├── Models Transactions Storage Queries Correlation Capture Comparison
│   └── Analysis/             DivergenceDetector, CircuitTracer
├── adapters/{Formats,Architectures,Runtimes,Drivers/{Cuda,Rocm,Vulkan,Cpu}}
├── services/
│   ├── Services.csproj       ModelIntrospection, Project, Capture, Query
│   └── host/                 StackScope.Coordinator (Grpc.AspNetCore)
│                             + WorkerLauncher (spawn + readiness + kill-tree)
├── workers/
│   ├── inference_worker_py/  hooks + per-head attention + TensorArena
│   │                         + AnomalyDetector + ablation hooks
│   │                         + DirectML/CUDA/MPS/CPU routing
│   ├── llamacpp_worker/      C harness + gRPC C++ + submodule
│   └── instrumentation_agent/
├── app/desktop/              WPF (net8.0-windows), dark theme, AvalonDock
│                             Views: Overview, Tokens, Layers, Attention,
│                             Activations, Tensors, Driver, Kernels, Memory,
│                             Timeline, Compare (Diff Mode), Heatmap,
│                             KV cache, Analysis (divergence + circuit trace
│                             + ablation), Capture Library, Project Tree
│                             Chrome: Menu, Selector bar, Breadcrumb,
│                             Token Scrubber, Device dropdown, Recovery banner,
│                             Command Palette, Saved layouts
├── tests/                    xUnit (Core+Adapters) + pytest + integration
└── packaging/wix/            Product.wxs + Bundle.wxs + build.ps1
```

## End-to-end flows live

- **UI ↔ EventStore** — 15 views bound to real mmap+SQLite via `QueryService`. Cross-view selection sync via `SelectionState` singleton.
- **Diff Mode** — `HeadDiffAnalyzer` ranks (layer, head) cells by composite σ-shift + cosine + energy score. xUnit-verified outlier top rank.
- **Attention heatmap** — per-head strips with entropy labels, bound to (layer, token).
- **KV cache** — walks Alloc/Free, tracks live-per-layer, emits peak-bytes bars.
- **Prompt-replay divergence** — `DivergenceDetector` finds first divergent token across N runs and earliest layer past σ threshold. xUnit-verified.
- **Circuit trace** — `CircuitTracer` walks logits → attention output → top-weighted heads → qkv, per selected token.
- **Attention ablation** — proto field, coordinator forwarding, and Python worker forward-hook that zeroes head slice `(layer, head)` before returning attention output. Compare vs baseline via Diff Mode to measure that head's contribution.
- **Coordinator worker-spawning** — `WorkerLauncher` reserves free loopback port, launches Python or llama.cpp worker, waits ≤30s for readiness, tracks process for clean StopWorker.
- **Anomaly detector** — NaN/Inf logits, entropy collapse/degeneracy, latency outliers. Verified end-to-end here.
- **Recovery** — partial captures surface as dismissable banner on next launch.

## Non-negotiable rules held
No stubs. No mock data. Deferred items honestly absent. Every visual bound to a real event id. Correlation confidence labelled. Out-of-process capture. Keyboard-complete. Progressive disclosure.

## Verified in this environment
- 190 files, Python parses + `ruff` clean, all YAML workflows parse clean.
- Python worker tests: **7 passed, 1 skipped, 0 failed** (HF network test skipped).
- Anomaly emits `stackscope.anomaly` on NaN logit.
- DivergenceDetector + HeadDiffAnalyzer xUnit tests assert the correct outlier.

## Bug fixes landed (Feb 2026)
- **LAYER_BEGIN over-count** — a 3-block model was emitting 6 `LAYER_BEGIN`
  events because `infer_layer_index()` returns the same layer id for any
  descendant of `model.layers.<N>` (needed for tagging attention/activation
  events with their owning layer), so the old gate
  `(layer_idx >= 0 and proj is None)` matched inner leaves like
  `model.layers.<N>.mlp` too. Fixed by introducing
  `is_transformer_block(name)` — True iff the last two dotted segments are
  `<container>.<int>` — and gating `LAYER_BEGIN`/`LAYER_END` on that flag.
  Verified by testing agent (iteration 1): `test_hook_capture_emits_token_and_layer_events`
  now green, new regression `test_is_transformer_block_only_matches_block_itself` added.

## GitHub build canaries (added Feb 2026)
Six workflows under `.github/workflows/` act as precompile tests before the
maintainer pulls to the Windows 10 box:
- `dotnet-build.yml`     — windows-latest, full sln + xUnit Core + Adapters
- `python-worker.yml`    — ubuntu + windows, ruff + pytest on 3.11
- `proto-lint.yml`       — `buf lint` + `protoc` parse of the 3 .proto files
- `native-workers.yml`   — CMake configure/build of llama.cpp worker
- `msi-package.yml`      — WiX v4 unsigned MSI + Bundle, artefact uploaded
- `codeql.yml`           — security-and-quality for C# + Python

Repo hygiene added alongside: `.editorconfig`, `.gitattributes`,
`.github/dependabot.yml` (nuget + pip + actions + submodules),
`.github/CONTRIBUTING.md`, `.github/SECURITY.md`, issue + PR templates,
`buf.yaml`, `docs/BUILD.md` (full Win10 walkthrough).

## Deferred honestly absent
- Code-signing (no cert issued).
- Metal / Apple; Unreal client.
- Out-of-band cancel RPC (client-stream cancel works).

## Push runbook
1. `cd /app/stackscope && git init && git add . && git commit -m "v0.1"` → **Save to GitHub**
2. Windows box: `git submodule update --init --recursive`
3. `.\build_windows.ps1`
4. `scripts/gen_proto.sh` for Python gRPC stubs
5. `packaging/wix/build.ps1`
6. `StackScope.Coordinator.exe`, then `StackScope.exe`
