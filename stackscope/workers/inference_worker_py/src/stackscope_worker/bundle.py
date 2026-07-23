""".stackscope bundle format.

A .stackscope bundle is a plain zip containing everything needed to
diagnose a capture without the original weights:

    capture.stackscope/
      ├── manifest.json          # ReproducibilityManifest
      ├── model.json             # UnifiedModelDescriptor (tokenizer,
      │                          #   layer graph, quant scheme, dtype)
      ├── events.jsonl           # canonical JSONL export of the capture
      ├── notes.md               # optional user notes
      └── README.md              # brief pointer to StackScope

The point: paste one of these into a chat with an AI assistant and it
has enough context to run analyses and give useful advice. No weights,
no proprietary data, just events + provenance.
"""
from __future__ import annotations

import io
import json
import zipfile
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

from . import manifest as _manifest


BUNDLE_README = """\
# StackScope capture bundle

This zip is a self-contained trace of a transformer inference session.

- `manifest.json` — the software environment it was recorded in.
- `model.json`    — model architecture, tokenizer summary, quant scheme.
- `events.jsonl`  — every event, one JSON per line.
- `notes.md`      — optional human notes.

Reload with:

    stackscope-repro capture.stackscope

Or paste the contents into an AI assistant that speaks StackScope's MCP.
"""


@dataclass
class BundleContents:
    manifest_json: str
    model_json: str
    events_jsonl: str
    notes_md: str = ""


def pack(dst_path: str | Path, contents: BundleContents) -> Path:
    """Write a bundle. Returns the resolved path."""
    dst = Path(dst_path)
    dst.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(dst, "w", compression=zipfile.ZIP_DEFLATED) as z:
        z.writestr("manifest.json", contents.manifest_json)
        z.writestr("model.json",    contents.model_json)
        z.writestr("events.jsonl",  contents.events_jsonl)
        z.writestr("notes.md",      contents.notes_md)
        z.writestr("README.md",     BUNDLE_README)
    return dst


def unpack(src_path: str | Path) -> BundleContents:
    """Read a bundle back."""
    with zipfile.ZipFile(src_path, "r") as z:
        names = set(z.namelist())
        for required in ("manifest.json", "model.json", "events.jsonl"):
            if required not in names:
                raise ValueError(f"Bundle missing {required}: {src_path}")
        return BundleContents(
            manifest_json=z.read("manifest.json").decode("utf-8"),
            model_json=   z.read("model.json").decode("utf-8"),
            events_jsonl= z.read("events.jsonl").decode("utf-8"),
            notes_md=     z.read("notes.md").decode("utf-8") if "notes.md" in names else "",
        )


def build_from_events(
    events: Iterable[dict],
    model_descriptor: dict | None = None,
    manifest_obj: _manifest.ReproducibilityManifest | None = None,
    notes: str = "",
) -> BundleContents:
    """Build BundleContents in memory from an iterable of event dicts."""
    m = manifest_obj if manifest_obj is not None else _manifest.build()
    model = model_descriptor if model_descriptor is not None else {}
    buf = io.StringIO()
    for e in events:
        buf.write(json.dumps(e, separators=(",", ":"), sort_keys=True))
        buf.write("\n")
    return BundleContents(
        manifest_json=_manifest.to_json(m),
        model_json=json.dumps(model, indent=2, sort_keys=True),
        events_jsonl=buf.getvalue(),
        notes_md=notes,
    )


def main() -> int:
    """CLI: `stackscope-bundle pack <capture-dir> <bundle.stackscope>`
    or `stackscope-bundle unpack <bundle.stackscope> <output-dir>`."""
    import argparse
    p = argparse.ArgumentParser(prog="stackscope-bundle")
    sub = p.add_subparsers(dest="cmd", required=True)

    pk = sub.add_parser("pack", help="pack a directory into a .stackscope zip")
    pk.add_argument("dir")
    pk.add_argument("bundle")
    pk.add_argument("--notes", default="")

    un = sub.add_parser("unpack", help="unpack a .stackscope zip into a directory")
    un.add_argument("bundle")
    un.add_argument("dir")

    args = p.parse_args()
    if args.cmd == "pack":
        src = Path(args.dir)
        events_path = src / "events.jsonl"
        model_path = src / "model.json"
        events_jsonl = events_path.read_text("utf-8") if events_path.exists() else ""
        model_json = model_path.read_text("utf-8") if model_path.exists() else "{}"
        contents = BundleContents(
            manifest_json=_manifest.to_json(_manifest.build()),
            model_json=model_json,
            events_jsonl=events_jsonl,
            notes_md=args.notes,
        )
        out = pack(args.bundle, contents)
        print(f"wrote {out}")
    else:
        contents = unpack(args.bundle)
        dst = Path(args.dir); dst.mkdir(parents=True, exist_ok=True)
        (dst / "manifest.json").write_text(contents.manifest_json)
        (dst / "model.json").write_text(contents.model_json)
        (dst / "events.jsonl").write_text(contents.events_jsonl)
        (dst / "notes.md").write_text(contents.notes_md)
        print(f"unpacked {args.bundle} into {dst}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
