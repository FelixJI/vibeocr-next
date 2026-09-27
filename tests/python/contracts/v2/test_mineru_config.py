"""Contract tests for the ocr.mineru-config.v1 protocol extension.

Wire decisions under test:

* Four stable tier ids and three OCR modes single-sourced from the formal
  OpenAPI into the handwritten Python DTO/parser, both generated bindings and
  the .NET mirror.
* ``PipelineSelection.mineru`` is an optional, non-null typed block guarded by
  the capability; omission keeps the legacy payload byte-shape unchanged.
* ``page_range`` accepts only the canonical subset; semantic validation
  (page counts, bounds, file types) stays with the Backend.
* Mixed configs (legacy options or engine plus the mineru block) fail closed
  with VALIDATION_ERROR semantics; the block requires kind=mineru_parse and
  the MinerU pipeline.
* The catalog rides only on the capability descriptor carrier in both the
  HTTP health envelope and the Runtime Host schema.
* The client helper fails closed without any network access and without
  falling back to legacy options or the flash tier.
"""

from __future__ import annotations

import json
import typing
from pathlib import Path

import jsonschema
import pytest
from vibeocr.runtime.recognition.mineru_contracts import MINERU_TIER_CHAIN
from vibeocr.runtime_contracts import (
    MineruConfig,
    MineruOcrMode,
    MineruTier,
    dtos,
    parse_pipeline_selection,
    parse_submit_request,
)
from vibeocr.runtime_contracts.generated import ALL_CAPABILITIES, wire_types
from vibeocr.runtime_contracts.generated.capabilities import (
    OCR_MINERU_CONFIG_V1,
    OCR_MINERU_REMOTE_API_V1,
)
from vibeocr.runtime_contracts.generated.error_codes import (
    ERROR_REGISTRY,
    RuntimeErrorCode,
)
from vibeocr.runtime_contracts.parser import ContractError

ROOT = Path(__file__).resolve().parents[4]
V2 = ROOT / "contracts/runtime/python/vibeocr/runtime_contracts"
STABLE_TIER_IDS = ("flash", "basic", "standard", "advanced")
STABLE_OCR_MODES = ("auto", "txt", "ocr")
TIER_AVAILABILITIES = ("ready", "preparation_required", "unavailable")
MINERU_CONFIG_ERRORS = {
    "MINERU_CONFIG_UNAVAILABLE": ("capability", 426, False),
    "MINERU_CONFIG_MIGRATION_REQUIRED": ("validation", 400, False),
    "MINERU_TIER_UNAVAILABLE": ("capability", 426, False),
    "MINERU_TIER_PREPARATION_REQUIRED": ("capability", 428, False),
}
VALID_PAGE_RANGES = (
    "all",
    "1",
    "r1",
    "1-5",
    "1-5,8",
    "r3-r1",
    "3-1",
    "r3-1",
    "1,r2",
    "10-20,30,r5",
)
INVALID_PAGE_RANGES = (
    "1\n",
    "",
    "0",
    "r0",
    "01",
    " 1",
    "1 ",
    "1, 2",
    "1,",
    ",1",
    "all,1",
    "1,all",
    "-1",
    "1--2",
    "1.5",
    "*",
)


def _spec() -> dict:
    return json.loads((V2 / "openapi.yaml").read_text(encoding="utf-8"))


def _runtime_api_golden() -> dict:
    return json.loads((V2 / "golden/runtime-api.json").read_text(encoding="utf-8"))


def _golden() -> dict:
    return json.loads((V2 / "golden/golden.json").read_text(encoding="utf-8"))


def _manifest(pipeline_overrides: dict) -> dict:
    manifest = json.loads(json.dumps(_runtime_api_golden()["multipart_manifest"]))
    manifest["pipeline"].update(pipeline_overrides)
    return manifest


def _submit_request_validator(
    spec: dict | None = None,
) -> jsonschema.Draft202012Validator:
    spec = spec or _spec()
    return jsonschema.Draft202012Validator(
        {
            "$ref": "#/components/schemas/SubmitRequest",
            "components": spec["components"],
        }
    )


def _catalog() -> dict:
    return json.loads(json.dumps(_golden()["mineru_config_catalog"]))


# ---------------------------------------------------------------------------
# Single sourcing
# ---------------------------------------------------------------------------


