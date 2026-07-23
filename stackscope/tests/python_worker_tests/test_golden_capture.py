"""Golden capture regression test.

A tiny check-in capture that every future build must reproduce.
This is the safety net: if any change to hook classification,
event serialization, or JSONL export perturbs the canonical hash,
CI fails immediately.
"""
import json
from pathlib import Path

from stackscope_worker import bundle, jsonl_export, repro


GOLDEN_EVENTS = [
    {"ts": 100, "kind": "TOKEN_BEGIN", "token": 0, "layer": -1, "head": -1,
     "payload_hex": "", "marker_name": None},
    {"ts": 110, "kind": "LAYER_BEGIN", "token": 0, "layer": 0, "head": -1,
     "payload_hex": "", "marker_name": "layer.0"},
    {"ts": 120, "kind": "ATTENTION_QKV", "token": 0, "layer": 0, "head": -1,
     "payload_hex": "00", "marker_name": "model.layers.0.self_attn.q_proj"},
    {"ts": 130, "kind": "ATTENTION_OUTPUT", "token": 0, "layer": 0, "head": -1,
     "payload_hex": "03", "marker_name": "model.layers.0.self_attn.o_proj"},
    {"ts": 140, "kind": "LAYER_END", "token": 0, "layer": 0, "head": -1,
     "payload_hex": "", "marker_name": "layer.0"},
    {"ts": 150, "kind": "LOGITS", "token": 0, "layer": -1, "head": -1,
     "payload_hex": "01000000010000009a99993e", "marker_name": None},
    {"ts": 160, "kind": "TOKEN_END", "token": 0, "layer": -1, "head": -1,
     "payload_hex": "2a0000009a99993e", "marker_name": None},
]

# This hash was frozen once. Anyone who changes it MUST justify the
# change in the PR body — it is the whole point of a golden test.
GOLDEN_HASH = "501ba78bd1825c9e3246dd94b53c93171c83becb5505e4e9d4a57fe5550ccf3b"


def test_golden_hash_is_stable():
    # If this line ever needs updating, you are either intentionally
    # changing the on-disk event shape (and know what breaks) or you
    # have accidentally regressed hook classification. Read AGENTS.md.
    assert repro._canonical_hash_of_dicts(GOLDEN_EVENTS) == GOLDEN_HASH


def test_golden_survives_bundle_round_trip(tmp_path: Path):
    contents = bundle.build_from_events(GOLDEN_EVENTS)
    out = tmp_path / "golden.stackscope"
    bundle.pack(out, contents)
    round_trip = bundle.unpack(out)
    events = [json.loads(line) for line in round_trip.events_jsonl.splitlines() if line.strip()]
    assert repro._canonical_hash_of_dicts(events) == GOLDEN_HASH


def test_compare_reports_no_drift_for_identical_events():
    identical, report = repro.compare(GOLDEN_EVENTS, GOLDEN_EVENTS)
    assert identical is True
    assert report == "" or "differ" not in report


def test_compare_pinpoints_first_divergence():
    mutated = [dict(e) for e in GOLDEN_EVENTS]
    mutated[3]["kind"] = "LAYER_END"   # perturb one event
    identical, report = repro.compare(GOLDEN_EVENTS, mutated)
    assert identical is False
    assert "first divergence at index 3" in report


def test_kind_names_are_stable_across_jsonl():
    # This locks in the KIND name mapping between the enum and JSONL.
    # Renaming a Kind (e.g. LAYER_BEGIN -> BLOCK_BEGIN) breaks every
    # golden bundle ever written; the test would surface it.
    for e in GOLDEN_EVENTS:
        assert e["kind"] in jsonl_export._NAME_TO_KIND
