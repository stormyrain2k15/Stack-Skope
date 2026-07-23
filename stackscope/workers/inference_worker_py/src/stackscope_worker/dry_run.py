"""Hook discovery introspector — the fastest diagnosis tool.

`python -m stackscope_worker.dry_run --model <hf-id-or-arch>` prints
one row per module in the loaded model:

    NAME                                            IS_BLOCK  PROJ     LAYER
    model.embed_tokens                              -         -        -
    model.layers.0                                  BLOCK     -        0
    model.layers.0.self_attn                        -         -        0
    model.layers.0.self_attn.q_proj                 -         q_proj   0
    model.layers.0.mlp                              -         -        0
    ...

If a BLOCK row appears where you don't expect it, or a real block is
missing, you've found the bug in 30 seconds — no capture needed.

The tool supports three modes:

* `--model llama|gemma|qwen|mistral|gpt2|tiny` — build a small
  no-weights instance of that architecture in memory (fastest).
* `--model <hf-id>`                           — actually load the HF
  config and instantiate a random-weight variant (slower, requires
  transformers).
* `--stdin`                                   — read a newline-list of
  module names from stdin (works without torch installed).
"""
from __future__ import annotations

import argparse
import json
import sys
from typing import Iterable

from .hooks import infer_layer_index, is_attention_projection, is_transformer_block


def classify(names: Iterable[str]) -> list[dict]:
    """Classify each module name. Deterministic; safe to snapshot."""
    rows: list[dict] = []
    for name in names:
        rows.append({
            "name": name,
            "is_block": is_transformer_block(name),
            "proj": is_attention_projection(name),
            "layer_idx": infer_layer_index(name),
        })
    return rows


def _format_rows(rows: list[dict]) -> str:
    if not rows:
        return "(no modules)"
    w = max(len(r["name"]) for r in rows)
    lines = [f"{'NAME':<{w}}  IS_BLOCK  PROJ     LAYER"]
    for r in rows:
        block = "BLOCK" if r["is_block"] else "-"
        proj = r["proj"] or "-"
        layer = str(r["layer_idx"]) if r["layer_idx"] >= 0 else "-"
        lines.append(f"{r['name']:<{w}}  {block:<8}  {proj:<7}  {layer}")
    n_block = sum(1 for r in rows if r["is_block"])
    n_proj  = sum(1 for r in rows if r["proj"])
    lines.append("")
    lines.append(f"summary: {len(rows)} modules, {n_block} blocks, "
                 f"{n_proj} attention projections")
    return "\n".join(lines)


def _named_modules_from_stdin() -> list[str]:
    return [line.rstrip("\r\n") for line in sys.stdin if line.strip()]


def _named_modules_from_toy(arch: str) -> list[str]:
    """Fully static — no torch required. Mirrors HF's naming conventions
    so this tool works in any container regardless of torch/CUDA."""
    if arch == "tiny":
        n_layers = 3
        names = ["model", "model.layers"]
        for i in range(n_layers):
            names += [
                f"model.layers.{i}",
                f"model.layers.{i}.self_attn",
                f"model.layers.{i}.self_attn.q_proj",
                f"model.layers.{i}.self_attn.k_proj",
                f"model.layers.{i}.self_attn.v_proj",
                f"model.layers.{i}.self_attn.o_proj",
                f"model.layers.{i}.mlp",
            ]
        return names
    if arch in ("llama", "mistral", "qwen", "gemma"):
        n_layers = 4
        names = ["model", "model.embed_tokens", "model.norm", "model.layers", "lm_head"]
        for i in range(n_layers):
            names += [
                f"model.layers.{i}",
                f"model.layers.{i}.input_layernorm",
                f"model.layers.{i}.self_attn",
                f"model.layers.{i}.self_attn.q_proj",
                f"model.layers.{i}.self_attn.k_proj",
                f"model.layers.{i}.self_attn.v_proj",
                f"model.layers.{i}.self_attn.o_proj",
                f"model.layers.{i}.post_attention_layernorm",
                f"model.layers.{i}.mlp",
                f"model.layers.{i}.mlp.gate_proj",
                f"model.layers.{i}.mlp.up_proj",
                f"model.layers.{i}.mlp.down_proj",
            ]
        return names
    if arch == "gpt2":
        n_layers = 4
        names = ["transformer", "transformer.wte", "transformer.wpe",
                 "transformer.h", "transformer.ln_f", "lm_head"]
        for i in range(n_layers):
            names += [
                f"transformer.h.{i}",
                f"transformer.h.{i}.ln_1",
                f"transformer.h.{i}.attn",
                f"transformer.h.{i}.attn.c_attn",
                f"transformer.h.{i}.attn.c_proj",
                f"transformer.h.{i}.ln_2",
                f"transformer.h.{i}.mlp",
                f"transformer.h.{i}.mlp.c_fc",
                f"transformer.h.{i}.mlp.c_proj",
            ]
        return names
    raise SystemExit(f"unknown toy arch: {arch!r}. use one of "
                     "tiny, llama, mistral, qwen, gemma, gpt2, or "
                     "pass an HF id with --hf.")


def _named_modules_from_hf(hf_id: str) -> list[str]:
    """Materialise a small random-weight variant of an HF checkpoint's
    architecture. Requires transformers; skips gracefully if absent."""
    try:
        from transformers import AutoConfig, AutoModelForCausalLM
    except ImportError as ex:
        raise SystemExit(
            "transformers not installed. install it or use --model tiny."
        ) from ex
    cfg = AutoConfig.from_pretrained(hf_id)
    # Shrink to keep memory tiny.
    if hasattr(cfg, "num_hidden_layers"):
        cfg.num_hidden_layers = min(cfg.num_hidden_layers, 2)
    if hasattr(cfg, "n_layer"):
        cfg.n_layer = min(cfg.n_layer, 2)
    if hasattr(cfg, "hidden_size"):
        cfg.hidden_size = min(cfg.hidden_size, 64)
    if hasattr(cfg, "n_embd"):
        cfg.n_embd = min(cfg.n_embd, 64)
    if hasattr(cfg, "intermediate_size"):
        cfg.intermediate_size = min(cfg.intermediate_size, 128)
    model = AutoModelForCausalLM.from_config(cfg)
    return [n for n, _ in model.named_modules() if n]


def main() -> int:
    p = argparse.ArgumentParser(
        prog="stackscope-dry-run",
        description="Print StackScope's hook classification for a model. "
                    "The fastest way to find hook-detection bugs.")
    src = p.add_mutually_exclusive_group(required=True)
    src.add_argument("--model",
                     help="Toy architecture: tiny | llama | mistral | qwen | gemma | gpt2")
    src.add_argument("--hf", help="Actually load an HF config by id.")
    src.add_argument("--stdin", action="store_true",
                     help="Read newline-separated module names from stdin.")
    p.add_argument("--json", action="store_true",
                   help="Emit JSON instead of a table.")
    args = p.parse_args()

    if args.stdin:
        names = _named_modules_from_stdin()
    elif args.hf:
        names = _named_modules_from_hf(args.hf)
    else:
        names = _named_modules_from_toy(args.model)

    rows = classify(names)
    if args.json:
        print(json.dumps(rows, indent=2))
    else:
        print(_format_rows(rows))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