def test_tier_and_mode_ids_are_single_sourced_across_layers() -> None:
    spec = _spec()
    assert tuple(spec["components"]["schemas"]["MineruTierId"]["enum"]) == (
        STABLE_TIER_IDS
    )
    assert tuple(spec["components"]["schemas"]["MineruOcrMode"]["enum"]) == (
        STABLE_OCR_MODES
    )
    assert tuple(dtos.MineruTier) == tuple(MineruTier(v) for v in STABLE_TIER_IDS)
    assert tuple(dtos.MineruOcrMode) == tuple(
        MineruOcrMode(v) for v in STABLE_OCR_MODES
    )
    assert set(typing.get_args(wire_types.MineruTierId)) == set(STABLE_TIER_IDS)
    assert set(typing.get_args(wire_types.MineruOcrMode)) == set(STABLE_OCR_MODES)
    assert MINERU_TIER_CHAIN == STABLE_TIER_IDS

    csharp_wire = (V2 / "generated/RuntimeWireTypes.g.cs").read_text(encoding="utf-8")
    tier_enum = csharp_wire.split("public enum MineruTierId", 1)[1].split("}", 1)[0]
    for value in STABLE_TIER_IDS:
        assert f'"{value}"' in tier_enum


def test_frozen_wire_samples_round_trip() -> None:
    fixture = _golden()["pipeline_selection_mineru"]
    assert fixture == {
        "pipeline_id": "MinerU",
        "options_version": 1,
        "options": {},
        "mineru": {
            "tier": "basic",
            "ocr_mode": "auto",
            "page_range": "all",
            "language": "ch",
        },
    }
    selection = dtos.PipelineSelection(
        pipeline_id="MinerU",
        options={},
        mineru=MineruConfig(tier=MineruTier.BASIC),
    )
    assert selection.to_payload() == fixture
    assert parse_pipeline_selection(fixture).to_payload() == fixture

    legacy = dtos.PipelineSelection(pipeline_id="MinerU")
    assert legacy.to_payload() == {
        "pipeline_id": "MinerU",
        "options_version": 1,
        "options": {},
    }


# ---------------------------------------------------------------------------
# page_range / language validation
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("value", VALID_PAGE_RANGES)
def test_page_range_accepts_the_canonical_subset(value: str) -> None:
    assert MineruConfig(tier=MineruTier.BASIC, page_range=value).page_range == value
    selection = parse_pipeline_selection(
        {
            "pipeline_id": "MinerU",
            "options_version": 1,
            "options": {},
            "mineru": {"tier": "basic", "page_range": value},
        }
    )
    assert selection.mineru is not None
    assert selection.mineru.page_range == value
    _submit_request_validator().validate(
        _manifest(
            {"pipeline_id": "MinerU", "mineru": {"tier": "basic", "page_range": value}}
        )
    )


@pytest.mark.parametrize("value", INVALID_PAGE_RANGES)
def test_page_range_rejects_non_canonical_values(value: str) -> None:
    with pytest.raises(ValueError):
        MineruConfig(tier=MineruTier.BASIC, page_range=value)
    with pytest.raises(ContractError):
        parse_pipeline_selection(
            {
                "pipeline_id": "MinerU",
                "options_version": 1,
                "options": {},
                "mineru": {"tier": "basic", "page_range": value},
            }
        )
    with pytest.raises(jsonschema.ValidationError):
        _submit_request_validator().validate(
            _manifest(
                {
                    "pipeline_id": "MinerU",
                    "mineru": {"tier": "basic", "page_range": value},
                }
            )
        )


def test_page_range_pattern_is_shared_with_the_formal_spec() -> None:
    pattern = _spec()["components"]["schemas"]["MineruConfig"]["properties"][
        "page_range"
    ]["pattern"]
    assert pattern == dtos.MINERU_PAGE_RANGE_PATTERN
    job_interface = json.loads(
        (V2 / "schemas/job-interface.schema.json").read_text(encoding="utf-8")
    )
    assert (
        job_interface["$defs"]["MineruConfig"]["properties"]["page_range"]["pattern"]
        == pattern
    )


@pytest.mark.parametrize(
    "language", ["ch", "en", "chinese- simplify ", "", " ch", "ch "]
)
def test_language_must_be_a_trimmed_non_empty_id(language: str) -> None:
    valid = language and language.strip() == language
    if valid:
        assert MineruConfig(tier=MineruTier.BASIC, language=language).language == (
            language
        )
    else:
        with pytest.raises(ValueError):
            MineruConfig(tier=MineruTier.BASIC, language=language)


