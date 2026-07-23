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

## GPU selection & detection (Feb 2026)
- **New Python module** `stackscope_worker/devices.py` — rich
  accelerator enumeration returning `DeviceInfo` records (id, kind,
  name, total/free VRAM, compute capability, driver version). Handles
  CUDA (torch + nvidia-smi fallback), ROCm (torch-rocm + rocm-smi
  fallback), DirectML (torch_directml), Apple MPS, and CPU. Flags one
  device as `is_default=True`. Exposed via `stackscope-devices` CLI.
- **Proto**: added `DeviceInfo` message to `worker.proto` (field 8 of
  `CapabilitiesReply` as new optional list; legacy `devices` string
  list preserved on field 4). Added `Coordinator.ListDevices` RPC to
  `coordinator.proto`.
- **Coordinator**: `CoordinatorService.ListDevices` forwards to the
  worker's `GetCapabilities` and returns the rich list.
- **WPF**: `DeviceSelectorViewModel` now holds `ObservableCollection<DeviceInfo>`;
  the top-bar dropdown renders "cuda:0 · NVIDIA RTX 4090 · 24 GB · 8.9"
  with `(default)` badge, tooltip showing detect status, and a
  **Detect** button that calls `Shell.DetectDevicesAsync`. The
  selection persists in `WorkspaceState.SelectedDevice` and gets
  forwarded on every `LoadModel` RPC.

## Verified in this environment
- 190+ files, Python parses + `ruff` clean, all YAML workflows parse clean.
- Python worker tests: **41 passed, 1 skipped, 0 failed** (HF network test skipped).
- Every Python CLI end-to-end smoke-tested (`dry-run`, `manifest`, `bundle`,
  `repro`, `jsonl-canonical-hash`, `worker --dry-run-hooks`).
- Anomaly emits `stackscope.anomaly` on NaN logit.
- DivergenceDetector + HeadDiffAnalyzer xUnit tests assert the correct outlier.

## Feb 2026 sweep — everything is one WPF click

The user constraint: this is a product, not a CLI toolkit. Every capability
must be reachable from a button in the WPF shell. The Python CLIs are the
transport underneath.

**New Python modules** (all with pytest coverage):
- `manifest.py` — reproducibility manifest (torch/CUDA/ROCm/Vulkan versions,
  driver strings, seed, dtype, quant, env snapshot).
- `bundle.py` — `.stackscope` zip pack/unpack.
- `dry_run.py` — hook-classification introspector for toy archs + real HF ids.
- `jsonl_export.py` — canonical, git-diffable event export + stable hash.
- `repro.py` — replay a bundle, diff against another, drift detection.
- `tail.py` — live event streaming with grep + kind filters.
- `mcp_server.py` — Model Context Protocol server (stdlib-only, JSON-RPC 2.0)
  exposing 7 tools an AI can call directly.
- `attach.py` — in-process attach for already-running Python processes.

**New .NET core analysis passes** (all with xUnit coverage):
- `NumericalHealth.cs` — per-layer NaN/Inf, entropy stats, latency outliers.
- `QuantizationDiff.cs` — f16 vs q4/q8/… divergence with per-layer sigma shift.
- `DeterminismAuditor.cs` — content-hash mismatch across two runs.
- `AttributionGraph.cs` — weighted causal graph rooted at output token.
- `ReproducibilityManifest.cs` — C# twin of the Python manifest.
- `SnapshotAnnotation.cs` + `AnnotationStore.cs` — SQLite-backed research
  notes with markdown export.

**New WPF views** (one-click surfaces, backed by `PythonCli.cs` +
`PowerShellRunner.cs`):
- HooksInspectorView, BundleWorkbenchView, LiveTailView, MCPServerView,
  AttachSessionView, ReproDiffView, BugReportExporterView,
  HealthDashboardView, QuantizationDiffView, DeterminismAuditorView,
  AttributionGraphView, AnnotationsView, NaturalQueryBar.

**New commands + hotkeys**: 14 new `RoutedUICommand`s wired into menus,
key bindings, and the command palette. Includes `F4 Debug this token`
which seeds + runs Attribution Graph and Numerical Health in one keystroke.

**Repo hygiene** added earlier still valid: `.editorconfig`,
`.gitattributes`, `.github/dependabot.yml`, CONTRIBUTING, SECURITY,
issue + PR templates, `buf.yaml`, `docs/BUILD.md`, `AGENTS.md`,
`CLAUDE.md`, `.cursorrules`. All updated with the new invariants.

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


## Feb 2026 — Repo-wide "no hollow UI" audit
Full sweep across every XAML `Command="{Binding …}"`, `Click="…"`, `[RelayCommand]`,
and gRPC field. Fixed four disconnects where a UI control (or a proto field)
looked functional but its value never reached the runtime:

