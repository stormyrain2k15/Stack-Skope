"""Accelerator enumeration.

Returns rich info per device — name, memory, compute capability,
driver version. The WPF dropdown renders these so the user sees
"cuda:0 · NVIDIA RTX 4090 · 24 GB" instead of just "cuda:0".

All probing is best-effort: a missing driver returns a partial entry
rather than raising, so a workstation with no GPU still lists "cpu".
"""
from __future__ import annotations

import re
import shutil
import subprocess
from dataclasses import dataclass


@dataclass
class DeviceInfo:
    id: str
    kind: str                   # "cuda" | "rocm" | "dml" | "mps" | "cpu"
    name: str = ""
    total_memory_bytes: int = 0
    free_memory_bytes: int = 0
    compute_capability: str = ""
    driver_version: str = ""
    multi_processor_count: int = 0
    is_integrated: bool = False
    is_default: bool = False


def _run(cmd: list[str]) -> str | None:
    try:
        r = subprocess.run(cmd, capture_output=True, text=True, timeout=3, check=False)
        return r.stdout.strip() if r.returncode == 0 else None
    except (OSError, subprocess.TimeoutExpired):
        return None


def _nvidia_driver() -> str:
    v = _run(["nvidia-smi", "--query-gpu=driver_version", "--format=csv,noheader"])
    return v.splitlines()[0] if v else ""


def _amd_driver() -> str:
    return _run(["rocm-smi", "--showdriverversion"]) or ""


def _cuda_devices() -> list[DeviceInfo]:
    """Enumerate NVIDIA CUDA devices via torch if available, falling
    back to nvidia-smi if torch is missing (fresh worker startup)."""
    devs: list[DeviceInfo] = []
    try:
        import torch  # noqa: WPS433
        if not torch.cuda.is_available():
            return devs
        drv = _nvidia_driver()
        for i in range(torch.cuda.device_count()):
            props = torch.cuda.get_device_properties(i)
            free, total = 0, int(props.total_memory)
            try:
                free_b, total_b = torch.cuda.mem_get_info(i)
                free, total = int(free_b), int(total_b)
            except Exception:
                pass
            devs.append(DeviceInfo(
                id=f"cuda:{i}",
                kind="cuda",
                name=props.name,
                total_memory_bytes=total,
                free_memory_bytes=free,
                compute_capability=f"{props.major}.{props.minor}",
                driver_version=drv,
                multi_processor_count=int(getattr(props, "multi_processor_count", 0)),
                is_integrated=False,
            ))
        return devs
    except ImportError:
        pass
    # torch missing — fall back to nvidia-smi.
    csv = _run([
        "nvidia-smi",
        "--query-gpu=index,name,memory.total,memory.free,compute_cap,driver_version",
        "--format=csv,noheader,nounits",
    ])
    if not csv:
        return devs
    for line in csv.splitlines():
        cols = [c.strip() for c in line.split(",")]
        if len(cols) < 6:
            continue
        idx, name, total_mib, free_mib, cap, drv = cols[:6]
        try:
            devs.append(DeviceInfo(
                id=f"cuda:{idx}",
                kind="cuda",
                name=name,
                total_memory_bytes=int(float(total_mib) * 1024 * 1024),
                free_memory_bytes=int(float(free_mib) * 1024 * 1024),
                compute_capability=cap,
                driver_version=drv,
            ))
        except ValueError:
            continue
    return devs


