from __future__ import annotations

import json

import pytest
from vibeocr.runtime_contracts.runtime_host import (
    RuntimeHostValidationError,
    parse_runtime_host_response,
    validate_runtime_host_response,
)


def _success() -> dict[str, object]:
    return {
        "protocol_version": 2,
        "ok": True,
        "operation": "ensure",
        "state": {
            "runtime_root": "C:/VibeOCR/data/runtime",
            "accelerator": "cpu",
            "status": "ready",
            "integrity": "verified",
            "manifest_sha256": "a" * 64,
            "backend_version": "0.11.1",
        },
    }


def test_parse_runtime_host_response_uses_authoritative_schema() -> None:
    payload = _success()

    assert parse_runtime_host_response(json.dumps(payload)) == payload


def test_runtime_host_response_allows_future_optional_fields() -> None:
    payload = _success()
    payload["future_optional"] = {"enabled": True}

    assert validate_runtime_host_response(payload) is payload


def test_runtime_host_response_rejects_missing_required_field() -> None:
    payload = _success()
    del payload["state"]

    with pytest.raises(RuntimeHostValidationError, match="violates Runtime Host"):
        validate_runtime_host_response(payload)


def test_parse_runtime_host_response_rejects_malformed_json() -> None:
    with pytest.raises(RuntimeHostValidationError, match="valid JSON"):
        parse_runtime_host_response("{not-json")
