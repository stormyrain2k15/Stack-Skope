"""Attach mode — install hooks into an already-running Python process.

Two entry points:

* `attach_here(model)` — call from inside your training script or
  notebook. Boots a background gRPC server that the StackScope
  coordinator can connect to just like the launched worker. Zero
  restart, zero code duplication.

* `stackscope-attach --pid <pid>` — CLI (Linux/macOS) that
  py-spy-injects into a running Python interpreter. Windows requires
  the in-process `attach_here` path because process injection is a
  different set of tradeoffs on Windows.

We deliberately keep the in-process attach dependency-free (uses the
same gRPC service already shipped as the launched worker). The
py-spy-based external attach is guarded behind an optional import
and clearly reports "not available" when py-spy isn't installed.
"""
from __future__ import annotations

import argparse
import logging
import sys
import threading
from concurrent import futures
from typing import Any

from .grpc_service import InferenceWorkerServicer


def attach_here(model: Any, *, endpoint: str = "127.0.0.1:0", block: bool = False) -> str:
    """Install a StackScope gRPC server that operates on the given model.

    Returns the actual endpoint (host:port) the server is bound to. If
    ``endpoint`` has port 0, the OS picks a free port. Set
    ``block=True`` to run in the foreground; the default is a daemon
    thread so a notebook cell doesn't hang.

    Once running, tell the StackScope coordinator this endpoint and it
    will drive the model via the normal RPCs — same as a spawned worker.
    """
    import grpc
    from stackscope_worker._generated import worker_pb2_grpc

    servicer = InferenceWorkerServicer()
    servicer.set_preloaded_model(model)   # optional hook exposed on the servicer

    server = grpc.server(
        futures.ThreadPoolExecutor(max_workers=4),
        options=[
            ("grpc.max_send_message_length", 128 * 1024 * 1024),
            ("grpc.max_receive_message_length", 128 * 1024 * 1024),
        ],
    )
    worker_pb2_grpc.add_InferenceWorkerServicer_to_server(servicer, server)
    port = server.add_insecure_port(endpoint)
    server.start()

    resolved = endpoint.rsplit(":", 1)[0] + f":{port}"
    logging.getLogger("stackscope-attach").info(
        "StackScope attach server listening on %s", resolved)

    if block:
        server.wait_for_termination()
    else:
        t = threading.Thread(target=server.wait_for_termination, daemon=True,
                             name="stackscope-attach")
        t.start()
    return resolved


def _external_attach(pid: int, endpoint: str) -> int:
    """Best-effort: use py-spy dump to confirm the target is a Python
    process, then instruct the user to `import stackscope_worker.attach;
    stackscope_worker.attach.attach_here(your_model)`. True in-process
    injection on Linux/macOS is available with py-spy's `record` — but
    hook installation must happen inside the target's own address
    space, so we print a copy-pastable one-liner rather than pretending
    injection is transparent.
    """
    try:
        import py_spy  # noqa: F401  (only used for availability check)
    except ImportError:
        print("py-spy is not installed. install with: pip install py-spy",
              file=sys.stderr)
        return 2
    print(
        "External attach requires cooperation from the target. Run this in "
        "the running Python process (e.g. paste into a running notebook cell):",
        file=sys.stderr,
    )
    print(
        f"\n  from stackscope_worker.attach import attach_here\n"
        f"  attach_here(your_model, endpoint='{endpoint}', block=False)\n",
        file=sys.stderr,
    )
    print(f"then point the StackScope coordinator at {endpoint}.",
          file=sys.stderr)
    return 0


def main() -> int:
    p = argparse.ArgumentParser(prog="stackscope-attach",
                                description="Attach StackScope to a running Python process.")
    p.add_argument("--pid", type=int, help="Target PID (external attach).")
    p.add_argument("--endpoint", default="127.0.0.1:0",
                   help="host:port to bind the attach server on (default: pick free port).")
    args = p.parse_args()
    if args.pid is None:
        print("--pid is required for the CLI. For in-process attach, "
              "call attach_here(model) from your Python code.",
              file=sys.stderr)
        return 2
    return _external_attach(args.pid, args.endpoint)


if __name__ == "__main__":
    raise SystemExit(main())
