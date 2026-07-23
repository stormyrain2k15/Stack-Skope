"""Tests for the hook-classification dry-run tool."""
from stackscope_worker import dry_run


def test_classify_llama_toy_returns_exactly_n_blocks():
    names = dry_run._named_modules_from_toy("llama")
    rows = dry_run.classify(names)
    blocks = [r for r in rows if r["is_block"]]
    # Toy llama has 4 blocks: model.layers.0..3
    assert len(blocks) == 4
    assert {r["layer_idx"] for r in blocks} == {0, 1, 2, 3}
    # Descendants share the layer id but are NOT blocks
    mlps = [r for r in rows if r["name"].endswith(".mlp")]
    assert len(mlps) == 4
    assert all(not r["is_block"] for r in mlps)
    assert all(r["layer_idx"] in (0, 1, 2, 3) for r in mlps)


def test_classify_gpt2_toy_uses_h_container():
    names = dry_run._named_modules_from_toy("gpt2")
    rows = dry_run.classify(names)
    blocks = [r for r in rows if r["is_block"]]
    assert len(blocks) == 4
    # GPT-2 uses transformer.h.<N>
    assert all(r["name"].startswith("transformer.h.") for r in blocks)


def test_classify_finds_c_attn_and_c_proj():
    names = dry_run._named_modules_from_toy("gpt2")
    rows = dry_run.classify(names)
    projs = {r["proj"] for r in rows if r["proj"]}
    assert "c_attn" in projs
    assert "o_proj" in projs  # c_proj is aliased to o_proj by is_attention_projection


def test_classify_tiny_matches_regression_test_shape():
    """The tiny toy arch must have exactly 3 blocks — this is the same
    shape our hook-capture regression test asserts against."""
    names = dry_run._named_modules_from_toy("tiny")
    rows = dry_run.classify(names)
    blocks = [r for r in rows if r["is_block"]]
    assert len(blocks) == 3
    assert [r["layer_idx"] for r in blocks] == [0, 1, 2]


def test_format_rows_includes_summary():
    rows = dry_run.classify(dry_run._named_modules_from_toy("tiny"))
    out = dry_run._format_rows(rows)
    assert "summary:" in out
    assert "3 blocks" in out
