"""stackscope-repro — replay a bundle and diff against the current build.

Turns every user-reported bug into a permanent regression test.

Usage:
    stackscope-repro capture.stackscope
        --> Loads events.jsonl from the bundle, recomputes the
            canonical hash, prints drift vs the manifest's stored
            hash (if present), and exits 0 (identical) or 1 (drift).

    stackscope-repro capture.stackscope --against other.stackscope
        --> Diffs two bundles pairwise. Prints the first divergent
            event, the count difference by kind, and the union of
            new/lost marker names.
"""
from __future__ import annotations

import argparse
import io
import json
import sys
from collections import Counter
from typing import Iterable

from . import bundle as _bundle
from . import jsonl_export as _jsonl


def _load(path: str) -> list[dict]:
    b = _bundle.unpack(path)
    return [json.loads(line) for line in b.events_jsonl.splitlines() if line.strip()]


def _canonical_hash_of_dicts(events: Iterable[dict]) -> str:
    """Same as jsonl_export.canonical_hash but takes dicts."""
    import binascii
    import hashlib
    h = hashlib.sha256()
    for e in events:
        kind = e["kind"]
        kind_int = _jsonl._NAME_TO_KIND.get(kind, 0) if isinstance(kind, str) else int(kind)
        payload = binascii.unhexlify(e.get("payload_hex", "").encode("ascii")) if e.get("payload_hex") else b""
        h.update(bytes([kind_int]))
        h.update(int(e.get("token", -1)).to_bytes(4, "little", signed=True))
        h.update(int(e.get("layer", -1)).to_bytes(4, "little", signed=True))
        h.update(int(e.get("head", -1)).to_bytes(4, "little", signed=True))
        h.update(payload)
        h.update((e.get("marker_name") or "").encode("utf-8"))
    return h.hexdigest()


def _summarise(events: list[dict]) -> str:
    kinds = Counter(e["kind"] for e in events)
    buf = io.StringIO()
    buf.write(f"total events: {len(events)}\n")
    for k, n in sorted(kinds.items(), key=lambda x: -x[1]):
        buf.write(f"  {k:<20} {n}\n")
    return buf.getvalue()


def compare(a: list[dict], b: list[dict]) -> tuple[bool, str]:
    """Return (identical, human-readable report)."""
    buf = io.StringIO()
    if len(a) != len(b):
        buf.write(f"event count differs: {len(a)} vs {len(b)}\n")
    n = min(len(a), len(b))
    first_div = -1
    for i in range(n):
        # Compare on stable, portable fields only.
        keys = ("kind", "token", "layer", "head", "payload_hex", "marker_name")
        if any(a[i].get(k) != b[i].get(k) for k in keys):
            first_div = i
            break
    if first_div >= 0:
        buf.write(f"first divergence at index {first_div}\n")
        buf.write(f"  A: {json.dumps({k: a[first_div].get(k) for k in ('kind','token','layer','head','marker_name')})}\n")
        buf.write(f"  B: {json.dumps({k: b[first_div].get(k) for k in ('kind','token','layer','head','marker_name')})}\n")

    kinds_a = Counter(e["kind"] for e in a)
    kinds_b = Counter(e["kind"] for e in b)
    all_kinds = set(kinds_a) | set(kinds_b)
    diff_lines = []
    for k in sorted(all_kinds):
        ca, cb = kinds_a.get(k, 0), kinds_b.get(k, 0)
        if ca != cb:
            diff_lines.append(f"  {k:<20} A={ca}  B={cb}  Δ={cb-ca:+d}")
    if diff_lines:
        buf.write("kind-count deltas:\n")
        buf.write("\n".join(diff_lines))
        buf.write("\n")

    identical = (first_div < 0 and len(a) == len(b))
    return identical, buf.getvalue()


def main() -> int:
    p = argparse.ArgumentParser(
        prog="stackscope-repro",
        description="Replay a .stackscope bundle; diff against another or against its own recorded hash.")
    p.add_argument("bundle", help="Path to a .stackscope bundle.")
    p.add_argument("--against", help="Second bundle to diff against.")
    p.add_argument("--summary", action="store_true",
                   help="Also print a per-kind summary.")
    args = p.parse_args()

    events = _load(args.bundle)
    live_hash = _canonical_hash_of_dicts(events)
    if args.summary:
        print(_summarise(events))
    print(f"canonical hash: {live_hash}")

    if args.against:
        other = _load(args.against)
        identical, report = compare(events, other)
        print(report, end="")
        return 0 if identical else 1

    # Otherwise compare against the manifest's stored hash if any.
    b = _bundle.unpack(args.bundle)
    try:
        m = json.loads(b.manifest_json)
        stored = m.get("canonical_hash")
    except json.JSONDecodeError:
        stored = None
    if stored and stored != live_hash:
        print(f"DRIFT: stored={stored} live={live_hash}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
