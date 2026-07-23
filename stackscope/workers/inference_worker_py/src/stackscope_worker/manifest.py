"""Reproducibility manifest.

Every capture ships with a manifest that describes the exact software
environment it was recorded in. Bug reports without a manifest cannot
be repro'd — that is by design, and the bug-report template enforces
it.

Manifest fields are chosen so an AI reading a bug report can answer
"why doesn't this repro on my box" without asking the reporter more
questions.
"""
from __future__ import annotations

import getpass
import json
import os
import platform
import subprocess
import sys
from dataclasses import asdict, dataclass, field
from typing import Any


@dataclass
class ReproducibilityManifest:
    stackscope_version: str
    stackscope_build_sha: str
    python_version: str
    platform: str
    processor: str
    torch_version: str | None
    torch_cuda_version: str | None
    torch_cuda_available: bool
    torch_backends: list[str]
    transformers_version: str | None
    numpy_version: str | None
    cuda_toolkit_version: str | None
    cudnn_version: str | None
    rocm_version: str | None
    vulkan_sdk_version: str | None
    nvidia_driver_version: str | None
    amd_driver_version: str | None
    seed: int | None
    dtype: str | None
    quantization: str | None
    env_snapshot: dict[str, str] = field(default_factory=dict)
    user: str = ""
    hostname: str = ""


_RELEVANT_ENV_KEYS = (
    "CUDA_VISIBLE_DEVICES",
    "HIP_VISIBLE_DEVICES",
    "ROCR_VISIBLE_DEVICES",
    "PYTORCH_CUDA_ALLOC_CONF",
    "PYTORCH_ROCM_ARCH",
    "TORCH_LOGS",
    "TORCH_DYNAMO_VERBOSE",
    "OMP_NUM_THREADS",
    "MKL_NUM_THREADS",
    "TOKENIZERS_PARALLELISM",
    "TRANSFORMERS_CACHE",
    "HF_HOME",
    "VK_LOADER_DEBUG",
    "STACKSCOPE_DTYPE",
    "STACKSCOPE_QUANT",
    "STACKSCOPE_SEED",
)


def _try_import(name: str) -> Any | None:
    try:
        return __import__(name)
    except Exception:
        return None


def _capture_env() -> dict[str, str]:
    return {k: os.environ[k] for k in _RELEVANT_ENV_KEYS if k in os.environ}


def _run(cmd: list[str]) -> str | None:
    """Run a subprocess best-effort, return stripped stdout or None."""
    try:
        out = subprocess.run(cmd, capture_output=True, text=True, timeout=5, check=False)
        if out.returncode != 0:
            return None
        return out.stdout.strip() or None
    except (OSError, subprocess.TimeoutExpired):
        return None


def _nvidia_driver_version() -> str | None:
    v = _run(["nvidia-smi", "--query-gpu=driver_version", "--format=csv,noheader"])
    return v.splitlines()[0] if v else None


def _cuda_toolkit_version() -> str | None:
    v = _run(["nvcc", "--version"])
    if not v:
        return None
    for line in v.splitlines():
        if "release" in line.lower():
            return line.strip()
    return v.splitlines()[-1] if v else None


def _rocm_version() -> str | None:
    return _run(["rocminfo"]) and _run(["cat", "/opt/rocm/.info/version"]) or _run(
        ["cat", "/opt/rocm/.info/version"])


def _amd_driver_version() -> str | None:
    return _run(["rocm-smi", "--showdriverversion"])


def _vulkan_sdk_version() -> str | None:
    v = _run(["vulkaninfo", "--summary"])
    if not v:
        return os.environ.get("VULKAN_SDK_VERSION")
    for line in v.splitlines():
        if "Vulkan Instance Version" in line:
            return line.split(":")[-1].strip()
    return None


def _cudnn_version(torch_mod: Any | None) -> str | None:
    if torch_mod is None:
        return None
    try:
        v = torch_mod.backends.cudnn.version()
        return str(v) if v else None
    except Exception:
        return None


def build(
    stackscope_version: str = "0.1.0",
    build_sha: str = "unknown",
    seed: int | None = None,
    dtype: str | None = None,
    quantization: str | None = None,
) -> ReproducibilityManifest:
    """Snapshot the running environment into a manifest."""
    torch = _try_import("torch")
    transformers = _try_import("transformers")
    numpy = _try_import("numpy")

    torch_backends: list[str] = []
    torch_cuda_avail = False
    torch_cuda_version: str | None = None
    if torch is not None:
        try:
            torch_cuda_avail = bool(torch.cuda.is_available())
            torch_cuda_version = getattr(torch.version, "cuda", None)
            if torch_cuda_avail:
                torch_backends.append("cuda")
            if getattr(torch.backends, "mps", None) and torch.backends.mps.is_available():
                torch_backends.append("mps")
            if getattr(torch.backends, "openmp", None):
                torch_backends.append("openmp")
        except Exception:
            pass

    return ReproducibilityManifest(
        stackscope_version=stackscope_version,
        stackscope_build_sha=build_sha,
        python_version=sys.version.split()[0],
        platform=platform.platform(),
        processor=platform.processor(),
        torch_version=getattr(torch, "__version__", None) if torch else None,
        torch_cuda_version=torch_cuda_version,
        torch_cuda_available=torch_cuda_avail,
        torch_backends=torch_backends,
        transformers_version=getattr(transformers, "__version__", None) if transformers else None,
        numpy_version=getattr(numpy, "__version__", None) if numpy else None,
        cuda_toolkit_version=_cuda_toolkit_version(),
        cudnn_version=_cudnn_version(torch),
        rocm_version=_rocm_version(),
        vulkan_sdk_version=_vulkan_sdk_version(),
        nvidia_driver_version=_nvidia_driver_version(),
        amd_driver_version=_amd_driver_version(),
        seed=seed,
        dtype=dtype,
        quantization=quantization,
        env_snapshot=_capture_env(),
        user=getpass.getuser() if hasattr(getpass, "getuser") else "",
        hostname=platform.node(),
    )


def to_json(m: ReproducibilityManifest) -> str:
    return json.dumps(asdict(m), indent=2, sort_keys=True)


def from_json(text: str) -> ReproducibilityManifest:
    return ReproducibilityManifest(**json.loads(text))


def main() -> int:
    """CLI: `stackscope-manifest --emit` prints JSON to stdout."""
    import argparse
    parser = argparse.ArgumentParser(prog="stackscope-manifest")
    parser.add_argument("--emit", action="store_true",
                        help="Print manifest as JSON to stdout.")
    parser.add_argument("--seed", type=int, default=None)
    parser.add_argument("--dtype", default=None)
    parser.add_argument("--quant", default=None)
    args = parser.parse_args()
    m = build(seed=args.seed, dtype=args.dtype, quantization=args.quant)
    if args.emit:
        print(to_json(m))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
