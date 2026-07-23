"""Adapter contract tests — the exact set of blocks each family emits.

If a future HF release renames a layer container (or a new adapter is
added that changes classification), these tests fail immediately. That
prevents the class of bug that produced today's LAYER_BEGIN over-count.
"""
from stackscope_worker import dry_run


# Ground truth: for each supported architecture, the module names we
# consider transformer blocks. These are stable across HF versions.
EXPECTED_BLOCKS = {
    "llama":   {f"model.layers.{i}" for i in range(4)},
    "mistral": {f"model.layers.{i}" for i in range(4)},
    "qwen":    {f"model.layers.{i}" for i in range(4)},
    "gemma":   {f"model.layers.{i}" for i in range(4)},
    "gpt2":    {f"transformer.h.{i}" for i in range(4)},
    "tiny":    {f"model.layers.{i}" for i in range(3)},
}


def _blocks_from(names):
    rows = dry_run.classify(names)
    return {r["name"] for r in rows if r["is_block"]}


def test_llama_contract():
    assert _blocks_from(dry_run._named_modules_from_toy("llama")) == EXPECTED_BLOCKS["llama"]


def test_mistral_contract():
    assert _blocks_from(dry_run._named_modules_from_toy("mistral")) == EXPECTED_BLOCKS["mistral"]


def test_qwen_contract():
    assert _blocks_from(dry_run._named_modules_from_toy("qwen")) == EXPECTED_BLOCKS["qwen"]


def test_gemma_contract():
    assert _blocks_from(dry_run._named_modules_from_toy("gemma")) == EXPECTED_BLOCKS["gemma"]


def test_gpt2_contract():
    assert _blocks_from(dry_run._named_modules_from_toy("gpt2")) == EXPECTED_BLOCKS["gpt2"]


def test_tiny_contract():
    assert _blocks_from(dry_run._named_modules_from_toy("tiny")) == EXPECTED_BLOCKS["tiny"]


def test_no_family_flags_container_itself_as_block():
    """Container names like ``model.layers`` or ``transformer.h`` are
    NOT blocks — those are the parent containers."""
    for arch in ("llama", "mistral", "qwen", "gemma", "gpt2", "tiny"):
        blocks = _blocks_from(dry_run._named_modules_from_toy(arch))
        for b in blocks:
            # The block name must end with an integer.
            last = b.split(".")[-1]
            assert last.isdigit(), f"{arch}: block name {b!r} does not end in an int"


def test_projections_never_flagged_as_blocks():
    """Attention projections and MLP gate/up/down projections must not
    show up as blocks even though they share the layer's inferred id."""
    for arch in ("llama", "mistral", "qwen", "gemma", "gpt2"):
        rows = dry_run.classify(dry_run._named_modules_from_toy(arch))
        for r in rows:
            if r["proj"] is not None:
                assert not r["is_block"], f"{arch}: {r['name']} both proj+block"
            if r["name"].endswith((".mlp", ".mlp.gate_proj",
                                     ".mlp.up_proj", ".mlp.down_proj")):
                assert not r["is_block"], f"{arch}: mlp leaf {r['name']} flagged as block"
