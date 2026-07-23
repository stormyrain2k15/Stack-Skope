# AGENTS.md — Instructions for AI coding agents working in StackScope

If you are Claude Code / Cursor / Aider / Cline / any other AI coding
agent, **read this file first**. It codifies the invariants that make
StackScope work. Violating them causes real, reproducible bugs — one of
which we already had to fix (see the LAYER_BEGIN over-count regression).

---

## 0. Ground rules that override defaults

- **No stubs. No mocks.** Every pipeline is wired to real contracts.
  If a backend cannot be exercised in your environment (Windows, CUDA,
  ROCm, Vulkan), leave it *alone* — do not "helpfully" replace it with
  a fake.
- **This is a Windows-native app.** The WPF UI targets
  `net8.0-windows` only. If you are running in a Linux container:
  - The .NET solution restores + builds partially. `net8.0-windows`
    projects will fail; that is expected.
  - You can still run **all** Python worker tests and **most** xUnit
    tests for `StackScope.Core`.
  - The `GitHub build canaries` under `.github/workflows/` are the
    real precompile signal. Trust them, not local `dotnet build`.
- **Do not rewrite `requirements.txt`, `package.json`, or `.env`
  files.** Add packages via the appropriate tool
  (`pip install ... && pip freeze > requirements.txt`, `yarn add`,
  `dotnet add package`). Never overwrite these files wholesale.

---

## 1. Architecture map (know before touching)

```
proto/                       gRPC contracts — events, worker, coordinator
core/                        .NET 8 Core lib (target: net8.0, no WPF)
├── Models/                  UnifiedModelDescriptor, LayerGraph, …
├── Transactions/            EventKind, TransactionEvent, TraceMarker, Ulid
├── Storage/                 EventStore (mmap+SQLite), EventRecord, …
├── Correlation/             CorrelationEngine, confidence labelling
├── Queries/                 QueryEngine — the ONE way to read events
├── Analysis/                DivergenceDetector, CircuitTracer,
│                            AttributionGraph, NumericalHealth,
│                            QuantizationDiff, DeterminismAuditor
├── Comparison/              HeadDiffAnalyzer, DistributionStats
├── Capture/                 Driver-agnostic capture contracts
adapters/                    Formats, Architectures, Runtimes, Drivers
├── Formats/                 SafeTensors, GGUF, Transformers, TensorFlow
├── Architectures/           Llama, Gemma, Qwen, Mistral, GPT-2
├── Runtimes/                
└── Drivers/                 Cuda (CUPTI), Rocm, Vulkan, Cpu
services/                    In-process services + standalone Coordinator host
workers/
├── inference_worker_py/     PyTorch/Transformers hook worker (Python 3.11)
│   └── stackscope_worker/   hooks, anomaly, markers, manifest, bundle,
│                            dry_run, jsonl_export, repro, tail,
│                            mcp_server, attach
├── llamacpp_worker/         Native llama.cpp harness (C++/gRPC)
└── instrumentation_agent/
app/desktop/                 WPF UI (net8.0-windows)
storage/                     
packaging/wix/               MSI + Bundle build scripts (WiX v4)
tests/                       xUnit (Core + Adapters) + pytest + integration
```

---

## 2. HARD invariants — do not violate

### 2.1 Hook classification is a TWO-CONCEPT problem

The single biggest bug we've had. In
`workers/inference_worker_py/src/stackscope_worker/hooks.py`:

- `infer_layer_index(name)` — returns the layer id this module *belongs
  to* (propagates to descendants). Used to tag activations and attention
  events with their owning layer.
- `is_transformer_block(name)` — returns `True` **only** if this module
  *is* the transformer block itself (name ends with
  `layers.<int>` / `h.<int>` / `block(s).<int>`).

`LAYER_BEGIN` / `LAYER_END` MUST be gated on `is_transformer_block`. If
you gate on `layer_idx >= 0 and proj is None` instead, a three-block
model emits six `LAYER_BEGIN` events because inner leaves like
`model.layers.0.mlp` share the same inferred layer index.

Verified by `tests/python_worker_tests/test_hooks.py::test_hook_capture_emits_token_and_layer_events`
(exact-count assertion) and
`tests/python_worker_tests/test_adapter_contracts.py` (block set per
HF architecture).

### 2.2 Every event has a stable ULID

Events are addressable across the whole system by their ULID event id
+ transaction id. When you cite an event in a diagnostic, USE THE
ULID — never a positional index.

### 2.3 Storage is mmap + SQLite, NOT MongoDB

This is a desktop app. `MONGO_URL` from the platform's default env
is irrelevant here. Storage lives under a per-transaction directory
with `.mmap` + `.sqlite` files. Never introduce a document store,
never store JSON blobs where a binary record would do.

### 2.4 All queries go through `QueryEngine`

