"""Contract tests for the runtime.component-selection.v1 protocol extension.

Wire decisions under test:

* The variant catalog groups optional feature dependencies by feature and
  accelerator; feature ids never appear in requests and plain-text OCR
  features reuse the OcrEngineId wire values.
* The manual install scope is a capability-protected ``install_component_ids``
  field, distinct from the repair-scope ``component_ids``; unknown ids fail
  closed with ``RUNTIME_COMPONENT_UNKNOWN`` and the dependency closure is
  reported honestly through requested/effective component ids.
* Omitting the field selects the server default set (clients must omit it
  entirely when the runtime does not declare the capability).
"""

from __future__ import annotations

import json
import typing
from pathlib import Path

import jsonschema
import pytest
from vibeocr.runtime_contracts import dtos
from vibeocr.runtime_contracts.generated import ALL_CAPABILITIES, wire_types
from vibeocr.runtime_contracts.generated.capabilities import (
    RUNTIME_COMPONENT_SELECTION_V1,
)
from vibeocr.runtime_contracts.generated.error_codes import (
    ERROR_REGISTRY,
    RuntimeErrorCode,
)

ROOT = Path(__file__).resolve().parents[4]
V2 = ROOT / "contracts/runtime/python/vibeocr/runtime_contracts"
ACCELERATORS = ("cpu", "nvidia_cuda")
COMPONENT_SELECTION_ERRORS = {
    "RUNTIME_COMPONENT_UNKNOWN": ("validation", 400, False),
}


def _spec() -> dict:
    return json.loads((V2 / "openapi.yaml").read_text(encoding="utf-8"))


def _host_schema() -> dict:
    return json.loads((V2 / "runtime-host.schema.json").read_text(encoding="utf-8"))


def _runtime_api_golden() -> dict:
    return json.loads((V2 / "golden/runtime-api.json").read_text(encoding="utf-8"))


def _golden() -> dict:
    return json.loads((V2 / "golden/golden.json").read_text(encoding="utf-8"))


def _component_schema_validator(name: str) -> jsonschema.Draft202012Validator:
    return jsonschema.Draft202012Validator(
        {
            "$ref": f"#/components/schemas/{name}",
            "components": _spec()["components"],
        }
    )


def _host_request_validator() -> jsonschema.Draft202012Validator:
    return jsonschema.Draft202012Validator(
        {
            "$ref": "#/$defs/RuntimeHostRequest",
            "$defs": _host_schema()["$defs"],
        }
    )


def _host_request(selection: list | None, operation: str = "ensure") -> dict:
    request = {
        "protocol_version": 2,
        "operation": operation,
        "product_root": "C:/VibeOCR",
        "component_lock": "C:/VibeOCR/component-lock.json",
        "runtime_manifest": "C:/VibeOCR/backend/runtime-manifest.json",
    }
    if selection is not None:
        request["install_component_ids"] = selection
    return request


def test_variant_shapes_are_single_sourced_across_layers() -> None:
    spec = _spec()["components"]["schemas"]
    descriptor = spec["ComponentVariantDescriptor"]
    assert set(descriptor["required"]) == {
        "feature_id",
        "accelerator",
        "component_id",
    }
    assert descriptor["properties"]["accelerator"]["enum"] == list(ACCELERATORS)

    host = _host_schema()["$defs"]
    assert set(host["ComponentVariantDescriptor"]["required"]) == {
        "feature_id",
        "accelerator",
        "component_id",
    }
    assert host["ComponentVariantDescriptor"]["properties"]["accelerator"] == {
        "$ref": "#/$defs/Accelerator"
    }

    hints = typing.get_type_hints(wire_types.ComponentVariantDescriptor)
    assert hints["feature_id"] is str
    assert set(typing.get_args(hints["accelerator"])) == set(ACCELERATORS)
    assert hints["component_id"] is str


