# packaging/wix/

WiX installer scaffolding placeholder.

**Status:** scaffolding only. The MSI installer pipeline is deferred
this pass per project rule §38 ("no stubs"): shipping a WiX project
that emits an unsigned, feature-incomplete MSI is worse than shipping
nothing. When we ship the installer, this directory will grow:

- `Product.wxs` — full component graph.
- `Bundle.wxs` — bootstrapper covering CUDA/ROCm/Vulkan runtime detection.
- `Build.targets` — MSBuild integration for CI.
- `sign.ps1` — code-signing invocation (deferred until a cert is issued).

For local Windows testing, run the app directly from
`app/desktop/bin/Release/net8.0-windows/StackScope.exe`.
