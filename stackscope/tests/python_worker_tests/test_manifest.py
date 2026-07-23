"""Tests for the reproducibility manifest."""
import json

from stackscope_worker import manifest as m


def test_build_returns_all_required_fields():
    r = m.build(seed=42, dtype="float16", quantization="q4_k_m")
    d = json.loads(m.to_json(r))
    for key in (
        "stackscope_version", "python_version", "platform",
        "torch_cuda_available", "torch_backends", "seed",
        "dtype", "quantization", "env_snapshot", "hostname",
    ):
        assert key in d, f"missing manifest field: {key}"
    assert d["seed"] == 42
    assert d["dtype"] == "float16"
    assert d["quantization"] == "q4_k_m"
    assert isinstance(d["torch_backends"], list)


def test_round_trip_json():
    a = m.build(seed=7)
    b = m.from_json(m.to_json(a))
    assert b.seed == 7
    assert b.python_version == a.python_version
    assert b.platform == a.platform


def test_env_snapshot_only_captures_relevant_keys(monkeypatch):
    monkeypatch.setenv("CUDA_VISIBLE_DEVICES", "0,1")
    monkeypatch.setenv("OMP_NUM_THREADS", "8")
    monkeypatch.setenv("SOMETHING_UNRELATED", "should_not_appear")
    r = m.build()
    assert r.env_snapshot.get("CUDA_VISIBLE_DEVICES") == "0,1"
    assert r.env_snapshot.get("OMP_NUM_THREADS") == "8"
    assert "SOMETHING_UNRELATED" not in r.env_snapshot