# ---------------------------------------------------------------------------
# Parser strictness and cross-field rules
# ---------------------------------------------------------------------------


def _mineru_selection(mineru: object, **overrides: object) -> dict:
    payload: dict = {
        "pipeline_id": "MinerU",
        "options_version": 1,
        "options": {},
        "mineru": mineru,
    }
    payload.update(overrides)
    return payload


def test_mineru_block_requires_tier_and_rejects_unknown_members() -> None:
    with pytest.raises(ContractError):
        parse_pipeline_selection(_mineru_selection({"ocr_mode": "auto"}))
    with pytest.raises(ContractError):
        parse_pipeline_selection(_mineru_selection(None))
    with pytest.raises(ContractError):
        parse_pipeline_selection(_mineru_selection({"tier": "basic", "extra": 1}))
    with pytest.raises(ContractError):
        parse_pipeline_selection(_mineru_selection({"tier": "auto"}))
    with pytest.raises(ContractError):
        parse_pipeline_selection(_mineru_selection({"tier": "basic", "ocr_mode": None}))
    with pytest.raises(ContractError):
        parse_pipeline_selection(_mineru_selection({"tier": "basic", "language": None}))


def test_mineru_defaults_are_applied_when_fields_are_omitted() -> None:
    selection = parse_pipeline_selection(_mineru_selection({"tier": "standard"}))
    assert selection.mineru == MineruConfig(
        tier=MineruTier.STANDARD,
        ocr_mode=MineruOcrMode.AUTO,
        page_range="all",
        language="ch",
    )


def test_mineru_block_is_rejected_for_other_pipelines_and_kinds() -> None:
    with pytest.raises(ContractError):
        parse_pipeline_selection(
            {
                "pipeline_id": "OCR",
                "options_version": 1,
                "options": {},
                "mineru": {"tier": "basic"},
            }
        )
    manifest = json.loads(json.dumps(_runtime_api_golden()["multipart_manifest"]))
    manifest["kind"] = "recognition"
    manifest["pipeline"] = _mineru_selection({"tier": "basic"})
    with pytest.raises(ContractError):
        parse_submit_request(manifest)


@pytest.mark.parametrize(
    "options",
    [{"backend": "pipeline"}, {"effort": "high"}, {"start_page_id": 0}],
)
def test_mineru_block_rejects_non_empty_legacy_options(options: dict) -> None:
    with pytest.raises(ContractError):
        parse_pipeline_selection(_mineru_selection({"tier": "basic"}, options=options))


def test_mineru_block_rejects_engine_combination() -> None:
    with pytest.raises(ContractError, match="VALIDATION_ERROR"):
        parse_pipeline_selection(
            _mineru_selection({"tier": "basic"}, engine="rapidocr")
        )


def test_engine_field_is_parsed_strictly() -> None:
    selection = parse_pipeline_selection(
        {"pipeline_id": "OCR", "options_version": 1, "options": {}, "engine": "windows"}
    )
    assert selection.engine is dtos.OcrEngine.WINDOWS
    with pytest.raises(ContractError):
        parse_pipeline_selection(
            {"pipeline_id": "OCR", "options_version": 1, "options": {}, "engine": "x"}
        )
    with pytest.raises(ContractError):
        parse_pipeline_selection(
            {
                "pipeline_id": "MinerU",
                "options_version": 1,
                "options": {},
                "engine": "windows",
            }
        )


def test_mineru_parse_submit_manifest_round_trips() -> None:
    manifest = json.loads(json.dumps(_runtime_api_golden()["multipart_manifest"]))
    manifest["kind"] = "mineru_parse"
    manifest["pipeline"] = _golden()["pipeline_selection_mineru"]
    _submit_request_validator().validate(manifest)
    request = parse_submit_request(manifest)
    assert request.kind is dtos.JobKind.MINERU_PARSE
    assert request.pipeline.mineru is not None
    assert request.pipeline.mineru.tier is MineruTier.BASIC


