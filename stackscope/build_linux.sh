#!/usr/bin/env bash
# StackScope — Linux build + test (cross-platform components only).
# The WPF desktop app is excluded here; it is built on Windows via
# build_windows.ps1.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"

echo "==> Restoring .NET solution (excluding WPF)"
dotnet restore StackScope.sln \
  /p:ExcludeWpf=true

echo "==> Building non-WPF projects"
for proj in \
  core/StackScope.Core.csproj \
  adapters/Formats/SafeTensors/StackScope.Adapters.Formats.SafeTensors.csproj \
  adapters/Formats/Gguf/StackScope.Adapters.Formats.Gguf.csproj \
  adapters/Formats/Transformers/StackScope.Adapters.Formats.Transformers.csproj \
  adapters/Formats/TensorFlow/StackScope.Adapters.Formats.TensorFlow.csproj \
  adapters/Architectures/StackScope.Adapters.Architectures.csproj \
  adapters/Runtimes/StackScope.Adapters.Runtimes.csproj \
  adapters/Drivers/Cpu/StackScope.Adapters.Drivers.Cpu.csproj \
  services/StackScope.Services.csproj \
  tests/Core.Tests/StackScope.Core.Tests.csproj \
  tests/Adapters.Tests/StackScope.Adapters.Tests.csproj
do
  dotnet build "$proj" -c Release --nologo
done

echo "==> Running .NET tests"
dotnet test tests/Core.Tests/StackScope.Core.Tests.csproj -c Release --nologo --no-build
dotnet test tests/Adapters.Tests/StackScope.Adapters.Tests.csproj -c Release --nologo --no-build

echo "==> Running Python worker tests"
if command -v python3.11 >/dev/null 2>&1; then
  PY=python3.11
else
  PY=python3
fi
$PY -m pip install --quiet -r workers/inference_worker_py/requirements.txt
$PY -m pytest tests/python_worker_tests -q

echo "==> Linux build complete."
