"""RapidOCR 引擎 adapter：全局默认的轻量离线纯文本 OCR。

约束：

* 惰性初始化：首次 ``recognize_many`` 才加载模型；禁用网络下载，
  模型随 base-offline 运行时携带（wheel 内置）。
* 复用进程内单份 ONNX Runtime——本 adapter 不自行携带 ORT 依赖，
  仅通过 rapidocr 使用现有闭包中的 onnxruntime。
* 设备选项（pipeline option ``device``: auto/cpu/cuda）：cuda 必须
  ``CUDAExecutionProvider`` 真实就绪（providers + get_device 双探针），
  否则 fail closed，绝不静默以 CPU 冒充 GPU；auto 仅在真实就绪时用 GPU。
* 批量有界并行：RapidOCR ``__call__`` 内部 ``update_params`` 会修改实例
  cfg/postprocess，共享实例并行不安全——CPU 多图任务使用有界
  ThreadPoolExecutor + 每 worker 独立实例（独立 ORT session），总
  intra_op 预算有界；输出严格保序，异常使整批失败走既有恢复路径。
  依据本仓实测（12 张 1000×700 票据，总 intra 预算 8）：1×8=5.02s、
  2×4=4.07s、4×2=3.68s，不宣称通用加速比。
* 输出映射到稳定 OCRResult payload：text/score/polygon（[0,1000]
  归一化）与阅读顺序，与 Paddle 引擎同构。
"""

from __future__ import annotations

import io
import logging
import os
import threading
from concurrent.futures import ThreadPoolExecutor
from typing import Any, Callable

from vibeocr.runtime.jobs.budgets import AdapterCapability
from vibeocr.runtime.recognition.ocr_engines import (
    OCR_PIPELINE_ID,
    REASON_ENGINE_INIT_FAILED,
    REASON_ENGINE_NOT_INSTALLED,
    EngineAvailability,
    EngineDescriptor,
    OcrEngineError,
)
from vibeocr.runtime.recognition.rapid_devices import (
    activate_cuda_sessions,
    cuda_provider_ready,
)
from vibeocr.runtime_contracts import ErrorCode, ResidencyStatus
from vibeocr.runtime_contracts.dtos import OcrEngine

logger = logging.getLogger(__name__)

REASON_DEVICE_PROVIDER_UNAVAILABLE = "device_provider_unavailable"
DEVICE_VALUES = ("auto", "cpu", "cuda")

# CPU intra 预算与并行 worker 上限（实测 4×2 最优，保守封顶 4 worker）。
_MAX_PARALLEL_WORKERS = 4
_CPU_INTRA_BUDGET = 8


def resolve_device(value: Any, *, engine: str = "rapidocr") -> str:
    """解析 device 选项：None→auto；非法值 fail closed（wire 端由 parser
    enum 拒绝，这里是引擎层第二道门）；cuda 未就绪抛 OcrEngineError。"""
    if value is None:
        device = "auto"
    elif isinstance(value, str) and value in DEVICE_VALUES:
        device = value
    else:
        raise OcrEngineError(
            ErrorCode.VALIDATION_ERROR,
            reason_code="device_value_invalid",
            engine=engine,
            detail={"device": value if isinstance(value, str) else None},
        )
    if device == "cuda" and not cuda_provider_ready():
        raise OcrEngineError(
            ErrorCode.OCR_ENGINE_UNAVAILABLE,
            reason_code=REASON_DEVICE_PROVIDER_UNAVAILABLE,
            engine=engine,
            detail={"device": "cuda"},
        )
    if device == "auto":
        return "cuda" if cuda_provider_ready() else "cpu"
    return device


def device_from_selection(options: Any | None) -> Any:
    """从 PipelineSelection 提取 device 选项原始值（缺省 None）。"""
    if options is None:
        return None
    selection_options = getattr(options, "options", None) or {}
    return selection_options.get("device")