# ---------------------------------------------------------------------------
# Capability + catalog registration
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("capability", "introduced_in"),
    [(OCR_MINERU_CONFIG_V1, "2.8.1"), (OCR_MINERU_REMOTE_API_V1, "2.9.0")],
)
def test_mineru_config_capability_is_registered_everywhere(
    capability: str, introduced_in: str
) -> None:
    registry = json.loads((V2 / "capabilities.json").read_text(encoding="utf-8"))
    assert capability in registry["capabilities"]
    definition = registry["definitions"][capability]
    assert definition["lifecycle"] == "active"
    assert definition["introduced_in"] == introduced_in

    assert OCR_MINERU_CONFIG_V1 == "ocr.mineru-config.v1"
    assert capability in ALL_CAPABILITIES

    spec = _spec()
    health_values = spec["components"]["schemas"]["Health"]["properties"][
        "capabilities"
    ]["items"]["x-vibeocr-known-values"]
    bootstrap = json.loads((V2 / "bootstrap.schema.json").read_text(encoding="utf-8"))
    bootstrap_values = bootstrap["properties"]["capabilities"]["items"][
        "x-vibeocr-known-values"
    ]
    for known_values in (health_values, bootstrap_values):
        assert capability in known_values


def test_capability_descriptor_carries_optional_mineru_catalog() -> None:
    schema = _spec()["components"]["schemas"]
    assert schema["CapabilityDescriptor"]["properties"]["mineru_config_catalog"] == {
        "$ref": "#/components/schemas/MineruConfigCatalog"
    }
    assert "mineru_config_catalog" not in schema["CapabilityDescriptor"]["required"]

    host = json.loads((V2 / "runtime-host.schema.json").read_text(encoding="utf-8"))
    assert host["$defs"]["CapabilityDescriptor"]["properties"][
        "mineru_config_catalog"
    ] == {"$ref": "#/$defs/MineruConfigCatalog"}
    assert host["$defs"]["MineruTierId"]["enum"] == list(STABLE_TIER_IDS)
    assert host["$defs"]["MineruTierAvailability"]["enum"] == list(TIER_AVAILABILITIES)
    from vibeocr.runtime_contracts.generated import runtime_host_types

    assert hasattr(runtime_host_types, "MineruConfigCatalog")
    assert hasattr(runtime_host_types, "MineruTierDescriptor")


def test_golden_catalog_and_health_validate_and_cover_all_states() -> None:
    spec = _spec()
    catalog = _golden()["mineru_config_catalog"]
    jsonschema.Draft202012Validator(
        {
            "$ref": "#/components/schemas/MineruConfigCatalog",
            "components": spec["components"],
        }
    ).validate(catalog)
    tiers = {tier["id"]: tier for tier in catalog["tiers"]}
    assert set(tiers) == set(STABLE_TIER_IDS)
    assert {tier["availability"] for tier in tiers.values()} == set(TIER_AVAILABILITIES)
    assert catalog["default_tier"] == "basic"
    assert catalog["languages"] == ["ch", "en"]

    golden_health = _runtime_api_golden()["health"]
    jsonschema.Draft202012Validator(
        {
            "$ref": "#/components/schemas/Health",
            "components": spec["components"],
        }
    ).validate(golden_health)
    descriptors = {
        descriptor["name"]: descriptor
        for descriptor in golden_health["capability_descriptors"]
    }
    assert descriptors["ocr.mineru-config.v1"]["mineru_config_catalog"] == catalog
    assert "ocr.mineru-config.v1" in golden_health["capabilities"]
    # Legacy descriptors keep their original wire shape.
    assert "mineru_config_catalog" not in descriptors["ocr.recognition.v2"]

    host = json.loads((V2 / "runtime-host.schema.json").read_text(encoding="utf-8"))
    jsonschema.Draft202012Validator(
        {
            "$ref": "#/$defs/MineruConfigCatalog",
            "$defs": host["$defs"],
        }
    ).validate(catalog)


def test_job_interface_schema_carries_the_same_mineru_block() -> None:
    job_interface = json.loads(
        (V2 / "schemas/job-interface.schema.json").read_text(encoding="utf-8")
    )
    validator = jsonschema.Draft202012Validator(
        {"$ref": "#/$defs/PipelineSelection", "$defs": job_interface["$defs"]}
    )
    validator.validate(_golden()["pipeline_selection_mineru"])
    legacy = {"pipeline_id": "MinerU", "options_version": 1, "options": {}}
    validator.validate(legacy)
    with pytest.raises(jsonschema.ValidationError):
        validator.validate(
            {
                "pipeline_id": "MinerU",
                "options_version": 1,
                "options": {},
                "mineru": None,
            }
        )
    with pytest.raises(jsonschema.ValidationError):
        validator.validate(
            {
                "pipeline_id": "MinerU",
                "options_version": 1,
                "options": {},
                "mineru": {"tier": "basic", "unknown": True},
            }
        )


