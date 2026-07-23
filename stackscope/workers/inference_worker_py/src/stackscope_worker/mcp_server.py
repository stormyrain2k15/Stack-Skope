"""MCP (Model Context Protocol) server for StackScope.

Exposes the capture as a set of MCP tools an AI assistant (Claude,
Cursor, any MCP client) can call directly. No prompt engineering, no
guessing — the assistant queries structured data and gets structured
answers.

Tools exposed:
    stackscope.list_events(kinds?, tokens?, layers?, head?, limit?)
    stackscope.hash()
    stackscope.summary()
    stackscope.first_divergence(other_bundle_path)
    stackscope.manifest()
    stackscope.model_descriptor()
    stackscope.classify(module_names)

Transport: newline-delimited JSON-RPC 2.0 over stdio, matching the
official MCP spec's "stdio" transport. Any MCP-aware client can spawn
this server and talk to it directly:

    stackscope-mcp <bundle.stackscope>

The server is deliberately dependency-free (stdlib only) so it runs
in the same container the worker runs in, no extra install.
"""
from __future__ import annotations

import argparse
import json
import sys
from typing import Any, Callable

from . import bundle as _bundle
from . import dry_run as _dry_run
from . import repro as _repro


PROTOCOL_VERSION = "2024-11-05"


class MCPServer:
    def __init__(self, bundle_path: str) -> None:
        self._bundle_path = bundle_path
        self._contents = _bundle.unpack(bundle_path)
        self._events = [
            json.loads(line)
            for line in self._contents.events_jsonl.splitlines()
            if line.strip()
        ]
        self._manifest = json.loads(self._contents.manifest_json)
        self._model = json.loads(self._contents.model_json) if self._contents.model_json.strip() else {}
        self._tools: dict[str, Callable[[dict], Any]] = {
            "stackscope.list_events":      self._list_events,
            "stackscope.hash":             self._hash,
            "stackscope.summary":          self._summary,
            "stackscope.first_divergence": self._first_divergence,
            "stackscope.manifest":         self._get_manifest,
            "stackscope.model_descriptor": self._get_model,
            "stackscope.classify":         self._classify,
        }

    # -- tool implementations ---------------------------------------------

    def _list_events(self, args: dict) -> list[dict]:
        events = self._events
        if kinds := args.get("kinds"):
            kinds_set = set(kinds)
            events = [e for e in events if e.get("kind") in kinds_set]
        if tokens := args.get("tokens"):
            tset = set(tokens)
            events = [e for e in events if e.get("token") in tset]
        if layers := args.get("layers"):
            lset = set(layers)
            events = [e for e in events if e.get("layer") in lset]
        if (head := args.get("head")) is not None:
            events = [e for e in events if e.get("head") == head]
        limit = int(args.get("limit", 500))
        return events[:limit]

    def _hash(self, _args: dict) -> str:
        return _repro._canonical_hash_of_dicts(self._events)

    def _summary(self, _args: dict) -> dict:
        from collections import Counter
        counts = Counter(e.get("kind") for e in self._events)
        layers = {e.get("layer") for e in self._events if e.get("layer", -1) >= 0}
        tokens = {e.get("token") for e in self._events if e.get("token", -1) >= 0}
        return {
            "total_events": len(self._events),
            "counts_by_kind": dict(counts),
            "unique_layers": sorted(layers),
            "unique_tokens": sorted(tokens),
        }

    def _first_divergence(self, args: dict) -> dict:
        other = _bundle.unpack(args["other_bundle_path"])
        others = [json.loads(line) for line in other.events_jsonl.splitlines() if line.strip()]
        identical, report = _repro.compare(self._events, others)
        return {"identical": identical, "report": report}

    def _get_manifest(self, _args: dict) -> dict:
        return self._manifest

    def _get_model(self, _args: dict) -> dict:
        return self._model

    def _classify(self, args: dict) -> list[dict]:
        return _dry_run.classify(args["module_names"])

    # -- JSON-RPC plumbing -------------------------------------------------

    def handle(self, req: dict) -> dict | None:
        rid = req.get("id")
        method = req.get("method")

        if method == "initialize":
            return {
                "jsonrpc": "2.0", "id": rid,
                "result": {
                    "protocolVersion": PROTOCOL_VERSION,
                    "serverInfo": {"name": "stackscope-mcp", "version": "0.1.0"},
                    "capabilities": {"tools": {"listChanged": False}},
                },
            }
        if method == "notifications/initialized":
            return None  # notification, no response
        if method == "tools/list":
            return {
                "jsonrpc": "2.0", "id": rid,
                "result": {
                    "tools": [
                        {"name": name, "description": self._describe(name),
                         "inputSchema": {"type": "object"}}
                        for name in self._tools
                    ]
                },
            }
        if method == "tools/call":
            params = req.get("params") or {}
            name = params.get("name")
            fn = self._tools.get(name)
            if fn is None:
                return {"jsonrpc": "2.0", "id": rid,
                        "error": {"code": -32601, "message": f"unknown tool {name}"}}
            try:
                result = fn(params.get("arguments") or {})
                return {"jsonrpc": "2.0", "id": rid,
                        "result": {"content": [{"type": "text",
                                                  "text": json.dumps(result)}]}}
            except Exception as ex:
                return {"jsonrpc": "2.0", "id": rid,
                        "error": {"code": -32000, "message": str(ex)}}
        return {"jsonrpc": "2.0", "id": rid,
                "error": {"code": -32601, "message": f"unknown method {method}"}}

    def _describe(self, name: str) -> str:
        return {
            "stackscope.list_events":      "List events, optionally filtered by kinds/tokens/layers/head.",
            "stackscope.hash":             "Canonical hash of the event stream.",
            "stackscope.summary":          "Counts by kind + unique layers/tokens.",
            "stackscope.first_divergence": "Compare events to another bundle. args: other_bundle_path.",
            "stackscope.manifest":         "Reproducibility manifest of the recording environment.",
            "stackscope.model_descriptor": "Model architecture + tokenizer + quant summary.",
            "stackscope.classify":         "Classify a list of module names by is_transformer_block, is_attention_projection, layer_idx.",
        }[name]


def main() -> int:
    p = argparse.ArgumentParser(
        prog="stackscope-mcp",
        description="MCP server exposing a .stackscope bundle as structured tools.")
    p.add_argument("bundle", help="Path to a .stackscope bundle.")
    args = p.parse_args()

    server = MCPServer(args.bundle)

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except json.JSONDecodeError:
            continue
        resp = server.handle(req)
        if resp is not None:
            sys.stdout.write(json.dumps(resp))
            sys.stdout.write("\n")
            sys.stdout.flush()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
