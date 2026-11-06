"""Recognition model sources and read-only expansion of historical preferences."""

from __future__ import annotations

from collections.abc import Sequence
from typing import Any

from vibeocr.runtime_contracts import ErrorCode

# 历史契约值：package_index 源 kind 已随在线依赖安装退役，仅保留常量
# 供旧 payload 的读取识别，不再进入目录或有效选择。
DOWNLOAD_SOURCE_KIND_PACKAGE_INDEX = "package_index"
DOWNLOAD_SOURCE_KIND_MODEL_REGISTRY = "model_registry"
DOWNLOAD_SOURCE_KIND_PADDLEOCR_MODEL_REGISTRY = "paddleocr_model_registry"
DOWNLOAD_SOURCE_KIND_MINERU_MODEL_REGISTRY = "mineru_model_registry"

# Backend release 声明的候选源。依赖全部随产品离线分发后目录只剩模型
# 源：选择只投影到 PaddleX/MinerU 官方环境值，上游原生 downloader 继续
# 拥有模型下载与文件生命周期。历史 package_index 源 id（tuna-pypi/pypi）
# 与共享 model_registry 条目不再出现在目录中，只作为旧计划/意图/设置
# 的读取兼容在解析点过滤或展开，默认模型源为 ModelScope。
_DOWNLOAD_SOURCES: tuple[dict[str, str], ...] = (
    {
        "kind": DOWNLOAD_SOURCE_KIND_MODEL_REGISTRY,
        "id": "huggingface",
        "endpoint": "https://huggingface.co",
    },
    {
        "kind": DOWNLOAD_SOURCE_KIND_MODEL_REGISTRY,
        "id": "modelscope",
        "endpoint": "https://www.modelscope.cn",
    },
)
_DOWNLOAD_SOURCES += tuple(
    {
        "kind": f"{engine}_model_registry",
        "id": f"{engine}-{source}",
        "endpoint": endpoint,
    }
    for engine, source, endpoint in (
        ("paddleocr", "huggingface", "https://huggingface.co"),
        ("paddleocr", "modelscope", "https://www.modelscope.cn"),
        ("paddleocr", "bos", "https://paddle-model-ecology.bj.bcebos.com"),
        ("mineru", "huggingface", "https://huggingface.co"),
        ("mineru", "modelscope", "https://www.modelscope.cn"),
    )
)
# 历史 package index id：目录已退役，读取路径静默忽略。
_RETIRED_PACKAGE_SOURCE_IDS = frozenset({"tuna-pypi", "pypi"})
_DEFAULT_DOWNLOAD_SOURCE_IDS: tuple[str, ...] = ()
_DEFAULT_MODEL_SOURCE_IDS = ("paddleocr-modelscope", "mineru-modelscope")

# 展示名映射：目录 payload 仍只携带 kind/id/endpoint（wire 契约不变），
# 管理通道的名称投影由此处单点提供，避免各端重复硬编码。
_DOWNLOAD_SOURCE_DISPLAY_NAMES: dict[str, str] = {
    "huggingface": "Hugging Face",
    "modelscope": "ModelScope",
}

_MODEL_SOURCE_ENVIRONMENT: dict[str, dict[str, str]] = {
    "huggingface": {
        "PADDLE_PDX_MODEL_SOURCE": "huggingface",
        "MINERU_MODEL_SOURCE": "huggingface",
    },
    "modelscope": {
        "PADDLE_PDX_MODEL_SOURCE": "modelscope",
        "MINERU_MODEL_SOURCE": "modelscope",
    },
}


for _source in _DOWNLOAD_SOURCES:
    if _source["kind"] in {
        DOWNLOAD_SOURCE_KIND_PADDLEOCR_MODEL_REGISTRY,
        DOWNLOAD_SOURCE_KIND_MINERU_MODEL_REGISTRY,
    }:
        _engine, _provider = _source["id"].split("-", 1)
        _MODEL_SOURCE_ENVIRONMENT[_source["id"]] = {
            "PADDLE_PDX_MODEL_SOURCE"
            if _engine == "paddleocr"
            else "MINERU_MODEL_SOURCE": _provider
        }
        _DOWNLOAD_SOURCE_DISPLAY_NAMES[_source["id"]] = {
            "huggingface": "Hugging Face",
            "modelscope": "ModelScope",
            "bos": "百度 BOS",
        }[_provider]


def model_source_environment(source_ids: Sequence[str]) -> dict[str, str]:
    """Return selected model providers, using the product defaults for omitted engines."""
    environment: dict[str, str] = {}
    for source_id in (*_DEFAULT_MODEL_SOURCE_IDS, *source_ids):
        environment.update(_MODEL_SOURCE_ENVIRONMENT.get(source_id, {}))
    return environment


def download_source_provider(source_id: str) -> str | None:
    """模型提供方来自 Runtime 适配表，消费者不解析来源标识。"""
    values = set(_MODEL_SOURCE_ENVIRONMENT.get(source_id, {}).values())
    return next(iter(values)) if len(values) == 1 else None