def _rocm_devices() -> list[DeviceInfo]:
    """AMD ROCm devices. Torch reports them under `cuda:` too when
    built with rocm, but we surface a separate 'rocm' kind so the UI
    can badge them accurately."""
    devs: list[DeviceInfo] = []
    try:
        import torch
        # torch-rocm exposes ROCm devices via the cuda API too, so
        # this only fires if torch is CPU/CUDA and rocminfo is present.
        has_torch_rocm = getattr(torch.version, "hip", None) is not None
        if has_torch_rocm and torch.cuda.is_available():
            drv = _amd_driver()
            for i in range(torch.cuda.device_count()):
                props = torch.cuda.get_device_properties(i)
                devs.append(DeviceInfo(
                    id=f"cuda:{i}",   # torch-rocm still uses cuda: prefix
                    kind="rocm",
                    name=props.name,
                    total_memory_bytes=int(props.total_memory),
                    compute_capability=getattr(props, "gcnArchName", ""),
                    driver_version=drv,
                ))
            return devs
    except ImportError:
        pass

    if shutil.which("rocm-smi") is None:
        return devs
    out = _run(["rocm-smi", "--showproductname", "--showmeminfo", "vram", "--json"])
    if not out:
        return devs
    import json
    try:
        j = json.loads(out)
    except json.JSONDecodeError:
        return devs
    for k, v in j.items():
        m = re.match(r"card(\d+)", k)
        if not m:
            continue
        idx = int(m.group(1))
        total = int(v.get("VRAM Total Memory (B)", 0) or 0)
        devs.append(DeviceInfo(
            id=f"rocm:{idx}", kind="rocm",
            name=v.get("Card series", "AMD GPU"),
            total_memory_bytes=total,
            driver_version=_amd_driver(),
        ))
    return devs


def _directml_devices() -> list[DeviceInfo]:
    """DirectML — Windows-side path for any DXGI adapter."""
    devs: list[DeviceInfo] = []
    try:
        import torch_directml
    except ImportError:
        return devs
    for i in range(torch_directml.device_count()):
        try:
            name = torch_directml.device_name(i)
        except Exception:
            name = f"DirectML device {i}"
        devs.append(DeviceInfo(id=f"dml:{i}", kind="dml", name=name))
    return devs


def _mps_device() -> list[DeviceInfo]:
    try:
        import torch
        if getattr(torch.backends, "mps", None) and torch.backends.mps.is_available():
            return [DeviceInfo(id="mps", kind="mps", name="Apple GPU")]
    except ImportError:
        pass
    return []


def _cpu_device() -> DeviceInfo:
    import os
    cpu = DeviceInfo(id="cpu", kind="cpu",
                     name=f"CPU ({os.cpu_count() or 1} logical cores)")
    try:
        import psutil
        vm = psutil.virtual_memory()
        cpu.total_memory_bytes = int(vm.total)
        cpu.free_memory_bytes = int(vm.available)
    except ImportError:
        pass
    return cpu


def enumerate_devices() -> list[DeviceInfo]:
    """Return all accelerators visible to this worker. CPU is always
    last. The first CUDA device (if any) is flagged is_default=True."""
    out: list[DeviceInfo] = []
    cuda = _cuda_devices()
    out.extend(cuda)
    if not cuda:
        out.extend(_rocm_devices())
    out.extend(_directml_devices())
    out.extend(_mps_device())
    out.append(_cpu_device())

    # Default: first non-cpu device if any, else cpu.
    default_idx = 0
    for i, d in enumerate(out):
        if d.kind != "cpu":
            default_idx = i; break
    for i, d in enumerate(out):
        d.is_default = (i == default_idx)
    return out


def format_display(d: DeviceInfo) -> str:
    """Human-readable label for the WPF dropdown."""
    if d.kind == "cpu":
        return f"cpu · {d.name}"
    gb = d.total_memory_bytes / (1024**3) if d.total_memory_bytes else 0
    parts = [d.id]
    if d.name: parts.append(d.name)
    if gb: parts.append(f"{gb:.0f} GB")
    if d.compute_capability: parts.append(d.compute_capability)
    return " · ".join(parts)


def main() -> int:
    """CLI: `stackscope-devices` prints the table the WPF dropdown shows."""
    import argparse
    import json
    p = argparse.ArgumentParser(prog="stackscope-devices",
        description="Enumerate accelerators visible to StackScope.")
    p.add_argument("--json", action="store_true")
    args = p.parse_args()
    devs = enumerate_devices()
    if args.json:
        print(json.dumps([d.__dict__ for d in devs], indent=2))
    else:
        for d in devs:
            marker = " (default)" if d.is_default else ""
            print(f"{format_display(d)}{marker}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
