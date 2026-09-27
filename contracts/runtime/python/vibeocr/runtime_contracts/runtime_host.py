"""Schema-backed parsing for Runtime Host process responses."""

from __future__ import annotations

import json
from functools import cache
from importlib import resources
from typing import Any, TypeAlias, cast

from jsonschema import Draft202012Validator
from jsonschema.exceptions import ValidationError
from vibeocr.runtime_contracts.generated.runtime_host_types import (
    RuntimeHostFailure,
    RuntimeHostSuccess,
    RuntimeInstallPlanResponse,
    RuntimeMaintenanceEvent,
    RuntimeMaintenanceUpdate,
)

RuntimeHostResponse: TypeAlias = (
    RuntimeHostSuccess
    | RuntimeHostFailure
    | RuntimeMaintenanceEvent
    | RuntimeMaintenanceUpdate
    | RuntimeInstallPlanResponse
)

_RESPONSE_DEFINITIONS = (
    "RuntimeMaintenanceEvent",
    "RuntimeMaintenanceUpdate",
    "RuntimeHostSuccess",
    "RuntimeHostFailure",
    "RuntimeInstallPlanResponse",
)


class RuntimeHostValidationError(ValueError):
    """Raised when process output is not a valid Runtime Host response."""


def _forward_compatible_response_schema(value: Any) -> Any:
    if isinstance(value, dict):
        schema = {
            key: _forward_compatible_response_schema(item)
            for key, item in value.items()
        }
        if schema.get("additionalProperties") is False:
            schema["additionalProperties"] = True
        return schema
    if isinstance(value, list):
        return [_forward_compatible_response_schema(item) for item in value]
    return value


@cache
def _response_validator() -> Draft202012Validator:
    raw = (
        resources.files("vibeocr.runtime_contracts")
        .joinpath("runtime-host.schema.json")
        .read_text(encoding="utf-8")
    )
    authoritative = json.loads(raw)
    schema = {
        "$schema": authoritative["$schema"],
        "$defs": authoritative["$defs"],
        "oneOf": [
            {"$ref": f"#/$defs/{definition}"} for definition in _RESPONSE_DEFINITIONS
        ],
    }
    compatible = _forward_compatible_response_schema(schema)
    Draft202012Validator.check_schema(compatible)
    return Draft202012Validator(compatible)


def validate_runtime_host_response(value: object) -> RuntimeHostResponse:
    """Validate one decoded response while accepting future optional fields."""
    try:
        _response_validator().validate(value)
    except ValidationError as exc:
        instance_path = "/" + "/".join(str(item) for item in exc.path)
        schema_path = "/" + "/".join(str(item) for item in exc.schema_path)
        raise RuntimeHostValidationError(
            "runtime response violates Runtime Host schema "
            f"at {instance_path} (schema {schema_path}): {exc.message}"
        ) from exc
    response = cast("RuntimeHostResponse", value)
    if response.get("response_kind") == "install_plan":
        components = response["plan"]["components"]
        if len({item["component_id"] for item in components}) != len(components):
            raise RuntimeHostValidationError(
                "install plan component_id values must be unique"
            )
    return response


def parse_runtime_host_response(
    raw: str | bytes | bytearray,
) -> RuntimeHostResponse:
    """Decode and validate one JSON response emitted by the Runtime Host CLI."""
    try:
        value: object = json.loads(raw)
    except (TypeError, ValueError) as exc:
        raise RuntimeHostValidationError("runtime response must be valid JSON") from exc
    return validate_runtime_host_response(value)


__all__ = [
    "RuntimeHostResponse",
    "RuntimeHostValidationError",
    "parse_runtime_host_response",
    "validate_runtime_host_response",
]
