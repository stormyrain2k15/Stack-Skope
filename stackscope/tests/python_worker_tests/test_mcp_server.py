"""Tests for the MCP server."""
import json
from pathlib import Path

from stackscope_worker import bundle, manifest, mcp_server


def _write_test_bundle(tmp_path: Path, events: list[dict]) -> str:
    contents = bundle.build_from_events(
        events,
        model_descriptor={"arch": "TinyLlama", "n_layers": 2},
        manifest_obj=manifest.build(seed=1),
    )
    out = tmp_path / "cap.stackscope"
    bundle.pack(out, contents)
    return str(out)


def _sample_events():
    return [
        {"ts": 100, "kind": "TOKEN_BEGIN", "token": 0, "layer": -1, "head": -1, "payload_hex": ""},
        {"ts": 110, "kind": "LAYER_BEGIN", "token": 0, "layer": 0, "head": -1, "payload_hex": ""},
        {"ts": 120, "kind": "ATTENTION_QKV", "token": 0, "layer": 0, "head": 3, "payload_hex": "00"},
        {"ts": 130, "kind": "LAYER_END",   "token": 0, "layer": 0, "head": -1, "payload_hex": ""},
        {"ts": 140, "kind": "TOKEN_END",   "token": 0, "layer": -1, "head": -1, "payload_hex": ""},
    ]


def test_initialize_and_tools_list(tmp_path: Path):
    server = mcp_server.MCPServer(_write_test_bundle(tmp_path, _sample_events()))
    r = server.handle({"jsonrpc": "2.0", "id": 1, "method": "initialize"})
    assert r["result"]["protocolVersion"] == mcp_server.PROTOCOL_VERSION
    r = server.handle({"jsonrpc": "2.0", "id": 2, "method": "tools/list"})
    tool_names = {t["name"] for t in r["result"]["tools"]}
    assert "stackscope.list_events" in tool_names
    assert "stackscope.summary" in tool_names
    assert "stackscope.manifest" in tool_names
    assert "stackscope.classify" in tool_names


def _call(server, name, args):
    r = server.handle({
        "jsonrpc": "2.0", "id": 99, "method": "tools/call",
        "params": {"name": name, "arguments": args},
    })
    return json.loads(r["result"]["content"][0]["text"])


def test_summary_tool_returns_counts_by_kind(tmp_path: Path):
    server = mcp_server.MCPServer(_write_test_bundle(tmp_path, _sample_events()))
    result = _call(server, "stackscope.summary", {})
    assert result["total_events"] == 5
    assert result["counts_by_kind"]["LAYER_BEGIN"] == 1
    assert result["counts_by_kind"]["ATTENTION_QKV"] == 1
    assert result["unique_layers"] == [0]
    assert result["unique_tokens"] == [0]


def test_list_events_filters_by_kind_and_head(tmp_path: Path):
    server = mcp_server.MCPServer(_write_test_bundle(tmp_path, _sample_events()))
    result = _call(server, "stackscope.list_events",
                   {"kinds": ["ATTENTION_QKV"], "head": 3})
    assert len(result) == 1
    assert result[0]["kind"] == "ATTENTION_QKV"
    assert result[0]["head"] == 3


def test_classify_tool(tmp_path: Path):
    server = mcp_server.MCPServer(_write_test_bundle(tmp_path, _sample_events()))
    rows = _call(server, "stackscope.classify",
                 {"module_names": ["model.layers.0", "model.layers.0.mlp"]})
    assert rows[0]["is_block"] is True
    assert rows[1]["is_block"] is False
    assert rows[1]["layer_idx"] == 0


def test_hash_tool_is_deterministic(tmp_path: Path):
    b = _write_test_bundle(tmp_path, _sample_events())
    a = mcp_server.MCPServer(b)
    c = mcp_server.MCPServer(b)
    assert _call(a, "stackscope.hash", {}) == _call(c, "stackscope.hash", {})


def test_unknown_tool_returns_jsonrpc_error(tmp_path: Path):
    server = mcp_server.MCPServer(_write_test_bundle(tmp_path, _sample_events()))
    r = server.handle({
        "jsonrpc": "2.0", "id": 5, "method": "tools/call",
        "params": {"name": "stackscope.does_not_exist", "arguments": {}},
    })
    assert "error" in r
    assert r["error"]["code"] == -32601
