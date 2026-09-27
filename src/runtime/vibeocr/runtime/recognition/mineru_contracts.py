"""Pure MineRU option values shared by frontend forms and backend adapters."""

MINERU_BACKEND_DEFAULT = "hybrid-engine"
MINERU_BACKEND_CHAIN = ("hybrid-engine", "vlm-engine", "pipeline")
MINERU_BACKEND_LABELS = {
    "hybrid-engine": "混合引擎（推荐）",
    "vlm-engine": "VLM 智能引擎",
    "pipeline": "传统流水线",
}
MINERU_EFFORT_DEFAULT = "medium"
MINERU_EFFORT_LABELS = {
    "medium": "标准（更快，关闭图片/图表分析）",
    "high": "高精度（启用图片/图表分析，更慢）",
}
MINERU_TIER_CHAIN = ("flash", "basic", "standard", "advanced")
"""Stable MinerU 4 tier ids (``ocr.mineru-config.v1``).

Presentation metadata only: the wire enum lives in ``openapi.yaml``
``MineruTierId`` and the executable values in ``dtos.MineruTier``. Tier
availability and the product default (initially ``basic``) come from the
Backend-declared ``mineru_config_catalog``, never from this constant list."""

__all__ = [
    "MINERU_BACKEND_CHAIN",
    "MINERU_BACKEND_DEFAULT",
    "MINERU_BACKEND_LABELS",
    "MINERU_EFFORT_DEFAULT",
    "MINERU_EFFORT_LABELS",
    "MINERU_TIER_CHAIN",
]
