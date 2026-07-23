# Contributing to StackScope

Thanks for helping make transformer inference *legible*. This document explains
how to build, test, and land changes.

## Ground rules

- **No stubs, no mocks.** Every pipeline must be wired to real contracts and
  parsers. If a backend cannot be exercised in CI, mark it with the
  `deferred` label and add an entry to `docs/DEFERRED.md`.
- **Windows is the primary target.** WPF UI code is `net8.0-windows` only.
  Cross-platform code lives in `core/`, `adapters/` (non-driver), and
  `workers/`.
- **Determinism first.** Capture, storage, and correlation must round-trip
  bit-exactly. Add an integration test whenever you touch these layers.

## Development environment

| Tool           | Version                   | Purpose                      |
| -------------- | ------------------------- | ---------------------------- |
| .NET SDK       | 8.0.x                     | WPF + core libs + services   |
| Python         | 3.11                      | Inference worker             |
| Visual Studio  | 2022 (17.8+)              | WPF designer + C++ toolset   |
| CMake          | 3.24+                     | llama.cpp worker             |
| WiX Toolset    | v4 (dotnet tool)          | MSI packaging                |
| CUDA / CUPTI   | 12.x                      | NVIDIA driver capture        |
| ROCm           | 6.x                       | AMD driver capture           |
| Vulkan SDK     | 1.3+                      | Vulkan driver capture        |

See `docs/BUILD.md` for a step-by-step Windows 10 walkthrough.

## Workflow

1. Fork + branch: `feat/<short-slug>` or `fix/<short-slug>`.
2. Run the appropriate build script:
   - `pwsh ./build_windows.ps1` (Windows)
   - `bash ./build_linux.sh`   (Linux — non-WPF libs only)
3. Add tests under `tests/Core.Tests`, `tests/Adapters.Tests`, or
   `tests/python_worker_tests`. Integration tests belong in
   `tests/integration/`.
4. Ensure the GitHub build canaries stay green — they are the
   *precompile test* the maintainer relies on:
   - `dotnet-build` — full solution on Windows
   - `python-worker` — ruff + pytest on Ubuntu + Windows
   - `proto-lint`   — `buf lint` + `protoc` parse
   - `native-workers` — CMake configure/build for llama.cpp
   - `msi-package` — unsigned MSI + Bundle
   - `codeql` — C# and Python static analysis
5. Open a PR against `main`. Fill out the PR template. Link the tracking
   issue.

## Coding conventions

- C# — follow `.editorconfig`; prefer records + `sealed` where possible.
- XAML — one control per file, use `x:Bind` where the compile‑time overhead is
  worth it; otherwise `Binding`.
- Python — `ruff` clean; type hints on all public functions.
- Proto — snake_case fields, semantic file names, no field renumbering.

## Reporting issues

Use the issue templates. Include:
- OS + GPU vendor + driver version
- Model architecture + format (safetensors / GGUF)
- `stackscope --version` and worker log excerpts
