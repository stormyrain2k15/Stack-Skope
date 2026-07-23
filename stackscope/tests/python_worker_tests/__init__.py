"""Pytest suite for the StackScope Python inference worker.

Two test scopes:
  * Unit — exercise the hook capture machinery on a tiny synthetic
    torch.nn.Module tree. No transformers/network dependency.
  * Integration — load ``sshleifer/tiny-gpt2`` (~5 MB) via HF, install
    hooks, run generation for a couple tokens, assert we get
    TOKEN_BEGIN/TOKEN_END + at least one LAYER_BEGIN/LAYER_END pair
    per layer.

The integration test is skipped if ``TRANSFORMERS_OFFLINE=1`` is set
or if huggingface_hub is unavailable at runtime.
"""
