# packaging/wix/

Real WiX v4 installer pipeline. Produces:
- `StackScope.msi` — the MSI installer, unsigned.
- `StackScope-Setup.exe` — chained bundle installing the .NET Desktop
  Runtime 8 prerequisite first (only if absent), then the MSI.

## Prereqs

```powershell
dotnet tool install --global wix
```

## Build (from repo root)

```powershell
.\packaging\wix\build.ps1
```

Artifacts land in `build/`.

## What's inside

- `Product.wxs` — MSI: installs the WPF app, coordinator, Python worker,
  proto contracts, and a Start-menu shortcut.
- `Bundle.wxs` — chained bootstrapper: .NET Desktop Runtime 8 + MSI.
- `License.rtf` — MIT license shown on the license page.
- `build.ps1` — one-command driver.

## Code signing

Deliberately not wired. Signing requires an issued authenticode cert and
should never be committed. When a cert is available, add a `signtool.exe`
invocation to `build.ps1` after each `wix build` step.