1. **AnalysisView "Attention head ablation" → RunInferenceDialog.** The two
   textboxes updated `AblationVm.AblateLayer/AblateHead` but the capture
   dialog used its own private -1 defaults. Added
   `RunInferenceDialog.SeedAblation(layer, head)` called from
   `MainWindow.OnStartCapture` so typing "L5 H3" in Analysis Lab now
   actually zeros that head end-to-end.
2. **Python worker ignored `request.top_p`.** Nucleus filtering was
   missing after top-k; added a real top-p pass (keep smallest set with
   cumulative prob ≤ top_p, always keep the top token, renormalise). Six
   new unit tests in `test_grpc_service_wiring.py`.
3. **`StartWorkerRequest.device_hint` was silently dropped.**
   `CoordinatorService.StartWorker` now forwards it to
   `WorkerLauncher.SpawnAsync(kind, deviceHint)` which sets
   `STACKSCOPE_DEVICE_HINT` in the worker env. Python worker's
   `_resolve_device` and llama.cpp worker's `LoadModel` both fall back to
   that env when the request's device is empty. Both call sites
   (`MainWindow.OnOpenModel`, `ShellViewModel.DetectDevicesAsync`) now
   pass `WorkspaceState.SelectedDevice` as the hint.
4. **llama.cpp worker `main.cc` silently ignored `capture_level` and
   `ablate_layer/ablate_head`.** Now emits real `MARKER` events
   (`stackscope.capture_ceiling`, `stackscope.ablation_unsupported`)
   before generation so the timeline shows *why* the requested advanced
   events aren't present — no more silent drops.

5. **Ablation A/B Auto-Compare (Feb 2026 followup).** Persisted
   `prompt` / `ablate_layer` / `ablate_head` into every capture's
   SQLite index (both `CoordinatorService.RunInference` and
   `CaptureService.RunAsync`). Extended `TransactionMetadata` with
   these fields plus a `WasAblated` predicate. Added
   `ProjectService.FindLatestNonAblatedBaseline()` which returns the
   newest completed non-ablated run of the same prompt (and same model
   when both handles are known, else prompt-equality wins).
   `MainWindow.OnStartCapture` now inspects the just-finished
   transaction: if it was ablated, it auto-seeds `CompareVm.Left =
   baseline`, `CompareVm.Right = ablated`, invokes `RunCommand`, and
   focuses the Compare pane. When no baseline exists we say so out
   loud in the status bar rather than silently doing nothing. Five new
   xUnit tests in `ProjectServiceAutoCompareTests.cs` cover the
   baseline search branches.

All 54 Python tests pass. C# code changes are static-analysis verified
(cannot compile WPF in the Linux container).

## Feb 2026 — Capture-Ceiling Badge + Diff Pin Board

**Capture-Ceiling Badge** — When a worker emits a
`stackscope.capture_ceiling` or `stackscope.ablation_unsupported`
marker (currently llama.cpp), the coordinator now records the
detail string into the transaction's SQLite index as
`capture_ceiling`. `TransactionMetadata` exposes `CaptureCeiling` +
`HasCaptureCeiling`. `WorkspaceState.CaptureCeiling` is refreshed both
after Start Capture and when the user selects a different row in the
Capture Library. A rust-coloured badge next to the resolved-device
badge shows the message and full tooltip. Clicking the badge invokes
`ShellViewModel.JumpToCaptureCeilingCommand` which locates the first
ceiling marker event in the current transaction, seeds
`SelectionState` with its event id, and requests the Timeline pane —
no more silent fallbacks and no dead-end badges.

**Diff Pin Board** — Saved (baseline ⇆ candidate) diffs survive
across sessions in a project-scoped `pinned_diffs.sqlite` file at
`ProjectService.PinnedDiffsDbPath`. New `PinnedDiff` record +
`PinnedDiffStore` (CRUD; rejects empty tx ids; newest-first list).
New `PinnedDiffsViewModel` exposes Refresh / PinCurrent /
OpenSelected / DeleteSelected / SaveSelectedNote commands.
`PinnedDiffRow` wraps each pin with a live health check against
`ProjectService.ListTransactions` — dangling pins render in the
accent-rust colour with a status icon and refuse to Open (status bar
explains which side is missing). Compare view gets "Pin this diff"
and "Open Pin Board" buttons. Six xUnit tests in
`PinnedDiffStoreTests.cs`.

**Ablation Range + Sweep (Feb 2026)** — Extended proto
`RunInferenceRequest` / `CoordRunRequest` with
`ablate_layer_end` / `ablate_head_end`. Python worker's forward-hook
now iterates every (layer, head) inside
`[layer..layer_end] × [head..head_end]` and zeros them in one capture.
llama.cpp worker's `ablation_unsupported` marker reports the full
range instead of a single cell. `RunInferenceArgs` +
`TransactionMetadata` gained range fields (metadata roundtripped in
SQLite index). `AblationViewModel` grew range inputs. `AblationSweep`
is a new WPF workflow: `AblationSweepViewModel` drives the
Coordinator directly, launches one single-cell ablated capture per
(L, H) in a rectangular range against a chosen baseline, runs a
`HeadDiffAnalyzer` diff, and lays out peak σ as a live heatmap
(`AblationSweepView`). Cancel button interrupts the sweep mid-flight.
Eight new pytest cases in `test_ablation_range.py` and additional
xUnit `IsAblationRange` + range meta roundtrip coverage in
`ProjectServiceAutoCompareTests.cs`.

