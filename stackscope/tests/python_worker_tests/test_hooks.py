import os
import pytest


def test_infer_layer_index_from_hf_name(torch_module):
    from stackscope_worker.hooks import infer_layer_index
    assert infer_layer_index("model.layers.7.self_attn.q_proj") == 7
    assert infer_layer_index("transformer.h.11.attn.c_attn") == 11
    assert infer_layer_index("model.embed_tokens") == -1


def test_is_attention_projection():
    from stackscope_worker.hooks import is_attention_projection
    assert is_attention_projection("model.layers.3.self_attn.q_proj") == "q_proj"
    assert is_attention_projection("model.layers.3.self_attn.o_proj") == "o_proj"
    assert is_attention_projection("transformer.h.0.attn.c_attn")     == "c_attn"
    assert is_attention_projection("model.layers.0.mlp.gate_proj")     is None


def test_is_transformer_block_only_matches_block_itself():
    """Regression: ``model.layers.0.mlp`` used to be treated as a block
    because it shared the same inferred layer index. Only paths whose
    LAST two segments are ``<container>.<int>`` are the block."""
    from stackscope_worker.hooks import is_transformer_block
    assert is_transformer_block("model.layers.0")     is True
    assert is_transformer_block("model.layers.11")    is True
    assert is_transformer_block("transformer.h.0")    is True
    assert is_transformer_block("blocks.5")           is True
    # Descendants are NOT blocks
    assert is_transformer_block("model.layers.0.mlp")           is False
    assert is_transformer_block("model.layers.0.self_attn")     is False
    assert is_transformer_block("model.layers.0.self_attn.q_proj") is False
    # Containers themselves are NOT blocks
    assert is_transformer_block("model.layers")       is False
    assert is_transformer_block("transformer.h")      is False
    # Unrelated names
    assert is_transformer_block("model.embed_tokens") is False
    assert is_transformer_block("")                    is False
    assert is_transformer_block("lm_head")             is False


def test_hook_capture_emits_token_and_layer_events(torch_module):
    """Build a tiny transformer-like module tree and drive it through
    a single forward pass. We should observe TOKEN_BEGIN, LAYER_BEGIN,
    LAYER_END, and TOKEN_END events in order.
    """
    torch = torch_module
    nn = torch.nn

    class TinyBlock(nn.Module):
        def __init__(self, dim):
            super().__init__()
            self.self_attn = nn.Sequential()
            self.self_attn.add_module("q_proj", nn.Linear(dim, dim))
            self.self_attn.add_module("k_proj", nn.Linear(dim, dim))
            self.self_attn.add_module("v_proj", nn.Linear(dim, dim))
            self.self_attn.add_module("o_proj", nn.Linear(dim, dim))
            self.mlp = nn.Linear(dim, dim)

        def forward(self, x):
            q = self.self_attn.q_proj(x)
            k = self.self_attn.k_proj(x)
            v = self.self_attn.v_proj(x)
            attn = (q + k + v) / 3
            attn = self.self_attn.o_proj(attn)
            return self.mlp(attn + x)

    class TinyModel(nn.Module):
        def __init__(self, dim, n_layers):
            super().__init__()
            self.model = nn.Sequential()
            self.model.add_module("layers", nn.Sequential(*[TinyBlock(dim) for _ in range(n_layers)]))

        def forward(self, x):
            return self.model.layers(x)

    from stackscope_worker.hooks import HookCapture, Kind

    model = TinyModel(dim=8, n_layers=3)
    model.eval()

    hooks = HookCapture(capture_attention=True, capture_activations=False)
    hooks.attach(model)
    try:
        hooks.note_token_begin(0)
        with torch.no_grad():
            _ = model(torch.randn(2, 8))
        hooks.note_token_end(0, sampled_id=42, logit_top1=0.75)

        events = hooks.events(timeout=0.1)
    finally:
        hooks.detach()

    kinds = [e.kind for e in events]
    assert Kind.TOKEN_BEGIN in kinds
    assert Kind.TOKEN_END   in kinds
    assert kinds.count(Kind.LAYER_BEGIN) == 3
    assert kinds.count(Kind.LAYER_END)   == 3
    # Attention projections captured on every block.
    assert kinds.count(Kind.ATTENTION_QKV) >= 3 * 3  # q,k,v per block
    assert kinds.count(Kind.ATTENTION_OUTPUT) == 3


@pytest.mark.skipif(
    os.environ.get("TRANSFORMERS_OFFLINE") == "1"
    or os.environ.get("STACKSCOPE_SKIP_HF_TESTS") == "1",
    reason="HF network access disabled.")
def test_end_to_end_on_tiny_gpt2(torch_module):
    """Integration: load sshleifer/tiny-gpt2 and generate 2 tokens."""
    hf = pytest.importorskip("transformers")
    torch = torch_module
    from stackscope_worker.hooks import HookCapture, Kind

    model = hf.AutoModelForCausalLM.from_pretrained("sshleifer/tiny-gpt2")
    tok = hf.AutoTokenizer.from_pretrained("sshleifer/tiny-gpt2")
    model.eval()

    hooks = HookCapture(capture_attention=True, capture_activations=False)
    hooks.attach(model)
    try:
        ids = tok("hello", return_tensors="pt")["input_ids"]
        hooks.note_token_begin(0)
        with torch.no_grad():
            out = model(ids)
        logits = out.logits[:, -1, :]
        hooks.note_logits(0, logits[0])
        hooks.note_token_end(0, sampled_id=int(logits.argmax().item()),
                              logit_top1=float(logits.max().item()))
        events = hooks.events(timeout=0.5)
    finally:
        hooks.detach()

    kinds = [e.kind for e in events]
    assert Kind.TOKEN_BEGIN in kinds
    assert Kind.LOGITS in kinds
    assert Kind.TOKEN_END in kinds
    # tiny-gpt2 has 2 transformer blocks; we should see 2 LAYER_BEGIN/END pairs.
    assert kinds.count(Kind.LAYER_BEGIN) >= 2
    assert kinds.count(Kind.LAYER_END)   >= 2