def expand_legacy_model_sources(source_ids: Sequence[str]) -> tuple[str, ...]:
    """Expand historical shared preferences and drop retired package indexes.

    历史 package_index 源 id（tuna-pypi/pypi）已随在线依赖安装退役：读取
    旧计划/意图/设置时静默忽略；共享 huggingface/modelscope 偏好按原有
    规则展开，显式引擎偏好优先。
    """
    result = [
        source_id
        for source_id in source_ids
        if source_id not in {"huggingface", "modelscope"}
        and source_id not in _RETIRED_PACKAGE_SOURCE_IDS
    ]
    for source_id in source_ids:
        if source_id in {"huggingface", "modelscope"}:
            for engine in ("paddleocr", "mineru"):
                engine_ids = {
                    source["id"]
                    for source in _DOWNLOAD_SOURCES
                    if source["kind"] == f"{engine}_model_registry"
                }
                if not engine_ids.intersection(result):
                    result.append(f"{engine}-{source_id}")
    return tuple(result)


class RuntimeSelectionError(ValueError):
    """Raised when a selection cannot be normalized against the catalogs."""

    def __init__(self, code: ErrorCode, reason: str) -> None:
        super().__init__(reason)
        self.code = code
        self.reason = reason


def default_download_sources(
    *, include_model_sources: bool = True
) -> tuple[dict[str, str], ...]:
    """目录内当前缺省源：依赖索引源已退役，缺省即双引擎模型源。"""
    defaults = set(_DEFAULT_DOWNLOAD_SOURCE_IDS)
    if include_model_sources:
        defaults.update(_DEFAULT_MODEL_SOURCE_IDS)
    return tuple(source for source in _DOWNLOAD_SOURCES if source["id"] in defaults)


def download_source_display_name(source_id: str) -> str:
    """目录内源 id 的公开展示名；目录外 id 原样返回交由上层标注未知。"""
    return _DOWNLOAD_SOURCE_DISPLAY_NAMES.get(source_id, source_id)


def sanitize_download_endpoint(endpoint: str) -> str:
    """Endpoint 只保留 scheme://host[:port]/path：去掉 userinfo、query、fragment。

    签名/令牌通常在 query 或 userinfo 中；目录 endpoint 本身不含凭据，
    该投影统一保证即使未来目录携带参数也不会外泄。解析失败（含非法
    端口、非法主机、IPv6 未加括号等）返回空串，由调用方按“未知端点”
    呈现。IPv6 主机重新加回方括号，保持合法 netloc。
    """
    from urllib.parse import urlsplit, urlunsplit

    try:
        parts = urlsplit(endpoint.strip())
        # .port/.hostname 对非法端口/主机抛 ValueError，先取值再拼接。
        port = parts.port
        host = parts.hostname
    except ValueError:
        return ""
    scheme = parts.scheme.lower()
    if scheme not in {"http", "https"} or not host:
        return ""
    host = host.lower()
    host_part = f"[{host}]" if ":" in host else host
    port_part = f":{port}" if port is not None else ""
    return urlunsplit((scheme, f"{host_part}{port_part}", parts.path or "", "", ""))


def download_source_catalog_payload(
    sources: Sequence[dict[str, str]] | None = None,
) -> dict[str, Any]:
    """Build and validate the ``runtime.download-sources.v1`` catalog payload."""
    entries = list(_DOWNLOAD_SOURCES if sources is None else sources)
    seen_ids: set[str] = set()
    for source in entries:
        source_kind = source["kind"]
        if source_kind not in {
            DOWNLOAD_SOURCE_KIND_MODEL_REGISTRY,
            DOWNLOAD_SOURCE_KIND_PADDLEOCR_MODEL_REGISTRY,
            DOWNLOAD_SOURCE_KIND_MINERU_MODEL_REGISTRY,
        }:
            raise RuntimeSelectionError(
                ErrorCode.VALIDATION_ERROR,
                f"unsupported download source kind: {source_kind}",
            )
        source_id = source["id"]
        if source_id not in _MODEL_SOURCE_ENVIRONMENT:
            raise RuntimeSelectionError(
                ErrorCode.VALIDATION_ERROR,
                f"unsupported model registry source id: {source_id}",
            )
        # Protocol 只要求 source id 唯一；目录只携带模型源。
        if source_id in seen_ids:
            raise RuntimeSelectionError(
                ErrorCode.VALIDATION_ERROR,
                f"duplicate download source id: {source_id}",
            )
        seen_ids.add(source_id)
    return {"sources": [dict(source) for source in entries]}


def normalize_download_source_ids(
    requested: Sequence[str] | None,
) -> tuple[str, ...]:
    """规范化源选择：省略/空/仅历史 package 源 → 双引擎模型默认；未知 id、
    同 kind 多选 fail closed；历史 package index id 静默忽略。"""
    catalog = {
        source["id"]: source for source in download_source_catalog_payload()["sources"]
    }
    if requested is not None:
        requested = tuple(
            source_id
            for source_id in requested
            if source_id not in _RETIRED_PACKAGE_SOURCE_IDS
        )
    if not requested:
        return _DEFAULT_MODEL_SOURCE_IDS
    if len(set(requested)) != len(requested):
        raise RuntimeSelectionError(
            ErrorCode.VALIDATION_ERROR,
            "download_source_ids must not contain duplicates",
        )
    resolved_kinds: set[str] = set()
    for source_id in requested:
        source = catalog.get(source_id)
        if source is None:
            raise RuntimeSelectionError(
                ErrorCode.DOWNLOAD_SOURCE_UNKNOWN,
                f"unknown download source id: {source_id}",
            )
        if source["kind"] in resolved_kinds:
            raise RuntimeSelectionError(
                ErrorCode.VALIDATION_ERROR,
                f"multiple download sources for kind: {source['kind']}",
            )
        resolved_kinds.add(source["kind"])
    return tuple(requested)
