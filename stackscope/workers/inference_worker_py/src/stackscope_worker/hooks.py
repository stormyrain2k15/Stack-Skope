"""PyTorch forward-hook capture.

Every ``nn.Module`` in the loaded model gets a forward hook. Hooks emit
domain events (:mod:`stackscope_worker.events`) which are pushed into a
per-transaction queue that the gRPC layer drains.

Attention Q/K/V/output projection capture is done by installing extra
hooks on the ``q_proj``, ``k_proj``, ``v_proj``, ``o_proj`` submodules
identified by name — this is how HuggingFace canonicalises them for the
Llama/Mistral/Gemma/Qwen families. GPT-2 uses a fused ``c_attn`` so we
handle that separately.
"""
from __future__ import annotations

import dataclasses
import queue
import threading
from typing import Callable, Optional

import torch
from torch import nn

from .markers import now_ns, next_correlation_id, range_marker


# ---------------------------------------------------------------------------
# Event dataclasses — one Python-side representation per proto EventKind.
# The gRPC layer converts these into protobuf messages before shipping them.
# ---------------------------------------------------------------------------

@dataclasses.dataclass
class Event:
    kind: int
    timestamp_ns: int
    token_index: int = -1
    layer_index: int = -1
    head_index: int = -1
    thread_id: int = 0
    stream_id: int = -1
    device_id: int = -1
    payload: bytes = b""
    marker_name: Optional[str] = None
    marker_begin_ns: int = 0
    marker_end_ns: int = 0
    marker_correlation_id: int = 0


# EventKind numeric constants — must match proto/events.proto.
class Kind:
    UNKNOWN = 0
    TOKEN_BEGIN = 1
    TOKEN_END = 2
    LAYER_BEGIN = 3
    LAYER_END = 4
    ATTENTION_QKV = 5
    ATTENTION_SCORES = 6
    ATTENTION_OUTPUT = 7
    ACTIVATION = 8
    TENSOR_READ = 9
    TENSOR_WRITE = 10
    LOGITS = 16
    SAMPLE = 17
    MARKER = 18


# ---------------------------------------------------------------------------
# Layer index inference. HuggingFace models expose the block index via the
# module name (e.g. "model.layers.7.self_attn.q_proj"). We parse that once
# when the hook is installed so hook callbacks don't pay string cost.
# ---------------------------------------------------------------------------

def infer_layer_index(module_name: str) -> int:
    parts = module_name.split(".")
    for i, p in enumerate(parts):
        if p in ("layers", "h", "encoder", "decoder") and i + 1 < len(parts):
            try:
                return int(parts[i + 1])
            except ValueError:
                return -1
    return -1


def is_attention_projection(module_name: str) -> Optional[str]:
    if module_name.endswith(".q_proj"): return "q_proj"
    if module_name.endswith(".k_proj"): return "k_proj"
    if module_name.endswith(".v_proj"): return "v_proj"
    if module_name.endswith(".o_proj"): return "o_proj"
    if module_name.endswith(".c_attn"): return "c_attn"     # GPT-2 fused
    if module_name.endswith(".c_proj"): return "o_proj"     # GPT-2
    return None


# ---------------------------------------------------------------------------
# HookCapture: attaches to a model and streams events to a queue.
# ---------------------------------------------------------------------------

