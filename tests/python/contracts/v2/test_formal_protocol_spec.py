"""Formal Runtime API v2 specification and deterministic bindings tests."""

from __future__ import annotations

import base64
import json
import subprocess
import sys
import typing
from pathlib import Path

import jsonschema
import pytest

ROOT = Path(__file__).resolve().parents[4]
V2 = ROOT / "contracts/runtime/python/vibeocr/runtime_contracts"
METHODS = {"get", "put", "post", "delete", "patch", "head", "options", "trace"}


def _spec() -> dict:
    # JSON is a YAML 1.2 subset; keeping it JSON makes this formal .yaml
    # document independently parseable without adding a PyYAML build dependency.
    return json.loads((V2 / "openapi.yaml").read_text(encoding="utf-8"))


def _operations(spec: dict) -> list[tuple[str, str, dict]]:
    return [
        (method, path, operation)
        for path, item in spec["paths"].items()
        for method, operation in item.items()
        if method in METHODS
    ]


def test_error_json_schema_matches_the_formal_openapi_component() -> None:
    formal = _spec()["components"]["schemas"]["Error"]
    standalone = json.loads(
        (V2 / "schemas/errors.schema.json").read_text(encoding="utf-8")
    )

    assert {
        key: standalone[key]
        for key in ("type", "required", "properties", "additionalProperties")
    } == {
        key: formal[key]
        for key in ("type", "required", "properties", "additionalProperties")
    }


def test_formal_spec_is_openapi_31_with_real_41_operation_surface() -> None:
    spec = _spec()
    operations = _operations(spec)
    assert spec["openapi"] == "3.1.0"
    assert len(operations) == 41
    assert {(method.upper(), path) for method, path, _ in operations} == {
        ("GET", "/v2/health"),
        ("POST", "/v2/jobs"),
        ("GET", "/v2/jobs/{job_id}/observe"),
        ("POST", "/v2/jobs/command"),
        ("GET", "/v2/runtime/residency"),
        ("GET", "/v2/runtime/status"),
        ("POST", "/v2/runtime/install-plan"),
        ("POST", "/v2/runtime/maintenance"),
        ("POST", "/v2/runtime/maintenance/command"),
        ("GET", "/v2/runtime/operations/{operation_id}/observe"),
        ("GET", "/v2/runtime/operations/{operation_id}/events"),
        ("POST", "/v2/runtime/release"),
        ("POST", "/v2/runtime/preload"),
        ("GET", "/v2/settings"),
        ("PUT", "/v2/settings"),
        ("POST", "/v2/export"),
        ("POST", "/v2/qrcode/decode"),
        ("POST", "/v2/qrcode/generate"),
        ("POST", "/v2/pdf/sessions/open"),
        ("POST", "/v2/pdf/sessions/{session_id}/close"),
        ("POST", "/v2/pdf/sessions/{session_id}/model"),
        ("POST", "/v2/pdf/sessions/{session_id}/load"),
        ("GET", "/v2/pdf/sessions/{session_id}/render"),
        ("POST", "/v2/pdf/sessions/{session_id}/render_thumbnail"),
        ("POST", "/v2/pdf/sessions/{session_id}/render_preview"),
        ("POST", "/v2/pdf/sessions/{session_id}/detect_text_layers"),
        ("POST", "/v2/pdf/sessions/{session_id}/rotate"),
        ("POST", "/v2/pdf/sessions/{session_id}/delete_pages"),
        ("POST", "/v2/pdf/sessions/{session_id}/insert_blank"),
        ("POST", "/v2/pdf/sessions/{session_id}/insert_from"),
        ("POST", "/v2/pdf/sessions/{session_id}/move_page"),
        ("POST", "/v2/pdf/sessions/{session_id}/reorder"),
        ("POST", "/v2/pdf/sessions/{session_id}/add_text_layer"),
        ("POST", "/v2/pdf/sessions/{session_id}/add_text_layer_batch"),
        ("POST", "/v2/pdf/sessions/{session_id}/rewrite_text_layer"),
        ("POST", "/v2/pdf/sessions/{session_id}/update_block_text"),
        ("POST", "/v2/pdf/sessions/{session_id}/delete_text_layers"),
        ("POST", "/v2/pdf/sessions/{session_id}/save"),
        ("POST", "/v2/pdf/sessions/{session_id}/save_transactional"),
        ("POST", "/v2/pdf/sessions/{session_id}/cancel"),
        ("POST", "/v2/pdf/sessions/{session_id}/reset_cancel"),
    }


