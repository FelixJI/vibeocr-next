"""Pure OCR pipeline identifiers and presentation metadata.

This module is intentionally stdlib-only.  Frontends may import it without
loading pipeline implementations, model runtimes, Qt, or WorkerHost services.
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum, StrEnum
from typing import Any


class OCRPipeline(Enum):
    OCR = "OCR"
    PP_STRUCTURE_V3 = "PP-StructureV3"
    DOCUMENT_PARSING = "MinerU"
    PADDLEOCR_VL = "PaddleOCR-VL"
    TABLE_RECOGNITION = "TABLE_RECOGNITION"
    FORMULA_RECOGNITION = "FORMULA_RECOGNITION"

    @property
    def display_name(self) -> str:
        return get_pipeline_display_name(self)

    @property
    def description(self) -> str:
        return get_pipeline_description(self)


class RecognitionMode(StrEnum):
    """Stable user-facing recognition choices.

    A recognition mode owns the product semantics while ``OCRPipeline`` remains
    the execution routing vocabulary used by Protocol v2 requests.
    """

    RAPID_TEXT = "rapid_text"
    WINDOWS_TEXT = "windows_text"
    PADDLE_TEXT = "paddle_text"
    PADDLE_STRUCTURE = "paddle_structure"
    PADDLE_DOCUMENT_VL = "paddle_document_vl"
    MINERU_DOCUMENT = "mineru_document"
    PADDLE_TABLE = "paddle_table"
    PADDLE_FORMULA = "paddle_formula"


class RecognitionModeFamily(StrEnum):
    TEXT = "text"
    DOCUMENT = "document"
    SPECIALIZED = "specialized"


class RecognitionModeProvisioning(StrEnum):
    BASE_RUNTIME = "base_runtime"
    OPERATING_SYSTEM = "operating_system"
    ADVANCED_COMPONENT = "advanced_component"


class RecognitionModeLifecycleKind(StrEnum):
    UNMANAGED = "unmanaged"
    MODEL_RESIDENCY = "model_residency"
    PROCESS_KEEP_ALIVE = "process_keep_alive"


@dataclass(frozen=True, slots=True)
class RecognitionModeLifecycle:
    kind: RecognitionModeLifecycleKind
    supports_preload: bool
    supports_ttl: bool
    supports_pinning: bool
    supports_release: bool


@dataclass(frozen=True, slots=True)
class RecognitionModeDefinition:
    mode: RecognitionMode
    family: RecognitionModeFamily
    pipeline: OCRPipeline
    engine: str | None
    provisioning: RecognitionModeProvisioning
    lifecycle: RecognitionModeLifecycle
    display_name: str
    short_name: str
    description: str
    supported_options: tuple[str, ...]


_UNMANAGED_LIFECYCLE = RecognitionModeLifecycle(
    kind=RecognitionModeLifecycleKind.UNMANAGED,
    supports_preload=False,
    supports_ttl=False,
    supports_pinning=False,
    supports_release=False,
)
_MODEL_RESIDENCY_LIFECYCLE = RecognitionModeLifecycle(
    kind=RecognitionModeLifecycleKind.MODEL_RESIDENCY,
    supports_preload=True,
    supports_ttl=True,
    supports_pinning=True,
    supports_release=True,
)
# mineru_document reuses the preload endpoint for explicit first-time
# preparation; preload never turns the child process into model residency,
# so pinning stays unsupported.
_MINERU_PROCESS_KEEP_ALIVE_LIFECYCLE = RecognitionModeLifecycle(
    kind=RecognitionModeLifecycleKind.PROCESS_KEEP_ALIVE,
    supports_preload=True,
    supports_ttl=True,
    supports_pinning=False,
    supports_release=True,
)


_PIPELINE_METADATA: dict[OCRPipeline, dict[str, Any]] = {
    OCRPipeline.OCR: {
        "display_name": "通用 OCR",
        "short_name": "文字",
        "preloadable": False,
        "heavy": False,
        "cache_kind": "routed",
        "description": "文字识别执行管道；具体语义由识别模式和引擎共同确定",
        "supported_options": [
            "use_doc_orientation_classify",
            "use_doc_unwarping",
            "use_textline_orientation",
        ],
    },
    OCRPipeline.PP_STRUCTURE_V3: {
        "display_name": "文档结构识别（PP-StructureV3）",
        "short_name": "结构",
        "preloadable": True,
        "heavy": True,
        "cache_kind": "paddle",
        "description": "文档结构分析，支持表格、公式、印章、图表识别",
        "supported_options": [
            "use_doc_orientation_classify",
            "use_doc_unwarping",
            "use_textline_orientation",
            "use_table_recognition",
            "use_formula_recognition",
            "use_seal_recognition",
            "use_chart_recognition",
        ],
    },
    OCRPipeline.DOCUMENT_PARSING: {
        "display_name": "深度文档解析（MinerU）",
        "short_name": "文档M",
        "preloadable": True,
        "heavy": True,
        "cache_kind": "mineru",
        "description": "使用 MinerU 解析文档，支持 PDF/图片，提取文本、表格、公式等",
        "supported_options": [
            "parse_method",
            "backend",
            "effort",
            "enable_formula",
            "enable_table",
            "lang_list",
            "start_page_id",
            "end_page_id",
        ],
    },
    OCRPipeline.PADDLEOCR_VL: {
        "display_name": "视觉文档解析（PaddleOCR-VL）",
        "short_name": "文档P",
        "preloadable": True,
        "heavy": True,
        "cache_kind": "paddle",
        "description": "使用 PaddleOCR-VL-1.5 解析文档，支持图片/PDF，提取文本、表格、公式、图表等",
        "supported_options": [
            "use_doc_orientation_classify",
            "use_doc_unwarping",
            "vl_use_layout_detection",
            "vl_use_chart_recognition",
            "vl_use_seal_recognition",
            "use_ocr_for_image_block",
        ],
    },
    OCRPipeline.TABLE_RECOGNITION: {
        "display_name": "表格结构识别（PaddleOCR）",
        "short_name": "表格",
        "preloadable": True,
        "heavy": False,
        "cache_kind": "paddle",
        "description": "独立表格结构识别，支持有线和无线表格",
        "supported_options": [
            "use_doc_orientation_classify",
            "use_doc_unwarping",
            "use_table_orientation_classify",
            "use_ocr_results_with_table_cells",
        ],
    },
    OCRPipeline.FORMULA_RECOGNITION: {
        "display_name": "数学公式识别（PaddleOCR）",
        "short_name": "公式",
        "preloadable": True,
        "heavy": False,
        "cache_kind": "paddle",
        "description": "独立数学公式识别（LaTeX 输出）",
        "supported_options": [
            "use_doc_orientation_classify",
            "use_doc_unwarping",
            "formula_recognition_batch_size",
            "formula_recognition_model_name",
            "formula_recognition_model_dir",
        ],
    },
}


_RECOGNITION_MODE_DEFINITIONS: dict[RecognitionMode, RecognitionModeDefinition] = {
    RecognitionMode.RAPID_TEXT: RecognitionModeDefinition(
        mode=RecognitionMode.RAPID_TEXT,
        family=RecognitionModeFamily.TEXT,
        pipeline=OCRPipeline.OCR,
        engine="rapidocr",
        provisioning=RecognitionModeProvisioning.BASE_RUNTIME,
        lifecycle=_UNMANAGED_LIFECYCLE,
        display_name="快速 OCR（RapidOCR）",
        short_name="快速文字",
        description="随基础运行时提供的轻量文字识别，无需下载高级模型。",
        supported_options=(),
    ),
    RecognitionMode.WINDOWS_TEXT: RecognitionModeDefinition(
        mode=RecognitionMode.WINDOWS_TEXT,
        family=RecognitionModeFamily.TEXT,
        pipeline=OCRPipeline.OCR,
        engine="windows",
        provisioning=RecognitionModeProvisioning.OPERATING_SYSTEM,
        lifecycle=_UNMANAGED_LIFECYCLE,
        display_name="Windows OCR（系统内置）",
        short_name="系统文字",
        description="使用 Windows 系统 OCR；可用性由操作系统能力决定。",
        supported_options=(),
    ),
    RecognitionMode.PADDLE_TEXT: RecognitionModeDefinition(
        mode=RecognitionMode.PADDLE_TEXT,
        family=RecognitionModeFamily.TEXT,
        pipeline=OCRPipeline.OCR,
        engine="paddleocr",
        provisioning=RecognitionModeProvisioning.ADVANCED_COMPONENT,
        lifecycle=_MODEL_RESIDENCY_LIFECYCLE,
        display_name="通用 OCR（PaddleOCR）",
        short_name="通用文字",
        description="基于 PaddleOCR 模型的通用文字识别，需要高级组件。",
        supported_options=tuple(
            _PIPELINE_METADATA[OCRPipeline.OCR]["supported_options"]
        ),
    ),
    RecognitionMode.PADDLE_STRUCTURE: RecognitionModeDefinition(
        mode=RecognitionMode.PADDLE_STRUCTURE,
        family=RecognitionModeFamily.DOCUMENT,
        pipeline=OCRPipeline.PP_STRUCTURE_V3,
        engine=None,
        provisioning=RecognitionModeProvisioning.ADVANCED_COMPONENT,
        lifecycle=_MODEL_RESIDENCY_LIFECYCLE,
        display_name="文档结构识别（PP-StructureV3）",
        short_name="文档结构",
        description="识别版面、表格、公式、印章和图表等文档结构。",
        supported_options=tuple(
            _PIPELINE_METADATA[OCRPipeline.PP_STRUCTURE_V3]["supported_options"]
        ),
    ),
    RecognitionMode.PADDLE_DOCUMENT_VL: RecognitionModeDefinition(
        mode=RecognitionMode.PADDLE_DOCUMENT_VL,
        family=RecognitionModeFamily.DOCUMENT,
        pipeline=OCRPipeline.PADDLEOCR_VL,
        engine=None,
        provisioning=RecognitionModeProvisioning.ADVANCED_COMPONENT,
        lifecycle=_MODEL_RESIDENCY_LIFECYCLE,
        display_name="视觉文档解析（PaddleOCR-VL）",
        short_name="视觉文档",
        description="使用 PaddleOCR-VL 解析图文混排文档，需要高级组件。",
        supported_options=tuple(
            _PIPELINE_METADATA[OCRPipeline.PADDLEOCR_VL]["supported_options"]
        ),
    ),
    RecognitionMode.MINERU_DOCUMENT: RecognitionModeDefinition(
        mode=RecognitionMode.MINERU_DOCUMENT,
        family=RecognitionModeFamily.DOCUMENT,
        pipeline=OCRPipeline.DOCUMENT_PARSING,
        engine=None,
        provisioning=RecognitionModeProvisioning.ADVANCED_COMPONENT,
        lifecycle=_MINERU_PROCESS_KEEP_ALIVE_LIFECYCLE,
        display_name="深度文档解析（MinerU）",
        short_name="深度文档",
        description="使用独立 MinerU 进程深度解析 PDF 或图片，需要高级组件。",
        supported_options=tuple(
            _PIPELINE_METADATA[OCRPipeline.DOCUMENT_PARSING]["supported_options"]
        ),
    ),
    RecognitionMode.PADDLE_TABLE: RecognitionModeDefinition(
        mode=RecognitionMode.PADDLE_TABLE,
        family=RecognitionModeFamily.SPECIALIZED,
        pipeline=OCRPipeline.TABLE_RECOGNITION,
        engine=None,
        provisioning=RecognitionModeProvisioning.ADVANCED_COMPONENT,
        lifecycle=_MODEL_RESIDENCY_LIFECYCLE,
        display_name="表格结构识别（PaddleOCR）",
        short_name="表格结构",
        description="识别有线或无线表格的结构，需要高级组件。",
        supported_options=tuple(
            _PIPELINE_METADATA[OCRPipeline.TABLE_RECOGNITION]["supported_options"]
        ),
    ),
    RecognitionMode.PADDLE_FORMULA: RecognitionModeDefinition(
        mode=RecognitionMode.PADDLE_FORMULA,
        family=RecognitionModeFamily.SPECIALIZED,
        pipeline=OCRPipeline.FORMULA_RECOGNITION,
        engine=None,
        provisioning=RecognitionModeProvisioning.ADVANCED_COMPONENT,
        lifecycle=_MODEL_RESIDENCY_LIFECYCLE,
        display_name="数学公式识别（PaddleOCR）",
        short_name="数学公式",
        description="识别数学公式并输出 LaTeX，需要高级组件。",
        supported_options=tuple(
            _PIPELINE_METADATA[OCRPipeline.FORMULA_RECOGNITION]["supported_options"]
        ),
    ),
}


def _metadata(pipeline: OCRPipeline) -> dict[str, Any]:
    return _PIPELINE_METADATA.get(pipeline, {})


def get_pipeline_display_name(pipeline: OCRPipeline) -> str:
    return str(_metadata(pipeline).get("display_name", pipeline.value))


def get_pipeline_short_name(pipeline: OCRPipeline) -> str:
    return str(_metadata(pipeline).get("short_name", pipeline.value))


def get_pipeline_description(pipeline: OCRPipeline) -> str:
    return str(_metadata(pipeline).get("description", ""))


def get_pipeline_supported_options(pipeline: OCRPipeline) -> list[str]:
    return list(_metadata(pipeline).get("supported_options", []))


def get_all_pipelines() -> list[OCRPipeline]:
    return list(OCRPipeline)


def get_all_recognition_modes() -> list[RecognitionMode]:
    return list(RecognitionMode)


def get_recognition_mode_definition(
    mode: RecognitionMode,
) -> RecognitionModeDefinition:
    return _RECOGNITION_MODE_DEFINITIONS[mode]


def get_preloadable_pipelines() -> list[OCRPipeline]:
    return [p for p in OCRPipeline if _metadata(p).get("preloadable", False)]


def get_heavy_pipelines() -> list[OCRPipeline]:
    return [p for p in OCRPipeline if _metadata(p).get("heavy", False)]


def get_paddle_pipelines() -> list[OCRPipeline]:
    """走 paddle 回收路径的管道（del + paddle.device.cuda.empty_cache）。"""
    return [p for p in OCRPipeline if _metadata(p).get("cache_kind") == "paddle"]


def get_mineru_pipelines() -> list[OCRPipeline]:
    """走 mineru 回收路径的管道（仅移除 httpx 代理，不调 empty_cache）。"""
    return [p for p in OCRPipeline if _metadata(p).get("cache_kind") == "mineru"]


def is_option_supported(pipeline: OCRPipeline, option_name: str) -> bool:
    return option_name in get_pipeline_supported_options(pipeline)


__all__ = [
    "OCRPipeline",
    "RecognitionMode",
    "RecognitionModeDefinition",
    "RecognitionModeFamily",
    "RecognitionModeLifecycle",
    "RecognitionModeLifecycleKind",
    "RecognitionModeProvisioning",
    "get_all_pipelines",
    "get_all_recognition_modes",
    "get_heavy_pipelines",
    "get_mineru_pipelines",
    "get_paddle_pipelines",
    "get_pipeline_description",
    "get_pipeline_display_name",
    "get_pipeline_short_name",
    "get_pipeline_supported_options",
    "get_preloadable_pipelines",
    "get_recognition_mode_definition",
    "is_option_supported",
]