def test_install_selection_round_trips_and_omission_stays_wire_compatible() -> None:
    fixture = _golden()["install_selection"]

    request = dtos.RuntimeMaintenanceRequest(
        operation=dtos.RuntimeMaintenanceOperation.ENSURE,
        install_component_ids=tuple(fixture),
    )
    payload = request.to_payload()
    assert payload["install_component_ids"] == fixture
    assert payload["operation"] == "ensure"
    _component_schema_validator("RuntimeMaintenanceRequest").validate(payload)

    legacy = dtos.RuntimeMaintenanceRequest(
        operation=dtos.RuntimeMaintenanceOperation.ENSURE
    ).to_payload()
    assert "install_component_ids" not in legacy
    _component_schema_validator("RuntimeMaintenanceRequest").validate(legacy)

    explicit_base_only = dtos.RuntimeMaintenanceRequest(
        operation=dtos.RuntimeMaintenanceOperation.ENSURE,
        install_component_ids=(),
    ).to_payload()
    assert explicit_base_only["install_component_ids"] == []
    _component_schema_validator("RuntimeMaintenanceRequest").validate(
        explicit_base_only
    )

    with pytest.raises(ValueError, match="require ensure"):
        dtos.RuntimeMaintenanceRequest(
            operation=dtos.RuntimeMaintenanceOperation.REPAIR,
            install_component_ids=("paddleocr-cpu",),
        )
    with pytest.raises(jsonschema.ValidationError):
        _component_schema_validator("RuntimeMaintenanceRequest").validate(
            {
                "operation": "repair",
                "install_component_ids": ["paddleocr-cpu"],
            }
        )

    # The wire shape stays strict: duplicates and non-string ids are rejected
    # by the schema; unknown ids are a server-side fail-closed case.
    with pytest.raises(jsonschema.ValidationError):
        _component_schema_validator("RuntimeMaintenanceRequest").validate(
            {**legacy, "install_component_ids": ["paddleocr-cpu", "paddleocr-cpu"]}
        )
    with pytest.raises(jsonschema.ValidationError):
        _component_schema_validator("RuntimeMaintenanceRequest").validate(
            {**legacy, "install_component_ids": [1]}
        )

    # The install scope is a distinct field from the repair scope.
    schema = _spec()["components"]["schemas"]["RuntimeMaintenanceRequest"]
    assert "install_component_ids" in schema["properties"]
    assert "install_component_ids" not in schema["required"]
    description = schema["description"]
    assert "RUNTIME_COMPONENT_UNKNOWN" in description
    assert "MUST omit" in description


def test_retry_command_carries_optional_install_selection() -> None:
    command = dtos.RuntimeMaintenanceCommand(
        command_id="command-1",
        command=dtos.RuntimeMaintenanceCommandKind.RETRY,
        target_operation_id="op-1",
        new_operation_id="op-2",
        install_component_ids=("mineru-cpu",),
    )
    payload = command.to_payload()
    assert payload["install_component_ids"] == ["mineru-cpu"]
    _component_schema_validator("RuntimeMaintenanceCommandRequest").validate(payload)

    legacy = dtos.RuntimeMaintenanceCommand(
        command_id="command-1",
        command=dtos.RuntimeMaintenanceCommandKind.RETRY,
        target_operation_id="op-1",
        new_operation_id="op-2",
    ).to_payload()
    assert "install_component_ids" not in legacy
    _component_schema_validator("RuntimeMaintenanceCommandRequest").validate(legacy)

    with pytest.raises(ValueError, match="require retry"):
        dtos.RuntimeMaintenanceCommand(
            command_id="command-2",
            command=dtos.RuntimeMaintenanceCommandKind.CANCEL,
            target_operation_id="op-1",
            install_component_ids=(),
        )
    with pytest.raises(jsonschema.ValidationError):
        _component_schema_validator("RuntimeMaintenanceCommandRequest").validate(
            {
                "command_id": "command-2",
                "command": "cancel",
                "target_operation_id": "op-1",
                "install_component_ids": [],
            }
        )


def test_host_requests_carry_optional_install_selection() -> None:
    host = _host_schema()
    for envelope in ("RuntimeHostRequest", "RuntimeMaintenanceCommandRequest"):
        properties = host["$defs"][envelope]["properties"]
        assert properties["install_component_ids"] == {
            "type": "array",
            "uniqueItems": True,
            "items": {"type": "string", "minLength": 1},
        }
        assert "install_component_ids" not in host["$defs"][envelope]["required"]
    # Observe requests are read-only and never select an install scope.
    assert (
        "install_component_ids"
        not in host["$defs"]["RuntimeMaintenanceObserveRequest"]["properties"]
    )

    _host_request_validator().validate(_host_request(["paddleocr-cpu", "mineru-cpu"]))
    _host_request_validator().validate(_host_request(None))
    with pytest.raises(jsonschema.ValidationError):
        _host_request_validator().validate(
            _host_request(["paddleocr-cpu", "paddleocr-cpu"])
        )
    with pytest.raises(jsonschema.ValidationError):
        _host_request_validator().validate(
            _host_request(["paddleocr-cpu"], operation="inspect")
        )


def test_component_variant_catalog_rides_on_health_descriptor() -> None:
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
    catalog = descriptors["runtime.component-selection.v1"]["component_variant_catalog"]
    variants = catalog["variants"]
    # (feature_id, accelerator) pairs are unique across the catalog.
    pairs = {(variant["feature_id"], variant["accelerator"]) for variant in variants}
    assert len(pairs) == len(variants)
    # The golden catalog covers both accelerators for both offline engines.
    assert {variant["accelerator"] for variant in variants} == set(ACCELERATORS)
    assert {variant["feature_id"] for variant in variants} == {"paddleocr", "mineru"}
    # Plain-text OCR engines reuse the OcrEngineId wire values.
    engine_enum = _spec()["components"]["schemas"]["OcrEngineId"]["enum"]
    assert "paddleocr" in engine_enum
    # Legacy descriptors without a catalog keep their original wire shape.
    for name in (
        "ocr.recognition.v2",
        "ocr.engine-selection.v1",
        "runtime.download-sources.v1",
    ):
        assert "component_variant_catalog" not in descriptors[name]

    schema = _spec()["components"]["schemas"]
    assert schema["CapabilityDescriptor"]["properties"][
        "component_variant_catalog"
    ] == {"$ref": "#/components/schemas/ComponentVariantCatalog"}
    assert "component_variant_catalog" not in schema["CapabilityDescriptor"]["required"]
    catalog_description = schema["ComponentVariantCatalog"]["description"]
    assert "same feature_id and accelerator" in catalog_description
    assert "base runtime components" in catalog_description


