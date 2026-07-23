"""Entry point for the StackScope inference worker.

Usage:
    python -m stackscope_worker.worker --endpoint 127.0.0.1:50501
"""
from __future__ import annotations

import argparse
import logging
import signal
import sys
from concurrent import futures

import grpc

from .grpc_service import InferenceWorkerServicer


def main() -> int:
    parser = argparse.ArgumentParser(
        prog="stackscope-worker",
        description="StackScope Python inference worker (PyTorch/Transformers).",
    )
    parser.add_argument("--endpoint", default="127.0.0.1:50501",
                        help="host:port to bind the gRPC server on.")
    parser.add_argument("--max-workers", type=int, default=8)
    parser.add_argument("--log-level", default="INFO")
    parser.add_argument("--dry-run-hooks", metavar="MODEL", default=None,
                        help="Skip serving. Print the hook classification for "
                             "MODEL (a toy arch: tiny|llama|mistral|qwen|gemma|gpt2 "
                             "or 'hf:<id>' for an HF checkpoint) and exit. "
                             "Fastest way to diagnose hook-detection bugs.")
    args = parser.parse_args()

    if args.dry_run_hooks:
        from . import dry_run
        if args.dry_run_hooks.startswith("hf:"):
            names = dry_run._named_modules_from_hf(args.dry_run_hooks[3:])
        else:
            names = dry_run._named_modules_from_toy(args.dry_run_hooks)
        print(dry_run._format_rows(dry_run.classify(names)))
        return 0

    logging.basicConfig(level=args.log_level.upper(),
                        format="%(asctime)s %(levelname)s %(name)s: %(message)s")
    log = logging.getLogger("stackscope-worker")

    from stackscope_worker._generated import worker_pb2_grpc

    servicer = InferenceWorkerServicer()
    server = grpc.server(
        futures.ThreadPoolExecutor(max_workers=args.max_workers),
        options=[
            ("grpc.max_send_message_length", 128 * 1024 * 1024),
            ("grpc.max_receive_message_length", 128 * 1024 * 1024),
            ("grpc.keepalive_time_ms", 30_000),
        ],
    )
    worker_pb2_grpc.add_InferenceWorkerServicer_to_server(servicer, server)
    server.add_insecure_port(args.endpoint)
    server.start()
    log.info("StackScope inference worker listening on %s", args.endpoint)

    stop = server.wait_for_termination
    def _sig(*_):
        log.info("SIGTERM received; shutting down.")
        server.stop(grace=2).wait()
        sys.exit(0)

    signal.signal(signal.SIGINT, _sig)
    signal.signal(signal.SIGTERM, _sig)
    stop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