def test_operations_have_unique_ids_and_explicit_success_error_content() -> None:
    spec = _spec()
    operations = _operations(spec)
    ids = [operation["operationId"] for _, _, operation in operations]
    assert len(ids) == len(set(ids))
    for _, _, operation in operations:
        assert operation["responses"]
        assert any(
            "content" in response or "$ref" in response
            for response in operation["responses"].values()
        )
        assert any(
            "$ref" in response and response["$ref"].endswith("/Error")
            for response in operation["responses"].values()
        ) or operation["operationId"] in {"getSettings", "getRuntimeResidency"}


def test_multipart_binary_ndjson_and_ready_schema_are_explicit() -> None:
    spec = _spec()
    assert (
        "multipart/form-data"
        in spec["paths"]["/v2/jobs"]["post"]["requestBody"]["content"]
    )
    assert "image/png" in spec["components"]["responses"]["Png"]["content"]
    assert (
        "application/x-ndjson" in spec["components"]["responses"]["Ndjson"]["content"]
    )
    bootstrap = json.loads((V2 / "bootstrap.schema.json").read_text(encoding="utf-8"))
    jsonschema.Draft202012Validator.check_schema(bootstrap)
    jsonschema.validate(
        {
            "ready": True,
            "pid": 1,
            "port": 1,
            "instance_id": "x",
            "protocol_version": 2,
            "schema_version": 2,
            "ready_version": 1,
            "capabilities": ["ocr.recognition.v2"],
        },
        bootstrap,
    )


def test_runtime_host_control_protocol_is_formal_and_strict() -> None:
    host = json.loads((V2 / "runtime-host.schema.json").read_text(encoding="utf-8"))
    bootstrap = json.loads((V2 / "bootstrap.schema.json").read_text(encoding="utf-8"))
    jsonschema.Draft202012Validator.check_schema(host)
    assert (
        host["$defs"]["RuntimeHostRequest"]["properties"]["protocol_version"]["const"]
        == bootstrap["properties"]["protocol_version"]["const"]
    )
    request = {
        "protocol_version": 2,
        "operation": "ensure",
        "product_root": "C:/VibeOCR",
        "component_lock": "C:/VibeOCR/component-lock.json",
        "runtime_manifest": "C:/VibeOCR/backend/runtime-manifest.json",
        "accelerator": "nvidia_cuda",
    }
    jsonschema.validate(request, host)
    jsonschema.validate(
        {
            "protocol_version": 2,
            "ok": True,
            "operation": "inspect",
            "state": {
                "runtime_root": "C:/VibeOCR/data/runtime",
                "accelerator": "cpu",
                "status": "missing",
                "integrity": "not-installed",
                "manifest_sha256": "a" * 64,
                "backend_version": "0.8.0",
            },
            "launch": None,
        },
        host,
    )
    invalid = dict(request, profile="win-x64-cu126")
    with pytest.raises(jsonschema.ValidationError):
        jsonschema.validate(invalid, host)


def test_runtime_host_supports_opt_in_progress_and_profile_descriptors() -> None:
    host = json.loads((V2 / "runtime-host.schema.json").read_text(encoding="utf-8"))
    request = {
        "protocol_version": 2,
        "operation": "ensure",
        "product_root": "C:/VibeOCR",
        "component_lock": "C:/VibeOCR/component-lock.json",
        "runtime_manifest": "C:/VibeOCR/backend/runtime-manifest.json",
        "accepted_event_streams": ["ndjson.v1"],
    }
    jsonschema.validate(request, host)
    event = {
        "protocol_version": 2,
        "event_version": 1,
        "event_type": "progress",
        "sequence": 3,
        "operation": "ensure",
        "snapshot": {
            "operation_id": "install-1",
            "sequence": 3,
            "operation": "ensure",
            "operation_state": "running",
            "phase": "install_profile",
            "profile_id": "win-x64-cpu",
            "component_id": "ocr_engine",
            "updated_at": "2026-08-05T12:00:00Z",
            "progress": {"unit": "steps", "current": 2, "total": 5},
        },
        "message_code": "runtime.install.profile",
        "message_args": {"profile_id": "win-x64-cpu"},
        "fallback_message": "Installing the CPU runtime profile.",
    }
    jsonschema.validate(event, host)
    success = {
        "protocol_version": 2,
        "ok": True,
        "operation": "ensure",
        "state": {
            "runtime_root": "C:/VibeOCR/data/runtime",
            "accelerator": "cpu",
            "status": "ready",
            "integrity": "verified",
            "manifest_sha256": "a" * 64,
            "backend_version": "0.9.0",
        },
        "profile": {
            "profile_id": "win-x64-cpu",
            "accelerator": "cpu",
            "components": [
                {
                    "component_id": "ocr_engine",
                    "display_name": "OCR engine",
                    "version": "3.3.2",
                    "state": "ready",
                    "desired_state": "ready",
                    "desired_version": "3.3.2",
                    "actual_state": "ready",
                    "actual_version": "3.3.2",
                    "drift_reason": "none",
                    "repairable": False,
                }
            ],
        },
        "maintenance": event["snapshot"],
    }
    jsonschema.validate(success, host)


