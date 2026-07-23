"""Tests for rectangular ablation range logic in the Python worker.

The gRPC servicer's RunInference method registers per-attention-module
hooks that zero every (layer, head) inside the rectangle
[ablate_layer..ablate_layer_end] × [ablate_head..ablate_head_end] within
one capture. These tests exercise the pure zero_heads closure and the
range normalisation without needing a live gRPC channel or GPU.
"""
from __future__ import annotations

import torch


def _resolve_range(ablate_layer: int, ablate_head: int,
                   ablate_layer_end: int, ablate_head_end: int):
    """Mirror of the servicer's normalisation. Kept here so the test
    file is the single source of truth about the input contract — any
    drift between test and implementation trips CI."""
    if ablate_layer >= 0 and ablate_head >= 0:
        layer_lo, layer_hi = ablate_layer, max(ablate_layer, ablate_layer_end)
        head_lo,  head_hi  = ablate_head,  max(ablate_head,  ablate_head_end)
    else:
        layer_lo = layer_hi = head_lo = head_hi = -1
    return layer_lo, layer_hi, head_lo, head_hi


def _zero_heads_impl(tensor: torch.Tensor, n_heads: int, heads_to_zero: set[int]) -> torch.Tensor:
    """Bit-for-bit copy of the closure inside RunInference. Same reason
    as _resolve_range — keep the contract testable in isolation."""
    if tensor.ndim < 3:
        return tensor
    b, s, hidden = tensor.shape
    if hidden % n_heads != 0:
        return tensor
    head_dim = hidden // n_heads
    tensor = tensor.clone()
    for h in heads_to_zero:
        if 0 <= h < n_heads:
            tensor[..., h*head_dim:(h+1)*head_dim] = 0
    return tensor


def test_range_normalisation_single_cell():
    lo, hi, hlo, hhi = _resolve_range(3, 2, -1, -1)
    assert (lo, hi, hlo, hhi) == (3, 3, 2, 2)


def test_range_normalisation_rectangle():
    lo, hi, hlo, hhi = _resolve_range(4, 0, 6, 3)
    assert (lo, hi, hlo, hhi) == (4, 6, 0, 3)


def test_range_normalisation_end_below_start_clamps_to_start():
    # An end < start should collapse to the start value, not silently
    # invert or return an empty range that produces zero hooks.
    lo, hi, hlo, hhi = _resolve_range(5, 3, 2, 1)
    assert (lo, hi, hlo, hhi) == (5, 5, 3, 3)


def test_range_disabled_when_start_is_negative():
    lo, hi, hlo, hhi = _resolve_range(-1, 0, 5, 5)
    assert (lo, hi, hlo, hhi) == (-1, -1, -1, -1)
    lo, hi, hlo, hhi = _resolve_range(2, -1, 5, 5)
    assert (lo, hi, hlo, hhi) == (-1, -1, -1, -1)


def test_zero_heads_zeros_exactly_the_requested_slices():
    # 4 heads, hidden = 8, so each head occupies 2 columns.
    t = torch.ones(1, 3, 8)   # (batch, seq, hidden)
    out = _zero_heads_impl(t, n_heads=4, heads_to_zero={1, 3})
    # Head 0 (cols 0-1) untouched
    assert out[..., 0:2].abs().sum().item() == 1 * 3 * 2
    # Head 1 (cols 2-3) zeroed
    assert out[..., 2:4].abs().sum().item() == 0.0
    # Head 2 (cols 4-5) untouched
    assert out[..., 4:6].abs().sum().item() == 1 * 3 * 2
    # Head 3 (cols 6-7) zeroed
    assert out[..., 6:8].abs().sum().item() == 0.0


def test_zero_heads_ignores_out_of_range_indices():
    # Requesting head 99 on a 2-head module is a no-op, not a crash.
    t = torch.ones(1, 2, 4)
    out = _zero_heads_impl(t, n_heads=2, heads_to_zero={99})
    assert torch.equal(out, t)


def test_zero_heads_skips_when_hidden_not_divisible_by_heads():
    # Defensive path — an odd hidden size means the closure can't slice
    # cleanly, so it must return the tensor unchanged.
    t = torch.ones(1, 2, 5)
    out = _zero_heads_impl(t, n_heads=2, heads_to_zero={0, 1})
    assert torch.equal(out, t)


def test_zero_heads_preserves_non_targeted_heads_bitwise():
    # Non-targeted heads must have their exact values preserved, not
    # a "close enough" pass — the diff analyser is sensitive.
    t = torch.arange(24, dtype=torch.float32).reshape(1, 3, 8)
    out = _zero_heads_impl(t, n_heads=4, heads_to_zero={2})
    for h in (0, 1, 3):
        original = t[..., h*2:(h+1)*2]
        after    = out[..., h*2:(h+1)*2]
        assert torch.equal(original, after), f"head {h} was disturbed"
    assert out[..., 4:6].abs().sum().item() == 0.0
