"""PaddlePipelineAdapter: the unified ``recognize_many`` seam for Paddle.

Plan §4 Phase 4 goals:

* All Paddle pipelines enter the scheduler through one adapter.
* Single image is a one-element batch (no separate single implementation).
* The capability reports the *honest* real-batch support: only the OCR
  pipeline registers a true ``recognize_batch``; PP-StructureV3 / VL /
  formula / table currently fall back to per-item loops and must not be
  reported as real batch.
* Stable OCRResult / TextBlock / parsing logic is reused unchanged.

This adapter is deliberately thin: it injects the existing
:class:`OCRService`/registry and never re-implements result parsing.
"""

from __future__ import annotations

import contextlib
import io
import logging
import os
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import TYPE_CHECKING, Any, Protocol

import numpy as np
from vibeocr.runtime.jobs.budgets import AdapterCapability, ComputeBatch, InputItem
from vibeocr.runtime.recognition.ocr_engines import (
    REASON_ENGINE_INIT_FAILED,
    REASON_ENGINE_NOT_INSTALLED,
    EngineAvailability,
    EngineDescriptor,
)
from vibeocr.runtime_contracts import (
    PipelineSelection,
    ResidencyEntry,
    ResidencyKind,
    ResidencyStatus,
    SettingsSnapshot,
)
from vibeocr.runtime_contracts.dtos import OcrEngine

logger = logging.getLogger(__name__)

if TYPE_CHECKING:
    from vibeocr.runtime.recognition.result_assets import ResultAssetSink

# PaddleOCR ONNX 属于用户显式准备的重型闭包；#270 后组件 id 只指向
# Paddle ONNX 环境（原生 paddleocr-cpu/cuda 已退役）。
PADDLE_REQUIRED_COMPONENT = (
    "paddleocr-ort-cuda"
    if os.environ.get("VIBEOCR_RUNTIME_ACCELERATOR") == "nvidia_cuda"
    else "paddleocr-ort-cpu"
)


class _OCRServiceLike(Protocol):
    """Minimal slice of :class:`OCRService` we depend on."""

    def recognize_batch(
        self,
        images: list[Any],
        options: Any | None = ...,
        asset_sinks: list[Any] | None = ...,
    ) -> list[Any]: ...

    def preload_pipelines_sequential(self, pipelines: list[Any]) -> dict[str, bool]: ...


