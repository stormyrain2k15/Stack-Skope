"""Per-head attention disaggregation + activation tensor arena.

The Python worker's default hooks (see :mod:`hooks`) capture attention
at the q_proj / k_proj / v_proj / o_proj granularity. That gives you
"something moved in this layer" but not "head 7 specifically". This
module upgrades capture with two techniques:

1. **Per-head attention weights**. We wrap the top-level attention
   module (``LlamaAttention``, ``MistralAttention``, ``GPT2Attention``,
   ...) and force ``output_attentions=True`` via a kwargs pre-hook.
   The returned ``attn_weights`` tensor is ``[B, n_heads, S, S]``; we
   emit one ATTENTION_SCORES event per head with real statistics
   (mean, std, entropy) as the payload.

2. **Activation tensor arena**. Every hook-captured activation, plus
   every per-head attention slice, is written into a per-transaction
   memory-mapped tensor file and referenced by (transaction_id,
   event_id, byte_offset, byte_length). The gRPC ``ReadTensor`` RPC
   dereferences that reference. This is the *forensic* capture level
   from the plan and is the piece the plan's earlier pass honestly
   deferred as "session cache".
"""
from __future__ import annotations

import math
import os
import struct
import threading
from dataclasses import dataclass
from typing import Optional

import numpy as np
import torch
from torch import nn


# ---------------------------------------------------------------------------
# Arena
# ---------------------------------------------------------------------------

@dataclass
class ArenaSlice:
    """Location + type metadata of a tensor slice inside the arena."""
    txid: str
    event_id: int
    byte_offset: int
    byte_length: int
    dtype: str
    shape: tuple[int, ...]


class TensorArena:
    """Per-transaction tensor arena. Writes are append-only; reads use
    seek+read against the same file handle. Thread-safe.

    On teardown the arena is closed but not deleted — captures are
    valuable and stay resident on disk until the user prunes them.
    """

    _HEADER = b"SSTA\x01\x00\x00\x00"  # 'SSTA' + version 1

    def __init__(self, transaction_id: str, storage_dir: str) -> None:
        os.makedirs(storage_dir, exist_ok=True)
        self._path = os.path.join(storage_dir, f"{transaction_id}.tensors")
        self._txid = transaction_id
        self._lock = threading.Lock()
        # Open in a+b so the file is created on first write and we can
        # still read our own committed bytes for readback.
        newfile = not os.path.exists(self._path)
        self._fp = open(self._path, "a+b", buffering=0)
        if newfile:
            self._fp.write(self._HEADER)
            self._fp.flush()
        self._fp.seek(0, os.SEEK_END)
        self._length = self._fp.tell()
        self._slices: dict[int, ArenaSlice] = {}

    @property
    def path(self) -> str: return self._path

    def write(self, event_id: int, arr: np.ndarray) -> ArenaSlice:
        """Persist an activation slice; return its addressable location."""
        with self._lock:
            data = arr.tobytes(order="C")
            offset = self._length
            self._fp.write(data)
            self._fp.flush()
            self._length += len(data)
            s = ArenaSlice(
                txid=self._txid,
                event_id=event_id,
                byte_offset=offset,
                byte_length=len(data),
                dtype=str(arr.dtype),
                shape=tuple(int(x) for x in arr.shape),
            )
            self._slices[event_id] = s
            return s

    def read(self, event_id: int) -> tuple[bytes, str, tuple[int, ...]]:
        with self._lock:
            s = self._slices.get(event_id)
            if s is None:
                raise KeyError(f"No tensor slice for event_id={event_id}")
            # We can't move the write cursor around freely on all
            # platforms while in append mode; use os.pread on POSIX
            # and a locked seek+read fallback on Windows.
            if hasattr(os, "pread"):
                data = os.pread(self._fp.fileno(), s.byte_length, s.byte_offset)
            else:
                cur = self._fp.tell()
                self._fp.seek(s.byte_offset)
                data = self._fp.read(s.byte_length)
                self._fp.seek(cur)
            return data, s.dtype, s.shape

    def close(self) -> None:
        with self._lock:
            try: self._fp.flush()
            finally: self._fp.close()


# ---------------------------------------------------------------------------
# Per-head attention capture
# ---------------------------------------------------------------------------

_ATTN_MODULE_HINTS = (
    "LlamaAttention", "LlamaSdpaAttention", "LlamaFlashAttention2",
    "MistralAttention", "MistralSdpaAttention",
    "MixtralAttention", "MixtralSdpaAttention",
    "GemmaAttention", "GemmaSdpaAttention", "Gemma2Attention",
    "Qwen2Attention", "Qwen2SdpaAttention",
    "GPT2Attention", "GPTNeoAttention", "FalconAttention",
    "PhiAttention", "MptAttention",
)


def is_top_level_attention(module: nn.Module) -> bool:
    """Heuristic: match the top-level attention module by class name."""
    name = type(module).__name__
    return name in _ATTN_MODULE_HINTS


@dataclass
class HeadStats:
    """Per-head summary carried in the ATTENTION_SCORES payload."""
    head: int
    mean: float
    std:  float
    entropy: float          # softmax attention entropy (nats)
    max_prob: float
    argmax_source: int      # index of the source token receiving max weight


def compute_head_stats(attn_weights: torch.Tensor) -> list[HeadStats]:
    """Given ``[B, n_heads, S, S]`` attention weights, return per-head
    statistics summarising the distribution for the *last* query position
    (i.e. the one whose output is being emitted this step).
    """
    if attn_weights.ndim != 4:
        return []
    with torch.no_grad():
        # Focus on the last query row so cost is O(n_heads * S), not S^2.
        w = attn_weights[0, :, -1, :].float()  # [n_heads, S]
        # Sum to 1 already for softmax'd weights; guard in case of raw scores.
        row_sums = w.sum(dim=-1, keepdim=True)
        w = torch.where(row_sums > 0, w / row_sums, w)
        mean = w.mean(dim=-1)
        std  = w.std(dim=-1)
        eps  = 1e-12
        ent  = -(w * (w + eps).log()).sum(dim=-1)
        maxp, argmax = w.max(dim=-1)
    n_heads = w.shape[0]
    return [
        HeadStats(
            head=h,
            mean=float(mean[h]),
            std=float(std[h]),
            entropy=float(ent[h]),
            max_prob=float(maxp[h]),
            argmax_source=int(argmax[h]),
        )
        for h in range(n_heads)
    ]


def pack_head_stats(stats: HeadStats) -> bytes:
    """Payload layout for ATTENTION_SCORES per-head events."""
    return struct.pack(
        "<iffffi",
        stats.head, stats.mean, stats.std, stats.entropy,
        stats.max_prob, stats.argmax_source,
    )
