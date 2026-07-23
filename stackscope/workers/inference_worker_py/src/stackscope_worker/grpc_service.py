"""gRPC service for the Python inference worker.

Implements ``InferenceWorker`` from proto/worker.proto. This module
imports the generated ``stackscope_pb2`` and ``stackscope_pb2_grpc``
modules that live under ``stackscope_worker._generated`` — those are
produced at install time by the ``scripts/gen_proto.sh`` helper.
"""
from __future__ import annotations

import argparse
import asyncio
import logging
import os
import time
from concurrent import futures
from typing import Optional

import grpc
import torch
from transformers import AutoConfig, AutoModelForCausalLM, AutoTokenizer

from .hooks import HookCapture, Kind, Event
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
        self._start_time_ns = now_ns()

    # ---- Service interface ------------------------------------------------

    def GetCapabilities(self, request, context):
        return self._wp.CapabilitiesReply(
            worker_kind="pytorch",
            version="0.1.0",
            supported_formats=["safetensors", "hf_repo"],
            devices=self._enumerate_devices(),
            supports_attention=True,
            supports_activations=True,
            supports_tensor_readback=True,
        )

    def LoadModel(self, request, context):
        model_path = request.model_path
        device     = request.device or ("cuda:0" if torch.cuda.is_available() else "cpu")
        trust      = request.trust_remote_code

        cfg = AutoConfig.from_pretrained(model_path, trust_remote_code=trust)
        model = AutoModelForCausalLM.from_pretrained(
            model_path, torch_dtype=torch.float16 if "cuda" in device else torch.float32,
            trust_remote_code=trust,
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
        )

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
        hooks = HookCapture(
            capture_attention=(request.capture_level >= 1),
            capture_activations=(request.capture_level >= 1),
        )
        hooks.attach(model)

        event_id = 0

        try:
            tokens = tokenizer(request.prompt, return_tensors="pt").to(device)
            input_ids = tokens["input_ids"]
            attention_mask = tokens.get("attention_mask")

            gen = torch.Generator(device=device.split(":")[0] if ":" in device else device)
            gen.manual_seed(int(request.seed) if request.seed else 0)

            generated = input_ids
            for tok_i in range(request.max_new_tokens):
                hooks.note_token_begin(tok_i)
                with torch.no_grad():
                    out = model(generated, attention_mask=attention_mask)
                    logits = out.logits[:, -1, :]

                    # Sampling
                    if request.temperature > 0:
                        probs = torch.softmax(logits / max(1e-6, request.temperature), dim=-1)
                        if request.top_k > 0:
                            v, idx = torch.topk(probs, request.top_k)
                            probs = torch.zeros_like(probs).scatter_(1, idx, v)
                            probs = probs / probs.sum(dim=-1, keepdim=True)
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

                # Drain hook events into the wire.
                for e in hooks.events():
                    yield self._to_proto(e, request.transaction_id, event_id)
                    event_id += 1

                if tokenizer.eos_token_id is not None and sampled == tokenizer.eos_token_id:
                    break
        finally:
            hooks.detach()
            # Flush any remaining events.
            while True:
                pending = hooks.events(timeout=0.0)
                if not pending: break
                for e in pending:
                    yield self._to_proto(e, request.transaction_id, event_id)
                    event_id += 1

    def ReadTensor(self, request, context):
        # In this pass we don't persist worker-side tensor arenas across
        # streaming calls; if a tensor was captured as an inline payload,
        # the coordinator already has it. This RPC is here for the
        # forensic path where a worker keeps a session cache — deferred
        # honestly to the next pass.
        context.set_code(grpc.StatusCode.UNIMPLEMENTED)
        context.set_details("Tensor readback requires forensic session cache (deferred).")
        return self._wp.ReadTensorReply()

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
        devs = ["cpu"]
        if torch.cuda.is_available():
            devs.extend(f"cuda:{i}" for i in range(torch.cuda.device_count()))
        return devs

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