@dataclass
class PaddlePipelineAdapter:
    """Wraps an OCRService-like object behind ``recognize_many``.

    同时以 ``engine_id=paddleocr`` 实现 GeneralTextOcrEngine 契约；
    其他保留 Paddle 管道（表格/版面）继续经本 adapter 的
    ``pipeline_name`` 路由，不经过 engine resolver。
    """

    service: _OCRServiceLike
    pipeline_name: str = "OCR"
    # Capability is cached per semantic pipeline, never globally.
    _capabilities: dict[str, AdapterCapability] = field(default_factory=dict)
    _settings: SettingsSnapshot = field(default_factory=SettingsSnapshot)

    engine_id = OcrEngine.PADDLEOCR
    # Paddle CPU/DirectML 随基础运行时离线发布；CUDA 环境与大型模型按需准备。
    included_in_base = True

    # ------------------------------------------------------------------
    # Engine contract
    # ------------------------------------------------------------------

    def descriptor(self) -> EngineDescriptor:
        return self._probe_descriptor()

    @staticmethod
    def _probe_descriptor() -> EngineDescriptor:
        """#270：只探测 Paddle ONNX 后端可用性，不再导入原生 Paddle。

        非 ORT 环境直接报告待准备（指向对应 Paddle ONNX 组件），不触发
        原生探测；模型加载不发生。
        """
        from vibeocr.runtime.recognition.paddle_onnx import is_ort_environment

        if not is_ort_environment():
            return EngineDescriptor(
                engine_id=OcrEngine.PADDLEOCR,
                availability=EngineAvailability.PREPARATION_REQUIRED,
                included_in_base=True,
                reason_code=REASON_ENGINE_NOT_INSTALLED,
                required_component=PADDLE_REQUIRED_COMPONENT,
            )
        try:
            __import__("onnxruntime")
        except ImportError:
            return EngineDescriptor(
                engine_id=OcrEngine.PADDLEOCR,
                availability=EngineAvailability.PREPARATION_REQUIRED,
                included_in_base=True,
                reason_code=REASON_ENGINE_NOT_INSTALLED,
                required_component=PADDLE_REQUIRED_COMPONENT,
            )
        except Exception:
            logger.exception("[Supervisor][PaddleOCR] probe import failed")
            return EngineDescriptor(
                engine_id=OcrEngine.PADDLEOCR,
                availability=EngineAvailability.UNAVAILABLE,
                included_in_base=True,
                reason_code=REASON_ENGINE_INIT_FAILED,
            )
        return EngineDescriptor(
            engine_id=OcrEngine.PADDLEOCR,
            availability=EngineAvailability.READY,
            included_in_base=True,
        )

    # ------------------------------------------------------------------
    # Capability
    # ------------------------------------------------------------------

    def capabilities(
        self, options: PipelineSelection | None = None
    ) -> AdapterCapability:
        pipeline_name = (
            options.pipeline_id
            if isinstance(options, PipelineSelection)
            else self.pipeline_name
        )
        cached = self._capabilities.get(pipeline_name)
        if cached is not None:
            return cached
        real_batch = self._pipeline_supports_real_batch(pipeline_name)
        max_compute = 8 if real_batch else 1
        capability = AdapterCapability(
            name=pipeline_name,
            real_batch=real_batch,
            max_compute_batch=max_compute,
        )
        self._capabilities[pipeline_name] = capability
        return capability

    def _pipeline_supports_real_batch(self, pipeline_name: str) -> bool:
        """Return True only if the registry registers a real batch adapter."""
        try:
            from vibeocr.runtime.recognition.core.pipelines import (
                get_registry,  # type: ignore
            )

            registry = get_registry()
            spec = registry.get(pipeline_name) if registry.has(pipeline_name) else None
            return bool(
                spec is not None and getattr(spec, "recognize_batch", None) is not None
            )
        except Exception:
            # In test environments without the pipeline registry we report
            # conservatively: not a real batch.
            return False

    # ------------------------------------------------------------------
    # recognize_many — the unified entry point
    # ------------------------------------------------------------------

    def recognize_many(
        self,
        items: list[InputItem],
        *,
        options: Any | None = None,
        compute_batch: ComputeBatch | None = None,
    ) -> list[dict[str, Any]]:
        """Recognise a list of staged inputs.

        ``items`` must carry raw image bytes in ``encoded_bytes``-style
        payloads (we read bytes via the item's ``data`` attribute if present,
        else assume the caller pre-decoded). Returns one result dict per
        input, in identical order. Single-image callers pass a one-element
        list; there is no separate single path.
        """
        if not items:
            return []
        pipeline_name = (
            options.pipeline_id
            if isinstance(options, PipelineSelection)
            else self.pipeline_name
        )
        started = time.perf_counter()
        try:
            images = [self._to_ndarray(self._raw_bytes(it)) for it in items]
            service_options = self._service_options(options)
            asset_sinks = self._build_asset_sinks(items)
            manager = self._physical_cache_manager()
            lease = (
                manager.lease(pipeline_name)
                if manager is not None and hasattr(manager, "lease")
                else contextlib.nullcontext()
            )
            with lease:
                results = self.service.recognize_batch(
                    images, service_options, asset_sinks=asset_sinks
                )
            payloads = self._payloads_with_reference_images(
                results,
                asset_sinks,
                unwarping=bool(getattr(service_options, "use_doc_unwarping", False)),
            )
        except Exception:
            logger.exception(
                "[Supervisor][Recognize] action=recognize pipeline=%s "
                "items=%d result=failed elapsed_ms=%.1f",
                pipeline_name,
                len(items),
                (time.perf_counter() - started) * 1000,
            )
            raise
        logger.info(
            "[Supervisor][Recognize] action=recognize pipeline=%s "
            "items=%d result=success elapsed_ms=%.1f",
            pipeline_name,
            len(items),
            (time.perf_counter() - started) * 1000,
        )
        return payloads

    def _service_options(self, options: Any | None) -> Any | None:
        if not isinstance(options, PipelineSelection):
            return options
        # Temporary backend boundary conversion while the stable pipeline
        # option implementation now lives in the Backend package. The wire
        # DTO itself remains contracts-owned and strictly validated.
        from vibeocr.runtime.recognition.models.ocr_options import OCROptions

        return OCROptions.from_dict(
            {"pipeline": options.pipeline_id, **options.options}
        )

    @staticmethod
    def _build_asset_sinks(items: list[InputItem]) -> list[Any] | None:
        """为携带受控资产目录的 item 构造结果资产写入器。

        目录由 executor 从 stager 受信路径派生并经内部 IPC 传入；无目录的
        item 得到 None sink（管线标记 available=false，不落盘）。同一批
        共享一个 job 级字节预算。
        """
        from vibeocr.runtime.recognition.result_assets import (
            ResultAssetBudget,
            ResultAssetSink,
        )

        asset_items = [item for item in items if getattr(item, "asset_dir", "")]
        if not asset_items:
            return None
        budget = ResultAssetBudget(asset_dir=Path(asset_items[0].asset_dir))
        return [
            ResultAssetSink(item.asset_dir, item.item_id, budget)
            if getattr(item, "asset_dir", "")
            else None
            for item in items
        ]

    # ------------------------------------------------------------------
    # Residency passthrough
    # ------------------------------------------------------------------

    def preload(self, pipelines: tuple[str, ...]) -> ResidencyStatus:
        """顺序加载本 adapter 拥有的 Paddle 管道并返回真实驻留快照。"""
        from vibeocr.runtime.recognition.core.pipelines import (
            get_paddle_residency_pipelines,
        )
        from vibeocr.runtime.recognition.pipeline_contracts import OCRPipeline

        paddle_pipelines = set(get_paddle_residency_pipelines())
        selected = [
            pipeline
            for name in pipelines
            if (pipeline := OCRPipeline(name)) in paddle_pipelines
        ]
        if selected:
            started = time.perf_counter()
            try:
                results = self.service.preload_pipelines_sequential(selected)
            except Exception:
                elapsed_ms = (time.perf_counter() - started) * 1000
                for pipeline in selected:
                    logger.exception(
                        "[Supervisor][Preload] action=preload pipeline=%s "
                        "result=failed elapsed_ms=%.1f",
                        pipeline.value,
                        elapsed_ms,
                    )
                raise
            elapsed_ms = (time.perf_counter() - started) * 1000
            for pipeline in selected:
                succeeded = bool(results.get(pipeline.value, False))
                log = logger.info if succeeded else logger.error
                log(
                    "[Supervisor][Preload] action=preload pipeline=%s "
                    "result=%s elapsed_ms=%.1f",
                    pipeline.value,
                    "success" if succeeded else "failed",
                    elapsed_ms,
                )
            failed = [
                pipeline.value
                for pipeline in selected
                if not results.get(pipeline.value, False)
            ]
            if failed:
                raise RuntimeError(f"管道预加载失败: {', '.join(failed)}")
        return self.residency_status()

    def residency_status(self) -> ResidencyStatus:
        manager = self._physical_cache_manager()
        if manager is None or not hasattr(manager, "status"):
            return ResidencyStatus(
                default_ttl_seconds=self._settings.default_ttl_seconds,
                pipelines=self._settings.pipelines,
            )
        raw = manager.status()
        loaded = raw.get("loaded_pipelines", [])
        ttls = raw.get("pipeline_ttls", {})
        active = raw.get("active_counts", {})
        pinned = set(raw.get("pinned_pipelines", []))
        last_used = raw.get("last_used_unix_ms", {})
        now_ms = int(time.time() * 1000)
        entries = []
        for name in loaded:
            ttl = int(ttls.get(name, self._settings.default_ttl_seconds))
            remaining = None
            if name not in pinned and ttl > 0:
                used_ms = int(last_used.get(name, now_ms))
                remaining = max(0, ttl - max(0, now_ms - used_ms) // 1000)
            entries.append(
                ResidencyEntry(
                    pipeline=str(name),
                    kind=(
                        ResidencyKind.PINNED
                        if name in pinned
                        else ResidencyKind.SOFT_TTL
                    ),
                    active_leases=int(active.get(name, 0)),
                    remaining_ttl_seconds=remaining,
                )
            )
        return ResidencyStatus(
            default_ttl_seconds=self._settings.default_ttl_seconds,
            entries=tuple(entries),
            pipelines=self._settings.pipelines,
        )

    def release_idle(self, pipeline: str | None = None) -> ResidencyStatus:
        manager = self._physical_cache_manager()
        if manager is not None:
            if pipeline is None:
                manager.release(heavy_only=False)
            else:
                manager.release_one(pipeline)
        return self.residency_status()

    def configure_settings(self, snapshot: SettingsSnapshot) -> None:
        self._settings = snapshot

    def close(self) -> None:
        manager = self._physical_cache_manager()
        if manager is None:
            return
        manager.release(heavy_only=False, force=True)
        manager.shutdown()

    def _physical_cache_manager(self) -> Any | None:
        try:
            return self.service.cache_manager  # type: ignore[attr-defined]
        except Exception:
            return None

    # ------------------------------------------------------------------
    # Helpers
    # ------------------------------------------------------------------

    @staticmethod
    def _raw_bytes(item: InputItem) -> bytes:
        data = getattr(item, "data", b"")
        if isinstance(data, (bytes, bytearray)) and len(data) > 0:
            return bytes(data)
        raise ValueError(
            f"InputItem {item.item_id} has no raw bytes; decode before calling recognize_many"
        )

    @staticmethod
    def _to_ndarray(raw: bytes) -> np.ndarray:
        from PIL import Image as PILImage

        img = PILImage.open(io.BytesIO(raw))
        if img.mode != "RGB":
            img = img.convert("RGB")
        return np.array(img)

    @classmethod
    def _payloads_with_reference_images(
        cls,
        results: list[Any],
        asset_sinks: list[ResultAssetSink | None] | None,
        *,
        unwarping: bool,
    ) -> list[dict[str, Any]]:
        """序列化结果并按需发布顶层 ``reference_image``（见发布函数）。"""
        payloads: list[dict[str, Any]] = []
        for index, result in enumerate(results):
            payload = cls._result_to_payload(result)
            sink = (
                asset_sinks[index]
                if asset_sinks is not None and index < len(asset_sinks)
                else None
            )
            cls._publish_reference_image(payload, result, sink, unwarping=unwarping)
            payloads.append(payload)
        return payloads

    @staticmethod
    def _has_overlay_bbox(blocks: object) -> bool:
        """桌面 overlay 消费的最小校验：非空文本 + 4 个有限数 + 正面积。"""
        import math

        if not isinstance(blocks, list):
            return False
        for block in blocks:
            if not isinstance(block, dict):
                continue
            text = block.get("text")
            bbox = block.get("bbox")
            if not (isinstance(text, str) and text.strip()):
                continue
            if not isinstance(bbox, list) or len(bbox) != 4:
                continue
            if not all(
                isinstance(value, (int, float))
                and not isinstance(value, bool)
                and math.isfinite(value)
                for value in bbox
            ):
                continue
            x0, y0, x1, y1 = bbox
            if x1 > x0 and y1 > y0:
                return True
        return False

    @staticmethod
    def _publish_reference_image(
        payload: dict[str, Any],
        result: Any,
        sink: ResultAssetSink | None,
        *,
        unwarping: bool,
    ) -> None:
        """发布纯文本 OCR 的预处理参考图为 payload 顶层 ``reference_image``。

        bbox 在预处理图（旋转/去畸变后）坐标空间归一化，桌面按原图展示
        会错位；这里把 Paddle 真实预处理 PNG 经既有 ``ResultAssetSink``
        发布为 opaque 资产引用（形状与 ``content_list[].image`` 相同），
        不在消费侧猜变换。仅对无 ``content_list`` 且存在可 overlay 行
        bbox 的纯文本结果发布；预处理确实需要（角度非 0、预处理图尺寸
        已知，或本次开启 use_doc_unwarping——去畸变在角度 0 时也改变
        几何，该推导只在此处做）而图缺失/超预算/保存失败时写
        ``{available: false, reason}``，禁止原图 fallback；普通无预处理
        结果省略该字段。
        """
        if payload.get("content_list"):
            return
        if not PaddlePipelineAdapter._has_overlay_bbox(payload.get("text_blocks")):
            return
        from vibeocr.runtime.recognition.result_assets import (
            REASON_ENCODE_FAILED,
            REASON_SOURCE_UNAVAILABLE,
            REASON_STORE_UNAVAILABLE,
            unavailable_asset,
        )

        preprocessed = getattr(result, "preprocessed_image", None)
        if not isinstance(preprocessed, (bytes, bytearray)) or not preprocessed:
            angle = int(getattr(result, "preproc_angle", 0) or 0)
            width = int(getattr(result, "preproc_img_w", 0) or 0)
            height = int(getattr(result, "preproc_img_h", 0) or 0)
            if unwarping or angle != 0 or (width > 0 and height > 0):
                payload["reference_image"] = unavailable_asset(
                    REASON_SOURCE_UNAVAILABLE
                )
            return
        if sink is None:
            payload["reference_image"] = unavailable_asset(REASON_STORE_UNAVAILABLE)
            return
        from PIL import Image as PILImage

        # preprocessed_image 已是 PNG bytes；_encode_png 只接 PIL/ndarray，
        # 解码后复用 sink.save（预算/上限/落盘失败语义一致）。
        try:
            with PILImage.open(io.BytesIO(bytes(preprocessed))) as image:
                reference = image.copy()
        except Exception:
            payload["reference_image"] = unavailable_asset(REASON_ENCODE_FAILED)
            return
        payload["reference_image"] = sink.save(reference)

    @staticmethod
    def _result_to_payload(result: Any) -> dict[str, Any]:
        """Convert an OCRResult to a JSON-native payload.

        Delegates to :func:`vibeocr.runtime.recognition.models.ocr_result_serializer.ocr_result_to_payload`,
        which is the single source of truth for the wire shape and produces the
        key set consumed by downstream callers (``text_blocks``/``preproc_angle``
        for PDF text-layer writeback, ``raw_text``/``markdown_text``/
        ``html_text``/``content_list`` for export). ``dict`` inputs pass through
        unchanged so test fakes keep working.
        """
        # Imported lazily: the serializer lives in Backend domain models,
        # which the backend depends on via wheel. Lazy import keeps this module
        # importable in minimal test environments that monkeypatch the adapter.
        from vibeocr.runtime.recognition.models import ocr_result_to_payload

        return ocr_result_to_payload(result)


__all__ = ["PaddlePipelineAdapter"]
