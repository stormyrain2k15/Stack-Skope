"""Tests for the .stackscope bundle format."""
import json
from pathlib import Path

import pytest

from stackscope_worker import bundle, manifest


def test_pack_and_unpack_round_trip(tmp_path: Path):
    m = manifest.build(seed=1)
    events = [
        {"ts": 100, "kind": "TOKEN_BEGIN", "token": 0, "layer": -1, "head": -1, "payload_hex": ""},
        {"ts": 200, "kind": "LAYER_BEGIN", "token": 0, "layer": 0, "head": -1, "payload_hex": ""},
        {"ts": 300, "kind": "LAYER_END",   "token": 0, "layer": 0, "head": -1, "payload_hex": ""},
        {"ts": 400, "kind": "TOKEN_END",   "token": 0, "layer": -1, "head": -1, "payload_hex": "2a00000000000000"},
    ]
    contents = bundle.build_from_events(events, model_descriptor={"arch": "Tiny"},
                                        manifest_obj=m, notes="hello")
    out = tmp_path / "cap.stackscope"
    bundle.pack(out, contents)
    assert out.exists() and out.stat().st_size > 0

    round_trip = bundle.unpack(out)
    assert round_trip.notes_md == "hello"
    assert json.loads(round_trip.model_json) == {"arch": "Tiny"}
    lines = [json.loads(line) for line in round_trip.events_jsonl.splitlines() if line.strip()]
    assert len(lines) == 4
    assert lines[1]["kind"] == "LAYER_BEGIN"
    m2 = json.loads(round_trip.manifest_json)
    assert m2["seed"] == 1


def test_unpack_missing_required_raises(tmp_path: Path):
    import zipfile
    bad = tmp_path / "bad.stackscope"
    with zipfile.ZipFile(bad, "w") as z:
        z.writestr("notes.md", "no manifest here")
    with pytest.raises(ValueError, match="Bundle missing manifest.json"):
        bundle.unpack(bad)


def test_readme_is_included(tmp_path: Path):
    import zipfile
    contents = bundle.build_from_events([], manifest_obj=manifest.build())
    out = tmp_path / "e.stackscope"
    bundle.pack(out, contents)
    with zipfile.ZipFile(out) as z:
        assert "README.md" in z.namelist()
        assert b"StackScope" in z.read("README.md")