def test_runtime_host_schema_carries_the_same_catalog_seam() -> None:
    host = _host_schema()
    assert host["$defs"]["CapabilityDescriptor"]["properties"][
        "component_variant_catalog"
    ] == {"$ref": "#/$defs/ComponentVariantCatalog"}
    from vibeocr.runtime_contracts.generated import runtime_host_types

    assert hasattr(runtime_host_types, "ComponentVariantCatalog")
    assert hasattr(runtime_host_types, "ComponentVariantDescriptor")
    host_hints = typing.get_type_hints(runtime_host_types.ComponentVariantDescriptor)
    assert host_hints["accelerator"] == runtime_host_types.Accelerator


def test_component_selection_capability_is_registered_everywhere() -> None:
    registry = json.loads((V2 / "capabilities.json").read_text(encoding="utf-8"))
    assert "runtime.component-selection.v1" in registry["capabilities"]
    definition = registry["definitions"]["runtime.component-selection.v1"]
    assert definition["lifecycle"] == "active"
    assert definition["introduced_in"].startswith("2.")

    assert RUNTIME_COMPONENT_SELECTION_V1 == "runtime.component-selection.v1"
    assert RUNTIME_COMPONENT_SELECTION_V1 in ALL_CAPABILITIES

    health_values = _spec()["components"]["schemas"]["Health"]["properties"][
        "capabilities"
    ]["items"]["x-vibeocr-known-values"]
    bootstrap = json.loads((V2 / "bootstrap.schema.json").read_text(encoding="utf-8"))
    bootstrap_values = bootstrap["properties"]["capabilities"]["items"][
        "x-vibeocr-known-values"
    ]
    for known_values in (health_values, bootstrap_values):
        assert "runtime.component-selection.v1" in known_values


def test_component_selection_error_code_is_registered_and_fail_closed() -> None:
    registry = json.loads((V2 / "errors.json").read_text(encoding="utf-8"))
    entries = {entry["code"]: entry for entry in registry["codes"]}
    for code, (category, http_status, retryable) in COMPONENT_SELECTION_ERRORS.items():
        entry = entries[code]
        assert entry["category"] == category, code
        assert entry["http_status"] == http_status, code
        assert entry["retryable"] is retryable, code
        assert entry["message"].strip(), code
        # Component selection errors are HTTP-level; none maps onto a Runtime
        # Host IPC code.
        assert "runtime_host_code" not in entry, code

        definition = ERROR_REGISTRY[RuntimeErrorCode(code)]
        assert definition.category == category
        assert definition.http_status == http_status
        assert definition.retryable is retryable

    known = _spec()["components"]["schemas"]["Error"]["properties"]["code"][
        "x-vibeocr-known-values"
    ]
    assert set(COMPONENT_SELECTION_ERRORS).issubset(known)
    assert "fail" in entries["RUNTIME_COMPONENT_UNKNOWN"]["message"]


def test_generated_bindings_expose_strongly_typed_variant_catalog() -> None:
    csharp_wire = (V2 / "generated/RuntimeWireTypes.g.cs").read_text(encoding="utf-8")
    descriptor_record = csharp_wire.split(
        "public sealed record ComponentVariantDescriptor", 1
    )[1].split("\n}", 1)[0]
    assert "public required string FeatureId { get; init; }" in descriptor_record
    assert "public required string Accelerator { get; init; }" in descriptor_record
    assert "public required string ComponentId { get; init; }" in descriptor_record

    request_record = csharp_wire.split(
        "public sealed record RuntimeMaintenanceRequest", 1
    )[1].split("\n}", 1)[0]
    assert '[JsonPropertyName("install_component_ids")]' in request_record
    assert "public IReadOnlyList<string>? InstallComponentIds { get; init; }" in (
        request_record
    )

    host_wire = (V2 / "generated/RuntimeHostWireTypes.g.cs").read_text(encoding="utf-8")
    host_request = host_wire.split("public sealed record RuntimeHostRequest", 1)[
        1
    ].split("\n}", 1)[0]
    assert "public IReadOnlyList<string>? InstallComponentIds { get; init; }" in (
        host_request
    )
    host_descriptor = host_wire.split(
        "public sealed record ComponentVariantDescriptor", 1
    )[1].split("\n}", 1)[0]
    assert "public required Accelerator Accelerator { get; init; }" in host_descriptor
