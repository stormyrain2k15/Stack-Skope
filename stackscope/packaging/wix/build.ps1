# Build the StackScope MSI + Bundle. Requires the WiX v4 dotnet tool:
#   dotnet tool install --global wix
# Usage from repo root:
#   .\packaging\wix\build.ps1
$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
        throw "wix not found on PATH. Install with: dotnet tool install --global wix"
    }

    $buildDir = Join-Path $PSScriptRoot "..\..\build"
    New-Item -ItemType Directory -Force $buildDir | Out-Null

    Write-Host "==> Building the MSI"
    wix build Product.wxs -ext WixToolset.UI.wixext -o (Join-Path $buildDir "StackScope.msi")

    Write-Host "==> Building the Bundle"
    wix build Bundle.wxs -ext WixToolset.BootstrapperApplications.wixext `
        -o (Join-Path $buildDir "StackScope-Setup.exe")

    Write-Host "==> Done. Artifacts in build\ (unsigned)."
    Write-Host "    Code signing is intentionally not wired here; see DEFERRED.md."
} finally {
    Pop-Location
}
