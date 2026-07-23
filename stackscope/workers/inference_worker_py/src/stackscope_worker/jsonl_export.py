"""JSONL export — canonical, git-diffable representation of a capture.

The mmap binary format is fast to write and cheap to memory-map, but
it is opaque. JSONL is human-readable, diffable, and pastable into an
AI chat. This module converts between them losslessly.

Each JSONL line is one event with all fields:

    {"eid": 42, "ts": 1700000000123, "kind": "LAYER_BEGIN",
     "token": 0, "layer": 3, "head": -1, "payload_hex": "..."}
"""
from __future__ import annotations

import argparse
import binascii
import json
import sys
from typing import Iterable, TextIO

from .hooks import Event, Kind


_KIND_TO_NAME = {v: k for k, v in vars(Kind).items() if isinstance(v, int)}
_NAME_TO_KIND = {v: k for k, v in _KIND_TO_NAME.items()}


def event_to_dict(e: Event) -> dict:
    return {
        "ts":            e.timestamp_ns,
        "kind":          _KIND_TO_NAME.get(e.kind, str(e.kind)),
        "token":         e.token_index,
        "layer":         e.layer_index,
        "head":          e.head_index,
        "thread":        e.thread_id,
        "stream":        e.stream_id,
        "device":        e.device_id,
        "marker_name":   e.marker_name,
        "marker_begin":  e.marker_begin_ns,
        "marker_end":    e.marker_end_ns,
        "marker_corr":   e.marker_correlation_id,
        "payload_hex":   binascii.hexlify(e.payload).decode("ascii") if e.payload else "",
    }


def dict_to_event(d: dict) -> Event:
    kind = d["kind"]
    kind_int = _NAME_TO_KIND[kind] if isinstance(kind, str) else int(kind)
    payload = binascii.unhexlify(d.get("payload_hex", "").encode("ascii")) if d.get("payload_hex") else b""
    return Event(
        kind=kind_int,
        timestamp_ns=d["ts"],
        token_index=d.get("token", -1),
        layer_index=d.get("layer", -1),
        head_index=d.get("head", -1),
        thread_id=d.get("thread", 0),
        stream_id=d.get("stream", -1),
        device_id=d.get("device", -1),
        payload=payload,
        marker_name=d.get("marker_name"),
        marker_begin_ns=d.get("marker_begin", 0),
        marker_end_ns=d.get("marker_end", 0),
        marker_correlation_id=d.get("marker_corr", 0),
    )


def write_jsonl(events: Iterable[Event], out: TextIO) -> int:
    """Write events as JSONL. Returns the count written."""
    n = 0
    for e in events:
        out.write(json.dumps(event_to_dict(e), separators=(",", ":"), sort_keys=True))
        out.write("\n")
        n += 1
    return n


def read_jsonl(inp: TextIO) -> list[Event]:
    return [dict_to_event(json.loads(line)) for line in inp if line.strip()]


def canonical_hash(events: Iterable[Event]) -> str:
    """Stable hash of an ordered event sequence — used by golden tests.

    Timestamps and thread ids are excluded because they are not
    portable across runs. Everything else is included."""
    import hashlib
    h = hashlib.sha256()
    for e in events:
        h.update(bytes([e.kind]))
        h.update(int(e.token_index).to_bytes(4, "little", signed=True))
        h.update(int(e.layer_index).to_bytes(4, "little", signed=True))
        h.update(int(e.head_index).to_bytes(4, "little", signed=True))
        h.update(e.payload)
        h.update((e.marker_name or "").encode("utf-8"))
    return h.hexdigest()


def main() -> int:
    p = argparse.ArgumentParser(prog="stackscope-jsonl",
                                description="Convert between binary events and JSONL.")
    sub = p.add_subparsers(dest="cmd", required=True)

    hexp = sub.add_parser("canonical-hash",
                          help="Print the canonical hash of a JSONL stream.")
    hexp.add_argument("path", nargs="?", default="-")

    args = p.parse_args()
    if args.cmd == "canonical-hash":
        if args.path == "-":
            events = read_jsonl(sys.stdin)
        else:
            with open(args.path, encoding="utf-8") as f:
                events = read_jsonl(f)
        print(canonical_hash(events))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
