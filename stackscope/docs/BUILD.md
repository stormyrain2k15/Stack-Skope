# Building StackScope on Windows 10 Pro

This is the authoritative step-by-step for cloning + building the full
stack — WPF desktop, .NET core libs, Python inference worker, llama.cpp
native worker, and the unsigned MSI installer — on a fresh Windows 10
Pro box.

> The maintainer's daily driver. If a step here breaks, that is a bug.

---

## 0. Prerequisites (one-time)

Install these once, in this order:

| Tool                              | Where                                              | Notes                                                            |
| --------------------------------- | -------------------------------------------------- | ---------------------------------------------------------------- |
| **Git for Windows** (2.44+)       | <https://git-scm.com/download/win>                 | Enable "Git Credential Manager". Enable long paths.              |
| **Visual Studio 2022** (17.8+)    | <https://visualstudio.microsoft.com/downloads/>    | Workloads: **.NET desktop development**, **Desktop dev with C++**, **Windows 10 SDK (10.0.19041+)**. |
| **.NET 8 SDK** (8.0.x)            | <https://dotnet.microsoft.com/download/dotnet/8.0> | `dotnet --info` should list SDK 8.0.x.                           |
| **Python 3.11** (64-bit)          | <https://www.python.org/downloads/windows/>        | Tick "Add python.exe to PATH". Do not install 3.12+.             |
| **CMake** (3.24+)                 | <https://cmake.org/download/>                      | Add to PATH.                                                     |
| **WiX Toolset v4** (.NET tool)    | `dotnet tool install --global wix`                 | For MSI packaging only.                                          |
| **CUDA Toolkit 12.x + CUPTI**     | <https://developer.nvidia.com/cuda-downloads>      | NVIDIA path only. CUPTI ships inside the toolkit.                |
| **ROCm 6.x** (WSL / native)       | <https://rocm.docs.amd.com/>                       | AMD path only. Native Windows ROCm is preview‑grade — WSL is the fallback. |
| **Vulkan SDK 1.3+**               | <https://vulkan.lunarg.com/sdk/home>               | Any GPU vendor path.                                             |

Enable long paths (once, elevated PowerShell):

```pwsh
git config --system core.longpaths true
New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem" `
    -Name "LongPathsEnabled" -Value 1 -PropertyType DWORD -Force
```

## 1. Clone the repo (with submodules)

```pwsh
git clone --recurse-submodules https://github.com/<you>/StackScope.git
cd StackScope
```

If you already cloned without submodules:

```pwsh
git submodule update --init --recursive
```

The `llama.cpp` worker lives at `workers/llamacpp_worker/vendor/llama.cpp`.

## 2. One-shot build (recommended)

From the repo root, in an **elevated Developer PowerShell for VS 2022**:

```pwsh
./build_windows.ps1
```

This will:
1. `dotnet restore` + `dotnet build StackScope.sln -c Release`
2. Run xUnit tests for `Core` and `Adapters`
3. Install worker dependencies and run `pytest tests/python_worker_tests`
4. `cmake` configure + build the llama.cpp native worker (if submodule
   is populated)

Artefacts land under `**/bin/Release/net8.0-windows/` and
`workers/llamacpp_worker/build/Release/`.

## 3. Run the desktop app

```pwsh
dotnet run --project app/desktop/StackScope.Desktop.csproj -c Release
```

The coordinator + Python worker will be launched by the app the first
time you attach to a model. To run the coordinator standalone:

```pwsh
dotnet run --project services/host/StackScope.Coordinator.csproj -c Release
```

## 4. Build the MSI installer (optional)

```pwsh
./packaging/wix/build.ps1
```

Outputs (unsigned):
- `build/StackScope.msi`
- `build/StackScope-Setup.exe` (Bundle)

Code signing is intentionally not wired here — see `docs/DEFERRED.md`.

## 5. GitHub build canaries — precompile check

Six workflows guard the tree. All must be green before you pull to your
box:

| Workflow          | Runner            | What it proves                                   |
| ----------------- | ----------------- | ------------------------------------------------ |
| `dotnet-build`    | windows-latest    | Full solution compiles + xUnit tests pass        |
| `python-worker`   | ubuntu + windows  | Ruff clean + pytest green on 3.11                |
| `proto-lint`      | ubuntu-latest     | `buf lint` + `protoc` parse                      |
| `native-workers`  | ubuntu + windows  | llama.cpp worker CMake configure (+ build)       |
| `msi-package`     | windows-latest    | Unsigned MSI + Bundle build, artefact uploaded   |
| `codeql`          | windows + ubuntu  | Security-and-quality static analysis             |

Trigger any of them manually via **Actions → Run workflow**.

The `msi-package` job uploads a downloadable artefact named
`StackScope-msi-unsigned-<sha>` you can grab straight to your Win10 box
for smoke testing.

## 6. Troubleshooting

- **`WPF ... net8.0-windows not found`** — you installed the SDK but
  not the Windows Desktop workload. Re-run VS Installer.
- **`CUPTI not found`** — set `CUDA_PATH` to your toolkit root; CUPTI
  headers live under `%CUDA_PATH%\extras\CUPTI`.
- **`wix : The term 'wix' is not recognized`** — the .NET tools folder
  isn't on PATH. Restart the shell after
  `dotnet tool install --global wix`.
- **`llama.cpp: no CMakeLists`** — submodule not initialised. Run
  `git submodule update --init --recursive`.

## 7. Uninstall / clean

```pwsh
dotnet clean StackScope.sln
Remove-Item -Recurse -Force build, **/bin, **/obj
```