def test_runtime_host_formalizes_idempotent_commands_and_cursor_replay() -> None:
    host = json.loads((V2 / "runtime-host.schema.json").read_text(encoding="utf-8"))
    binding = {
        "product_root": "C:/VibeOCR",
        "component_lock": "C:/VibeOCR/component-lock.json",
        "runtime_manifest": "C:/VibeOCR/backend/runtime-manifest.json",
        "accelerator": "cpu",
        "layout_manifest": "C:/VibeOCR/portable-layout.json",
        "product_id": "classic",
    }
    start = {
        "protocol_version": 2,
        "operation": "repair",
        "operation_id": "operation-1",
        "component_ids": ["ocr_engine"],
        "required_capabilities": [
            "runtime.maintenance.v2",
            "runtime.component-repair.v1",
        ],
        "accepted_event_streams": ["ndjson.v2"],
        **binding,
    }
    jsonschema.validate(start, host)

    cancel = {
        "protocol_version": 2,
        "request_kind": "command",
        "command": "cancel",
        "command_id": "command-1",
        "target_operation_id": "operation-1",
        "expected_sequence": 7,
        **binding,
    }
    jsonschema.validate(cancel, host)

    retry = {
        **cancel,
        "command": "retry",
        "command_id": "command-2",
        "new_operation_id": "operation-2",
    }
    jsonschema.validate(retry, host)

    observe = {
        "protocol_version": 2,
        "request_kind": "observe",
        "operation_id": "operation-1",
        "after_sequence": 7,
        "limit": 128,
        **binding,
    }
    jsonschema.validate(observe, host)

    assert {
        "RuntimeMaintenanceCommandRequest",
        "RuntimeMaintenanceObserveRequest",
        "RuntimeMaintenanceUpdate",
    }.issubset(host["$defs"])


def test_runtime_http_status_and_typed_progress_are_formal() -> None:
    spec = _spec()
    status = spec["paths"]["/v2/runtime/status"]["get"]
    assert status["operationId"] == "getRuntimeStatus"
    assert status["responses"]["200"]["content"]["application/json"]["schema"] == {
        "$ref": "#/components/schemas/RuntimeStatusSnapshot"
    }
    schemas = spec["components"]["schemas"]
    assert schemas["JobSnapshot"]["properties"]["progress"] == {
        "$ref": "#/components/schemas/ProgressSnapshot"
    }


def test_runtime_http_maintenance_supports_commands_replay_and_streams() -> None:
    spec = _spec()
    paths = spec["paths"]
    assert paths["/v2/runtime/maintenance"]["post"]["operationId"] == (
        "startRuntimeMaintenance"
    )
    assert paths["/v2/runtime/maintenance/command"]["post"]["operationId"] == (
        "commandRuntimeMaintenance"
    )
    observe = paths["/v2/runtime/operations/{operation_id}/observe"]["get"]
    assert observe["operationId"] == "observeRuntimeMaintenance"
    assert observe["responses"]["200"]["content"]["application/json"]["schema"] == {
        "$ref": "#/components/schemas/RuntimeMaintenanceUpdate"
    }
    events = paths["/v2/runtime/operations/{operation_id}/events"]["get"]
    assert events["operationId"] == "streamRuntimeMaintenanceEvents"
    assert set(events["responses"]["200"]["content"]) == {
        "text/event-stream",
        "application/x-ndjson",
    }

    schemas = spec["components"]["schemas"]
    command = schemas["RuntimeMaintenanceCommandRequest"]
    assert {"command_id", "target_operation_id", "expected_sequence"}.issubset(
        command["properties"]
    )
    assert "new_operation_id" in command["properties"]
    update = schemas["RuntimeMaintenanceUpdate"]
    assert {
        "events",
        "oldest_sequence",
        "through_sequence",
        "more",
        "replay_expires_at",
    }.issubset(update["properties"])
    assert schemas["StageEvent"]["properties"]["progress"] == {
        "$ref": "#/components/schemas/ProgressSnapshot"
    }


