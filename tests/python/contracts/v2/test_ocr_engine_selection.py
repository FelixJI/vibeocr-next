"""Contract tests for the ocr.engine-selection.v1 protocol extension.

Wire decisions under test:

* Three stable engine ids shared by the formal OpenAPI, the handwritten
  Python/C# DTO layer and both generated bindings.
* ``PipelineSelection.engine`` is optional, only valid for the OCR pipeline
  (a server-side conformance case, not an OpenAPI conditional constraint),
  and unknown ids fail closed.
* The structured ``OcrEngineCatalog`` rides on the existing capability
  descriptor carrier in both the HTTP health envelope and the Runtime Host
  schema.
"""

from __future__ import annotations

import json
import typing
from pathlib import Path

import jsonschema
import pytest
from vibeocr.runtime_contracts import dtos
from vibeocr.runtime_contracts.generated import ALL_CAPABILITIES, wire_types
from vibeocr.runtime_contracts.generated.capabilities import OCR_ENGINE_SELECTION_V1
from vibeocr.runtime_contracts.generated.error_codes import (
    ERROR_REGISTRY,
    RuntimeErrorCode,
)

ROOT = Path(__file__).resolve().parents[4]
V2 = ROOT / "contracts/runtime/python/vibeocr/runtime_contracts"
STABLE_ENGINE_IDS = ("rapidocr", "windows", "paddleocr")
ENGINE_SELECTION_ERRORS = {
    "OCR_ENGINE_UNKNOWN": ("validation", 400, False),
    "OCR_ENGINE_UNAVAILABLE": ("capability", 426, False),
    "OCR_ENGINE_PREPARATION_REQUIRED": ("capability", 428, False),
    "OCR_ENGINE_NOT_VALID_FOR_PIPELINE": ("validation", 400, False),
    "OCR_ENGINE_LANGUAGE_UNAVAILABLE": ("capability", 426, False),
}


def _spec() -> dict:
    return json.loads((V2 / "openapi.yaml").read_text(encoding="utf-8"))


def _runtime_api_golden() -> dict:
    return json.loads((V2 / "golden/runtime-api.json").read_text(encoding="utf-8"))


def _submit_request_validator() -> jsonschema.Draft202012Validator:
    return jsonschema.Draft202012Validator(
        {
            "$ref": "#/components/schemas/SubmitRequest",
            "components": _spec()["components"],
        }
    )


def _manifest(pipeline_overrides: dict) -> dict:
    manifest = json.loads(json.dumps(_runtime_api_golden()["multipart_manifest"]))
    manifest["pipeline"].update(pipeline_overrides)
    return manifest


def test_engine_ids_are_single_sourced_across_python_layers() -> None:
    spec_enum = _spec()["components"]["schemas"]["OcrEngineId"]["enum"]
    assert tuple(spec_enum) == STABLE_ENGINE_IDS
    assert tuple(dtos.OcrEngine) == tuple(
        dtos.OcrEngine(value) for value in STABLE_ENGINE_IDS
    )
    assert set(typing.get_args(wire_types.OcrEngineId)) == set(STABLE_ENGINE_IDS)
    for engine in dtos.OcrEngine:
        assert engine.value in STABLE_ENGINE_IDS


def test_engine_selection_round_trips_and_omission_stays_wire_compatible() -> None:
    golden = json.loads((V2 / "golden/golden.json").read_text(encoding="utf-8"))
    fixture = golden["pipeline_selection_engine"]

    selection = dtos.PipelineSelection(
        pipeline_id=fixture["pipeline_id"],
        options=fixture["options"],
        engine=dtos.OcrEngine(fixture["engine"]),
    )
    assert selection.to_payload() == fixture

    legacy = dtos.PipelineSelection(pipeline_id="OCR")
    payload = legacy.to_payload()
    assert "engine" not in payload

    _submit_request_validator().validate(_manifest({"engine": "windows"}))
    _submit_request_validator().validate(_manifest({}))
    with pytest.raises(jsonschema.ValidationError):
        _submit_request_validator().validate(_manifest({"engine": "rapid-ocr"}))
    with pytest.raises(ValueError):
        dtos.OcrEngine("rapid-ocr")


def test_engine_is_only_valid_for_the_plain_text_ocr_pipeline() -> None:
    description = _spec()["components"]["schemas"]["PipelineSelection"]["description"]
    assert "OCR_ENGINE_NOT_VALID_FOR_PIPELINE" in description
    assert "silently ignoring" in description
    assert "OCR_ENGINE_UNKNOWN" in description

    # The conditional constraint is a documented server-side conformance case
    # because tightening a released request schema with allOf/if-then would be
    # a breaking change under scripts/check_openapi_quality.py.
    pipeline = _spec()["components"]["schemas"]["PipelineSelection"]
    assert "engine" in pipeline["properties"]
    assert "engine" not in pipeline["required"]
    assert "allOf" not in pipeline


def test_engine_catalog_covers_all_availability_states_on_health() -> None:
    golden = _runtime_api_golden()
    jsonschema.Draft202012Validator(
        {
            "$ref": "#/components/schemas/Health",
            "components": _spec()["components"],
        }
    ).validate(golden["health"])

    descriptors = {
        descriptor["name"]: descriptor
        for descriptor in golden["health"]["capability_descriptors"]
    }
    catalog = descriptors["ocr.engine-selection.v1"]["ocr_engine_catalog"]
    engines = {engine["id"]: engine for engine in catalog["engines"]}
    assert set(engines) == set(STABLE_ENGINE_IDS)
    assert {engine["availability"] for engine in engines.values()} == {
        "ready",
        "preparation_required",
        "unavailable",
    }
    # Legacy descriptors without a catalog keep their original wire shape.
    assert "ocr_engine_catalog" not in descriptors["ocr.recognition.v2"]

    schema = _spec()["components"]["schemas"]
    descriptor = schema["OcrEngineDescriptor"]
    assert set(descriptor["required"]) == {
        "id",
        "availability",
        "included_in_base",
        "reason_code",
        "required_component",
    }
    assert schema["CapabilityDescriptor"]["properties"]["ocr_engine_catalog"] == {
        "$ref": "#/components/schemas/OcrEngineCatalog"
    }
    assert "ocr_engine_catalog" not in schema["CapabilityDescriptor"]["required"]


