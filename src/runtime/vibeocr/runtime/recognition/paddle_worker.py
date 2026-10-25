"""Private stdio worker isolating Paddle ORT pipelines and their cv2."""

from __future__ import annotations

import base64
import json
import logging
import os
import sys
from dataclasses import asdict


def main() -> None:
    # Native libraries also print to fd 1. Reserve a duplicate for framed JSON
    # before redirecting stdout to stderr, so model logs cannot corrupt replies.
    replies = os.fdopen(os.dup(sys.stdout.fileno()), "w", encoding="utf-8", buffering=1)
    os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
        stream=sys.stderr,
    )
    from vibeocr.runtime.jobs.budgets import InputItem
    from vibeocr.runtime.recognition.ocr_service import OCRService
    from vibeocr.runtime.recognition.paddle_adapter import PaddlePipelineAdapter
    from vibeocr.runtime_contracts import (
        SettingsSnapshot,
        parse_pipeline_selection,
        parse_pipeline_spec,
    )

    adapter = PaddlePipelineAdapter(service=OCRService())
    try:
        for line in sys.stdin:
            try:
                request = json.loads(line)
                operation = request["operation"]
                if operation == "recognize":
                    items = [
                        InputItem(
                            **{
                                **item,
                                "data": base64.b64decode(item["data"], validate=True),
                            }
                        )
                        for item in request["items"]
                    ]
                    options = parse_pipeline_selection(request["options"])
                    from vibeocr.runtime.recognition.paddle_onnx import request_snapshot

                    with request_snapshot(request.get("snapshot")):
                        result = adapter.recognize_many(items, options=options)
                elif operation == "capabilities":
                    result = asdict(
                        adapter.capabilities(
                            parse_pipeline_selection(request["options"])
                        )
                    )
                elif operation == "settings":
                    raw = request["settings"]
                    residency = raw["residency"]
                    adapter.configure_settings(
                        SettingsSnapshot(
                            schema_version=raw["schema_version"],
                            default_ttl_seconds=residency["default_ttl_seconds"],
                            pipelines=tuple(
                                parse_pipeline_spec(p) for p in residency["pipelines"]
                            ),
                            extra=raw.get("extra", {}),
                            download_source_ids=tuple(
                                raw.get("download_source_ids", [])
                            ),
                        )
                    )
                    result = adapter.residency_status().to_payload()
                elif operation == "preload":
                    result = adapter.preload(tuple(request["pipelines"])).to_payload()
                elif operation == "release":
                    result = adapter.release_idle(request.get("pipeline")).to_payload()
                elif operation == "status":
                    result = adapter.residency_status().to_payload()
                else:
                    raise ValueError("Unknown Paddle worker operation")
                response = {"result": result}
            except Exception as exc:
                response = {"error": str(exc), "error_type": type(exc).__name__}
            replies.write(json.dumps(response, ensure_ascii=True) + "\n")
            replies.flush()
    finally:
        adapter.close()
        replies.close()


if __name__ == "__main__":
    main()
