"""Rapid 系引擎（rapidocr / rapid-table）共享的 CUDA 设备就绪真值与激活。

生产方案：CPU onnxruntime（不变）+ 官方 CUDA EP 插件（固定 DLL 资产）
+ nvidia 固定运行库。引擎 CPU 构造后对全部原生 session
``set_providers`` 并核实实际 providers，全程 fail closed，绝不静默以
CPU 冒充 GPU；不使用 onnxruntime-gpu wheel、不排除 CPU 发行物、
不升级 ORT。背景、实测证据与安装布局见
docs/rapid-parallel-rapidtable-interface.md。
"""

from __future__ import annotations

import ctypes
import logging
import os
import threading
from importlib.metadata import PackageNotFoundError, distribution
from pathlib import Path

logger = logging.getLogger(__name__)

NVIDIA_PACKAGES = (
    "nvidia-cublas",
    "nvidia-cuda-runtime",
    "nvidia-cuda-nvrtc",
    "nvidia-cudnn-cu13",
    "nvidia-curand",
    "nvidia-cufft",
    "nvidia-nvjitlink",
)
#: 关键 DLL 按依赖顺序显式加载（cu13 版本号来自锁定 wheel 内容）。
KEY_DLLS = (
    "cudart64_13.dll",
    "nvJitLink_130_0.dll",
    "cublasLt64_13.dll",
    "cublas64_13.dll",
    "cufft64_12.dll",
    "curand64_10.dll",
    "nvrtc64_130_0.dll",
    "cudnn64_9.dll",
)
_CUDA_PLUGIN_DLL_ENV = "VIBEOCR_ORT_CUDA_EP_DLL"
#: 安装 Owner 固定布局：``<runtime_root>/providers/cuda/onnxruntime_providers_cuda.dll``
#: （managed launch 显式设置 env 优先；模型同理位于
#: ``<runtime_root>/models/rapidtable/slanet-plus.onnx``）。
_PACK_PLUGIN_REL = Path("providers") / "cuda" / "onnxruntime_providers_cuda.dll"

_setup_lock = threading.Lock()
_state: dict[str, object] = {}
# add_dll_directory 句柄与 WinDLL 引用必须存活于进程生命周期。
_keepalive: list[object] = []


def _plugin_dll_candidates() -> list[Path]:
    candidates: list[Path] = []
    env_path = os.environ.get(_CUDA_PLUGIN_DLL_ENV, "").strip()
    if env_path:
        candidates.append(Path(env_path))
    runtime_root = os.environ.get("VIBEOCR_RUNTIME_ROOT", "").strip()
    if runtime_root:
        candidates.append(Path(runtime_root) / _PACK_PLUGIN_REL)
    return [path for path in candidates if path.is_file()]


def _nvidia_dll_dirs() -> tuple[dict[str, Path], list[Path]]:
    """收集 nvidia 运行库 DLL 与其所在目录（发行物 metadata 定位）。"""
    dlls: dict[str, Path] = {}
    for package in NVIDIA_PACKAGES:
        try:
            dist = distribution(package)
        except PackageNotFoundError:
            continue
        for entry in dist.files or ():
            if entry.suffix == ".dll":
                dlls.setdefault(entry.name, Path(dist.locate_file(entry)).resolve())
    return dlls, sorted({path.parent for path in dlls.values()})


