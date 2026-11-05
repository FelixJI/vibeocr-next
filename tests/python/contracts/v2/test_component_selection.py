"""Read compatibility for historical HTTP component-selection descriptors and errors."""

from __future__ import annotations

import json
import typing
from pathlib import Path

import jsonschema
from vibeocr.runtime_contracts.generated import ALL_CAPABILITIES, wire_types
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


def test_variant_shapes_are_single_sourced_across_layers() -> None:
    spec = _spec()["components"]["schemas"]
    descriptor = spec["ComponentVariantDescriptor"]
    assert set(descriptor["required"]) == {
        "feature_id",
        "accelerator",
        "component_id",
    }
    assert descriptor["properties"]["accelerator"]["enum"] == list(ACCELERATORS)

    hints = typing.get_type_hints(wire_types.ComponentVariantDescriptor)
    assert hints["feature_id"] is str
    assert set(typing.get_args(hints["accelerator"])) == set(ACCELERATORS)
    assert hints["component_id"] is str


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


def test_http_catalog_descriptors_use_the_product_wire_shape() -> None:
    schema = _spec()["components"]["schemas"]["ComponentVariantCatalog"]
    assert schema["properties"]["variants"]["items"] == {
        "$ref": "#/components/schemas/ComponentVariantDescriptor"
    }


def test_component_selection_is_readable_but_no_longer_advertised() -> None:
    registry = json.loads((V2 / "capabilities.json").read_text(encoding="utf-8"))
    assert "runtime.component-selection.v1" not in registry["capabilities"]
    assert "runtime.component-selection.v1" not in ALL_CAPABILITIES

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