def test_capabilities_are_versioned_and_generated_bindings_are_current() -> None:
    registry = json.loads((V2 / "capabilities.json").read_text(encoding="utf-8"))
    capabilities = registry["capabilities"]
    assert all(item.rsplit(".v", 1)[-1].isdigit() for item in capabilities)
    assert set(registry["definitions"]) == set(capabilities)
    assert all(item["description"].strip() for item in registry["definitions"].values())
    result = subprocess.run(
        [sys.executable, "scripts/generate_runtime_protocol.py", "--check"],
        cwd=ROOT,
        capture_output=True,
        text=True,
        check=False,
    )
    assert result.returncode == 0, result.stdout + result.stderr


def test_capability_registry_formalizes_lifecycle_and_negotiation_metadata() -> None:
    registry = json.loads((V2 / "capabilities.json").read_text(encoding="utf-8"))
    assert registry["version"] == 2
    expected = {
        "runtime.maintenance.v2",
        "runtime.component-repair.v1",
        "runtime.capability-metadata.v1",
        "runtime.events.sse.v1",
        "runtime.events.ndjson.v1",
    }
    assert expected.issubset(registry["capabilities"])
    for name, definition in registry["definitions"].items():
        assert definition["lifecycle"] in {"active", "deprecated"}, name
        assert definition["introduced_in"].startswith("2."), name
        assert {"deprecated_in", "sunset_at", "replacement"}.issubset(definition)

    schemas = _spec()["components"]["schemas"]
    descriptor = schemas["CapabilityDescriptor"]
    assert descriptor["properties"]["lifecycle"]["enum"] == [
        "active",
        "deprecated",
    ]
    assert schemas["Health"]["properties"]["capability_descriptors"] == {
        "type": "array",
        "items": {"$ref": "#/components/schemas/CapabilityDescriptor"},
    }

    host = json.loads((V2 / "runtime-host.schema.json").read_text(encoding="utf-8"))
    assert "CapabilityDescriptor" in host["$defs"]
    assert host["$defs"]["RuntimeHostSuccess"]["properties"]["negotiated_capabilities"][
        "items"
    ] == {"type": "string", "minLength": 1}


def test_runtime_errors_share_canonical_taxonomy_and_retry_after() -> None:
    registry = json.loads((V2 / "errors.json").read_text(encoding="utf-8"))
    expected = {
        "RUNTIME_OPERATION_NOT_FOUND",
        "RUNTIME_OPERATION_ID_CONFLICT",
        "RUNTIME_COMMAND_ID_CONFLICT",
        "RUNTIME_OPERATION_NOT_CANCELLABLE",
        "RUNTIME_OPERATION_NOT_RETRYABLE",
        "RUNTIME_CURSOR_EXPIRED",
        "RUNTIME_CAPABILITY_UNAVAILABLE",
        "RUNTIME_IDENTITY_MISMATCH",
        "RUNTIME_BUSY",
        "RUNTIME_INSTALL_FAILED",
        "RUNTIME_IO_ERROR",
    }
    assert expected.issubset({entry["code"] for entry in registry["codes"]})
    assert {"capability", "identity"}.issubset(registry["categories"])

    retry_after = _spec()["components"]["schemas"]["Error"]["properties"]["retry_after"]
    assert retry_after == {"type": ["integer", "null"], "minimum": 0}

    host = json.loads((V2 / "runtime-host.schema.json").read_text(encoding="utf-8"))
    host_error = host["$defs"]["RuntimeHostError"]["properties"]
    assert {
        "canonical_code",
        "category",
        "retry_after",
        "message_code",
        "detail",
    }.issubset(host_error)


