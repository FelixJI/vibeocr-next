"""PaddleX OCR 服务"""

from __future__ import annotations

import io
import logging
import os
import threading
from enum import Enum
from typing import TYPE_CHECKING, Any, cast

from vibeocr.runtime.recognition.core.pipelines import OCRPipeline
from vibeocr.runtime.recognition.core.singleton_meta import SingletonMeta
from vibeocr.runtime.recognition.models.ocr_options import OCROptions
from vibeocr.runtime.recognition.models.ocr_result import OCRResult, TextBlock

# 重新导出以保持向后兼容性
__all__ = [
    "OCROptions",
    "OCRPipeline",
    "OCRPreset",
    "OCRResult",
    "OCRService",
]

# 跳过模型源网络检测，避免推理时的网络超时开销
os.environ.setdefault("PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK", "True")

# HTML 表格规整化/转换已迁移到 vibeocr.runtime.documents.utils.html_tables（UI 层展示逻辑）。
# 此处重导出以保持向后兼容。
# 注意：所有操作在同一线程中执行（CPU 模式）
# 工作线程设计已确保这一点
from vibeocr.runtime.documents.utils.html_tables import (
    _extract_table_html as _extract_table_html,
)
from vibeocr.runtime.documents.utils.html_tables import (
    _html_table_to_markdown as _html_table_to_markdown,
)
from vibeocr.runtime.documents.utils.html_tables import (
    normalize_table_html as normalize_table_html,
)
from vibeocr.runtime.recognition.pipeline_status import (
    LOCAL_MARKABLE_PIPELINES,
    mark_pipeline_success,
)

_logger = logging.getLogger(__name__)

# 类型检查时导入（不影响运行时）
if TYPE_CHECKING:
    from collections.abc import Callable

    import numpy as np
    from PIL import Image


class OCRPreset(Enum):
    """OCR 预设模式（用于通用 OCR 管道的预处理配置）"""

    GENERAL = "general"  # 通用模式：适用于屏幕截图
    SCANNED = "scanned"  # 扫描件模式：适用于扫描文档/拍照

    @property
    def display_name(self) -> str:
        """获取显示名称"""
        names = {
            OCRPreset.GENERAL: "通用",
            OCRPreset.SCANNED: "扫描件",
        }
        return names.get(self, "通用")