def setup_cuda_plugin() -> bool:
    """幂等完成 DLL 环境 + 插件注册；失败返回 False（fail closed）。

    必须在构造任何 CUDA 会话前成功；重复调用直接返回上次结果。
    """
    with _setup_lock:
        if _state.get("ready"):
            return True
        if _state.get("failed"):
            return False
        try:
            import onnxruntime as ort

            register = getattr(ort, "register_execution_provider_library", None)
            if not callable(register):
                logger.info(
                    "[Supervisor][RapidDevices] onnxruntime lacks plugin EP API"
                )
                _state["failed"] = True
                return False
            dlls, directories = _nvidia_dll_dirs()
            if not dlls:
                logger.info("[Supervisor][RapidDevices] no nvidia CUDA runtime wheels")
                _state["failed"] = True
                return False
            # 进程 PATH 前缀（仅本进程）+ add_dll_directory 句柄保留。
            os.environ["PATH"] = (
                os.pathsep.join(str(directory) for directory in directories)
                + os.pathsep
                + os.environ.get("PATH", "")
            )
            _keepalive.extend(
                os.add_dll_directory(str(directory)) for directory in directories
            )
            for name in KEY_DLLS:
                if name in dlls:
                    _keepalive.append(ctypes.WinDLL(str(dlls[name])))
            candidates = _plugin_dll_candidates()
            if not candidates:
                logger.info("[Supervisor][RapidDevices] CUDA EP plugin dll missing")
                _state["failed"] = True
                return False
            registered = False
            for dll in candidates:
                try:
                    register("CUDAExecutionProvider", str(dll))
                    registered = True
                    logger.info(
                        "[Supervisor][RapidDevices] registered CUDA EP plugin: %s",
                        dll,
                    )
                    break
                except Exception:
                    logger.warning(
                        "[Supervisor][RapidDevices] plugin registration rejected: %s",
                        dll,
                        exc_info=True,
                    )
            if not registered:
                _state["failed"] = True
                return False
            _state["ready"] = True
            return True
        except Exception:
            logger.exception("[Supervisor][RapidDevices] CUDA EP plugin setup failed")
            _state["failed"] = True
            return False


def cuda_provider_ready() -> bool:
    """CUDA 真实就绪：插件注册成功且 ``get_ep_devices`` 可见 CUDA EP。

    这是引擎级 ``device=cuda`` 的唯一放行信号；探测不触发下载、不抛错。
    """
    if not setup_cuda_plugin():
        return False
    try:
        import onnxruntime as ort

        devices = getattr(ort, "get_ep_devices", None)
        if not callable(devices):
            return False
        return any(
            getattr(entry, "ep_name", "") == "CUDAExecutionProvider"
            for entry in devices()
        )
    except Exception:
        logger.exception("[Supervisor][RapidDevices] cuda provider probe failed")
        return False


def _iter_sessions(engine: object) -> list[object]:
    """发现引擎持有的原生 ORT 会话（``node.<attr>.session.session``）。

    RapidOCR/RapidTable 的 text_det/text_cls/text_rec/table_structure 挂载
    OrtInferSession 包装，真正原生 ``InferenceSession`` 在 ``.session.session``；
    包装层无 ``set_providers``，只取一层会遗漏（已真实 GPU 验证路径）。
    """
    sessions: list[object] = []
    # engine 根自身即 RapidOCR 实例（text_det/… 直挂）；RapidTable 实例
    # 则经 table_structure/ocr_engine 子节点持有。
    for node in (
        engine,
        getattr(engine, "table_structure", None),
        getattr(engine, "ocr_engine", None),
    ):
        if node is None:
            continue
        for attr in ("text_det", "text_cls", "text_rec", "session"):
            holder = getattr(node, attr, None)
            wrapper = getattr(holder, "session", None)
            native = getattr(wrapper, "session", wrapper)
            if native is not None and callable(getattr(native, "set_providers", None)):
                sessions.append(native)
    return sessions


def activate_cuda_sessions(engine: object) -> None:
    """CPU 构造后按已验证路径把全部会话切到 CUDA（非 monkeypatch）。

    逐会话 ``set_providers``，任何会话未能以 CUDA 为主 provider 时
    raise（fail closed，绝不静默 CPU 冒充 GPU）。
    """
    sessions = _iter_sessions(engine)
    if not sessions:
        raise RuntimeError("cuda activation found no ORT sessions")
    for session in sessions:
        session.set_providers(
            [
                ("CUDAExecutionProvider", {"device_id": "0"}),
                "CPUExecutionProvider",
            ]
        )
    for session in sessions:
        active = list(session.get_providers())
        if not active or active[0] != "CUDAExecutionProvider":
            raise RuntimeError(f"CUDA session degraded to CPU: providers={active}")


__all__ = [
    "KEY_DLLS",
    "NVIDIA_PACKAGES",
    "activate_cuda_sessions",
    "cuda_provider_ready",
    "setup_cuda_plugin",
]