class HookCapture:
    """Installs forward hooks over an entire model.

    Instantiate once, call :meth:`attach` with the loaded model, then
    drain events via :meth:`events` (thread-safe). Call :meth:`detach`
    when done.
    """

    def __init__(self, capture_attention: bool, capture_activations: bool) -> None:
        self._capture_attention = capture_attention
        self._capture_activations = capture_activations
        self._q: queue.Queue[Event] = queue.Queue(maxsize=65536)
        self._handles: list[torch.utils.hooks.RemovableHandle] = []
        self._current_token = -1
        self._current_layer_stack: list[int] = []
        self._lock = threading.Lock()

    def attach(self, model: nn.Module) -> None:
        for name, module in model.named_modules():
            if name == "":
                continue
            layer_idx = infer_layer_index(name)
            proj = is_attention_projection(name)

            pre_hook = self._make_pre_hook(name, module, layer_idx, proj)
            post_hook = self._make_post_hook(name, module, layer_idx, proj)

            self._handles.append(module.register_forward_pre_hook(pre_hook, with_kwargs=True))
            self._handles.append(module.register_forward_hook(post_hook, with_kwargs=True))

    def detach(self) -> None:
        for h in self._handles:
            h.remove()
        self._handles.clear()

    def note_token_begin(self, token_index: int) -> None:
        self._current_token = token_index
        self._q.put(Event(
            kind=Kind.TOKEN_BEGIN,
            timestamp_ns=now_ns(),
            token_index=token_index,
        ))

    def note_token_end(self, token_index: int, sampled_id: int, logit_top1: float) -> None:
        payload = sampled_id.to_bytes(4, "little", signed=True) + \
            _f32_bytes(logit_top1)
        self._q.put(Event(
            kind=Kind.TOKEN_END,
            timestamp_ns=now_ns(),
            token_index=token_index,
            payload=payload,
        ))
        self._current_token = -1

    def note_logits(self, token_index: int, logits: torch.Tensor) -> None:
        # Compress: only top-8 (index, value) pairs so we don't ship the
        # whole vocab per token when only the top matters for the UI.
        with torch.no_grad():
            k = min(8, logits.shape[-1])
            top = torch.topk(logits.reshape(-1), k)
            idxs = top.indices.tolist()
            vals = top.values.tolist()
        payload = bytearray()
        payload += k.to_bytes(4, "little", signed=False)
        for i, v in zip(idxs, vals):
            payload += int(i).to_bytes(4, "little", signed=True) + _f32_bytes(float(v))
        self._q.put(Event(
            kind=Kind.LOGITS,
            timestamp_ns=now_ns(),
            token_index=token_index,
            payload=bytes(payload),
        ))

    def events(self, timeout: float = 0.05) -> list[Event]:
        """Drain up to a burst of events. Non-blocking on empty queue."""
        out: list[Event] = []
        try:
            out.append(self._q.get(timeout=timeout))
        except queue.Empty:
            return out
        while True:
            try:
                out.append(self._q.get_nowait())
            except queue.Empty:
                return out

    # -- internal hook factories -------------------------------------------

    def _make_pre_hook(
        self, name: str, module: nn.Module, layer_idx: int, proj: Optional[str]
    ) -> Callable:
        def _hook(mod, args, kwargs):
            corr = next_correlation_id()
            with range_marker(f"stackscope.{name}"):
                pass
            # The range_marker above only pushes/pops a point marker; we
            # emit the range as a paired begin/end via LAYER_BEGIN /
            # LAYER_END events. The correlation id is stored on the
            # module so the post hook can complete the range.
            mod._stackscope_marker_begin = now_ns()  # type: ignore[attr-defined]
            mod._stackscope_marker_corr = corr       # type: ignore[attr-defined]

            if layer_idx >= 0 and proj is None:
                self._q.put(Event(
                    kind=Kind.LAYER_BEGIN,
                    timestamp_ns=mod._stackscope_marker_begin,  # type: ignore[attr-defined]
                    token_index=self._current_token,
                    layer_index=layer_idx,
                    marker_name=f"layer.{layer_idx}",
                    marker_begin_ns=mod._stackscope_marker_begin,  # type: ignore[attr-defined]
                    marker_correlation_id=corr,
                ))
                self._current_layer_stack.append(layer_idx)
        return _hook

    def _make_post_hook(
        self, name: str, module: nn.Module, layer_idx: int, proj: Optional[str]
    ) -> Callable:
        def _hook(mod, args, kwargs, output):
            end_ns = now_ns()
            begin_ns = getattr(mod, "_stackscope_marker_begin", end_ns)
            corr = getattr(mod, "_stackscope_marker_corr", 0)

            if proj is not None and self._capture_attention:
                self._emit_attention(name, proj, layer_idx, output, begin_ns, end_ns, corr)
            elif layer_idx >= 0 and proj is None:
                self._q.put(Event(
                    kind=Kind.LAYER_END,
                    timestamp_ns=end_ns,
                    token_index=self._current_token,
                    layer_index=layer_idx,
                    marker_name=f"layer.{layer_idx}",
                    marker_begin_ns=begin_ns,
                    marker_end_ns=end_ns,
                    marker_correlation_id=corr,
                ))
                if self._current_layer_stack and self._current_layer_stack[-1] == layer_idx:
                    self._current_layer_stack.pop()
            elif self._capture_activations:
                # Non-attention, non-block leaf — activation event.
                shape = _shape_of(output)
                dtype = _dtype_of(output)
                payload = _pack_activation_summary(shape, dtype)
                self._q.put(Event(
                    kind=Kind.ACTIVATION,
                    timestamp_ns=end_ns,
                    token_index=self._current_token,
                    layer_index=layer_idx if layer_idx >= 0 else -1,
                    payload=payload,
                    marker_name=name,
                    marker_begin_ns=begin_ns,
                    marker_end_ns=end_ns,
                    marker_correlation_id=corr,
                ))
        return _hook

    def _emit_attention(
        self,
        module_name: str, proj: str, layer_idx: int,
        output, begin_ns: int, end_ns: int, corr: int,
    ) -> None:
        # For q/k/v projections we emit ATTENTION_QKV; for o_proj we emit
        # ATTENTION_OUTPUT. Payload: proj tag byte + shape encoding.
        kind = Kind.ATTENTION_OUTPUT if proj == "o_proj" else Kind.ATTENTION_QKV
        shape = _shape_of(output)
        proj_tag = {"q_proj": 0, "k_proj": 1, "v_proj": 2, "o_proj": 3, "c_attn": 4}.get(proj, 5)
        payload = bytes([proj_tag]) + _pack_shape(shape)
        self._q.put(Event(
            kind=kind,
            timestamp_ns=end_ns,
            token_index=self._current_token,
            layer_index=layer_idx,
            head_index=-1,  # per-head split happens on the attention layer, not the linear
            payload=payload,
            marker_name=module_name,
            marker_begin_ns=begin_ns,
            marker_end_ns=end_ns,
            marker_correlation_id=corr,
        ))


# ---------------------------------------------------------------------------
# Payload packing helpers
# ---------------------------------------------------------------------------

def _shape_of(t) -> tuple[int, ...]:
    if isinstance(t, torch.Tensor):
        return tuple(t.shape)
    if isinstance(t, (tuple, list)):
        for x in t:
            if isinstance(x, torch.Tensor):
                return tuple(x.shape)
    return ()


def _dtype_of(t) -> str:
    if isinstance(t, torch.Tensor):
        return str(t.dtype).replace("torch.", "")
    if isinstance(t, (tuple, list)):
        for x in t:
            if isinstance(x, torch.Tensor):
                return str(x.dtype).replace("torch.", "")
    return "unknown"


def _pack_shape(shape: tuple[int, ...]) -> bytes:
    out = bytearray()
    out += len(shape).to_bytes(2, "little", signed=False)
    for dim in shape:
        out += int(dim).to_bytes(8, "little", signed=True)
    return bytes(out)


def _pack_activation_summary(shape: tuple[int, ...], dtype: str) -> bytes:
    d = dtype.encode("utf-8")
    return len(d).to_bytes(2, "little") + d + _pack_shape(shape)


def _f32_bytes(x: float) -> bytes:
    import struct
    return struct.pack("<f", x)
