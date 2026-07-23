# StackScope

Vertically integrated transformer inference debugger. Traces the entire chain:

```
Input → tokenization → layers → attention heads → activations → tensors
      → runtime operators → driver calls → kernels → memory transactions
      → logits → output token
```

Native Windows desktop (WPF, .NET 8) + out-of-process inference workers
(Python 3.11 PyTorch/Transformers + native llama.cpp) + driver capture
(CUPTI, ROCm rocprofiler, ETW).

## Non-Negotiable Rules

- Every line contributes. No stubs. No mock data.
- Inferred relationships are always labeled with an explicit confidence value.
- Capture and inference are always out-of-process.
- Every visual object is bound to a real event / tensor / allocation ID.
- Keyboard-complete.
- Progressive disclosure (Simple / Advanced / Forensic) is enforced.

If a subsystem cannot be delivered honestly in a given pass, it is deferred
wholesale, not stubbed. See `docs/DEFERRED.md`.

## Repository Layout

```
stackscope/
├── app/desktop/                  WPF UI (net8.0-windows, Windows-only compile)
├── core/                         .NET 8 libs (Linux-testable)
├── adapters/
│   ├── Formats/{SafeTensors,Gguf,Transformers,TensorFlow}
│   ├── Architectures/            Llama / Gemma / Qwen2 / Mistral / GPT-2
│   ├── Runtimes/                 gRPC clients to Python + llama.cpp workers
│   └── Drivers/{Cuda,Rocm,Cpu}   CUPTI, rocprofiler, CPU counters
├── services/                     Coordinator, Query, Capture, Project
├── workers/
│   ├── inference_worker_py/      Python 3.11 PyTorch/Transformers + gRPC
│   ├── llamacpp_worker/          C harness linking against llama.cpp
│   └── instrumentation_agent/
├── proto/                        gRPC .proto contracts
├── storage/                      Runtime capture files (empty at rest)
├── tests/{Core.Tests, Adapters.Tests, python_worker_tests, integration}
└── packaging/wix/                MSI scaffolding (installer pipeline deferred)
```

## Build

- **Linux CI (core, adapters, services, workers):**
  `./build_linux.sh`
- **Windows local (adds the WPF desktop app):**
  `.\build_windows.ps1`

The WPF project (`app/desktop`) uses `net8.0-windows` and only compiles on
Windows. Everything else is `net8.0` and cross-platform.

### Prerequisites

- .NET SDK 8.0.x
- Python 3.11 (for `workers/inference_worker_py`)
- Windows-only, for driver + UI:
  - Windows 10 Pro 1909+ or Windows 11
  - CUDA Toolkit 12.x with CUPTI (NVIDIA path)
  - ROCm 6.x with rocprofiler (AMD path)
  - Visual Studio 2022 Build Tools with C++ desktop workload
- llama.cpp submodule (see `.gitmodules`): `git submodule update --init --recursive`

## Testing

- `dotnet test` — xUnit suites in `tests/Core.Tests`, `tests/Adapters.Tests`
  (14 test classes: EventStore round-trip, Ulid, CorrelationEngine, QueryEngine,
  DistributionStats, HeadDiffAnalyzer, DivergenceDetector, NumericalHealth,
  QuantizationDiff, DeterminismAuditor, AttributionGraph, ReproducibilityManifest,
  AnnotationStore, and the format/architecture adapter tests).
- `STACKSCOPE_SKIP_HF_TESTS=1 PYTHONPATH=workers/inference_worker_py/src pytest tests/python_worker_tests -q`
  — 41 tests covering hook classification, per-arch adapter contracts (Llama,
  Mistral, Qwen, Gemma, GPT-2, plus the tiny toy), reproducibility manifest,
  bundle round-trip, JSONL export + canonical hash, MCP server, and the
  frozen golden capture regression.
- Integration test (`tests/integration`) drives worker → coordinator → query
  service and asserts real events are retrievable by (token, layer, head).

## Product surfaces — every one is a WPF button

None of these require the user to open a terminal. Each is a one-click
view in the desktop app that shells out to the matching Python CLI
underneath. The CLIs stay first-class for automation and scripting.

| WPF view                | CLI it wraps                       | What it does |
| ----------------------- | ---------------------------------- | ------------ |
| Device dropdown + Detect| `stackscope-devices`               | Rich accelerator enumeration (CUDA/ROCm/DirectML/MPS/CPU) with name, VRAM, compute cap, driver version |
| Hooks Inspector         | `stackscope-dry-run`               | Print module classification table (blocks / attn projs / layer ids) |
| Numerical Health        | (in-proc `NumericalHealth`)        | Per-layer NaN/Inf, entropy stats, latency outliers |
| Quantization Diff       | (in-proc `QuantizationDiff`)       | f16 vs quant divergence; first-token, first-layer, energy delta |
| Determinism Auditor     | (in-proc `DeterminismAuditor`)     | Content-hash mismatch pinpointing non-determinism |
| Attribution Graph       | (in-proc `AttributionGraph`)       | Weighted causal graph rooted at a token |
| Capture Bundles         | `stackscope-bundle`                | Pack / unpack / verify `.stackscope` files |
| Repro & Diff            | `stackscope-repro`                 | Diff two bundles event-by-event |
| Live Tail               | `stackscope-tail`                  | Stream new events with grep/kind filters |
| AI Assistant Access     | `stackscope-mcp`                   | Serve a bundle over MCP to Claude/Cursor/Cline |
| Attach to Running       | `stackscope_worker.attach.attach_here` | Two-line snippet copied to clipboard |
| Send Bug Report         | `stackscope-manifest` + `bundle`   | One-click zip of capture + manifest + notes |
| Annotations pane        | `AnnotationStore` (SQLite)         | Pinned research notes, markdown export |
| Natural Language Query  | (rule-based translator)            | Type a query in English → structured filter |
| Debug this token (F4)   | (chains AttributionGraph + Health) | One key: seeds and runs both views for the selected token |

## Deferred (honestly absent this pass)

See `docs/DEFERRED.md`.

## License

See `LICENSE`.
