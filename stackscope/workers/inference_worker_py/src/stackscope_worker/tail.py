"""Live tail — stream events from a running capture directory.

Watches a directory for JSONL appends and prints new events in a
grep-friendly form. Complements the WPF UI: you can tail a live
capture from a terminal while training/generating.

Usage:
    stackscope-tail <capture-dir>
    stackscope-tail --grep NaN <capture-dir>
    stackscope-tail --kind LAYER_BEGIN,ATTENTION_QKV <capture-dir>
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import time
from pathlib import Path


def _iter_new_lines(path: Path, poll_s: float = 0.1):
    """Yield newline-terminated additions to a growing text file."""
    with path.open("r", encoding="utf-8") as f:
        f.seek(0, 2)   # start at end
        while True:
            line = f.readline()
            if line:
                if line.endswith("\n"):
                    yield line.rstrip("\n")
                else:
                    # Partial line — wait for the newline.
                    pos = f.tell() - len(line)
                    time.sleep(poll_s)
                    f.seek(pos)
            else:
                time.sleep(poll_s)


def _format(d: dict) -> str:
    parts = [
        f"ts={d.get('ts', '?')}",
        f"kind={d.get('kind', '?')}",
        f"tok={d.get('token', '?')}",
        f"L={d.get('layer', '?')}",
    ]
    if d.get("head", -1) != -1:
        parts.append(f"H={d['head']}")
    if d.get("marker_name"):
        parts.append(f"marker={d['marker_name']}")
    return " ".join(parts)


def main() -> int:
    p = argparse.ArgumentParser(prog="stackscope-tail",
                                description="Stream new events from a capture dir.")
    p.add_argument("path",
                   help="Path to events.jsonl or a directory containing it.")
    p.add_argument("--grep", default=None,
                   help="Only print lines matching this regex.")
    p.add_argument("--kind", default=None,
                   help="Comma-separated event kinds to include "
                        "(e.g. LAYER_BEGIN,LOGITS,MARKER).")
    p.add_argument("--raw", action="store_true",
                   help="Print raw JSON instead of the human summary.")
    args = p.parse_args()

    src = Path(args.path)
    if src.is_dir():
        src = src / "events.jsonl"
    if not src.exists():
        print(f"no events.jsonl at {src}", file=sys.stderr)
        return 2

    kinds = None
    if args.kind:
        kinds = {k.strip() for k in args.kind.split(",") if k.strip()}
    grep = re.compile(args.grep) if args.grep else None

    try:
        for line in _iter_new_lines(src):
            if grep and not grep.search(line):
                continue
            try:
                d = json.loads(line)
            except json.JSONDecodeError:
                continue
            if kinds and d.get("kind") not in kinds:
                continue
            print(line if args.raw else _format(d), flush=True)
    except KeyboardInterrupt:
        return 0
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