class OCRService(metaclass=SingletonMeta):
    """OCR 识别服务 (使用 SingletonMeta 实现线程安全单例)

    双重角色（请勿误删或拆分）：
    1. **子进程模式（默认运行路径）**：本类在 worker 子进程内实例化
       （见 ``workers/ocr_worker.py``），持有真正的 PaddleOCR pipeline，
       由主进程经共享内存（RCBG 协议）调用其 recognize/recognize_batch。
    2. **主进程直连（仅调试逃生口）**：仅当 ``VIBEOCR_USE_SUBPROCESS=false``
       且 ``VIBEOCR_OCR_MODE=direct`` 时，本类才在主进程内直接加载模型运行，
       用于排查子进程开销/通信问题时定位。**生产环境不应走此路径**——
       主进程内加载 PaddleOCR 会阻塞 UI、占用 GPU 上下文。

    因此本类的所有 OCR 逻辑（含 recognize_batch 真批量）都是必需的，
    既是子进程的内核，也是调试路径的实现，二者共享同一份代码。
    """

    _pipelines: dict[str, Any] = {}  # 管道缓存：{pipeline_name: pipeline_instance}
    _pipeline_signatures: dict[
        str, tuple
    ] = {}  # 管道构造参数签名：{pipeline_name: signature}
    _lock = threading.Lock()
    _initialized = False
    _status_callback: Callable | None = None  # 状态回调函数
    _cache_manager: Any = None  # PipelineCacheManager 实例（懒加载）

    def is_ready(self) -> bool:
        """服务就绪（直连/worker 内：模型已加载即就绪）"""
        return self._initialized

    # 预加载相关状态
    _preload_progress_callback: Callable[[str, int, int], None] | None = (
        None  # (pipeline_name, current, total)
    )
    _preloaded_pipelines: set[str] = set()  # 已预加载的管道名称
    _is_preloading = False  # 是否正在预加载
    _preload_lock = (
        threading.Lock()
    )  # 预加载专用锁，保护 _preloaded_pipelines 和 _is_preloading

    @classmethod
    def set_status_callback(cls, callback: Callable | None) -> None:
        """设置状态回调函数

        Args:
            callback: 回调函数，接收 (stage, message) 参数
                     例如: ("模型下载", "正在下载 OCR 模型...")
        """
        cls._status_callback = callback

    @classmethod
    def _notify_status(cls, stage: str, message: str) -> None:
        """通知状态变化"""
        if cls._status_callback:
            try:
                cls._status_callback(stage, message)
            except Exception:
                pass  # 忽略回调错误

    def __init__(self):
        """初始化 OCR 服务

        使用 SingletonMeta 确保单例，_initialized 标志防止重复初始化。
        """
        if not self._initialized:
            with self._lock:
                if not self._initialized:
                    self._initialized = True

    @property
    def cache_manager(self) -> Any:
        """PipelineCacheManager 实例（懒加载，避免循环导入）。"""
        if self._cache_manager is None:
            from vibeocr.runtime.recognition.pipeline_cache_manager import (
                PipelineCacheManager,
            )

            # Backend 不读取前端配置。并存上限与 TTL 由 Supervisor RPC 显式
            # 注入；未注入时按硬件自动分档。
            self._cache_manager = PipelineCacheManager(self, ttls={}, max_heavy=None)
        return self._cache_manager

    @classmethod
    def _reset(cls) -> None:
        """重置服务状态

        供 SingletonMeta.reset_instance() 调用，用于测试清理。
        """
        with cls._lock:
            cls._pipelines = {}
            cls._pipeline_signatures = {}
            cls._initialized = False
            cls._status_callback = None
            cls._preload_progress_callback = None
            cls._cache_manager = None  # 重置 cache_manager（类属性）
            # 同步清理实例属性（property 懒加载会设实例属性，遮蔽类属性）
            from vibeocr.runtime.recognition.core.singleton_meta import SingletonMeta

            instance = SingletonMeta._instances.get(cls)
            if instance is not None:
                instance.__dict__.pop("_cache_manager", None)
                # 测试可能直接种入实例级缓存/签名（遮蔽类属性），必须同步
                # 清理，否则残留签名会让下一个测试误判可复用。
                instance.__dict__.pop("_pipelines", None)
                instance.__dict__.pop("_pipeline_signatures", None)
            cls._preloaded_pipelines = set()
            cls._is_preloading = False

    @classmethod
    def set_preload_progress_callback(
        cls, callback: Callable[[str, int, int], None] | None
    ) -> None:
        """设置预加载进度回调函数

        Args:
            callback: 回调函数，接收 (pipeline_name, current, total) 参数
                     例如: ("OCR", 1, 3) 表示正在加载第 1 个管道，共 3 个
        """
        cls._preload_progress_callback = callback

    @classmethod
    def is_pipeline_preloaded(cls, pipeline: OCRPipeline) -> bool:
        """检查指定管道是否已预加载

        Args:
            pipeline: 管道类型

        Returns:
            是否已预加载
        """
        with cls._preload_lock:
            return pipeline.value in cls._preloaded_pipelines

    @classmethod
    def get_preloaded_pipelines(cls) -> list[str]:
        """获取已预加载的管道名称列表"""
        with cls._preload_lock:
            return list(cls._preloaded_pipelines)

    @classmethod
    def preload_pipeline(cls, pipeline: OCRPipeline) -> bool:
        """预加载单个管道（同步）

        Args:
            pipeline: 要预加载的管道

        Returns:
            是否成功加载
        """
        pipeline_name = pipeline.value

        # 检查是否已缓存（使用主锁保护 _pipelines）
        with cls._lock:
            if pipeline_name in cls._pipelines:
                with cls._preload_lock:
                    cls._preloaded_pipelines.add(pipeline_name)
                return True

        try:
            _logger.debug(f"[预加载] 开始加载管道: {pipeline.display_name}")
            instance = cls()
            instance.get_pipeline(pipeline)

            # 更新预加载状态（使用预加载锁）
            with cls._preload_lock:
                cls._preloaded_pipelines.add(pipeline_name)

            _logger.debug(f"[预加载] 管道加载完成: {pipeline.display_name}")
            return True
        except Exception as e:
            _logger.error(f"[预加载] 管道加载失败 {pipeline.display_name}: {e}")
            return False

    @classmethod
    def preload_pipelines_sequential(
        cls,
        pipelines: list[OCRPipeline],
        progress_callback: Callable[[str, int, int], None] | None = None,
    ) -> dict[str, bool]:
        """顺序预加载多个管道

        Args:
            pipelines: 要预加载的管道列表
            progress_callback: 进度回调 (pipeline_name, current, total)

        Returns:
            各管道加载结果 {pipeline_name: success}
        """
        # 检查是否有预加载任务在进行中
        with cls._preload_lock:
            if cls._is_preloading:
                _logger.warning("[预加载] 已有预加载任务在进行中")
                return {}
            cls._is_preloading = True

        results = {}
        total = len(pipelines)

        try:
            for i, pipeline in enumerate(pipelines, 1):
                pipeline_name = pipeline.value
                display_name = pipeline.display_name

                # 通知进度
                if progress_callback:
                    progress_callback(pipeline_name, i, total)
                if cls._preload_progress_callback:
                    cls._preload_progress_callback(pipeline_name, i, total)

                _logger.debug(f"[预加载] ({i}/{total}) 加载 {display_name}...")
                results[pipeline_name] = cls.preload_pipeline(pipeline)

            # 汇总结果
            success_count = sum(1 for v in results.values() if v)
            _logger.info(f"[预加载] 完成: {success_count}/{total} 个管道加载成功")

        finally:
            # 确保重置状态
            with cls._preload_lock:
                cls._is_preloading = False

        return results

    @classmethod
    def warmup_with_test_image(
        cls,
        pipeline: OCRPipeline | None = None,
        progress_callback: Callable[[str, int], None] | None = None,
    ) -> bool:
        """使用测试图片预热 OCR 服务

        通过执行一次虚拟识别来触发模型加载。

        Args:
            pipeline: 要预热的管道，None 表示使用默认 OCR 管道
            progress_callback: 进度回调 (stage, percent)

        Returns:
            预热是否成功
        """
        from vibeocr.runtime.recognition.utils.warmup_utils import get_warmup_image

        pipeline = pipeline or OCRPipeline.OCR
        pipeline_name = pipeline.value

        try:
            _logger.debug(f"[预热] 开始使用测试图片预热管道: {pipeline_name}")
            if progress_callback:
                progress_callback("准备测试图片", 10)

            # 获取测试图片
            test_image = get_warmup_image()
            if test_image is None:
                _logger.error("[预热] 无法创建预热测试图片，预热中止")
                return False
            _logger.debug(f"[预热] 测试图片大小: {len(test_image)} 字节")

            if progress_callback:
                progress_callback("执行虚拟识别", 50)

            # 创建选项并执行识别
            options = OCROptions(pipeline=pipeline)
            instance = cls()
            instance.recognize(test_image, options)

            if progress_callback:
                progress_callback("预热完成", 100)

            _logger.debug(f"[预热] 管道 {pipeline_name} 预热成功")
            return True

        except Exception as e:
            _logger.error(f"[预热] 管道 {pipeline_name} 预热失败: {e}")
            return False

    @classmethod
    def warmup_pipelines(
        cls,
        pipelines: list[OCRPipeline],
        progress_callback: Callable[[str, int, int], None] | None = None,
    ) -> dict[str, bool]:
        """使用测试图片预热多个管道

        Args:
            pipelines: 要预热的管道列表
            progress_callback: 进度回调 (pipeline_name, current, total)

        Returns:
            各管道预热结果 {pipeline_name: success}
        """
        results = {}
        total = len(pipelines)

        _logger.debug(f"[预热] 开始批量预热 {total} 个管道")

        for i, pipeline in enumerate(pipelines, 1):
            pipeline_name = pipeline.value

            def make_progress(
                stage: str, percent: int, idx=i, name=pipeline_name, total_count=total
            ):
                if progress_callback:
                    overall_percent = int(((idx - 1) * 100 + percent) / total_count)
                    progress_callback(name, idx, overall_percent)

            _logger.debug(f"[预热] ({i}/{total}) 预热 {pipeline.display_name}...")
            results[pipeline_name] = cls.warmup_with_test_image(pipeline, make_progress)

        success_count = sum(1 for v in results.values() if v)
        _logger.info(f"[预热] 完成: {success_count}/{total} 个管道预热成功")

        return results

    @classmethod
    def preload_in_background(
        cls,
        pipelines: list[OCRPipeline],
        on_complete: Callable[[dict[str, bool]], None] | None = None,
    ) -> threading.Thread:
        """在后台线程中预加载管道（非阻塞）

        Args:
            pipelines: 要预加载的管道列表
            on_complete: 完成回调，接收加载结果字典

        Returns:
            后台线程对象
        """

        def _preload_task():
            try:
                results = cls.preload_pipelines_sequential(pipelines)

                if on_complete:
                    on_complete(results)
            except Exception as e:
                _logger.error(f"[预加载] 后台预加载任务失败: {e}")
                if on_complete:
                    on_complete({})

        thread = threading.Thread(
            target=_preload_task, daemon=True, name="PipelinePreload"
        )
        thread.start()
        return thread

    @staticmethod
    def _get_device() -> str:
        """推理设备投影：#270 后保留管道只走 Paddle ONNX，真实 provider
        由请求快照决定；这里不再探测原生 Paddle/CUDA DLL，恒为 CPU 形态。"""
        _logger.info("[推理设备] Paddle ONNX（provider 由请求快照决定）")
        return "cpu"

    @staticmethod
    def _get_project_root():
        from vibeocr.runtime.environments.env_manager import get_project_root

        return get_project_root()

    def get_pipeline(self, pipeline: OCRPipeline) -> Any:
        """延迟加载指定管道 (线程安全)

        向后兼容方法，内部委托给 get_or_create_pipeline。
        """
        return self.get_or_create_pipeline(pipeline.value)

    def get_or_create_pipeline(
        self, pipeline_name: str, options: Any | None = None
    ) -> Any:
        """根据管道名获取或创建管道实例（构造参数签名感知）。

        先尝试从注册表获取 PipelineSpec 并使用其 create_pipeline 工厂，
        回退到旧式 _create_pipeline 以保持向后兼容。

        构造参数变化不能复用旧实例：以 spec.constructor_kwargs(options)
        生成的签名比对缓存，签名不同则先经 cache_manager.release_for_replacement 释放
        旧实例（沿用既有 lease/TTL/容量预算，不新建平行缓存），再重建。
        调用方（worker 循环）串行调用，重建时不会有并发旧实例在用。

        Args:
            pipeline_name: 管道名称字符串 (e.g. "OCR", "PP-StructureV3")
            options: 本次请求的 provider options；None 时使用 spec 默认选项
                （preload 路径），保证与默认请求复用同一实例。

        Returns:
            管道实例
        """
        signature = self._pipeline_constructor_signature(pipeline_name, options)

        def _reusable() -> bool:
            """缓存实例可否复用：实例存在且记录的构造签名与本次一致。

            生产写入点只有本方法（创建后立即记录签名）；无签名记录的
            实例只能来自外部直接种入私有缓存，来源不明一律 fail-closed
            重建，不猜测其构造参数。构造参数变化绝不变相复用旧模型。
            """
            return (
                pipeline_name in self._pipelines
                and self._pipeline_signatures.get(pipeline_name) == signature
            )

        if not _reusable():
            with self._lock:
                if not _reusable():
                    if pipeline_name in self._pipelines:
                        _logger.info(
                            "[get_or_create_pipeline] 构造参数变化，释放并重建管道 %s"
                            "（旧签名=%s 新签名=%s）",
                            pipeline_name,
                            self._pipeline_signatures.get(pipeline_name),
                            signature,
                        )
                        self.cache_manager.release_for_replacement(pipeline_name)
                    self.cache_manager.prepare_load(pipeline_name)
                    _logger.debug(
                        "[get_or_create_pipeline] 创建管道 %s，已加载管道: %s",
                        pipeline_name,
                        list(self._pipelines.keys()),
                    )
                    from vibeocr.runtime.recognition.core.pipelines import get_registry

                    registry = get_registry()
                    if not registry.has(pipeline_name):
                        # #270：注册表外的管道（含已退役的 PaddleOCR-VL /
                        # FORMULA_RECOGNITION）没有可构造后端；不再回退旧式
                        # 原生构造，直接可诊断拒绝。
                        retired = {
                            member.value
                            for member in OCRPipeline
                            if member
                            in {
                                OCRPipeline.PADDLEOCR_VL,
                                OCRPipeline.FORMULA_RECOGNITION,
                            }
                        }
                        if pipeline_name in retired:
                            msg = (
                                f"不支持的管道类型: {pipeline_name}；"
                                "该能力已退役，请改用其他识别模式"
                            )
                        else:
                            msg = f"不支持的管道类型: {pipeline_name}"
                        raise ValueError(msg)
                    spec = registry.get(pipeline_name)
                    # 构造 kwargs 全部来自 spec.constructor_kwargs（Paddle ONNX
                    # 请求快照）；provider 真值由快照持有，不再有 mkldnn/
                    # 原生 DLL 探测。
                    kwargs: dict[str, Any] = {}
                    if spec.constructor_kwargs is not None:
                        resolved = (
                            options if options is not None else spec.options_class()
                        )
                        kwargs.update(spec.constructor_kwargs(resolved))
                    self._pipelines[pipeline_name] = spec.create_pipeline(
                        self._get_device(), **kwargs
                    )
                    self._pipeline_signatures[pipeline_name] = signature
                    _logger.debug(
                        "[get_or_create_pipeline] 管道 %s 创建完成", pipeline_name
                    )
        # 记录使用时间；容量已在加载前由 prepare_load 原子腾出。
        try:
            self.cache_manager.touch(pipeline_name)
        except Exception as e:
            _logger.debug("[get_or_create_pipeline] cache_manager 操作失败: %s", e)
        return self._pipelines[pipeline_name]

    def _pipeline_constructor_signature(
        self, pipeline_name: str, options: Any | None
    ) -> tuple:
        """计算管道构造参数签名（排序后的 kwargs 元组）。"""
        from vibeocr.runtime.recognition.core.pipelines import get_registry

        registry = get_registry()
        if not registry.has(pipeline_name):
            return ()
        spec = registry.get(pipeline_name)
        if spec.constructor_kwargs is None:
            return ()
        resolved = options if options is not None else spec.options_class()
        try:
            ctor_kwargs = spec.constructor_kwargs(resolved)
        except Exception as e:
            _logger.error(
                "[get_or_create_pipeline] 管道 %s 构造参数映射失败: %s",
                pipeline_name,
                e,
            )
            raise
        return tuple(sorted((key, value) for key, value in ctor_kwargs.items()))

    @classmethod
    def release_pipelines(cls, heavy_only: bool = True) -> list[str]:
        """释放管道缓存（直连模式：直接调 cache_manager.release）。

        Args:
            heavy_only: True 只释放重管道，False 释放全部（含 OCR）。

        Returns:
            被释放的管道名列表。
        """
        return cls().cache_manager.release(heavy_only=heavy_only)

    @classmethod
    def set_pipeline_ttls(cls, pipeline_ttls: dict[str, int]) -> bool:
        """设置每管道 TTL 闲置回收时间（直连模式）。

        Args:
            pipeline_ttls: 每管道 TTL 字典，0=持久。

        Returns:
            是否设置成功。
        """
        cls().cache_manager.ttls = pipeline_ttls
        return True

    @property
    def pipeline(self) -> Any:
        """延迟加载默认 OCR 流水线 (向后兼容)"""
        return self.get_pipeline(OCRPipeline.OCR)

    def recognize(
        self,
        image: Image.Image | np.ndarray | str | bytes,
        options: OCROptions | None = None,
    ) -> OCRResult:
        """
        对图像执行 OCR 识别

        Args:
            image: PIL Image, numpy 数组, 图像路径, 或图像字节数据
            options: OCR 识别选项

        Returns:
            OCRResult 对象，包含识别结果和置信度信息
        """
        image = self._to_ndarray(image)
        # _to_ndarray 对 str 路径输入返回 str，但 recognize_batch 只收 ndarray。
        # 实际调用方均传 bytes/PIL/ndarray（str 路径走 paddlex 内部加载），
        # 此处 cast 与 recognize_batch 的 ndarray 契约对齐。
        return self.recognize_batch([cast("np.ndarray", image)], options)[0]

    @staticmethod
    def _to_ndarray(
        image: Image.Image | np.ndarray | str | bytes,
    ) -> np.ndarray | str:
        """统一输入为 PaddleX 可接受的形态（numpy 数组或路径字符串）。

        bytes/PIL 输入转换为 RGB numpy 数组；ndarray/str 原样返回。

        Args:
            image: PIL Image, numpy 数组, 图像路径, 或图像字节数据。

        Returns:
            numpy 数组（bytes/PIL 输入）或原始字符串路径。
        """
        if isinstance(image, bytes):
            import io

            import numpy as np
            from PIL import Image as PILImage

            _logger.debug(
                f"[recognize] 输入是 bytes ({len(image)} 字节)，转换为 numpy.ndarray"
            )
            pil_image = PILImage.open(io.BytesIO(image))
            if pil_image.mode != "RGB":
                pil_image = pil_image.convert("RGB")  # type: ignore[assignment]
            return np.array(pil_image)
        if hasattr(image, "convert"):
            # PIL Image（非 ndarray）
            import numpy as np

            if image.mode != "RGB":
                image = image.convert("RGB")  # type: ignore[assignment]
            return np.array(image)
        # 到此 bytes/PIL 分支已 return，image 只剩 ndarray|str（hasattr 无法静态收窄）
        return image  # type: ignore[return-value]  # ndarray|str，符合签名

    def recognize_batch(
        self,
        images: list[np.ndarray],
        options: OCROptions | None = None,
        asset_sinks: list[Any] | None = None,
    ) -> list[OCRResult]:
        """对一组图像批量执行 OCR 识别（单次 predict 调用，利用 PaddleOCR 批处理）。

        相比逐张调用 recognize()，本方法将所有图像一次性送入 PaddleOCR 的
        predict(list)，由其内部的 ImageBatchSampler 按 batch_size 分批，
        避免每张图重复的管道开销，显著提升 PDF 等多页场景的吞吐。

        输入图像需为 numpy 数组（RGB，与单次识别路径一致）。结果顺序与输入
        一致。``asset_sinks``（可选）与 images 逐位对齐：结构类管线用它把
        块级图像写入 job 私有受控目录，产生 opaque 资产引用；None 项表示
        该输入不落资产（管线标记 available=false）。

        Args:
            images: 输入图像列表（numpy 数组）。
            options: OCR 识别选项（所有图像共享同一组选项）。
            asset_sinks: 结果资产写入器列表（与 images 对齐，可为 None）。

        Returns:
            OCRResult 列表，顺序与 images 一致。
        """
        actual_options = options if options is not None else OCROptions()
        sinks = list(asset_sinks) if asset_sinks is not None else None

        # 统一获取管道名称（处理枚举和字符串两种类型）
        pipeline_name = actual_options.pipeline.value
        _logger.debug(
            f"[recognize_batch] 开始批量识别 {len(images)} 张，管道: {pipeline_name}"
        )

        if not images:
            return []

        def _dispatch_once() -> list[OCRResult]:
            """执行一次注册表分发；兼容回退由外层严格控制为至多一次。"""
            from vibeocr.runtime.recognition.core.pipelines import get_registry

            registry = get_registry()
            if registry.has(pipeline_name) and (
                registry.get(pipeline_name).recognize_batch is not None
            ):
                spec = registry.get(pipeline_name)
                return spec.recognize_batch(  # type: ignore[misc,no-any-return]
                    self, images, actual_options, asset_sinks=sinks
                )

            _logger.debug(
                "[recognize_batch] 管道 %s 未注册批量接口，回退逐张识别",
                pipeline_name,
            )
            spec = registry.get(pipeline_name)
            return [
                spec.recognize(
                    self,
                    img,
                    actual_options,
                    asset_sink=sinks[index] if sinks is not None else None,
                )
                for index, img in enumerate(images)
            ]

        # 根据管道类型分发
        try:
            # #270：oneDNN/mkldnn 兼容回退已随原生 Paddle 后端退役；异常原样
            # 上抛，不再重试重建。
            results = _dispatch_once()

            # Normalize each result's bbox from pixel coords to [0-1000]
            for img, result in zip(images, results, strict=False):
                self._normalize_result_bbox(result, img)

            # 标记管道识别成功：覆盖全部本地管道（见 LOCAL_MARKABLE_PIPELINES），
            # 遗漏会导致 is_pipeline_ever_succeeded 永远 False，触发 QWebEngineView
            # 冷启动卡顿（见该常量的文档注释）。
            pipeline_val = pipeline_name
            if pipeline_val in LOCAL_MARKABLE_PIPELINES:
                try:
                    mark_pipeline_success(pipeline_val, self._get_project_root())
                except Exception:
                    pass

            _logger.debug(f"[recognize_batch] 完成，返回 {len(results)} 个结果")
            return results
        except Exception as e:
            _logger.error(f"[recognize_batch] 识别过程中发生异常: {e}", exc_info=True)
            raise

    @staticmethod
    def _normalize_result_bbox(result: OCRResult, image: Any) -> None:
        """将 OCRResult 中像素坐标 bbox 归一化到 [0, 1000]（原地修改）。

        bbox 坐标在预处理后图像空间中。优先使用 result.preproc_img_w/h；
        若预处理未提供尺寸，则回退到输入图像 shape，并在 90°/270° 旋转时
        互换宽高（旋转后图像宽高与原图互换）。

        Args:
            result: OCRResult，其 text_blocks/content_list 的 bbox 会被原地归一化。
            image: 对应的输入图像（用于回退取尺寸）。
        """
        img_w = img_h = 0
        if result.preproc_img_w > 0 and result.preproc_img_h > 0:
            img_w = result.preproc_img_w
            img_h = result.preproc_img_h
        elif hasattr(image, "shape"):
            _shape = image.shape
            if isinstance(_shape, tuple) and len(_shape) >= 2:
                img_h, img_w = _shape[:2]
        elif hasattr(image, "size"):
            _sz = image.size
            if isinstance(_sz, tuple):
                img_w, img_h = _sz
        # 预处理旋转 90°/270° 时宽高互换（仅当无预处理图像时需要）
        if result.preproc_img_w == 0 and result.preproc_angle in (90, 270):
            img_w, img_h = img_h, img_w
        if img_w <= 0 or img_h <= 0:
            return
        for block in result.text_blocks:
            if block.bbox:
                x0, y0, x1, y1 = block.bbox
                block.bbox = (
                    x0 / img_w * 1000,
                    y0 / img_h * 1000,
                    x1 / img_w * 1000,
                    y1 / img_h * 1000,
                )
            # 多边形与 bbox 同源、同像素空间，用同一 img_w/h 归一化（保持一致）。
            # _parse_single_result 直接透传 PaddleOCR 像素坐标，这里统一归一化。
            if block.polygon:
                block.polygon = tuple(
                    v / (img_w if i % 2 == 0 else img_h) * 1000
                    for i, v in enumerate(block.polygon)
                )
        for cl_block in result.content_list:
            bbox = cl_block.get("bbox")
            if bbox and len(bbox) >= 4:
                cl_block["bbox"] = [
                    bbox[0] / img_w * 1000,
                    bbox[1] / img_h * 1000,
                    bbox[2] / img_w * 1000,
                    bbox[3] / img_h * 1000,
                ]
        result.image_width = img_w
        result.image_height = img_h

    def _recognize_ocr(
        self,
        image: Image.Image | np.ndarray | str,
        options: OCROptions,
    ) -> OCRResult:
        """通用 OCR 识别"""
        _logger.debug("[_recognize_ocr] 获取 OCR 管道...")
        pipeline = self.get_pipeline(OCRPipeline.OCR)
        _logger.debug("[_recognize_ocr] 执行 predict...")
        try:
            output = pipeline.predict(
                input=image,
                use_doc_orientation_classify=options.use_doc_orientation_classify,
                use_doc_unwarping=options.use_doc_unwarping,
                use_textline_orientation=options.use_textline_orientation,
            )
            _logger.debug(f"[_recognize_ocr] predict 返回，类型: {type(output)}")
        except Exception as e:
            _logger.error(f"[_recognize_ocr] predict 调用失败: {e}", exc_info=True)
            raise

        _logger.debug("[_recognize_ocr] 开始处理输出...")
        result = self._process_ocr_output_safe(output)
        _logger.debug(f"[_recognize_ocr] 结果处理完成: {len(result.raw_text)} 字符")
        return result

    def _recognize_structure(
        self,
        image: Image.Image | np.ndarray | str,
        options: OCROptions,
    ) -> OCRResult:
        """通过管道注册表委托 PP-StructureV3 文档解析。"""
        from vibeocr.runtime.recognition.core.pipelines import get_registry

        spec = get_registry().get("PP-StructureV3")
        provider_options = spec.options_class.from_dict(options.to_dict())
        return spec.recognize(self, image, provider_options)

    def _recognize_paddlocr_vl(
        self,
        image: Image.Image | np.ndarray | str,
        options: OCROptions,
    ) -> OCRResult:
        """#270：PaddleOCR-VL 已退役；仅保留可诊断拒绝，不再有实现。"""
        raise ValueError("PaddleOCR-VL 文档解析已退役：请改用其他文档识别模式")

    @staticmethod
    def _extract_bbox(
        rec_boxes, index: int
    ) -> tuple[float, float, float, float] | None:
        """从 rec_boxes 提取第 index 个文本框的 bbox [x0, y0, x1, y1]

        支持格式:
        - (N, 4): [x0, y0, x1, y1] 轴对齐矩形
        - (N, 4, 2): [[x0,y0], [x1,y1], [x2,y2], [x3,y3]] 四点多边形
        - (N, 2, 2): [[x0,y0], [x1,y1]] 两点矩形
        """
        try:
            box = rec_boxes[index]
            if hasattr(box, "tolist"):
                box = box.tolist()
            if len(box) == 4:
                # (N, 4) 或 (N, 4, 2)
                if isinstance(box[0], (int, float)):
                    return (float(box[0]), float(box[1]), float(box[2]), float(box[3]))
                # (N, 4, 2) 多边形格式: 取外接矩形
                xs = [p[0] for p in box]
                ys = [p[1] for p in box]
                return (min(xs), min(ys), max(xs), max(ys))
            if len(box) == 2 and len(box[0]) == 2 and len(box[1]) == 2:
                return (
                    float(box[0][0]),
                    float(box[0][1]),
                    float(box[1][0]),
                    float(box[1][1]),
                )
        except (IndexError, TypeError, ValueError):
            pass
        return None

    @staticmethod
    def _consume_generator_safely(output) -> list:
        """安全地消费 generator（禁用 GC 避免 CUDA 内存管理冲突）"""
        import gc

        gc_was_enabled = gc.isenabled()
        gc.disable()
        try:
            return list(output)
        except Exception as e:
            _logger.error(f"[安全消费] 消费 generator 时出错: {e}", exc_info=True)
            return []
        finally:
            if gc_was_enabled:
                gc.enable()

    def _process_ocr_output_safe(self, output) -> OCRResult:
        """从 OCR 输出中提取结果

        先将 generator 完全消费为 list（禁用 GC），然后提取数据。
        """
        _logger.debug("[_process_ocr_output_safe] 开始提取结果...")
        text_with_scores: list[tuple[str, float]] = []
        text_blocks: list[TextBlock] = []

        output_list = self._consume_generator_safely(output)

        # 提取预处理信息：旋转角度和实际预处理后图像
        # res.img['preprocessed_img'] 是拼接可视化，不用；
        # 实际预处理图在 doc_preprocessor_res['output_img']（numpy RGB，
        # PaddleOCR 3.7 实测即为 RGB）。不可做 [::-1] 翻转，否则 R/B 对调。
        preproc_angle = 0
        preprocessed_png: bytes | None = None
        preproc_w = preproc_h = 0
        if output_list:
            res = output_list[0]
            dp_res = res.get("doc_preprocessor_res")
            if dp_res is not None:
                preproc_angle = dp_res.get("angle", 0)
                out_arr = dp_res.get("output_img")
                if out_arr is not None:
                    from PIL import Image as _PILImage

                    rgb = out_arr.copy()
                    pil_img = _PILImage.fromarray(rgb)
                    preproc_w, preproc_h = pil_img.size
                    buf = io.BytesIO()
                    pil_img.save(buf, format="PNG")
                    preprocessed_png = buf.getvalue()

        result_count = 0
        for res in output_list:
            result_count += 1
            if result_count > 100:  # 防止异常情况
                _logger.warning("[_process_ocr_output_safe] 结果项过多，可能有问题")
                break
            try:
                if hasattr(res, "rec_texts") and hasattr(res, "rec_scores"):
                    rec_boxes = getattr(res, "rec_boxes", None)
                    for i, (text, score) in enumerate(
                        zip(res.rec_texts, res.rec_scores, strict=False)
                    ):
                        if text:
                            fs = float(score)
                            text_with_scores.append((text, fs))
                            bbox = (
                                self._extract_bbox(rec_boxes, i)
                                if rec_boxes is not None
                                else None
                            )
                            text_blocks.append(
                                TextBlock(text=text, score=fs, bbox=bbox)
                            )
                elif hasattr(res, "rec_texts"):
                    rec_boxes = getattr(res, "rec_boxes", None)
                    for i, text in enumerate(res.rec_texts):
                        if text:
                            text_with_scores.append((text, 1.0))
                            bbox = (
                                self._extract_bbox(rec_boxes, i)
                                if rec_boxes is not None
                                else None
                            )
                            text_blocks.append(
                                TextBlock(text=text, score=1.0, bbox=bbox)
                            )
                elif hasattr(res, "ocr_text"):
                    text_with_scores.append((res.ocr_text, 1.0))
                    text_blocks.append(
                        TextBlock(text=res.ocr_text, score=1.0, bbox=None)
                    )
                elif isinstance(res, dict):
                    rec_texts = res.get("rec_texts", [])
                    rec_scores = res.get("rec_scores", [])
                    rec_boxes = res.get("rec_boxes")
                    if rec_scores:
                        for i, (text, score) in enumerate(
                            zip(rec_texts, rec_scores, strict=False)
                        ):
                            if text:
                                fs = float(score)
                                text_with_scores.append((text, fs))
                                bbox = (
                                    self._extract_bbox(rec_boxes, i)
                                    if rec_boxes is not None
                                    else None
                                )
                                text_blocks.append(
                                    TextBlock(text=text, score=fs, bbox=bbox)
                                )
                    else:
                        for i, text in enumerate(rec_texts):
                            if text:
                                text_with_scores.append((text, 1.0))
                                bbox = (
                                    self._extract_bbox(rec_boxes, i)
                                    if rec_boxes is not None
                                    else None
                                )
                                text_blocks.append(
                                    TextBlock(text=text, score=1.0, bbox=bbox)
                                )
            except Exception as e:
                _logger.error(
                    f"[_process_ocr_output_safe] 处理结果项 #{result_count} 时出错: {e}"
                )
                continue

        raw_text = "\n".join(t for t, _ in text_with_scores)
        _logger.debug(
            f"[_process_ocr_output_safe] 处理完成: 共 {result_count} 个结果项, {len(text_with_scores)} 个文本块"
        )

        result = self._build_ocr_result(
            raw_text=raw_text,
            text_with_scores=text_with_scores,
            pipeline_type="OCR",
            text_blocks=text_blocks,
        )
        result.preproc_angle = preproc_angle
        result.preprocessed_image = preprocessed_png
        result.preproc_img_w = preproc_w
        result.preproc_img_h = preproc_h
        return result

    def _build_ocr_result(
        self,
        raw_text: str,
        markdown_text: str = "",
        html_text: str = "",
        text_with_scores: list[tuple[str, float]] | None = None,
        pipeline_type: str = "OCR",
        images: dict[str, Any] | None = None,
        text_blocks: list[TextBlock] | None = None,
        content_list: list[dict[str, Any]] | None = None,
    ) -> OCRResult:
        """构建 OCRResult 对象

        Args:
            raw_text: 纯文本
            markdown_text: Markdown 格式文本
            html_text: HTML 格式文本
            text_with_scores: 文本块及置信度列表
            pipeline_type: 管道类型
            images: 图像字典
            text_blocks: 含坐标的文本块列表
            content_list: 结构化内容列表（含布局信息）

        Returns:
            OCRResult 对象
        """
        if text_with_scores is None:
            text_with_scores = []

        # 计算平均置信度（仅统计已知置信度；None=unknown 不参与，不伪造）
        known_scores = [s for _, s in text_with_scores if s is not None]
        avg_score = 0.0
        if known_scores:
            avg_score = sum(known_scores) / len(known_scores)

        # 收集低置信度项（低于 80%；unknown 不参与）
        low_confidence_items = [
            (text, score)
            for text, score in text_with_scores
            if score is not None and score < 0.80
        ]

        final_html = html_text or raw_text

        return OCRResult(
            raw_text=raw_text,
            markdown_text=markdown_text or raw_text,
            html_text=final_html,
            text_with_scores=text_with_scores,
            avg_score=avg_score,
            low_confidence_items=low_confidence_items,
            pipeline_type=pipeline_type,
            images=images or {},
            text_blocks=text_blocks or [],
            content_list=content_list or [],
        )
