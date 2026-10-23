"""RapidTable 引擎 adapter：RAPID_TABLE 管道的轻量表格结构识别。

约束（与 RapidOCR 引擎同族）：

* 惰性初始化；模型不随 wheel 携带（rapid-table 3.0.2 wheel 仅 43KB、
  无 onnx），解析顺序：``VIBEOCR_RAPIDTABLE_MODEL`` → 共享模型缓存
  固定布局 → base pack 模型目录；皆缺时 availability=
  ``preparation_required / model_not_prepared``，绝不识别期下载或写
  site-packages。
* 设备选项 device=auto/cpu/cuda：cuda 必须 CUDA 真实就绪（见
  :mod:`vibeocr.runtime.recognition.rapid_devices`），否则 fail closed，
  错误复用 OcrEngineError 边界映射为 per-item 协议错误码。上游
  ``use_cuda`` 在 provider 缺失时仅 warning 后退 CPU，本 adapter 在
  构造前后双重拦截，绝不静默降级。
* 输出与 TABLE_RECOGNITION（paddle）payload 同形：``ocr.v1`` +
  ``content_list[]`` ``type:"table"`` 块（table.v1 / table_body /
  projections），``pipeline_type`` 固定 ``"TABLE_RECOGNITION"``
  保持下游（导出/编辑/桌面）形状兼容。
"""

from __future__ import annotations

import io
import logging
import os
import threading
from pathlib import Path
from typing import Any

from vibeocr.runtime.jobs.budgets import AdapterCapability
from vibeocr.runtime.recognition.ocr_engines import OcrEngineError
from vibeocr.runtime.recognition.rapid_devices import (
    activate_cuda_sessions,
    cuda_provider_ready,
)
from vibeocr.runtime_contracts import ErrorCode

logger = logging.getLogger(__name__)

RAPID_TABLE_PIPELINE_ID = "RAPID_TABLE"
REASON_ENGINE_INIT_FAILED = "engine_init_failed"
REASON_ENGINE_NOT_INSTALLED = "engine_not_installed"
REASON_MODEL_NOT_PREPARED = "model_not_prepared"
REASON_DEVICE_PROVIDER_UNAVAILABLE = "device_provider_unavailable"
DEVICE_VALUES = ("auto", "cpu", "cuda")

_MODEL_NAME = "slanet-plus.onnx"
_NAMESPACE = "rapidtable-3.0.2"
_MODEL_CACHE_REL = Path("modelscope") / "RapidAI" / "RapidTable" / _MODEL_NAME
_PACK_MODELS_REL = Path("models") / "rapidtable" / _MODEL_NAME


def _device_error(
    reason_code: str, detail: dict[str, Any] | None = None
) -> OcrEngineError:
    """确定性设备失败：复用引擎选择错误边界，executor 将其映射为
    per-item 协议错误码，不进入恢复重试。"""
    return OcrEngineError(
        ErrorCode.OCR_ENGINE_UNAVAILABLE,
        reason_code=reason_code,
        engine="rapidtable",
        detail=detail or {},
    )


def resolve_model_path() -> Path | None:
    """模型解析：env → 共享缓存布局 → base pack 模型目录。只读探测。"""
    env_path = os.environ.get("VIBEOCR_RAPIDTABLE_MODEL", "").strip()
    if env_path:
        candidate = Path(env_path)
        return candidate if candidate.is_file() else None
    shared = os.environ.get("VIBEOCR_SHARED_MODEL_CACHE", "").strip()
    if shared:
        candidate = Path(shared) / _NAMESPACE / _MODEL_CACHE_REL
        if candidate.is_file():
            return candidate
    pack_root = os.environ.get("VIBEOCR_RUNTIME_ROOT", "").strip()
    if pack_root:
        candidate = Path(pack_root) / _PACK_MODELS_REL
        if candidate.is_file():
            return candidate
    return None


