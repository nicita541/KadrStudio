from __future__ import annotations

import argparse
import json
import os
import signal
from concurrent import futures
from pathlib import Path

import grpc

from .analyzers import CapabilityUnavailable, _shutdown_llama, count_tokens, resolve_assets, run_analyzer
from .proto_wire import binary, field_bytes, field_int, field_text, parse_fields, text, texts


PROTOCOL_VERSION = "2"


class WorkerService:
    def __init__(self, analyzer: str, data_root: Path, protocol_version: str) -> None:
        if protocol_version != PROTOCOL_VERSION:
            raise ValueError(f"unsupported protocol version {protocol_version}")
        self.analyzer = analyzer
        self.data_root = data_root.resolve()

    def run_job(self, request: bytes, context: grpc.ServicerContext) -> bytes:
        try:
            fields = parse_fields(request)
            analyzer = text(fields, 2)
            if analyzer != self.analyzer:
                raise ValueError(f"worker {self.analyzer} cannot execute {analyzer}")
            if text(fields, 6) != PROTOCOL_VERSION:
                raise ValueError("worker protocol mismatch")
            parameters = json.loads(binary(fields, 5) or b"{}")
            assets = resolve_assets(self.data_root, texts(fields, 4), parameters)
            result = run_analyzer(analyzer, assets, parameters, self.data_root)
            payload = json.dumps(result, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
            return field_int(1, 1) + field_bytes(4, payload)
        except CapabilityUnavailable as exception:
            return field_int(1, 0) + field_text(2, "capability_unavailable") + field_text(3, str(exception))
        except Exception as exception:  # process survives one damaged job
            return field_int(1, 0) + field_text(2, "worker_failed") + field_text(3, str(exception))

    def count_tokens(self, request: bytes, context: grpc.ServicerContext) -> bytes:
        fields = parse_fields(request)
        if text(fields, 3) != PROTOCOL_VERSION:
            context.abort(grpc.StatusCode.FAILED_PRECONDITION, "worker protocol mismatch")
        try:
            return field_int(1, count_tokens(text(fields, 1), text(fields, 2)))
        except CapabilityUnavailable as exception:
            context.abort(grpc.StatusCode.UNAVAILABLE, str(exception))

    def shutdown(self, request: bytes, context: grpc.ServicerContext) -> bytes:
        _shutdown_llama()
        return field_int(1, 1)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--analyzer", required=True)
    parser.add_argument("--grpc-port", required=True, type=int)
    parser.add_argument("--protocol-version", required=True)
    parser.add_argument("--data-root", required=True, type=Path)
    arguments = parser.parse_args()
    os.environ["KADR_WORKER_ANALYZER"] = arguments.analyzer
    service = WorkerService(arguments.analyzer, arguments.data_root, arguments.protocol_version)
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=2), options=[
        ("grpc.max_receive_message_length", 64 * 1024 * 1024),
        ("grpc.max_send_message_length", 64 * 1024 * 1024),
    ])
    handler = grpc.method_handlers_generic_handler("kadr.worker.v2.Worker", {
        "RunJob": grpc.unary_unary_rpc_method_handler(
            service.run_job, request_deserializer=lambda value: value, response_serializer=lambda value: value),
        "CountTokens": grpc.unary_unary_rpc_method_handler(
            service.count_tokens, request_deserializer=lambda value: value, response_serializer=lambda value: value),
        "Shutdown": grpc.unary_unary_rpc_method_handler(
            service.shutdown, request_deserializer=lambda value: value, response_serializer=lambda value: value),
    })
    server.add_generic_rpc_handlers((handler,))
    server.add_insecure_port(f"127.0.0.1:{arguments.grpc_port}")
    server.start()
    signal.signal(signal.SIGINT, lambda *_: server.stop(grace=3))
    signal.signal(signal.SIGTERM, lambda *_: server.stop(grace=3))
    server.wait_for_termination()


if __name__ == "__main__":
    main()