Don't read the mmap or SQLite directly from analysis code. Every
read of the event store MUST go through
`StackScope.Core.Queries.QueryEngine` (C#) or
`stackscope_worker._generated` grpc calls (Python). This gives us
one place to add indices, caches, and filters.

### 2.5 Timestamps are `time.perf_counter_ns()` on the worker side

The coordinator and worker share the same monotonic clock. Use
`stackscope_worker.markers.now_ns()`. Never `time.time()`, never
`datetime.now()`, never `time.monotonic()` — precision differences
break correlation.

### 2.6 NEVER modify existing tests to make them pass

If a test starts failing, the code is wrong. Fix the code. Only
add tests, never delete or weaken assertions unless the assertion
itself was demonstrably wrong (with justification in the PR body).

---

## 3. Adding a new analysis pass

1. Land the algorithm as a pure class in `core/Analysis/<Name>.cs`.
   No I/O beyond `EventStore` + `QueryEngine`.
2. Add xUnit tests in `tests/Core.Tests/<Name>Tests.cs` that build a
   tiny in-memory `EventStore` and assert the algorithm's output.
3. Add a `ViewModel` in
   `app/desktop/ViewModels/AnalysisViewModels.cs`.
4. Add a `View` XAML in `app/desktop/Views/<Name>View.xaml` following
   the pattern of `AnalysisView.xaml` (StackPanel + Text.Header +
   input controls + result display).
5. Wire it into the shell (dock layout + command palette).

## 4. Adding a new architecture adapter

1. Implement in `adapters/Architectures/<Family>.cs`.
2. Add contract test in
   `tests/python_worker_tests/test_adapter_contracts.py` — you MUST
   list the exact set of module names that pass `is_transformer_block`
   for a real HF checkpoint of that family. The test loads the
   checkpoint's `config.json` via `AutoConfig` and materialises a
   small no-weights variant.

## 5. Adding a new driver capture backend

1. Implement in `adapters/Drivers/<Vendor>/`.
2. Emit events into the same `EventKind` enum. Never invent a new
   enum value without adding it to `proto/events.proto` and running
   `scripts/gen_proto.sh`.

## 6. When you cannot compile (Linux container)

- Run `mcp_lint_python /app/stackscope/workers/inference_worker_py/src`
  and `python3 -m pytest tests/python_worker_tests -q`.
- YAML canaries lint with `python3 -c "import yaml,glob; [yaml.safe_load(open(p)) for p in glob.glob('.github/workflows/*.yml')]"`.
- Do **not** try to build the .NET WPF projects on Linux. Push and
  let the `dotnet-build` canary tell you.

## 7. Bug reports — the good shape

A well-formed bug report contains:
- The capture ULID (transaction id).
- The build SHA of the running binary.
- The reproducibility manifest (`stackscope-manifest --emit`).
- The exact event ULID where the anomaly was first observed.
- The command line + environment variables.

The bug template at `.github/ISSUE_TEMPLATE/bug_report.yml` walks a
human user through this; when triaging as an agent, refuse to attempt
a fix until you have all five fields.

## 8. Where to add new documentation

- **Internal invariants that affect code**: this file.
- **User-facing build guide**: `docs/BUILD.md`.
- **Product-level PRD + roadmap**: `/app/memory/PRD.md` +
  `docs/DEFERRED.md`.
- **Contributor onboarding**: `.github/CONTRIBUTING.md`.

## 9. Never do these

- Rewrite an entire file with `create_file` when a surgical edit will
  do. Prefer `search_replace`.
- Introduce a "compatibility shim" for code you removed. Delete it
  outright.
- Change a `.proto` field number. Ever. Add new fields with new
  numbers.
- Add `datetime.utcnow()` — deprecated in Python 3.12+, use
  `datetime.now(timezone.utc)`.
- Turn on any dev backend without an accompanying real test that
  round-trips at least one real event.
- Add a CLI-only capability without a matching WPF surface. Every
  capability must be one click in the app. The CLI is the transport,
  not the product.

## 10. Product surfaces — one-click, not command-line

Everything the user can do must be reachable by a button in the WPF
shell. The Python CLIs are the transport layer — `PythonCli.cs`
(`app/desktop/Services/PythonCli.cs`) is how the UI invokes them.
`PowerShellRunner.cs` is how the UI invokes shell tooling (clipboard,
Explorer). Never expose a raw terminal command as the primary UX.

Existing one-click surfaces you must not break:
- Hooks Inspector, Numerical Health, Quantization Diff, Determinism
  Auditor, Attribution Graph
- Capture Bundles, Repro &amp; Diff, Live Tail
- AI Assistant Access (MCP), Attach to Running
- Send Bug Report, Annotations, Natural Language Query
- "Debug this token" F4 shortcut

## 11. Golden capture regression

`tests/python_worker_tests/test_golden_capture.py` contains a frozen
event sequence + SHA-256 hash. Anyone who changes the on-disk event
shape or the hash function MUST update the frozen hash AND justify
it in the PR body. Silent drift here breaks every check-in bundle.