def _device_from_selection(options: Any | None) -> str:
    """None→auto；非法值 fail closed（wire 端由 parser enum 拒绝，
    引擎层第二道门）；cuda 未就绪同样 fail closed。"""
    raw = (getattr(options, "options", None) or {}).get("device")
    if raw is None:
        device = "auto"
    elif raw in DEVICE_VALUES:
        device = raw
    else:
        raise _device_error("device_value_invalid", {"device": str(raw)})
    if device == "cuda" and not cuda_provider_ready():
        raise _device_error(REASON_DEVICE_PROVIDER_UNAVAILABLE, {"device": "cuda"})
    if device == "auto":
        return "cuda" if cuda_provider_ready() else "cpu"
    return device


class RapidTableEngine:
    """RAPID_TABLE 管道引擎（线程安全、惰性加载、单实例保序）。"""

    included_in_base = True

    def __init__(self) -> None:
        self._engine: Any = None
        self._engine_device: str | None = None
        self._init_error: str | None = None
        self._lock = threading.Lock()

    # ------------------------------------------------------------------
    # Descriptor / capability
    # ------------------------------------------------------------------

    def probe_reason(self) -> str | None:
        """可用性探针：就绪返回 None，否则返回稳定 reason_code。

        RAPID_TABLE 不进入 ocr.engine-selection.v1 目录（engine-only-OCR
        不变量），探针结果只驱动 recognition mode availability。
        """
        if self._init_error is not None:
            return REASON_ENGINE_INIT_FAILED
        try:
            __import__("rapid_table")
        except ImportError:
            return REASON_ENGINE_NOT_INSTALLED
        except Exception:
            logger.exception("[Supervisor][RapidTable] probe import failed")
            return REASON_ENGINE_INIT_FAILED
        if resolve_model_path() is None:
            return REASON_MODEL_NOT_PREPARED
        return None

    def capabilities(self, options: Any | None = None) -> AdapterCapability:
        """保守能力：逐图推理；不抛错（executor 的 capabilities 调用点只容
        AttributeError/TypeError，设备 fail-closed 在 recognize_many 抛
        结构化 OcrEngineError）。"""
        return AdapterCapability(
            name=RAPID_TABLE_PIPELINE_ID,
            real_batch=False,
            max_compute_batch=1,
        )

    # ------------------------------------------------------------------
    # recognize_many
    # ------------------------------------------------------------------

    def recognize_many(
        self,
        items: list[Any],
        *,
        options: Any | None = None,
        compute_batch: Any | None = None,
    ) -> list[dict[str, Any]]:
        del compute_batch
        if not items:
            return []
        device = _device_from_selection(options)
        engine = self._ensure_engine(device)
        payloads: list[dict[str, Any]] = []
        for item in items:
            image = self._to_ndarray(self._raw_bytes(item))
            payloads.append(self._recognize_one(engine, image))
        return payloads

    def _ensure_engine(self, device: str) -> Any:
        with self._lock:
            if self._engine is not None and self._engine_device == device:
                return self._engine
            if self._init_error is not None:
                raise RuntimeError(
                    f"RapidTable engine failed to init: {self._init_error}"
                )
            try:
                from rapid_table import RapidTable, RapidTableInput

                model_path = resolve_model_path()
                if model_path is None:
                    raise RuntimeError("RapidTable model is not prepared")
                cpu_count = os.cpu_count() or 1
                intra = max(1, min(8, cpu_count))
                engine_cfg: dict[str, Any] = {
                    "intra_op_num_threads": intra,
                    "inter_op_num_threads": 1,
                }
                ocr_params: dict[str, Any] = {
                    "EngineConfig.onnxruntime.intra_op_num_threads": intra,
                    "EngineConfig.onnxruntime.inter_op_num_threads": 1,
                }
                if device == "cuda":
                    # 已验证插件路径（见 rapid_devices）：CPU 构造后对全部原生
                    # session set_providers 并核实；激活失败不缓存降级实例，
                    # 下次重试重新构造+激活。
                    if not cuda_provider_ready():
                        raise _device_error(
                            REASON_DEVICE_PROVIDER_UNAVAILABLE,
                            {"device": "cuda"},
                        )
                candidate = RapidTable(
                    RapidTableInput(
                        model_dir_or_path=str(model_path),
                        engine_cfg=engine_cfg,
                        ocr_params=ocr_params,
                    )
                )
            except OcrEngineError:
                raise
            except Exception as exc:
                self._init_error = str(exc)
                logger.exception("[Supervisor][RapidTable] engine init failed")
                raise RuntimeError(f"RapidTable engine init failed: {exc}") from exc
            # 设备激活失败可重试，也不能阻断下一次 CPU 识别。
            if device == "cuda":
                activate_cuda_sessions(candidate)
            self._engine = candidate
            self._engine_device = device
            logger.info("[Supervisor][RapidTable] engine initialized device=%s", device)
            return self._engine

    def _recognize_one(self, engine: Any, image: Any) -> dict[str, Any]:
        try:
            result = engine(image)
        except Exception:
            logger.exception("[Supervisor][RapidTable] recognize failed")
            raise
        return self._result_to_payload(result, image)

    # ------------------------------------------------------------------
    # Result mapping（与 pipeline_table 同形）
    # ------------------------------------------------------------------

    @staticmethod
    def _result_to_payload(result: Any, image: Any) -> dict[str, Any]:
        from dataclasses import replace

        from vibeocr.runtime.documents.tables.blocks import (
            canonicalize_table_block,
            table_model_from_block,
        )
        from vibeocr.runtime.documents.tables.contracts import (
            CoordinateSpace,
            TableProvenanceV1,
        )
        from vibeocr.runtime.documents.tables.projections import (
            table_model_to_markdown,
            table_model_to_plain_text,
        )
        from vibeocr.runtime.documents.tables.reducer import rebuild_result_projections
        from vibeocr.runtime.recognition.models import ocr_result_to_payload
        from vibeocr.runtime.recognition.models.ocr_result import OCRResult, TextBlock

        height, width = (
            (image.shape[0], image.shape[1]) if image is not None else (0, 0)
        )
        pred_htmls = list(getattr(result, "pred_htmls", None) or ())
        cell_bboxes = list(getattr(result, "cell_bboxes", None) or ())
        content_list: list[dict[str, Any]] = []
        text_blocks: list[TextBlock] = []
        text_with_scores: list[tuple[str, Any]] = []
        markdown_parts: list[str] = []

        for idx, html_text in enumerate(pred_htmls):
            table_id = f"rapid-table-{idx}"
            canonical_block = canonicalize_table_block(
                {
                    "type": "table",
                    "table_body": html_text,
                    "bbox": None,
                    "source": {
                        "source_html": html_text,
                        "provider_schema": "rapid-table-pred-html",
                    },
                },
                table_id=table_id,
                pipeline=RAPID_TABLE_PIPELINE_ID,
            )
            table_model = table_model_from_block(canonical_block)
            warnings: list[str] = []
            boxes = cell_bboxes[idx] if idx < len(cell_bboxes) else None
            if boxes is not None and len(boxes) == len(table_model.cells):
                enriched = []
                for cell, box in zip(table_model.cells, boxes, strict=True):
                    values = [float(v) for v in box]
                    xs, ys = values[::2], values[1::2]
                    enriched.append(
                        replace(
                            cell,
                            bbox=(min(xs), min(ys), max(xs), max(ys)),
                            source_refs=(f"rapid-table:cell_box:{cell.cell_id}",),
                        )
                    )
                table_model = replace(
                    table_model,
                    cells=tuple(enriched),
                    coordinate_space=CoordinateSpace.PIXEL,
                    provenance=TableProvenanceV1(
                        pipeline=RAPID_TABLE_PIPELINE_ID,
                        provider_schema="rapid-table-pred-html",
                        warnings=tuple(warnings),
                    ),
                )
            else:
                warnings.append(f"{table_id}:cell-box-count-mismatch")
                table_model = replace(
                    table_model,
                    provenance=TableProvenanceV1(
                        pipeline=RAPID_TABLE_PIPELINE_ID,
                        provider_schema="rapid-table-pred-html",
                        warnings=tuple(warnings),
                    ),
                )
            canonical_block["table"] = table_model.to_payload()
            from vibeocr.runtime.documents.tables.html_adapter import (
                table_model_to_html,
            )

            canonical_block["table_body"] = table_model_to_html(table_model)
            plain = table_model_to_plain_text(table_model)
            markdown = table_model_to_markdown(table_model).text
            if markdown:
                markdown_parts.append(markdown)
            cl_idx = len(content_list)
            text_blocks.append(
                TextBlock(
                    text=plain,
                    score=None,
                    bbox=None,
                    label="table",
                    order=idx,
                    content_index=cl_idx,
                    content_id=table_id,
                )
            )
            text_with_scores.append((plain, None))
            content_list.append(canonical_block)

        raw_text = "\n".join(block.text for block in text_blocks)
        ocr_result = OCRResult(
            raw_text=raw_text,
            markdown_text="\n\n".join(markdown_parts) if markdown_parts else raw_text,
            html_text="",
            text_with_scores=text_with_scores,
            # pipeline_type 沿用 TABLE_RECOGNITION：结果形状兼容优先。
            pipeline_type="TABLE_RECOGNITION",
            text_blocks=text_blocks,
            content_list=content_list,
            image_width=int(width),
            image_height=int(height),
        )
        rebuild_result_projections(ocr_result)
        return ocr_result_to_payload(ocr_result)

    # ------------------------------------------------------------------
    # Lifecycle（unmanaged：无 TTL/pin 语义，惰性加载）
    # ------------------------------------------------------------------

    def preload(self, pipelines: tuple[str, ...]) -> Any:
        from vibeocr.runtime_contracts import ResidencyStatus

        if RAPID_TABLE_PIPELINE_ID in pipelines:
            self._ensure_engine("cpu")
        return ResidencyStatus()

    def residency_status(self) -> Any:
        from vibeocr.runtime_contracts import ResidencyStatus

        return ResidencyStatus()

    def release_idle(self, pipeline: str | None = None) -> Any:
        from vibeocr.runtime_contracts import ResidencyStatus

        del pipeline
        with self._lock:
            self._engine = None
            self._engine_device = None
            self._init_error = None
        return ResidencyStatus()

    def configure_settings(self, snapshot: Any) -> None:
        del snapshot

    def close(self) -> None:
        self.release_idle()

    # ------------------------------------------------------------------
    # Helpers
    # ------------------------------------------------------------------

    @staticmethod
    def _raw_bytes(item: Any) -> bytes:
        data = getattr(item, "data", b"")
        if isinstance(data, (bytes, bytearray)) and len(data) > 0:
            return bytes(data)
        raise ValueError(
            f"InputItem {getattr(item, 'item_id', '?')} has no raw bytes; "
            "decode before calling recognize_many"
        )

    @staticmethod
    def _to_ndarray(raw: bytes) -> Any:
        import numpy as np
        from PIL import Image as PILImage

        img = PILImage.open(io.BytesIO(raw))
        if img.mode != "RGB":
            img = img.convert("RGB")
        # RapidTable/内置 RapidOCR 将 ndarray 直接视为 BGR。
        return np.array(img)[:, :, ::-1].copy()


__all__ = [
    "RAPID_TABLE_PIPELINE_ID",
    "REASON_DEVICE_PROVIDER_UNAVAILABLE",
    "REASON_MODEL_NOT_PREPARED",
    "RapidTableEngine",
    "resolve_model_path",
]