All 62 Python tests pass. C# code changes are static-analysis verified
(cannot compile WPF in the Linux container).

## Feb 2026 — Sweep-follow-up wave (final of this session)

**Sweep Cell Click → Auto-Pin**: Each completed heatmap cell is now a
`MouseBinding LeftClick` on the sweep's `PinCellCommand`. Refuses to
pin queued/running/failed cells (status message instead of silent
no-op). Adds a `sweep,ablation` tag + descriptive note
(`sweep p0 L5 H3 σ=1.42`) so pins from sweeps are visually distinct
in the Pin Board. `AblationSweepViewModel` gained a
`PinnedDiffsViewModel` constructor dep for the direct handoff.

**Sweep Progress Persistence**: Each cell completion writes to
`sweep-resume-{baseline}-L{L0}_{L1}-H{H0}_{H1}.json` at the project
root. On the next `Run sweep` (with `Resume` checked, the default),
the VM loads the JSON, seeds matching cells with `State = "done"` +
their cached txn/sigma, and skips them in the run loop. Partial
sweeps survive cancels, crashes, and app restarts.

**Cross-Prompt Attribution**: `AblationSweepViewModel.ExtraPromptsText`
takes one prompt per line. Column 0 is always the baseline's own
prompt; each additional non-empty line runs the same range against
that prompt using the baseline's model handle. Column 0 diffs against
the sweep's baseline; extra columns diff against a matching
non-ablated capture of that prompt (throws early with a clear error
if none exists — no fake attribution numbers). Heatmap `PromptCount`
drives the column axis so the grid reshapes automatically.

**Ablation Preset Library**: `AblationPreset` record +
`AblationPresetStore` (upsert-by-name; rejects blank names). New
`AblationPresetsViewModel` with SaveCurrent / LoadSelected /
DeleteSelected / Refresh. `AblationPresetsView` docks alongside Sweep.
LoadSelected seeds *both* AblationVm (capture path) and SweepVm (sweep
path) so one click reproduces a full study. Menu + Ctrl+Alt+L hotkey
wired. Five xUnit tests in `AblationPresetStoreTests.cs`
(roundtrip, upsert-replace, blank-name rejection, delete, reopen
persistence).

## Feb 2026 — Final polish wave

**Preset Sharing Export/Import**: `ExportedAblationPreset` DTO lives
in `core/Models/` (portable, no WPF deps). `AblationPresetsViewModel`
gained `ExportSelected` + `ImportFromFile` commands using
`SaveFileDialog`/`OpenFileDialog`. Schema is versioned; the importer
refuses `SchemaVersion` mismatches and blank names with a clear
message rather than silently misinterpreting. `.stackscope-preset.json`
files are the shareable unit — one file per study. Three xUnit tests
in `ExportedPresetSchemaTests.cs` pin the wire shape (roundtrip,
schema-version field name, default-value tolerance).

**Sweep Compare Panel**: New `SweepCompareViewModel` +
`SweepCompareView` load any two `sweep-resume-*.json` files from the
project and overlay them as a delta heatmap (red = right sweep had
bigger σ; blue = left; magnitude proportional to |Δ|). Auto-discovers
available sweeps from the project root; peak |Δ| shown in the status
bar. Docked pane + menu entry + `Ctrl+Alt+C` hotkey. Answers "is this
head model-universal or model-specific?" in one click.

**Ablation Undo**: `AblationViewModel` grew a bounded (depth-32)
`Stack<AblationSnapshot>` ring buffer. Every observed field change
(`OnAblate*Changing`, `OnAutoCompare*Changing`) pushes a snapshot;
`Undo()` pops and restores under a `_suspendUndo` guard so restoration
doesn't re-push. Menu + `Ctrl+Alt+Z` hotkey routed to
`MainWindow.OnAblationUndo`; status bar reports the outcome so users
know whether the stack was empty. Load-preset spam can't grow the
heap forever.

**Sweep Speedup**: The sweep loop no longer does a full
`ListTransactions()` disk scan and re-opens the baseline EventStore on
every cell — both are now memoised per prompt column at the start of
the sweep. For an N-cell × M-prompt run this drops from
`O(N × M × ListTransactions + N × M × open baseline store)` to
`O(M × ListTransactions + M × open baseline store) + O(N × M × open
ablated store)`. Baseline stores are disposed at the end of the sweep.
Sweep speedup delivered without a proto change; deeper batch-of-prompts
via a new gRPC RPC remains as a future proto+worker feature.


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
