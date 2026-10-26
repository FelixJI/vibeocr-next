"""Contract tests for the recognition-mode semantic catalog."""

from __future__ import annotations

import json
from pathlib import Path

import pytest
from vibeocr.runtime.recognition.pipeline_contracts import (
    OCRPipeline,
    RecognitionMode,
    get_all_recognition_modes,
    get_preloadable_pipelines,
    get_recognition_mode_definition,
)
from vibeocr.runtime_contracts.dtos import (
    PipelineSpec,
    RecognitionResourceKind,
    ResidencyEntry,
    ResidencyKind,
)
from vibeocr.runtime_contracts.errors import ErrorCode
from vibeocr.runtime_contracts.generated import ALL_CAPABILITIES, wire_types
from vibeocr.runtime_contracts.parser import parse_pipeline_spec, parse_residency_entry

ROOT = Path(__file__).resolve().parents[4]
V2 = ROOT / "contracts/runtime/python/vibeocr/runtime_contracts"

EXPECTED_MODES = {
    "windows_text": (
        "text",
        "OCR",
        "windows",
        "operating_system",
        "unmanaged",
    ),
    "paddle_text": (
        "text",
        "OCR",
        "paddleocr",
        "base_runtime",
        "model_residency",
    ),
    "paddle_structure": (
        "document",
        "PP-StructureV3",
        None,
        "advanced_component",
        "model_residency",
    ),
    "mineru_document": (
        "document",
        "MinerU",
        None,
        "advanced_component",
        "process_keep_alive",
    ),
    "paddle_table": (
        "specialized",
        "TABLE_RECOGNITION",
        None,
        "advanced_component",
        "model_residency",
    ),
}

# 已退役模式：wire 枚举与历史 id 解析保留（识别旧请求/旧记录并拒绝），
# 公开目录、生命周期与执行能力不再暴露。
RETIRED_MODES = {
    "paddle_document_vl": ("document", "PaddleOCR-VL", None, "advanced_component"),
    "paddle_formula": (
        "specialized",
        "FORMULA_RECOGNITION",
        None,
        "advanced_component",
    ),
    "rapid_text": ("text", "OCR", "rapidocr", "base_runtime"),
    "rapid_table": ("specialized", "RAPID_TABLE", None, "base_runtime"),
}

# RapidOCR/RapidTable 全面退役：rapid_text/rapid_table 保留为退休枚举值
# 仅供历史记录反序列化；提交/目录/生命周期 fail closed，旧持久化默认
# 只在设置 seam 按别名迁移到 paddle_text/paddle_table。
RETIRED_RAPID_MODES = ("rapid_text", "rapid_table")

EXPECTED_LIFECYCLE = {
    "windows_text": ("unmanaged", False, False, False, False),
    "paddle_text": ("model_residency", True, True, True, True),
    "paddle_structure": ("model_residency", True, True, True, True),
    "mineru_document": ("process_keep_alive", True, True, False, True),
    "paddle_table": ("model_residency", True, True, True, True),
}


def _spec() -> dict:
    return json.loads((V2 / "openapi.yaml").read_text(encoding="utf-8"))


def _runtime_golden() -> dict:
    return json.loads((V2 / "golden/runtime-api.json").read_text(encoding="utf-8"))


