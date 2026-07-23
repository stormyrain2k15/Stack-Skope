"""Monotonic host-clock timestamps in nanoseconds and (opt-in) NVTX/rocTX
range wrappers. This module is the single place workers get timestamps
from, so every event across the worker is on the same clock."""
from __future__ import annotations

import contextlib
import ctypes
import ctypes.util
import time
from typing import Iterator


def now_ns() -> int:
    """Return monotonic host clock in nanoseconds.

    Uses time.perf_counter_ns which is guaranteed monotonic and has the
    finest resolution the platform provides. This is the same clock
    domain the coordinator uses for ordering events.
    """
    return time.perf_counter_ns()


# ---------------------------------------------------------------------------
# NVTX / rocTX. We resolve the shared libraries lazily; if unavailable, the
# context managers are no-ops. This lets the worker run on machines without
# CUDA/ROCm at all.
# ---------------------------------------------------------------------------

_nvtx_lib = None
_roctx_lib = None
_next_corr_id = 1


def _load_nvtx() -> ctypes.CDLL | None:
    global _nvtx_lib
    if _nvtx_lib is not None:
        return _nvtx_lib
    for candidate in ("nvToolsExt64_1", "nvToolsExt", "libnvToolsExt.so", "libnvToolsExt.so.1"):
        try:
            _nvtx_lib = ctypes.CDLL(candidate)
            _nvtx_lib.nvtxRangePushA.argtypes = [ctypes.c_char_p]
            _nvtx_lib.nvtxRangePushA.restype = ctypes.c_int
            _nvtx_lib.nvtxRangePop.argtypes = []
            _nvtx_lib.nvtxRangePop.restype = ctypes.c_int
            return _nvtx_lib
        except OSError:
            continue
    _nvtx_lib = None
    return None


def _load_roctx() -> ctypes.CDLL | None:
    global _roctx_lib
    if _roctx_lib is not None:
        return _roctx_lib
    for candidate in ("libroctx64.so", "libroctx.so", "roctx64.dll"):
        try:
            _roctx_lib = ctypes.CDLL(candidate)
            _roctx_lib.roctxRangePushA.argtypes = [ctypes.c_char_p]
            _roctx_lib.roctxRangePushA.restype = ctypes.c_int
            _roctx_lib.roctxRangePop.argtypes = []
            _roctx_lib.roctxRangePop.restype = ctypes.c_int
            return _roctx_lib
        except OSError:
            continue
    _roctx_lib = None
    return None


def next_correlation_id() -> int:
    """Monotonically increasing per-process ID used to link marker events
    with driver-level kernel launches."""
    global _next_corr_id
    _next_corr_id += 1
    return _next_corr_id


@contextlib.contextmanager
def range_marker(name: str) -> Iterator[int]:
    """Context manager that pushes a marker into the active profiler
    (NVTX on NVIDIA, rocTX on AMD). Yields the correlation id that was
    generated for this range so the caller can attach it to the emitted
    event.
    """
    corr = next_correlation_id()
    nvtx = _load_nvtx()
    roctx = _load_roctx()
    encoded = name.encode("utf-8")
    if nvtx is not None:
        nvtx.nvtxRangePushA(encoded)
    if roctx is not None:
        roctx.roctxRangePushA(encoded)
    try:
        yield corr
    finally:
        if nvtx is not None:
            nvtx.nvtxRangePop()
        if roctx is not None:
            roctx.roctxRangePop()
