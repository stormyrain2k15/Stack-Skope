# Deferred (honestly absent this pass)

Per project rule §38 — "No stubs, no mock data" — the following subsystems
are wholesale deferred rather than half-shipped:

- **DirectML backend** — requires DirectML runtime + ONNX Runtime.
- **Metal backend** — Apple-only, out of scope for a Windows product.
- **Unreal visualization client** — no UE integration this pass.
- **Auto-update system** — no signed update endpoint.
- **Code signing pipeline** — no cert or CI signing job.
- **MSI installer pipeline** — WiX scaffolding lives under `packaging/wix/`
  but no `Product.wxs`, no `light.exe`/`candle.exe` invocation is wired.
  See `packaging/wix/README.md`.

If a view depends on one of these, the view is **not present**. It is not
stubbed with fake data.
