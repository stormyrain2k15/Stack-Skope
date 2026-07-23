# CLAUDE.md — StackScope project instructions

You are working on **StackScope**, a native Windows transformer
debugger (WPF + .NET 8 + Python 3.11 + gRPC + mmap/SQLite storage +
CUDA/CUPTI/ROCm/Vulkan driver capture).

**Read `AGENTS.md` at the repo root before making any change.**
It codifies the hard invariants — hook classification, ULID
addressing, storage layer, query path, timestamp clock, and the
"no stubs, no mocks" rule.

Fast facts:

- **Target OS**: Windows 10 Pro (WPF is Windows-only).
- **CI runners**: GitHub Actions — `dotnet-build`, `python-worker`,
  `proto-lint`, `native-workers`, `msi-package`, `codeql`. These
  are the *precompile canaries* — trust them over a local Linux
  `dotnet build`.
- **Storage**: memory-mapped binary event log + SQLite index.
  Never MongoDB. Never JSON blobs where records fit.
- **Clock**: `time.perf_counter_ns()` (Python) / `Stopwatch.GetTimestamp()`
  scaled (C#). Both give monotonic ns.
- **Layer detection**: gated on `is_transformer_block(name)`
  (last two dotted segments are `<container>.<int>`), NOT on
  `infer_layer_index(name) >= 0`. Regression test:
  `test_hook_capture_emits_token_and_layer_events`.

Fastest workflow if you're stuck:

1. `python3 -m stackscope_worker.dry_run --model <hf-id-or-arch>`
   prints the exact classification table (which modules pass
   `is_transformer_block`, which pass `is_attention_projection`,
   which have `layer_idx>=0`) — the single fastest way to diagnose
   hook bugs.
2. `stackscope-repro <capture.stackscope>` replays a capture
   against the current build and prints any event drift.
3. `stackscope-tail --grep NaN <capture-dir>` streams events in
   real time.

If you're an AI reviewing a bug report, you need the capture ULID,
build SHA, and the repro manifest — no manifest, no fix.