# ---------------------------------------------------------------------------
# Typed errors
# ---------------------------------------------------------------------------


def test_mineru_error_codes_are_registered_and_fail_closed() -> None:
    registry = json.loads((V2 / "errors.json").read_text(encoding="utf-8"))
    entries = {entry["code"]: entry for entry in registry["codes"]}
    for code, (category, http_status, retryable) in MINERU_CONFIG_ERRORS.items():
        entry = entries[code]
        assert entry["category"] == category, code
        assert entry["http_status"] == http_status, code
        assert entry["retryable"] is retryable, code
        assert entry["message"].strip(), code
        assert "runtime_host_code" not in entry, code

        definition = ERROR_REGISTRY[RuntimeErrorCode(code)]
        assert definition.category == category
        assert definition.http_status == http_status
        assert definition.retryable is retryable

    known = _spec()["components"]["schemas"]["Error"]["properties"]["code"][
        "x-vibeocr-known-values"
    ]
    assert set(MINERU_CONFIG_ERRORS).issubset(known)
    # The messages keep RUNTIME_CAPABILITY_UNAVAILABLE maintenance-only.
    assert entries["RUNTIME_CAPABILITY_UNAVAILABLE"]["message"].startswith(
        "A capability required by the Runtime maintenance request"
    )

    csharp_protocol = (V2 / "generated/RuntimeProtocol.g.cs").read_text(
        encoding="utf-8"
    )
    for code in MINERU_CONFIG_ERRORS:
        assert code in csharp_protocol


# ---------------------------------------------------------------------------
# Cross-version compatibility
# ---------------------------------------------------------------------------


def test_old_runtime_schema_rejects_the_un_negotiated_block() -> None:
    baseline = json.loads(
        (V2 / "baselines/openapi-2.0.0.yaml").read_text(encoding="utf-8")
    )
    validator = jsonschema.Draft202012Validator(
        {
            "$ref": "#/components/schemas/SubmitRequest",
            "components": baseline["components"],
        }
    )
    with pytest.raises(jsonschema.ValidationError):
        validator.validate(
            _manifest(
                {
                    "pipeline_id": "MinerU",
                    "mineru": {"tier": "basic"},
                }
            )
        )


def test_old_sdk_payloads_still_validate_under_the_new_schema() -> None:
    baseline = json.loads(
        (V2 / "baselines/openapi-2.0.0.yaml").read_text(encoding="utf-8")
    )
    legacy_manifest = json.loads(
        json.dumps(_runtime_api_golden()["multipart_manifest"])
    )
    jsonschema.Draft202012Validator(
        {
            "$ref": "#/components/schemas/SubmitRequest",
            "components": baseline["components"],
        }
    ).validate(legacy_manifest)
    _submit_request_validator().validate(legacy_manifest)

    legacy_mineru_options = {
        "pipeline_id": "MinerU",
        "options_version": 1,
        "options": {"backend": "hybrid-engine", "effort": "medium"},
    }
    _submit_request_validator().validate(_manifest({}))
    selection = parse_pipeline_selection(legacy_mineru_options)
    assert selection.options == {"backend": "hybrid-engine", "effort": "medium"}


def test_unknown_future_response_values_stay_compatible() -> None:
    # Responses only ever add optional content; the catalog reader tolerates a
    # descriptor with no mineru_config_catalog and health with unknown
    # capability ids keeps validating.
    golden_health = json.loads(json.dumps(_runtime_api_golden()["health"]))
    golden_health["capabilities"].append("future.feature.v3")
    jsonschema.Draft202012Validator(
        {
            "$ref": "#/components/schemas/Health",
            "components": _spec()["components"],
        }
    ).validate(golden_health)


# ---------------------------------------------------------------------------
# Client construction helper (fail closed, zero network)
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("language", [" ch", "ch ", "ch\n"])
def test_mineru_language_is_strict_in_both_source_schemas(language: str) -> None:
    spec = _spec()
    legacy = json.loads(
        (V2 / "schemas/job-interface.schema.json").read_text(encoding="utf-8")
    )
    for schema in (
        {"$ref": "#/components/schemas/MineruConfig", "components": spec["components"]},
        {"$ref": "#/$defs/MineruConfig", "$defs": legacy["$defs"]},
    ):
        with pytest.raises(jsonschema.ValidationError):
            jsonschema.Draft202012Validator(schema).validate(
                {"tier": "basic", "language": language}
            )