def test_recognition_mode_catalog_is_the_semantic_source_of_truth() -> None:
    spec = _spec()
    schemas = spec["components"]["schemas"]

    assert set(schemas["RecognitionModeId"]["enum"]) == (
        set(EXPECTED_MODES) | set(RETIRED_MODES)
    )
    assert schemas["RecognitionModeFamily"]["enum"] == [
        "text",
        "document",
        "specialized",
    ]
    assert schemas["RecognitionModeProvisioning"]["enum"] == [
        "base_runtime",
        "operating_system",
        "advanced_component",
    ]
    assert schemas["RecognitionModeLifecycleKind"]["enum"] == [
        "unmanaged",
        "model_residency",
        "process_keep_alive",
    ]
    assert schemas["CapabilityDescriptor"]["properties"][
        "recognition_mode_catalog"
    ] == {"$ref": "#/components/schemas/RecognitionModeCatalog"}

    health = _runtime_golden()["health"]
    descriptor = next(
        item
        for item in health["capability_descriptors"]
        if item["name"] == "ocr.recognition-modes.v1"
    )
    modes = {
        item["id"]: (
            item["family"],
            item["pipeline_id"],
            item["engine"],
            item["provisioning"],
            item["lifecycle"]["kind"],
        )
        for item in descriptor["recognition_mode_catalog"]["modes"]
    }
    assert modes == EXPECTED_MODES
    # #270：已退役模式不得出现在公开目录（golden 与 Runtime 同一投影）。
    assert set(modes).isdisjoint(RETIRED_MODES)
    lifecycle = {
        item["id"]: (
            item["lifecycle"]["kind"],
            item["lifecycle"]["supports_preload"],
            item["lifecycle"]["supports_ttl"],
            item["lifecycle"]["supports_pinning"],
            item["lifecycle"]["supports_release"],
        )
        for item in descriptor["recognition_mode_catalog"]["modes"]
    }
    assert lifecycle == EXPECTED_LIFECYCLE
    assert "ocr.recognition-modes.v1" in health["capabilities"]
    assert "ocr.recognition-modes.v1" in ALL_CAPABILITIES
    assert hasattr(wire_types, "RecognitionModeCatalog")


def test_paddle_local_models_policy_is_strict_and_request_only():
    import pytest
    from vibeocr.runtime.recognition.models.ocr_options import OCROptions
    from vibeocr.runtime_contracts.parser import ContractError, parse_pipeline_selection

    definition = get_recognition_mode_definition(RecognitionMode.PADDLE_TEXT)
    assert "local_models_only" in definition.supported_options
    selected = parse_pipeline_selection(
        {
            "pipeline_id": "OCR",
            "engine": "paddleocr",
            "options_version": 1,
            "options": {"local_models_only": True},
        }
    )
    assert selected.options["local_models_only"] is True
    assert OCROptions.from_dict(selected.options).local_models_only
    assert not OCROptions().local_models_only
    for invalid in (1, "true", None):
        with pytest.raises(ContractError):
            parse_pipeline_selection(
                {
                    "pipeline_id": "OCR",
                    "engine": "paddleocr",
                    "options_version": 1,
                    "options": {"local_models_only": invalid},
                }
            )


def test_recognition_modes_project_to_legacy_execution_without_lifecycle_ambiguity() -> (
    None
):
    # 已退役 id 保留在枚举中（顺序固定，历史读取/拒绝用），目录另排除。
    assert tuple(mode.value for mode in get_all_recognition_modes()) == (
        "rapid_text",
        "windows_text",
        "paddle_text",
        "paddle_structure",
        "paddle_document_vl",
        "mineru_document",
        "paddle_table",
        "paddle_formula",
        "rapid_table",
    )
    assert set(EXPECTED_MODES) | set(RETIRED_MODES) == {
        mode.value for mode in RecognitionMode
    }

    expected_names = {
        "rapid_text": "快速 OCR（RapidOCR，已退役）",
        "windows_text": "Windows OCR（系统内置）",
        "paddle_text": "通用 OCR（PaddleOCR）",
        "paddle_structure": "文档结构识别（PP-StructureV3）",
        "paddle_document_vl": "视觉文档解析（PaddleOCR-VL，已退役）",
        "mineru_document": "深度文档解析（MinerU）",
        "paddle_table": "表格结构识别（PaddleOCR）",
        "paddle_formula": "数学公式识别（PaddleOCR，已退役）",
        "rapid_table": "表格结构识别（RapidTable，已退役）",
    }
    for mode in RecognitionMode:
        definition = get_recognition_mode_definition(mode)
        assert definition.display_name == expected_names[mode.value]
        expected = EXPECTED_MODES.get(mode.value)
        if expected is not None:
            assert definition.pipeline.value == expected[1]
            assert definition.engine == expected[2]
            assert definition.family == expected[0]
            assert definition.provisioning == expected[3]
            assert definition.lifecycle.kind == expected[4]
        else:
            # 退役模式：仅保留历史身份字段，不再携带可执行生命周期。
            retired = RETIRED_MODES[mode.value]
            assert definition.pipeline.value == retired[1]
            assert definition.engine == retired[2]
            assert definition.family == retired[0]
            assert definition.provisioning == retired[3]
            assert not definition.lifecycle.supports_preload
            assert not definition.lifecycle.supports_ttl
            assert not definition.lifecycle.supports_pinning
            assert not definition.lifecycle.supports_release
            assert definition.supported_options == ()

    # 退休 rapid 模式仅保留历史身份，不再携带可执行选项/生命周期。
    assert (
        get_recognition_mode_definition(RecognitionMode.RAPID_TEXT).supported_options
        == ()
    )
    assert (
        get_recognition_mode_definition(RecognitionMode.WINDOWS_TEXT).supported_options
        == ()
    )
    assert (
        "use_doc_unwarping"
        in get_recognition_mode_definition(
            RecognitionMode.PADDLE_TEXT
        ).supported_options
    )

    # The legacy OCR execution pipeline is routed across three modes and must
    # never again be advertised as generically preloadable.
    assert OCRPipeline.OCR not in get_preloadable_pipelines()
    assert not get_recognition_mode_definition(
        RecognitionMode.RAPID_TEXT
    ).lifecycle.supports_preload
    assert get_recognition_mode_definition(
        RecognitionMode.PADDLE_TEXT
    ).lifecycle.supports_pinning
    # mineru_document reuses the shared preload endpoint for explicit
    # first-time preparation while staying process_keep_alive: preload is
    # supported, pinning never is.
    mineru = get_recognition_mode_definition(RecognitionMode.MINERU_DOCUMENT)
    assert mineru.lifecycle.supports_preload
    assert not mineru.lifecycle.supports_pinning
    assert mineru.lifecycle.kind.value == "process_keep_alive"
    assert OCRPipeline.DOCUMENT_PARSING in get_preloadable_pipelines()