class _WorkerPool:
    """有界并行 worker 池：每 worker 线程私有 RapidOCR 实例。

    实例隔离是硬要求：共享实例的 ``update_params`` 并发不安全。总
    intra_op 预算 = workers × per_worker_intra ≤ min(8, cpu_count)。
    """

    def __init__(
        self,
        *,
        workers: int,
        factory: Callable[[dict[str, Any]], Any],
    ) -> None:
        self._workers = workers
        self._factory = factory
        self._local = threading.local()
        self._executor = ThreadPoolExecutor(
            max_workers=workers, thread_name_prefix="rapidocr-worker"
        )

    @property
    def workers(self) -> int:
        return self._workers

    def _engine_for_thread(self) -> Any:
        engine = getattr(self._local, "engine", None)
        if engine is None:
            cpu_count = os.cpu_count() or 1
            intra = max(1, min(_CPU_INTRA_BUDGET, cpu_count) // self._workers)
            engine = self._factory(
                {
                    "EngineConfig.onnxruntime.intra_op_num_threads": intra,
                    "EngineConfig.onnxruntime.inter_op_num_threads": 1,
                }
            )
            self._local.engine = engine
        return engine

    def map_ordered(self, images: list[Any]) -> list[Any]:
        def _call(image: Any) -> Any:
            # 在 worker 线程内解析 thread-local 实例（共享实例并发不安全）。
            return self._engine_for_thread()(image)

        futures = [self._executor.submit(_call, image) for image in images]
        try:
            return [future.result() for future in futures]
        except BaseException:
            # 异常时先取消未开始的任务，再等在途任务收尾，避免线程泄漏
            # 与后续状态变更竞态；在途异常只用于回收，不覆盖原始异常。
            for future in futures:
                future.cancel()
            for future in futures:
                try:
                    future.result()
                except Exception:
                    pass
            raise

    def close(self) -> None:
        # 等待在途推理收尾后再返回（单图粒度，有界）。
        self._executor.shutdown(wait=True, cancel_futures=True)


class RapidOcrEngine:
    """``GeneralTextOcrEngine`` 的 RapidOCR 实现（线程安全、惰性加载）。"""

    engine_id = OcrEngine.RAPIDOCR
    included_in_base = True

    def __init__(self, *, engine_params: dict[str, Any] | None = None) -> None:
        self._engine_params = dict(engine_params or {})
        # CPU 单实例（串行路径/1 worker 时复用）。
        self._engine: Any = None
        # CUDA 单实例（GPU 不做实例并行：未实测收益，显存共享保守）。
        self._cuda_engine: Any = None
        self._init_error: str | None = None
        self._lock = threading.Lock()
        self._pool: _WorkerPool | None = None
        self._pool_device: str | None = None

    # ------------------------------------------------------------------
    # Descriptor / capability
    # ------------------------------------------------------------------

    def descriptor(self) -> EngineDescriptor:
        probe = self._probe()
        if probe is not None:
            return EngineDescriptor(
                engine_id=self.engine_id,
                availability=EngineAvailability.UNAVAILABLE,
                included_in_base=self.included_in_base,
                reason_code=probe,
            )
        return EngineDescriptor(
            engine_id=self.engine_id,
            availability=EngineAvailability.READY,
            included_in_base=self.included_in_base,
        )

    def _probe(self) -> str | None:
        """返回不可用 reason_code，可用返回 None。"""
        if self._init_error is not None:
            # 已证明初始化失败：保持 unavailable，直到 close() 复位。
            return REASON_ENGINE_INIT_FAILED
        try:
            __import__("rapidocr")
        except ImportError:
            return REASON_ENGINE_NOT_INSTALLED
        except Exception:
            logger.exception("[Supervisor][RapidOCR] probe import failed")
            return REASON_ENGINE_INIT_FAILED
        return None

    def parallel_workers(self) -> int:
        """CPU 并行 worker 数：min(4, max(1, cpu//2))。"""
        cpu_count = os.cpu_count() or 1
        return max(1, min(_MAX_PARALLEL_WORKERS, cpu_count // 2))

    def capabilities(self, options: Any | None = None) -> AdapterCapability:
        """保守推导批量能力；不抛错（executor 的 capabilities 调用点只容
        AttributeError/TypeError，设备 fail-closed 在 recognize_many 抛
        结构化 OcrEngineError 映射为 per-item 错误）。"""
        raw = device_from_selection(options)
        if raw in (None, "auto", "cpu"):
            device = "cuda" if (raw != "cpu" and cuda_provider_ready()) else "cpu"
        else:
            device = "cuda"
        parallel = device == "cpu" and self.parallel_workers() > 1
        return AdapterCapability(
            name=OCR_PIPELINE_ID,
            real_batch=False,
            parallel_compute=parallel,
            max_compute_batch=self.parallel_workers() if parallel else 1,
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
        device = resolve_device(device_from_selection(options))
        images = [self._to_ndarray(self._raw_bytes(item)) for item in items]
        if device == "cpu":
            payloads = self._recognize_cpu(images)
        else:
            payloads = self._recognize_cuda(images)
        return payloads

    def _recognize_cpu(self, images: list[Any]) -> list[dict[str, Any]]:
        workers = self.parallel_workers()
        if workers > 1 and len(images) > 1:
            pool = self._ensure_pool(workers)
            results = pool.map_ordered(images)
        else:
            engine = self._ensure_engine(None)
            results = [self._call_one(engine, image) for image in images]
        return [
            self._result_to_payload(result, image)
            for image, result in zip(images, results, strict=True)
        ]

    def _recognize_cuda(self, images: list[Any]) -> list[dict[str, Any]]:
        engine = self._ensure_engine("cuda")
        results = [self._call_one(engine, image) for image in images]
        return [
            self._result_to_payload(result, image)
            for image, result in zip(images, results, strict=True)
        ]

    @staticmethod
    def _call_one(engine: Any, image: Any) -> Any:
        try:
            return engine(image)
        except Exception:
            logger.exception("[Supervisor][RapidOCR] recognize failed")
            raise

    def _ensure_pool(self, workers: int) -> _WorkerPool:
        with self._lock:
            if self._pool is not None and self._pool_device == "cpu":
                return self._pool
            if self._pool is not None:
                self._pool.close()
            self._pool = _WorkerPool(
                workers=workers, factory=self._build_engine_for_params
            )
            self._pool_device = "cpu"
            return self._pool

    def _build_engine_for_params(self, params: dict[str, Any]) -> Any:
        return self._construct_engine(params)

    def _ensure_engine(self, device: str | None) -> Any:
        with self._lock:
            if device == "cuda":
                if self._cuda_engine is not None:
                    return self._cuda_engine
                if self._init_error is not None:
                    raise RuntimeError(
                        f"RapidOCR engine failed to init: {self._init_error}"
                    )
                # 已验证插件路径（见 rapid_devices 模块 docstring）：CPU
                # 构造后对全部原生 session set_providers 并核实；激活失败
                # 不得缓存降级实例，下次重试重新构造+激活。
                if not cuda_provider_ready():
                    raise OcrEngineError(
                        ErrorCode.OCR_ENGINE_UNAVAILABLE,
                        reason_code=REASON_DEVICE_PROVIDER_UNAVAILABLE,
                        engine="rapidocr",
                        detail={"device": "cuda"},
                    )
                candidate = self._construct_engine(None)
                activate_cuda_sessions(candidate)
                self._cuda_engine = candidate
                return self._cuda_engine
            if self._engine is not None:
                return self._engine
            if self._init_error is not None:
                raise RuntimeError(
                    f"RapidOCR engine failed to init: {self._init_error}"
                )
            self._engine = self._construct_engine(None)
            return self._engine

    def _construct_engine(self, extra_params: dict[str, Any] | None) -> Any:
        from rapidocr import RapidOCR

        engine_params = dict(self._engine_params)
        if not engine_params.get("config_path"):
            cpu_count = os.cpu_count()
            params = dict(engine_params.get("params") or {})
            if cpu_count is not None and cpu_count > 0:
                params.setdefault(
                    "EngineConfig.onnxruntime.intra_op_num_threads",
                    min(_CPU_INTRA_BUDGET, cpu_count),
                )
                params.setdefault("EngineConfig.onnxruntime.inter_op_num_threads", 1)
            if extra_params:
                params.update(extra_params)
            if params:
                engine_params["params"] = params
        try:
            return RapidOCR(**engine_params)
        except Exception as exc:
            self._init_error = str(exc)
            logger.exception("[Supervisor][RapidOCR] engine init failed")
            raise RuntimeError(f"RapidOCR engine init failed: {exc}") from exc

    # ------------------------------------------------------------------
    # Result mapping
    # ------------------------------------------------------------------

    @staticmethod
    def _result_to_payload(result: Any, image: Any) -> dict[str, Any]:
        """把 RapidOCROutput（txts/scores/boxes）映射为稳定 ocr.v1 payload。"""
        from vibeocr.runtime.recognition.models import ocr_result_to_payload
        from vibeocr.runtime.recognition.models.ocr_result import OCRResult, TextBlock

        height, width = (
            (image.shape[0], image.shape[1]) if image is not None else (0, 0)
        )
        raw_txts = getattr(result, "txts", None)
        raw_scores = getattr(result, "scores", None)
        raw_boxes = getattr(result, "boxes", None)
        # numpy 数组的真值判断是 ambiguous：必须显式判 None。
        txts = raw_txts if raw_txts is not None else ()
        scores = raw_scores if raw_scores is not None else ()
        boxes = raw_boxes if raw_boxes is not None else ()

        def _norm_x(value: float) -> float:
            return value / width * 1000 if width else value

        def _norm_y(value: float) -> float:
            return value / height * 1000 if height else value

        blocks: list[TextBlock] = []
        pairs: list[tuple[str, float]] = []
        for index, text in enumerate(txts):
            score = float(scores[index]) if index < len(scores) else 0.0
            box = boxes[index] if index < len(boxes) else None
            polygon: tuple[float, ...] | None = None
            bbox: tuple[float, float, float, float] | None = None
            if box is not None and len(box) > 0:
                xs = [float(point[0]) for point in box]
                ys = [float(point[1]) for point in box]
                # RapidOCR 输出原始像素坐标：显式换算到 [0,1000]，不走
                # normalize_bbox 的范围启发式（小图像像素值会被误判）。
                flat: list[float] = []
                for point in box:
                    flat.append(_norm_x(float(point[0])))
                    flat.append(_norm_y(float(point[1])))
                polygon = tuple(flat)
                bbox = (
                    _norm_x(min(xs)),
                    _norm_y(min(ys)),
                    _norm_x(max(xs)),
                    _norm_y(max(ys)),
                )
            blocks.append(
                TextBlock(
                    text=str(text),
                    score=score,
                    bbox=bbox,
                    polygon=polygon,
                    order=index,
                )
            )
            pairs.append((str(text), score))

        avg = sum(score for _, score in pairs) / len(pairs) if pairs else 0.0
        ocr_result = OCRResult(
            raw_text="\n".join(text for text, _ in pairs),
            markdown_text="\n".join(text for text, _ in pairs),
            html_text="",
            text_with_scores=pairs,
            avg_score=avg,
            low_confidence_items=[pair for pair in pairs if pair[1] < 0.6],
            pipeline_type=OCR_PIPELINE_ID,
            text_blocks=blocks,
            image_width=int(width),
            image_height=int(height),
        )
        return ocr_result_to_payload(ocr_result)

    # ------------------------------------------------------------------
    # Lifecycle / residency（轻量引擎：无 TTL 驻留语义）
    # ------------------------------------------------------------------

    def preload(self, pipelines: tuple[str, ...]) -> ResidencyStatus:
        if OCR_PIPELINE_ID in pipelines:
            self._ensure_engine(None)
        return self.residency_status()

    def residency_status(self) -> ResidencyStatus:
        return ResidencyStatus()

    def release_idle(self, pipeline: str | None = None) -> ResidencyStatus:
        del pipeline
        with self._lock:
            self._engine = None
            self._cuda_engine = None
            self._init_error = None
            if self._pool is not None:
                self._pool.close()
                self._pool = None
                self._pool_device = None
        return self.residency_status()

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
        return np.array(img)


__all__ = [
    "DEVICE_VALUES",
    "REASON_DEVICE_PROVIDER_UNAVAILABLE",
    "RapidOcrEngine",
    "device_from_selection",
    "resolve_device",
]