def test_runtime_host_schema_carries_the_same_catalog_seam() -> None:
    host = json.loads((V2 / "runtime-host.schema.json").read_text(encoding="utf-8"))
    assert host["$defs"]["OcrEngineId"]["enum"] == list(STABLE_ENGINE_IDS)
    assert host["$defs"]["OcrEngineAvailability"]["enum"] == [
        "ready",
        "preparation_required",
        "unavailable",
    ]
    assert host["$defs"]["CapabilityDescriptor"]["properties"][
        "ocr_engine_catalog"
    ] == {"$ref": "#/$defs/OcrEngineCatalog"}
    from vibeocr.runtime_contracts.generated import runtime_host_types

    assert hasattr(runtime_host_types, "OcrEngineCatalog")
    assert hasattr(runtime_host_types, "OcrEngineDescriptor")


def test_engine_selection_capability_is_registered_everywhere() -> None:
    registry = json.loads((V2 / "capabilities.json").read_text(encoding="utf-8"))
    assert "ocr.engine-selection.v1" in registry["capabilities"]
    definition = registry["definitions"]["ocr.engine-selection.v1"]
    assert definition["lifecycle"] == "active"
    assert definition["introduced_in"].startswith("2.")

    assert OCR_ENGINE_SELECTION_V1 == "ocr.engine-selection.v1"
    assert OCR_ENGINE_SELECTION_V1 in ALL_CAPABILITIES

    spec = _spec()
    health_values = spec["components"]["schemas"]["Health"]["properties"][
        "capabilities"
    ]["items"]["x-vibeocr-known-values"]
    bootstrap = json.loads((V2 / "bootstrap.schema.json").read_text(encoding="utf-8"))
    bootstrap_values = bootstrap["properties"]["capabilities"]["items"][
        "x-vibeocr-known-values"
    ]
    for known_values in (health_values, bootstrap_values):
        assert "ocr.engine-selection.v1" in known_values


def test_engine_selection_error_codes_are_registered_and_fail_closed() -> None:
    registry = json.loads((V2 / "errors.json").read_text(encoding="utf-8"))
    entries = {entry["code"]: entry for entry in registry["codes"]}
    for code, (category, http_status, retryable) in ENGINE_SELECTION_ERRORS.items():
        entry = entries[code]
        assert entry["category"] == category, code
        assert entry["http_status"] == http_status, code
        assert entry["retryable"] is retryable, code
        assert entry["message"].strip(), code
        # Engine errors are HTTP-level; none maps onto a Runtime Host IPC code.
        assert "runtime_host_code" not in entry, code

        definition = ERROR_REGISTRY[RuntimeErrorCode(code)]
        assert definition.category == category
        assert definition.http_status == http_status
        assert definition.retryable is retryable

    known = _spec()["components"]["schemas"]["Error"]["properties"]["code"][
        "x-vibeocr-known-values"
    ]
    assert set(ENGINE_SELECTION_ERRORS).issubset(known)
    # Unavailable/language errors advertise the selectable engines through the
    # open detail object instead of a second typed field on the shared Error.
    assert "selectable engine ids" in entries["OCR_ENGINE_UNAVAILABLE"]["message"]


def test_submit_job_declares_engine_capability_negotiation_responses() -> None:
    responses = _spec()["paths"]["/v2/jobs"]["post"]["responses"]
    assert {"400", "403", "426", "428"}.issubset(responses)


def test_generated_bindings_expose_strongly_typed_engine_selection() -> None:
    hints = typing.get_type_hints(wire_types.PipelineSelection)
    assert hints["engine"] == wire_types.OcrEngineId
    assert "engine" in wire_types.PipelineSelection.__optional_keys__
    descriptor_hints = typing.get_type_hints(wire_types.OcrEngineDescriptor)
    assert descriptor_hints["id"] == wire_types.OcrEngineId
    assert descriptor_hints["reason_code"] == str | None

    csharp_wire = (V2 / "generated/RuntimeWireTypes.g.cs").read_text(encoding="utf-8")
    pipeline_record = csharp_wire.split("public sealed record PipelineSelection", 1)[
        1
    ].split("\n}", 1)[0]
    assert '[JsonPropertyName("engine")]' in pipeline_record
    assert "public OcrEngineId? Engine { get; init; }" in pipeline_record

    descriptor_record = csharp_wire.split(
        "public sealed record OcrEngineDescriptor", 1
    )[1].split("\n}", 1)[0]
    assert "public required OcrEngineId Id { get; init; }" in descriptor_record
    assert (
        "public required OcrEngineAvailability Availability { get; init; }"
        in descriptor_record
    )
    assert "public required string? ReasonCode { get; init; }" in descriptor_record
    assert (
        "public required string? RequiredComponent { get; init; }" in descriptor_record
    )

    enum_block = csharp_wire.split("public enum OcrEngineId", 1)[1].split("}", 1)[0]
    for value in STABLE_ENGINE_IDS:
        assert f'"{value}"' in enum_block