def test_lifecycle_contract_targets_modes_and_names_the_managed_resource() -> None:
    schemas = _spec()["components"]["schemas"]

    preload = schemas["RuntimePreloadRequest"]
    assert preload["required"] == ["pipelines"]
    assert preload["properties"]["recognition_modes"]["items"] == {
        "$ref": "#/components/schemas/RecognitionModeId"
    }
    assert schemas["RuntimeReleaseRequest"]["properties"]["recognition_mode"] == {
        "anyOf": [
            {"$ref": "#/components/schemas/RecognitionModeId"},
            {"type": "null"},
        ]
    }
    assert schemas["PipelineSpec"]["properties"]["recognition_mode"] == {
        "$ref": "#/components/schemas/RecognitionModeId"
    }
    residency_properties = schemas["ResidencyEntry"]["properties"]
    assert residency_properties["recognition_mode"] == {
        "$ref": "#/components/schemas/RecognitionModeId"
    }
    assert residency_properties["resource_kind"] == {
        "$ref": "#/components/schemas/RecognitionResourceKind"
    }
    assert residency_properties["resource_id"] == {"type": "string", "minLength": 1}

    legacy_spec = PipelineSpec(name="OCR", ttl_seconds=120)
    assert legacy_spec.to_payload() == {
        "name": "OCR",
        "ttl_seconds": 120,
        "pinned": False,
    }
    mode_spec = PipelineSpec(
        name="OCR",
        recognition_mode="paddle_text",
        ttl_seconds=120,
        pinned=True,
    )
    assert mode_spec.to_payload()["recognition_mode"] == "paddle_text"
    assert parse_pipeline_spec(mode_spec.to_payload()) == mode_spec

    legacy_entry = ResidencyEntry(pipeline="OCR", kind=ResidencyKind.IDLE)
    assert "recognition_mode" not in legacy_entry.to_payload()
    mode_entry = ResidencyEntry(
        pipeline="OCR",
        recognition_mode="paddle_text",
        resource_kind=RecognitionResourceKind.MODEL,
        resource_id="paddleocr.text.server-v5",
        kind=ResidencyKind.PINNED,
    )
    assert parse_residency_entry(mode_entry.to_payload()) == mode_entry
    assert mode_entry.to_payload()["resource_kind"] == "model"

    assert "recognition_modes" in wire_types.RuntimePreloadRequest.__annotations__
    assert "recognition_mode" in wire_types.RuntimeReleaseRequest.__annotations__
    assert "recognition_mode" in wire_types.ResidencyEntry.__annotations__
    assert {
        code.value
        for code in (
            ErrorCode.RECOGNITION_MODE_UNKNOWN,
            ErrorCode.RECOGNITION_MODE_UNAVAILABLE,
            ErrorCode.RECOGNITION_MODE_RETIRED,
            ErrorCode.RECOGNITION_MODE_LIFECYCLE_UNSUPPORTED,
            ErrorCode.RECOGNITION_MODE_PIPELINE_MISMATCH,
        )
    } == {
        "RECOGNITION_MODE_UNKNOWN",
        "RECOGNITION_MODE_UNAVAILABLE",
        "RECOGNITION_MODE_RETIRED",
        "RECOGNITION_MODE_LIFECYCLE_UNSUPPORTED",
        "RECOGNITION_MODE_PIPELINE_MISMATCH",
    }


