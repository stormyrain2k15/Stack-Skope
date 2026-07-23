# StackScope llama.cpp worker

Native C/C++ worker that links against llama.cpp (as a git submodule)
and exposes the StackScope `InferenceWorker` gRPC service over
`0.0.0.0:50502`.

## Bootstrap

```bash
# From repo root:
git submodule update --init --recursive
```

## Build (Windows, MSVC + vcpkg)

Requires: Visual Studio 2022 with C++ desktop workload; vcpkg installed
with `grpc`, `protobuf` triplet `x64-windows`.

```powershell
cd workers/llamacpp_worker
cmake -S . -B build -G "Visual Studio 17 2022" -A x64 `
      -DCMAKE_TOOLCHAIN_FILE="$env:VCPKG_ROOT/scripts/buildsystems/vcpkg.cmake"
cmake --build build --config Release
```

## Build (Linux)

Requires: `libprotobuf-dev protobuf-compiler libgrpc++-dev
protobuf-compiler-grpc cmake ninja-build build-essential`.

```bash
cd workers/llamacpp_worker
cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build
```

## Run

```bash
./build/stackscope_llamacpp_worker --endpoint 127.0.0.1:50502
```

The worker's gRPC surface is identical to the Python worker (same
`InferenceWorker` service definition), so the StackScope coordinator can
target either interchangeably per model format.
