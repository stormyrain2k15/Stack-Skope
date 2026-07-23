"""gRPC service for the Python inference worker.

Implements ``InferenceWorker`` from proto/worker.proto. This module
imports the generated ``stackscope_pb2`` and ``stackscope_pb2_grpc``
modules that live under ``stackscope_worker._generated`` — those are
produced at install time by the ``scripts/gen_proto.sh`` helper.
"""
from __future__ import annotations

import logging
import os

import grpc
import torch
from transformers import AutoConfig, AutoModelForCausalLM, AutoTokenizer

from .hooks import HookCapture, Kind, Event
from .attention_capture import (
    TensorArena, compute_head_stats, is_top_level_attention, pack_head_stats,
)
from .anomaly import AnomalyDetector
from .markers import now_ns

log = logging.getLogger(__name__)


# The generated stubs are imported lazily so unit tests that don't need
# gRPC don't require the codegen step to have run.
def _load_stubs():
    from stackscope_worker._generated import worker_pb2, worker_pb2_grpc, events_pb2
    return worker_pb2, worker_pb2_grpc, events_pb2


class InferenceWorkerServicer:
    def __init__(self):
        # Bind to generated stubs at construction so a stale codegen
        # error surfaces at server startup, not on first call.
        wp, wg, ep = _load_stubs()
        self._wp = wp
        self._wg = wg
        self._ep = ep
        self._models: dict[str, dict] = {}
        self._arenas: dict[str, TensorArena] = {}
        self._arena_dir = os.environ.get("STACKSCOPE_ARENA_DIR",
                                         os.path.join(os.getcwd(), "arena"))
        self._start_time_ns = now_ns()

    # ---- Attach-mode entry point -----------------------------------------

    def set_preloaded_model(self, model, *, handle: str = "attached",
                            tokenizer=None, device: str = "cpu",
                            config=None) -> str:
        """Register a model instance loaded elsewhere (e.g. from a
        running notebook) so RunInference can drive it without
        re-loading weights. Returns the handle to pass to RunInference.
        """
        self._models[handle] = {
            "model": model, "tokenizer": tokenizer,
            "device": device, "config": config,
        }
        return handle

    # ---- Service interface ------------------------------------------------

    def GetCapabilities(self, request, context):
        from . import devices as _devices_mod
        rich = _devices_mod.enumerate_devices()
        device_info_msgs = []
        for d in rich:
            device_info_msgs.append(self._wp.DeviceInfo(
                id=d.id, kind=d.kind, name=d.name,
                total_memory_bytes=d.total_memory_bytes,
                free_memory_bytes=d.free_memory_bytes,
                compute_capability=d.compute_capability,
                driver_version=d.driver_version,
                multi_processor_count=d.multi_processor_count,
                is_integrated=d.is_integrated,
                is_default=d.is_default,
            ))
        return self._wp.CapabilitiesReply(
            worker_kind="pytorch",
            version="0.1.0",
            supported_formats=["safetensors", "hf_repo"],
            devices=[d.id for d in rich],   # legacy simple list
            supports_attention=True,
            supports_activations=True,
            supports_tensor_readback=True,
            device_info=device_info_msgs,
        )

    def LoadModel(self, request, context):
        model_path = request.model_path
        device     = self._resolve_device(request.device)
        trust      = request.trust_remote_code

        cfg = AutoConfig.from_pretrained(model_path, trust_remote_code=trust)
        dtype = torch.float16 if device.startswith("cuda") or device.startswith("dml") \
                              else torch.float32
        model = AutoModelForCausalLM.from_pretrained(
            model_path, torch_dtype=dtype, trust_remote_code=trust,
        )
        model.to(device)
        model.eval()
        tokenizer = AutoTokenizer.from_pretrained(model_path, trust_remote_code=trust)

        handle = f"m-{len(self._models)}"
        self._models[handle] = {
            "model": model, "tokenizer": tokenizer, "device": device, "config": cfg,
        }
        return self._wp.LoadModelReply(
            model_handle=handle,
            architecture=(cfg.architectures or ["Unknown"])[0],
            n_layers=getattr(cfg, "num_hidden_layers", getattr(cfg, "n_layer", 0)),
            n_heads=getattr(cfg, "num_attention_heads", getattr(cfg, "n_head", 0)),
            hidden_size=getattr(cfg, "hidden_size", getattr(cfg, "n_embd", 0)),
            vocab_size=getattr(cfg, "vocab_size", 0),
            resolved_device=str(device),
            # Python path: torch actually placed the tensors on `device`
            # (we called `.to(device)` ourselves), so this readback is
            # authoritative — flag it verified.
            resolved_device_verified=True,
        )

    def _resolve_device(self, requested: str) -> str:
        """Turn a UI device string into a concrete torch device.

        Accepts: ``""``, ``"cpu"``, ``"cuda:N"``, ``"dml:N"`` (DirectML
        via torch-directml), ``"mps"``. Falls back to CPU if the
        requested runtime is unavailable rather than exploding.

        When the request is empty and ``STACKSCOPE_DEVICE_HINT`` is set
        (the coordinator seeds this from ``StartWorkerRequest.device_hint``
        which the WPF dropdown fills), we use that as the default so
        the very first LoadModel already honours the user's UI pick
        even before RunInference runs.
        """
        req = (requested or "").strip().lower()
        if req == "":
            hint = (os.environ.get("STACKSCOPE_DEVICE_HINT") or "").strip().lower()
            if hint:
                req = hint
                log.info("device empty on LoadModel — using STACKSCOPE_DEVICE_HINT=%s", hint)
        if req == "" or req == "cpu":
            return "cpu"
        if req.startswith("cuda") and torch.cuda.is_available():
            return req
        if req.startswith("dml"):
            try:
                import torch_directml  # type: ignore
                # torch_directml.device(0) returns a torch.device; the
                # string form is what the .to(...) call expects.
                idx = int(req.split(":", 1)[1]) if ":" in req else 0
                return str(torch_directml.device(idx))
            except ImportError:
                return "cpu"
        if req.startswith("mps") and getattr(torch.backends, "mps", None) \
                and torch.backends.mps.is_available():
            return "mps"
        return "cpu"

    def UnloadModel(self, request, context):
        entry = self._models.pop(request.model_handle, None)
        if entry is not None:
            del entry["model"]; del entry["tokenizer"]
            if torch.cuda.is_available():
                torch.cuda.empty_cache()
        return self._wp.UnloadModelReply()

    def RunInference(self, request, context):
        entry = self._models.get(request.model_handle)
        if entry is None:
            context.set_code(grpc.StatusCode.NOT_FOUND)
            context.set_details(f"unknown model_handle: {request.model_handle}")
            return

        model, tokenizer, device = entry["model"], entry["tokenizer"], entry["device"]
        arena = TensorArena(request.transaction_id, self._arena_dir) \
            if request.capture_level >= 2 else None
        if arena is not None:
            self._arenas[request.transaction_id] = arena

        hooks = HookCapture(
            capture_attention=(request.capture_level >= 1),
            capture_activations=(request.capture_level >= 1),
        )
        hooks.attach(model)
        anomaly = AnomalyDetector()

        ablate_layer     = getattr(request, "ablate_layer", -1)
        ablate_head      = getattr(request, "ablate_head", -1)
        ablate_layer_end = getattr(request, "ablate_layer_end", -1)
        ablate_head_end  = getattr(request, "ablate_head_end", -1)
        # Normalise: -1 on the *_end fields means "single cell", i.e. the
        # rectangle collapses to just (ablate_layer, ablate_head). ≥ start
        # activates rectangular zeroing so [layer..layer_end] × [head..head_end]
        # are all zeroed within this one capture.
        if ablate_layer >= 0 and ablate_head >= 0:
            layer_lo, layer_hi = ablate_layer, max(ablate_layer, ablate_layer_end)
            head_lo,  head_hi  = ablate_head,  max(ablate_head,  ablate_head_end)
        else:
            layer_lo = layer_hi = head_lo = head_hi = -1

        ablation_handles = []
        if layer_lo >= 0 and head_lo >= 0:
            heads_to_zero = set(range(head_lo, head_hi + 1))
            layers_to_hook = set(range(layer_lo, layer_hi + 1))
            for name, mod in model.named_modules():
                if not is_top_level_attention(mod): continue
                parts = name.split(".")
                li = next((int(p) for p in parts if p.isdigit()), -1)
                if li not in layers_to_hook: continue

                def zero_heads(m, args, kwargs, output, _heads=heads_to_zero):
                    def zero_tensor(t):
                        if not isinstance(t, torch.Tensor) or t.ndim < 3: return t
                        n_heads = getattr(m, "num_heads", None) or \
                                  getattr(m, "n_head", None) or \
                                  getattr(getattr(m, "config", None), "num_attention_heads", None)
                        if not n_heads: return t
                        try:
                            b, s, hidden = t.shape
                        except ValueError:
                            return t
                        if hidden % n_heads != 0: return t
                        head_dim = hidden // n_heads
                        t = t.clone()
                        for h in _heads:
                            if 0 <= h < n_heads:
                                t[..., h*head_dim:(h+1)*head_dim] = 0
                        return t
                    if isinstance(output, torch.Tensor):
                        return zero_tensor(output)
                    if isinstance(output, tuple):
                        return tuple(zero_tensor(x) if isinstance(x, torch.Tensor) else x
                                     for x in output)
                    return output
                ablation_handles.append(
                    mod.register_forward_hook(zero_heads, with_kwargs=True))

        # Per-head attention capture: wrap each top-level attention module.
        attn_handles = []
        current_token_ref = [0]
        current_event_id = [0]

        def make_attn_pre(_layer_idx):
            def pre_hook(mod, args, kwargs):
                # Force output_attentions=True so we get the tensor back.
                if "output_attentions" in kwargs or "output_attentions" in \
                        (getattr(mod, "forward", None).__code__.co_varnames if callable(getattr(mod, "forward", None)) else ()):
                    kwargs["output_attentions"] = True
                return args, kwargs
            return pre_hook

        def make_attn_post(layer_idx):
            def post_hook(mod, args, kwargs, output):
                attn_weights = None
                if isinstance(output, tuple):
                    for item in output:
                        if isinstance(item, torch.Tensor) and item.ndim == 4:
                            attn_weights = item; break
                if attn_weights is None: return
                stats = compute_head_stats(attn_weights)
                ts = now_ns()
                for st in stats:
                    payload = pack_head_stats(st)
                    if arena is not None:
                        # Also persist the raw per-head row for forensic readback.
                        row = attn_weights[0, st.head, -1, :].detach().cpu().numpy()
                        arena.write(current_event_id[0], row)
                    hooks._q.put(Event(  # type: ignore[attr-defined]
                        kind=Kind.ATTENTION_SCORES,
                        timestamp_ns=ts,
                        token_index=current_token_ref[0],
                        layer_index=layer_idx,
                        head_index=st.head,
                        payload=payload,
                        marker_name=f"attn.L{layer_idx}.H{st.head}",
                        marker_begin_ns=ts, marker_end_ns=ts,
                    ))
                    current_event_id[0] += 1
            return post_hook

        for name, module in model.named_modules():
            if not is_top_level_attention(module): continue
            layer_idx = -1
            for part in name.split("."):
                if part.isdigit(): layer_idx = int(part); break
            attn_handles.append(module.register_forward_pre_hook(
                make_attn_pre(layer_idx), with_kwargs=True))
            attn_handles.append(module.register_forward_hook(
                make_attn_post(layer_idx), with_kwargs=True))

        event_id = 0

        try:
            tokens = tokenizer(request.prompt, return_tensors="pt").to(device)
            input_ids = tokens["input_ids"]
            attention_mask = tokens.get("attention_mask")

            gen = torch.Generator(device=device.split(":")[0] if ":" in device else device)
            gen.manual_seed(int(request.seed) if request.seed else 0)

            generated = input_ids
            for tok_i in range(request.max_new_tokens):
                current_token_ref[0] = tok_i
                hooks.note_token_begin(tok_i)
                with torch.no_grad():
                    out = model(generated, attention_mask=attention_mask)
                    logits = out.logits[:, -1, :]

                    if request.temperature > 0:
                        probs = torch.softmax(logits / max(1e-6, request.temperature), dim=-1)
                        if request.top_k > 0:
                            v, idx = torch.topk(probs, request.top_k)
                            probs = torch.zeros_like(probs).scatter_(1, idx, v)
                            probs = probs / probs.sum(dim=-1, keepdim=True)
                        # Nucleus (top-p) filter: keep the smallest set of
                        # tokens whose cumulative probability exceeds top_p,
                        # zero the rest, renormalise. Runs after top-k so
                        # both filters compose the way llama.cpp orders
                        # them (top-k → top-p → temp).
                        if 0.0 < request.top_p < 1.0:
                            sorted_probs, sorted_idx = torch.sort(probs, descending=True, dim=-1)
                            cum = torch.cumsum(sorted_probs, dim=-1)
                            keep = cum <= request.top_p
                            # Always keep the top token so we never end up
                            # with an all-zero row when the first token
                            # already exceeds top_p.
                            keep[..., 0] = True
                            filtered = torch.where(keep, sorted_probs, torch.zeros_like(sorted_probs))
                            probs = torch.zeros_like(probs).scatter_(1, sorted_idx, filtered)
                            probs = probs / probs.sum(dim=-1, keepdim=True).clamp_min(1e-12)
                        next_id = torch.multinomial(probs, 1, generator=gen)
                    else:
                        next_id = torch.argmax(logits, dim=-1, keepdim=True)

                    hooks.note_logits(tok_i, logits[0].detach())
                    generated = torch.cat([generated, next_id], dim=-1)
                    if attention_mask is not None:
                        attention_mask = torch.cat(
                            [attention_mask, torch.ones_like(next_id)], dim=-1)

                sampled = int(next_id.item())
                hooks.note_token_end(
                    tok_i, sampled, float(logits[0, sampled].item()))

                for e in hooks.events():
                    for extra in anomaly.observe(e):
                        yield self._to_proto(extra, request.transaction_id, event_id)
                        event_id += 1
                    yield self._to_proto(e, request.transaction_id, event_id)
                    event_id += 1

                if tokenizer.eos_token_id is not None and sampled == tokenizer.eos_token_id:
                    break
        finally:
            hooks.detach()
            for h in attn_handles: h.remove()
            for h in ablation_handles: h.remove()
            while True:
                pending = hooks.events(timeout=0.0)
                if not pending: break
                for e in pending:
                    for extra in anomaly.observe(e):
                        yield self._to_proto(extra, request.transaction_id, event_id)
                        event_id += 1
                    yield self._to_proto(e, request.transaction_id, event_id)
                    event_id += 1

    def ReadTensor(self, request, context):
        arena = self._arenas.get(request.transaction_id)
        if arena is None:
            context.set_code(grpc.StatusCode.FAILED_PRECONDITION)
            context.set_details(
                "No tensor arena for this transaction. "
                "Rerun with capture_level=CAPTURE_FORENSIC to enable readback.")
            return self._wp.ReadTensorReply()
        try:
            data, dtype, shape = arena.read(request.event_id)
        except KeyError:
            context.set_code(grpc.StatusCode.NOT_FOUND)
            context.set_details(
                f"No tensor slice for event_id={request.event_id} "
                f"in transaction {request.transaction_id}.")
            return self._wp.ReadTensorReply()
        return self._wp.ReadTensorReply(
            data=data, dtype=dtype, shape=list(shape))

    def Heartbeat(self, request, context):
        vram = 0
        if torch.cuda.is_available():
            try: vram = torch.cuda.memory_allocated()
            except Exception: vram = 0
        rss = _process_rss()
        return self._wp.HeartbeatReply(
            uptime_ns=now_ns() - self._start_time_ns,
            rss_bytes=rss, vram_bytes=vram,
            active_txns=len(self._models),
        )

    # ---- helpers ---------------------------------------------------------

    def _enumerate_devices(self) -> list[str]:
        # Legacy helper kept for callers outside GetCapabilities.
        # For rich enumeration use `devices.enumerate_devices()`.
        from . import devices as _dm
        return [d.id for d in _dm.enumerate_devices()]

    def _to_proto(self, e: Event, txid: str, event_id: int):
        markers = []
        if e.marker_name is not None:
            markers.append(self._ep.TraceMarker(
                name=e.marker_name,
                begin_ns=e.marker_begin_ns,
                end_ns=e.marker_end_ns,
                color_rgba=0xFFA0A0A0,
                thread_id=e.thread_id,
                stream_id=e.stream_id,
                correlation_id=e.marker_correlation_id,
            ))
        return self._ep.Event(
            event_id=event_id,
            transaction_id=txid,
            timestamp_ns=e.timestamp_ns,
            kind=e.kind,
            token_index=e.token_index,
            layer_index=e.layer_index,
            head_index=e.head_index,
            thread_id=e.thread_id,
            stream_id=e.stream_id,
            device_id=e.device_id,
            payload=e.payload,
            markers=markers,
        )


def _process_rss() -> int:
    try:
        import resource
        return resource.getrusage(resource.RUSAGE_SELF).ru_maxrss * 1024
    except Exception:
        try:
            with open(f"/proc/{os.getpid()}/status", "r") as f:
                for line in f:
                    if line.startswith("VmRSS:"):
                        return int(line.split()[1]) * 1024
        except Exception:
            pass
    return 0
