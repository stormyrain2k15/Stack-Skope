# StackScope — Windows build (WPF desktop + all libs + workers).
# Requires: .NET SDK 8, Python 3.11, Visual Studio 2022 C++ Build Tools,
# CUDA Toolkit 12.x with CUPTI (for CUDA path) and/or ROCm 6.x
# (for AMD path). llama.cpp submodule must be initialised.
$ErrorActionPreference = "Stop"

Push-Location $PSScriptRoot
try {
    Write-Host "==> Restoring StackScope.sln"
    dotnet restore StackScope.sln

    Write-Host "==> Building StackScope.sln (Release, Windows)"
    dotnet build StackScope.sln -c Release --nologo

    Write-Host "==> Running .NET tests"
    dotnet test tests/Core.Tests/StackScope.Core.Tests.csproj `
        -c Release --nologo --no-build
    dotnet test tests/Adapters.Tests/StackScope.Adapters.Tests.csproj `
        -c Release --nologo --no-build

    Write-Host "==> Python worker tests"
    python -m pip install --quiet -r workers/inference_worker_py/requirements.txt
    $env:STACKSCOPE_SKIP_HF_TESTS = "1"
    $env:PYTHONPATH = "workers/inference_worker_py/src"
    python -m pytest tests/python_worker_tests -q

    Write-Host "==> Python ruff"
    python -m pip install --quiet ruff
    python -m ruff check workers/inference_worker_py/src

    Write-Host "==> llama.cpp worker (CMake)"
    if (Test-Path workers/llamacpp_worker/vendor/llama.cpp) {
        Push-Location workers/llamacpp_worker
        cmake -S . -B build -G "Visual Studio 17 2022" -A x64
        cmake --build build --config Release
        Pop-Location
    } else {
        Write-Warning "llama.cpp submodule not initialised; skipping worker build."
        Write-Warning "Run: git submodule update --init --recursive"
    }

    Write-Host "==> Windows build complete."
} finally {
    Pop-Location
}