def test_retired_modes_resolve_for_diagnostics_but_reject_execution() -> None:
    """#270：退役模式历史 id 可解析、目录不暴露、执行性操作明确拒绝。"""
    from vibeocr.runtime.recognition.pipeline_contracts import (
        RETIRED_RECOGNITION_MODES,
        is_recognition_mode_retired,
    )
    from vibeocr.runtime.recognition.recognition_modes import (
        RecognitionModeError,
        RecognitionModeRegistry,
        resolve_default_recognition_mode,
    )

    assert {mode.value for mode in RETIRED_RECOGNITION_MODES} == set(RETIRED_MODES)
    assert is_recognition_mode_retired(RecognitionMode.PADDLE_FORMULA)
    assert is_recognition_mode_retired(RecognitionMode.PADDLE_DOCUMENT_VL)

    registry = RecognitionModeRegistry()
    catalog_ids = {entry["id"] for entry in registry.catalog_payload()["modes"]}
    assert catalog_ids == set(EXPECTED_MODES)
    assert catalog_ids.isdisjoint(RETIRED_MODES)

    # 历史 id 仍可解析（诊断/迁移映射），但一切执行性入口 fail closed。
    for mode_id in RETIRED_MODES:
        definition = registry.definition(mode_id)
        assert definition.mode_id.value == mode_id
        for invoke in (
            lambda: registry.ensure_mode_available(mode_id),
            lambda: registry.validate_lifecycle(mode_id, "preload"),
            lambda: registry.validate_pipeline_spec(
                mode_id, pipeline_id=definition.pipeline_id, pinned=False
            ),
            lambda: registry.resolve_execution(
                mode_id,
                pipeline_id=definition.pipeline_id,
                engine=definition.engine,
            ),
            lambda: registry.resolve_execution_fields(
                pipeline_id=definition.pipeline_id, engine=definition.engine
            ),
            # 旧 rapid 默认值例外：设置 seam 按别名安全迁移到 Paddle，
            # 不走 RECOGNITION_MODE_RETIRED（见 rapid 迁移专项测试）。
            *(
                (
                    lambda mode_id=mode_id: resolve_default_recognition_mode(
                        {"default_recognition_mode": mode_id},
                        registry,
                        require_available=True,
                    ),
                )
                if mode_id not in RETIRED_RAPID_MODES
                else ()
            ),
        ):
            with pytest.raises(RecognitionModeError) as error:
                invoke()
            assert error.value.code_name == "RECOGNITION_MODE_RETIRED"
            assert error.value.reason_code == "recognition_mode_retired"
            assert error.value.detail.get("next_action") == (
                "choose_available_recognition_mode"
            )

    # 读配置不崩溃：非 rapid 退役默认值在不校验可用性时可解析回显（GET
    # settings）；旧 rapid 默认值在设置 seam 按别名迁移到 Paddle，缺省
    # 默认已是 paddle_text。
    assert (
        resolve_default_recognition_mode(
            {"default_recognition_mode": "paddle_formula"},
            registry,
            require_available=False,
        )
        is RecognitionMode.PADDLE_FORMULA
    )
    assert (
        resolve_default_recognition_mode({}, registry, require_available=True)
        is RecognitionMode.PADDLE_TEXT
    )
    assert (
        resolve_default_recognition_mode(
            {"default_recognition_mode": "rapid_text"},
            registry,
            require_available=False,
        )
        is RecognitionMode.PADDLE_TEXT
    )