def test_runtime_status_exposes_verified_identity_component_drift_and_real_eta() -> (
    None
):
    spec = _spec()
    schemas = spec["components"]["schemas"]
    source = schemas["RuntimeSourceIdentity"]
    assert source["required"] == [
        "backend_version",
        "backend_source_sha",
        "runtime_manifest_sha256",
        "protocol_version",
        "protocol_manifest_sha256",
    ]
    assert "source" in schemas["RuntimeStatusSnapshot"]["properties"]

    component = schemas["RuntimeComponentStatus"]["properties"]
    assert {
        "desired_state",
        "desired_version",
        "actual_state",
        "actual_version",
        "drift_reason",
        "repairable",
    }.issubset(component)
    maintenance = schemas["RuntimeMaintenanceStatus"]["properties"]
    assert {"requested_component_ids", "effective_component_ids"}.issubset(maintenance)

    progress_schema = {
        "$ref": "#/components/schemas/ProgressSnapshot",
        "components": spec["components"],
    }
    validator = jsonschema.Draft202012Validator(progress_schema)
    validator.validate(
        {
            "unit": "bytes",
            "current": 50,
            "total": 100,
            "estimated_remaining_seconds": 2,
        }
    )
    with pytest.raises(jsonschema.ValidationError):
        validator.validate(
            {
                "unit": "steps",
                "current": 1,
                "total": 4,
                "estimated_remaining_seconds": 3,
            }
        )
    with pytest.raises(jsonschema.ValidationError):
        validator.validate(
            {
                "unit": "items",
                "current": 1,
                "estimated_remaining_seconds": 3,
            }
        )

    host = json.loads((V2 / "runtime-host.schema.json").read_text(encoding="utf-8"))
    assert "RuntimeSourceIdentity" in host["$defs"]
    host_snapshot = host["$defs"]["RuntimeMaintenanceSnapshot"]["properties"]
    assert {
        "source",
        "requested_component_ids",
        "effective_component_ids",
    }.issubset(host_snapshot)


def test_all_local_refs_resolve_and_permissive_catch_all_schemas_are_gone() -> None:
    spec = _spec()
    assert "RuntimeObject" not in spec["components"]["schemas"]
    assert "PdfJson" not in spec["components"]["responses"]

    def walk(value: object) -> None:
        if isinstance(value, dict):
            ref = value.get("$ref")
            if isinstance(ref, str) and ref.startswith("#/"):
                target: object = spec
                for part in ref.removeprefix("#/").split("/"):
                    assert isinstance(target, dict)
                    target = target[part]
            for child in value.values():
                walk(child)
        elif isinstance(value, list):
            for child in value:
                walk(child)

    walk(spec)
    for _, _, operation in _operations(spec):
        success = operation["responses"]["200"]
        if "$ref" in success:
            success = spec["components"]["responses"][
                success["$ref"].rsplit("/", 1)[-1]
            ]
        assert any(media.get("schema") for media in success.get("content", {}).values())
        if "requestBody" in operation:
            request = operation["requestBody"]
            if "$ref" in request:
                request = spec["components"]["requestBodies"][
                    request["$ref"].rsplit("/", 1)[-1]
                ]
            assert any(media.get("schema") for media in request["content"].values())


def test_security_and_error_registry_are_explicit() -> None:
    spec = _spec()
    health = spec["paths"]["/v2/health"]["get"]
    assert health["security"] == []
    assert "401" not in health["responses"]
    assert "403" in health["responses"]
    for _, _, operation in _operations(spec):
        if operation is not health:
            assert {"401", "403"} <= operation["responses"].keys()

    registry = json.loads((V2 / "errors.json").read_text(encoding="utf-8"))
    error_code = spec["components"]["schemas"]["Error"]["properties"]["code"]
    assert error_code["type"] == "string"
    assert error_code["x-vibeocr-known-values"] == [
        entry["code"] for entry in registry["codes"]
    ]


def test_generated_python_wire_models_are_strict() -> None:
    from vibeocr.runtime_contracts.generated import (
        RuntimeHealthEnvelope,
        RuntimeReadyEnvelope,
    )

    ready = RuntimeReadyEnvelope.from_payload(
        {
            "ready": True,
            "pid": 1,
            "port": 2,
            "instance_id": "instance",
            "protocol_version": 2,
            "schema_version": 2,
            "ready_version": 1,
            "capabilities": ["ocr.recognition.v2"],
        }
    )
    assert ready.port == 2
    health = RuntimeHealthEnvelope.from_payload(
        {
            "schema_version": 2,
            "instance_id": "instance",
            "protocol_version": 2,
            "ready": True,
            "draining": False,
            "capabilities": ["pdf.edit.v2"],
        }
    )
    assert health.to_payload()["capabilities"] == ["pdf.edit.v2"]
    with pytest.raises(ValueError):
        RuntimeReadyEnvelope.from_payload(
            {
                "ready": 1,
                "pid": 1,
                "port": 2,
                "instance_id": "instance",
                "protocol_version": 2,
                "schema_version": 2,
                "ready_version": 1,
                "capabilities": [],
            }
        )


