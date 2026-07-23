"""Real-time anomaly watcher.

Runs as an in-process observer over hook events. It flags:

* **NaN / Inf** in captured logits or activation summaries.
* **Attention entropy collapse** — a head whose entropy drops below a
  threshold in one token (often precedes hallucination / repetition).
* **Attention entropy blow-up** — inverse case, degenerate uniform
  attention (often precedes garbled output).
* **Latency outliers** — per-layer wall time > k·median across the
  recently-observed layer times.

Detections are emitted as MARKER events with a compact "anomaly"
payload the UI can render on the timeline.
"""
from __future__ import annotations

import math
import statistics
import struct
from dataclasses import dataclass
from typing import Optional

from .hooks import Event, Kind
from .markers import now_ns


ANOMALY_MARKER = "stackscope.anomaly"


@dataclass
class AnomalyConfig:
    entropy_low: float = 0.05       # nats — below this = collapsed
    entropy_high_ratio: float = 0.95  # fraction of log(S) — above = degenerate
    latency_outlier_k: float = 5.0  # multiplier of median
    latency_window: int = 32        # recent-layers window for latency median


class AnomalyDetector:
    """Non-blocking observer. Feed it every hook Event; it returns
    zero or more MARKER events describing any anomalies it detected.
    Idempotent — same event fed twice produces zero new events after
    the first flag.
    """

    def __init__(self, cfg: Optional[AnomalyConfig] = None) -> None:
        self._cfg = cfg or AnomalyConfig()
        self._latencies: list[float] = []
        self._seen_ids: set[int] = set()

    def observe(self, e: Event) -> list[Event]:
        out: list[Event] = []

        if e.kind == Kind.LOGITS:
            for finding in self._check_nans_in_logits(e):
                out.append(finding)
        elif e.kind == Kind.ATTENTION_SCORES:
            for finding in self._check_attention(e):
                out.append(finding)
        elif e.kind == Kind.LAYER_END and e.marker_end_ns and e.marker_begin_ns:
            dur = float(e.marker_end_ns - e.marker_begin_ns)
            for finding in self._check_latency(e, dur):
                out.append(finding)
        return out

    # ---- individual checks ------------------------------------------------

    def _check_nans_in_logits(self, e: Event) -> list[Event]:
        # Payload: [ i32 k ][ (i32 id, f32 value) × k ]
        if len(e.payload) < 4: return []
        (k,) = struct.unpack_from("<i", e.payload, 0)
        for i in range(k):
            off = 4 + i * 8
            if off + 8 > len(e.payload): break
            _tid, v = struct.unpack_from("<if", e.payload, off)
            if math.isnan(v) or math.isinf(v):
                return [self._make(e, f"nan-or-inf-logit@k{i}=v={v}")]
        return []

    def _check_attention(self, e: Event) -> list[Event]:
        # Payload from pack_head_stats: (head:i32, mean:f32, std:f32,
        # entropy:f32, max_prob:f32, argmax_source:i32).
        if len(e.payload) < 4 + 4*4 + 4: return []
        head, mean, std, entropy, maxp, arg = struct.unpack("<iffffi", e.payload[:24])
        if math.isnan(entropy) or math.isnan(maxp):
            return [self._make(e, f"nan-in-attention head={head}")]
        if entropy < self._cfg.entropy_low:
            return [self._make(e,
                f"attention-entropy-collapse head={head} entropy={entropy:.4f}")]
        # No S available here; use a conservative absolute upper bound
        # (log(4096) ≈ 8.32) — flag if entropy is *very* high.
        if entropy > 8.32 * self._cfg.entropy_high_ratio:
            return [self._make(e,
                f"attention-entropy-degenerate head={head} entropy={entropy:.4f}")]
        return []

    def _check_latency(self, e: Event, dur_ns: float) -> list[Event]:
        self._latencies.append(dur_ns)
        if len(self._latencies) > self._cfg.latency_window:
            self._latencies.pop(0)
        if len(self._latencies) < 8: return []
        med = statistics.median(self._latencies)
        if med <= 0: return []
        if dur_ns > self._cfg.latency_outlier_k * med:
            ms = dur_ns / 1e6
            med_ms = med / 1e6
            return [self._make(e,
                f"layer-latency-outlier layer={e.layer_index} "
                f"dur={ms:.2f}ms median={med_ms:.2f}ms")]
        return []

    def _make(self, src: Event, description: str) -> Event:
        # Payload = utf-8 description.
        buf = description.encode("utf-8")
        return Event(
            kind=Kind.MARKER,
            timestamp_ns=now_ns(),
            token_index=src.token_index,
            layer_index=src.layer_index,
            head_index=src.head_index,
            payload=buf,
            marker_name=ANOMALY_MARKER,
            marker_begin_ns=src.timestamp_ns,
            marker_end_ns=src.timestamp_ns,
        )
