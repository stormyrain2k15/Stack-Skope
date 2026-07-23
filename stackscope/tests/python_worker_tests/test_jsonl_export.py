"""Tests for JSONL export + canonical hash."""
import io

from stackscope_worker.hooks import Event, Kind
from stackscope_worker import jsonl_export


def _sample_events():
    return [
        Event(kind=Kind.TOKEN_BEGIN, timestamp_ns=1, token_index=0),
        Event(kind=Kind.LAYER_BEGIN, timestamp_ns=2, token_index=0, layer_index=0),
        Event(kind=Kind.LAYER_END,   timestamp_ns=3, token_index=0, layer_index=0),
        Event(kind=Kind.TOKEN_END,   timestamp_ns=4, token_index=0,
              payload=b"\x2a\x00\x00\x00\x00\x00\x00\x00"),
    ]


def test_write_and_read_round_trip():
    events = _sample_events()
    buf = io.StringIO()
    n = jsonl_export.write_jsonl(events, buf)
    assert n == 4
    buf.seek(0)
    round_trip = jsonl_export.read_jsonl(buf)
    assert len(round_trip) == 4
    for original, restored in zip(events, round_trip):
        assert original.kind == restored.kind
        assert original.token_index == restored.token_index
        assert original.layer_index == restored.layer_index
        assert original.payload == restored.payload


def test_canonical_hash_is_stable_and_excludes_timestamps():
    events_a = _sample_events()
    events_b = _sample_events()
    for e in events_b:
        e.timestamp_ns += 999_999   # mutate timestamps only
        e.thread_id = 42
    assert jsonl_export.canonical_hash(events_a) == jsonl_export.canonical_hash(events_b)


def test_canonical_hash_changes_when_kind_changes():
    a = _sample_events()
    b = _sample_events()
    b[1].kind = Kind.LAYER_END   # swap a LAYER_BEGIN to LAYER_END
    assert jsonl_export.canonical_hash(a) != jsonl_export.canonical_hash(b)


def test_canonical_hash_changes_when_layer_changes():
    a = _sample_events()
    b = _sample_events()
    b[1].layer_index = 1
    assert jsonl_export.canonical_hash(a) != jsonl_export.canonical_hash(b)