def test_generated_envelopes_accept_future_capabilities_and_fields() -> None:
    from vibeocr.runtime_contracts.generated import RuntimeReadyEnvelope

    ready = RuntimeReadyEnvelope.from_payload(
        {
            "ready": True,
            "pid": 1,
            "port": 2,
            "instance_id": "instance",
            "protocol_version": 2,
            "schema_version": 2,
            "ready_version": 1,
            "capabilities": ["ocr.recognition.v2", "future.feature.v3"],
            "future_optional_field": {"version": 3},
        }
    )

    assert ready.capabilities == ("ocr.recognition.v2", "future.feature.v3")


def test_codegen_covers_wire_dtos_errors_and_operation_signatures() -> None:
    from vibeocr.runtime_contracts.generated import (
        ERROR_REGISTRY,
        OPERATIONS,
        RuntimeErrorCode,
        wire_types,
    )

    spec = _spec()
    object_schemas = {
        name
        for name, schema in spec["components"]["schemas"].items()
        if isinstance(schema.get("properties"), dict)
    }
    assert all(hasattr(wire_types, name) for name in object_schemas)
    assert typing.get_type_hints(wire_types.Health)["protocol_version"] is not None
    assert len(OPERATIONS) == 41
    assert len({operation.operation_id for operation in OPERATIONS}) == 41
    registry = json.loads((V2 / "errors.json").read_text(encoding="utf-8"))
    assert {code.value for code in RuntimeErrorCode} == {
        entry["code"] for entry in registry["codes"]
    }
    assert set(ERROR_REGISTRY) == set(RuntimeErrorCode)

    csharp_protocol = (V2 / "generated/RuntimeProtocol.g.cs").read_text(
        encoding="utf-8"
    )
    csharp_wire = (V2 / "generated/RuntimeWireTypes.g.cs").read_text(encoding="utf-8")
    assert "RuntimeErrorCode" in csharp_protocol
    assert "RuntimeOperation" in csharp_protocol
    assert all(f"record {name}" in csharp_wire for name in object_schemas)


def test_generated_csharp_preserves_const_enum_and_nullable_field_types() -> None:
    csharp_wire = (V2 / "generated/RuntimeWireTypes.g.cs").read_text(encoding="utf-8")
    command_result = csharp_wire.split("public sealed record CommandResult", 1)[
        1
    ].split("\n}", 1)[0]
    error = csharp_wire.split("public sealed record Error", 1)[1].split("\n}", 1)[0]

    assert "public required int SchemaVersion { get; init; }" in command_result
    assert "public required string Kind { get; init; }" in command_result
    assert "public required string? CancelMode { get; init; }" in command_result
    assert "public required int SchemaVersion { get; init; }" in error
    assert "public required string? InstanceId { get; init; }" in error
    assert "public required string Code { get; init; }" in error
    assert "public required string Category { get; init; }" in error
    assert "public required string? JobId { get; init; }" in error


def test_runtime_multipart_ndjson_binary_and_error_goldens_validate() -> None:
    spec = _spec()
    golden = json.loads((V2 / "golden/runtime-api.json").read_text(encoding="utf-8"))
    bootstrap = json.loads((V2 / "bootstrap.schema.json").read_text(encoding="utf-8"))
    jsonschema.validate(golden["bootstrap_ready"], bootstrap)

    def validate_component(name: str, value: object) -> None:
        schema = {
            "$ref": f"#/components/schemas/{name}",
            "components": spec["components"],
        }
        jsonschema.Draft202012Validator(schema).validate(value)

    validate_component("Health", golden["health"])
    validate_component("SubmitRequest", golden["multipart_manifest"])
    validate_component("Error", golden["unauthorized_error"])
    for event in golden["ndjson_progress_lines"]:
        validate_component("ProgressEvent", event)
    png = base64.b64decode(golden["png"]["content_base64"], validate=True)
    assert golden["png"]["media_type"] == "image/png"
    assert png.startswith(b"\x89PNG\r\n\x1a\n")
