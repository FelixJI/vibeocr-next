"""Paddle ONNX 环境共享的 CUDA 设备就绪真值与 EP 插件激活。

生产方案：CPU onnxruntime（不变）+ 官方 CUDA EP 插件（固定 DLL 资产）
+ nvidia 固定运行库。Paddle worker 在构造 CUDA 会话前调用
``setup_cuda_plugin`` 完成 DLL 环境与插件注册，并经 ``cuda_provider_ready``
核实真实可见性，全程 fail closed，绝不静默以 CPU 冒充 GPU；不使用
onnxruntime-gpu wheel、不排除 CPU 发行物、不升级 ORT。背景与安装布局
由 environments 的离线资产模块维护。
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
#: （managed launch 显式设置 env 优先）。
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
                logger.info("[Supervisor][OnnxDevices] onnxruntime lacks plugin EP API")
                _state["failed"] = True
                return False
            dlls, directories = _nvidia_dll_dirs()
            if not dlls:
                logger.info("[Supervisor][OnnxDevices] no nvidia CUDA runtime wheels")
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
                logger.info("[Supervisor][OnnxDevices] CUDA EP plugin dll missing")
                _state["failed"] = True
                return False
            registered = False
            for dll in candidates:
                try:
                    register("CUDAExecutionProvider", str(dll))
                    registered = True
                    logger.info(
                        "[Supervisor][OnnxDevices] registered CUDA EP plugin: %s",
                        dll,
                    )
                    break
                except Exception:
                    logger.warning(
                        "[Supervisor][OnnxDevices] plugin registration rejected: %s",
                        dll,
                        exc_info=True,
                    )
            if not registered:
                _state["failed"] = True
                return False
            _state["ready"] = True
            return True
        except Exception:
            logger.exception("[Supervisor][OnnxDevices] CUDA EP plugin setup failed")
            _state["failed"] = True
            return False


def cuda_provider_ready() -> bool:
    """CUDA 真实就绪：插件注册成功且 ``get_ep_devices`` 可见 CUDA EP。

    这是设备级 ``cuda`` 放行的唯一信号；探测不触发下载、不抛错。
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
        logger.exception("[Supervisor][OnnxDevices] cuda provider probe failed")
        return False


__all__ = [
    "KEY_DLLS",
    "NVIDIA_PACKAGES",
    "cuda_provider_ready",
    "setup_cuda_plugin",
]
