"""Contract tests for the runtime.download-sources.v1 protocol extension.

Wire decisions under test:

* Selections reference stable source ids declared by the Backend catalog;
  custom source URLs are out of scope and unknown ids fail closed with
  ``DOWNLOAD_SOURCE_UNKNOWN``.
* The structured ``DownloadSourceCatalog`` rides on the existing capability
  descriptor carrier in both the HTTP health envelope and the Runtime Host
  schema.
* The selection is exchanged as ``download_source_ids`` on the HTTP settings
  snapshot (persistent preference) and on the stateless Runtime Host
  request/retry envelopes; observe requests stay read-only.
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
    RUNTIME_DOWNLOAD_SOURCES_V1,
)
from vibeocr.runtime_contracts.generated.error_codes import (
    ERROR_REGISTRY,
    RuntimeErrorCode,
)

ROOT = Path(__file__).resolve().parents[4]
V2 = ROOT / "contracts/runtime/python/vibeocr/runtime_contracts"
DOWNLOAD_SOURCE_KINDS = ("package_index", "model_registry")
DOWNLOAD_SOURCE_ERRORS = {
    "DOWNLOAD_SOURCE_UNKNOWN": ("validation", 400, False),
}


def _spec() -> dict:
    return json.loads((V2 / "openapi.yaml").read_text(encoding="utf-8"))


def _host_schema() -> dict:
    return json.loads((V2 / "runtime-host.schema.json").read_text(encoding="utf-8"))


def _runtime_api_golden() -> dict:
    return json.loads((V2 / "golden/runtime-api.json").read_text(encoding="utf-8"))


def _golden() -> dict:
    return json.loads((V2 / "golden/golden.json").read_text(encoding="utf-8"))


def _settings_snapshot_validator() -> jsonschema.Draft202012Validator:
    return jsonschema.Draft202012Validator(
        {
            "$ref": "#/components/schemas/SettingsSnapshot",
            "components": _spec()["components"],
        }
    )


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


def _host_request(selection: list | None) -> dict:
    request = {
        "protocol_version": 2,
        "operation": "ensure",
        "product_root": "C:/VibeOCR",
        "component_lock": "C:/VibeOCR/component-lock.json",
        "runtime_manifest": "C:/VibeOCR/backend/runtime-manifest.json",
    }
    if selection is not None:
        request["download_source_ids"] = selection
    return request


def test_source_kinds_are_single_sourced_across_layers() -> None:
    spec_kind = _spec()["components"]["schemas"]["DownloadSourceKind"]
    assert tuple(spec_kind["x-vibeocr-known-values"]) == DOWNLOAD_SOURCE_KINDS
    assert "enum" not in spec_kind
    host_kind = _host_schema()["$defs"]["DownloadSourceKind"]
    assert tuple(host_kind["x-vibeocr-known-values"]) == DOWNLOAD_SOURCE_KINDS
    assert "enum" not in host_kind
    assert wire_types.DownloadSourceKind is str
    from vibeocr.runtime_contracts.generated import runtime_host_types

    assert runtime_host_types.DownloadSourceKind is str


def test_legacy_model_registry_catalog_remains_wire_compatible() -> None:
    fixture = _golden()["legacy_download_source_catalog"]
    _component_schema_validator("DownloadSourceCatalog").validate(fixture)
    assert fixture == {
        "sources": [
            {
                "kind": "model_registry",
                "id": "legacy-models",
                "endpoint": "https://models.example.invalid",
            }
        ]
    }


def test_current_catalog_exposes_native_model_source_preferences() -> None:
    fixture = _golden()["download_source_catalog"]
    _component_schema_validator("DownloadSourceCatalog").validate(fixture)
    assert fixture == {
        "sources": [
            {
                "kind": "package_index",
                "id": "pypi-official",
                "endpoint": "https://pypi.org/simple",
            },
            {
                "kind": "package_index",
                "id": "pypi-tuna",
                "endpoint": "https://pypi.tuna.tsinghua.edu.cn/simple",
            },
            {
                "kind": "model_registry",
                "id": "huggingface",
                "endpoint": "https://huggingface.co",
            },
            {
                "kind": "model_registry",
                "id": "modelscope",
                "endpoint": "https://www.modelscope.cn",
            },
        ]
    }


def test_settings_selection_round_trips_and_omission_stays_wire_compatible() -> None:
    fixture = _golden()["download_source_selection"]

    snapshot = dtos.SettingsSnapshot(
        download_source_ids=tuple(fixture),
    )
    payload = snapshot.to_payload()
    assert payload["download_source_ids"] == fixture
    _settings_snapshot_validator().validate(payload)

    legacy = dtos.SettingsSnapshot().to_payload()
    assert "download_source_ids" not in legacy
    _settings_snapshot_validator().validate(legacy)

    # The wire shape stays strict: duplicates, empty and non-string ids are
    # rejected by the schema; unknown ids are a server-side fail-closed case.
    with pytest.raises(jsonschema.ValidationError):
        _settings_snapshot_validator().validate(
            {**legacy, "download_source_ids": ["pypi-tuna", "pypi-tuna"]}
        )
    with pytest.raises(jsonschema.ValidationError):
        _settings_snapshot_validator().validate({**legacy, "download_source_ids": [""]})
    with pytest.raises(jsonschema.ValidationError):
        _settings_snapshot_validator().validate({**legacy, "download_source_ids": [1]})
    with pytest.raises(jsonschema.ValidationError):
        _settings_snapshot_validator().validate({**legacy, "download_source_ids": []})

    schema = _spec()["components"]["schemas"]["SettingsSnapshot"]
    assert "download_source_ids" in schema["properties"]
    assert "download_source_ids" not in schema["required"]
    description = schema["description"]
    assert "DOWNLOAD_SOURCE_UNKNOWN" in description
    assert "MUST omit" in description
    assert "kinds not present" in description
    assert "Backend-declared default for that kind" in description

    status = _spec()["components"]["schemas"]["RuntimeMaintenanceStatus"]
    assert {
        "requested_download_source_ids",
        "effective_download_source_ids",
    }.issubset(status["properties"])


def test_empty_settings_selection_normalizes_to_golden_omission() -> None:
    payload = dtos.SettingsSnapshot(download_source_ids=()).to_payload()

    assert payload == _golden()["settings_snapshot_empty_selection"]
    assert "download_source_ids" not in payload
    _settings_snapshot_validator().validate(payload)


def test_http_maintenance_snapshots_source_selection_and_rejects_wrong_operations() -> (
    None
):
    request = dtos.RuntimeMaintenanceRequest(
        operation=dtos.RuntimeMaintenanceOperation.ENSURE,
        download_source_ids=("pypi-tuna", "hf-mirror"),
    )
    payload = request.to_payload()
    assert payload["download_source_ids"] == ["pypi-tuna", "hf-mirror"]
    _component_schema_validator("RuntimeMaintenanceRequest").validate(payload)

    command = dtos.RuntimeMaintenanceCommand(
        command_id="command-1",
        command=dtos.RuntimeMaintenanceCommandKind.RETRY,
        target_operation_id="op-1",
        new_operation_id="op-2",
        download_source_ids=("pypi-official", "hf-official"),
    )
    _component_schema_validator("RuntimeMaintenanceCommandRequest").validate(
        command.to_payload()
    )

    with pytest.raises(ValueError, match="require ensure"):
        dtos.RuntimeMaintenanceRequest(
            operation=dtos.RuntimeMaintenanceOperation.INSPECT,
            download_source_ids=("pypi-tuna",),
        )
    with pytest.raises(ValueError, match="require retry"):
        dtos.RuntimeMaintenanceCommand(
            command_id="command-2",
            command=dtos.RuntimeMaintenanceCommandKind.CANCEL,
            target_operation_id="op-1",
            download_source_ids=("pypi-tuna",),
        )
    with pytest.raises(ValueError, match="must be non-empty"):
        dtos.RuntimeMaintenanceRequest(
            operation=dtos.RuntimeMaintenanceOperation.ENSURE,
            download_source_ids=(),
        )
    with pytest.raises(ValueError, match="must be non-empty"):
        dtos.RuntimeMaintenanceCommand(
            command_id="command-3",
            command=dtos.RuntimeMaintenanceCommandKind.RETRY,
            target_operation_id="op-1",
            new_operation_id="op-3",
            download_source_ids=(),
        )


def test_runtime_host_requests_carry_optional_selection() -> None:
    host = _host_schema()
    for envelope in ("RuntimeHostRequest", "RuntimeMaintenanceCommandRequest"):
        properties = host["$defs"][envelope]["properties"]
        selection = properties["download_source_ids"]
        assert {
            key: selection[key] for key in ("type", "minItems", "uniqueItems", "items")
        } == {
            "type": "array",
            "minItems": 1,
            "uniqueItems": True,
            "items": {"type": "string", "minLength": 1},
        }
        assert "kinds not present" in selection["description"]
        assert "Backend-declared default for that kind" in selection["description"]
        assert "download_source_ids" not in host["$defs"][envelope]["required"]
    # Observe requests are read-only and never select sources.
    assert (
        "download_source_ids"
        not in host["$defs"]["RuntimeMaintenanceObserveRequest"]["properties"]
    )

    _host_request_validator().validate(_host_request(["pypi-tuna", "hf-mirror"]))
    _host_request_validator().validate(_host_request(None))
    with pytest.raises(jsonschema.ValidationError):
        _host_request_validator().validate(_host_request(["pypi-tuna", "pypi-tuna"]))
    with pytest.raises(jsonschema.ValidationError):
        _host_request_validator().validate(_host_request([]))


def test_download_source_catalog_rides_on_health_descriptor() -> None:
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
    catalog = descriptors["runtime.download-sources.v1"]["download_source_catalog"]
    sources = catalog["sources"]
    # Catalog ids are unique across kinds (server conformance case).
    ids = [source["id"] for source in sources]
    assert len(ids) == len(set(ids))
    assert {source["kind"] for source in sources} == {
        "package_index",
        "model_registry",
    }
    assert {
        source["id"] for source in sources if source["kind"] == "model_registry"
    } == {"huggingface", "modelscope"}
    for source in sources:
        assert source["endpoint"].startswith("https://")
    # Legacy descriptors without a catalog keep their original wire shape.
    assert "download_source_catalog" not in descriptors["ocr.recognition.v2"]
    assert "download_source_catalog" not in descriptors["ocr.engine-selection.v1"]

    schema = _spec()["components"]["schemas"]
    descriptor = schema["DownloadSourceDescriptor"]
    assert set(descriptor["required"]) == {"kind", "id", "endpoint"}
    assert schema["CapabilityDescriptor"]["properties"]["download_source_catalog"] == {
        "$ref": "#/components/schemas/DownloadSourceCatalog"
    }
    assert "download_source_catalog" not in schema["CapabilityDescriptor"]["required"]
    # The catalog never carries display text or product defaults.
    catalog_description = schema["DownloadSourceCatalog"]["description"]
    assert "unique" in catalog_description or "MUST NOT" in catalog_description
    assert "display text" in schema["DownloadSourceDescriptor"]["description"]
    endpoint_description = descriptor["properties"]["endpoint"]["description"]
    assert "must not render or parse" in endpoint_description
    kind_description = schema["DownloadSourceKind"]["description"]
    assert "model_registry selects an upstream model-source preference" in (
        kind_description
    )
    assert "does not make VibeOCR responsible for model files" in kind_description


def test_runtime_host_schema_carries_the_same_catalog_seam() -> None:
    host = _host_schema()
    assert host["$defs"]["CapabilityDescriptor"]["properties"][
        "download_source_catalog"
    ] == {"$ref": "#/$defs/DownloadSourceCatalog"}
    descriptor = host["$defs"]["DownloadSourceDescriptor"]
    assert set(descriptor["required"]) == {"kind", "id", "endpoint"}
    kind_description = host["$defs"]["DownloadSourceKind"]["description"]
    assert "model_registry selects an upstream model-source preference" in (
        kind_description
    )
    assert "does not make VibeOCR responsible for model files" in kind_description
    from vibeocr.runtime_contracts.generated import runtime_host_types

    assert hasattr(runtime_host_types, "DownloadSourceCatalog")
    assert hasattr(runtime_host_types, "DownloadSourceDescriptor")


def test_download_source_capability_is_registered_everywhere() -> None:
    registry = json.loads((V2 / "capabilities.json").read_text(encoding="utf-8"))
    assert "runtime.download-sources.v1" in registry["capabilities"]
    definition = registry["definitions"]["runtime.download-sources.v1"]
    assert definition["lifecycle"] == "active"
    assert definition["introduced_in"].startswith("2.")

    assert RUNTIME_DOWNLOAD_SOURCES_V1 == "runtime.download-sources.v1"
    assert RUNTIME_DOWNLOAD_SOURCES_V1 in ALL_CAPABILITIES

    spec = _spec()
    health_values = spec["components"]["schemas"]["Health"]["properties"][
        "capabilities"
    ]["items"]["x-vibeocr-known-values"]
    bootstrap = json.loads((V2 / "bootstrap.schema.json").read_text(encoding="utf-8"))
    bootstrap_values = bootstrap["properties"]["capabilities"]["items"][
        "x-vibeocr-known-values"
    ]
    for known_values in (health_values, bootstrap_values):
        assert "runtime.download-sources.v1" in known_values


def test_download_source_error_code_is_registered_and_fail_closed() -> None:
    registry = json.loads((V2 / "errors.json").read_text(encoding="utf-8"))
    entries = {entry["code"]: entry for entry in registry["codes"]}
    for code, (category, http_status, retryable) in DOWNLOAD_SOURCE_ERRORS.items():
        entry = entries[code]
        assert entry["category"] == category, code
        assert entry["http_status"] == http_status, code
        assert entry["retryable"] is retryable, code
        assert entry["message"].strip(), code
        # Source errors are HTTP-level; none maps onto a Runtime Host IPC code.
        assert "runtime_host_code" not in entry, code

        definition = ERROR_REGISTRY[RuntimeErrorCode(code)]
        assert definition.category == category
        assert definition.http_status == http_status
        assert definition.retryable is retryable

    known = _spec()["components"]["schemas"]["Error"]["properties"]["code"][
        "x-vibeocr-known-values"
    ]
    assert set(DOWNLOAD_SOURCE_ERRORS).issubset(known)
    assert "falling back" in entries["DOWNLOAD_SOURCE_UNKNOWN"]["message"]


def test_generated_bindings_expose_strongly_typed_source_selection() -> None:
    hints = typing.get_type_hints(wire_types.SettingsSnapshot)
    assert hints["download_source_ids"] == list[str]
    assert "download_source_ids" in wire_types.SettingsSnapshot.__optional_keys__
    descriptor_hints = typing.get_type_hints(wire_types.DownloadSourceDescriptor)
    assert descriptor_hints["kind"] is str

    csharp_wire = (V2 / "generated/RuntimeWireTypes.g.cs").read_text(encoding="utf-8")
    settings_record = csharp_wire.split("public sealed record SettingsSnapshot", 1)[
        1
    ].split("\n}", 1)[0]
    assert '[JsonPropertyName("download_source_ids")]' in settings_record
    assert "public IReadOnlyList<string>? DownloadSourceIds { get; init; }" in (
        settings_record
    )

    descriptor_record = csharp_wire.split(
        "public sealed record DownloadSourceDescriptor", 1
    )[1].split("\n}", 1)[0]
    assert "public required string Kind { get; init; }" in descriptor_record
    assert "public required string Id { get; init; }" in descriptor_record
    assert "public required string Endpoint { get; init; }" in descriptor_record

    assert "public enum DownloadSourceKind" not in csharp_wire

    host_wire = (V2 / "generated/RuntimeHostWireTypes.g.cs").read_text(encoding="utf-8")
    request_record = host_wire.split("public sealed record RuntimeHostRequest", 1)[
        1
    ].split("\n}", 1)[0]
    assert "public IReadOnlyList<string>? DownloadSourceIds { get; init; }" in (
        request_record
    )
    command_record = host_wire.split(
        "public sealed record RuntimeMaintenanceCommandRequest", 1
    )[1].split("\n}", 1)[0]
    assert "public IReadOnlyList<string>? DownloadSourceIds { get; init; }" in (
        command_record
    )
