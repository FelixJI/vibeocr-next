"""VibeOCR MinerU API 启动入口：先应用锁定 SDK 公开策略再转发官方 api_server。

背景（#317 GPU 根因适配）：锁定 mineru 4.1.3 默认
``METACOMMANDS["nvidia"]["layout"] = True``，NVIDIA 上 DML layout
metacommands 在 ONNX Run 内稳定原生崩溃（WER nvcuda64.dll）；同一固定
运行时/合成图/屏蔽条件下仅关闭该策略即恢复正常 DML 推理。本入口只把该
公开策略表项改为 False：保留 DML provider、其它厂商/模型策略、mem
pattern 关闭与顺序执行；不改 wheel/pin、不强制 CPU。

另承担 GPU 线模型的 factory 边界严格校验：上游显式 gpu 在 provider 缺失/
无硬件/初始化失败时仅 warning 后回退 CPU（锁定包 ``onnx_device._select`` /
``onnx.ort_session``）；已提交 directml 意图的 GPU 线模型不得把 CPU 结果
当成功。不加载 GPU 模型的路径（如 flash 纯抽取）不受任何全局预检拦截。
"""

from __future__ import annotations

import sys
from typing import Callable

#: 需要关闭的 metacommands 表项（厂商, 模型线）：仅 NVIDIA layout。
DISABLED_METACOMMAND_KEYS: tuple[tuple[str, str], ...] = (("nvidia", "layout"),)


def apply_locked_sdk_metacommand_policy() -> dict[str, bool]:
    """关闭 NVIDIA layout metacommands；返回实际应用的表项快照。

    锁定 SDK 公开导出 ``METACOMMANDS`` 表存在且包含目标表项时才应用；
    结构漂移时抛错（fail closed 提示评审适配），绝不静默跳过。
    """
    from mineru.model.runtime.onnx_device import METACOMMANDS

    applied: dict[str, bool] = {}
    for vendor, model_kind in DISABLED_METACOMMAND_KEYS:
        table = METACOMMANDS.get(vendor)
        if not isinstance(table, dict) or model_kind not in table:
            raise RuntimeError(
                "locked mineru METACOMMANDS policy table changed; review adapter"
            )
        table[model_kind] = False
        applied[f"{vendor}.{model_kind}"] = False
    return applied


def gpu_eligible_model_kinds() -> frozenset[str]:
    """从锁定 SDK 公开 METACOMMANDS 表派生可走 GPU 的模型线集合。

    单一真相源：集合随 SDK 锁版本漂移而变化（当前 Windows 为
    layout/mfr/ocr_rec/unet），不在其它端复制第二套推导。
    """
    from mineru.model.runtime.onnx_device import METACOMMANDS

    kinds: set[str] = set()
    for table in METACOMMANDS.values():
        if isinstance(table, dict):
            kinds.update(str(key) for key in table)
    return frozenset(kinds)


def install_strict_directml_factory_guard() -> Callable[[], None] | None:
    """已提交 directml 意图时，在公开 ``ort_session`` factory 边界严格校验。

    原封委托原 factory（保留 SDK 锁/线程/会话选项与参数），仅检查返回
    session 的实际首 provider：GPU 线模型得到 CPU 首 provider（无硬件/
    EP/初始化失败回退）时释放该未用 session 并结构化失败，不把 CPU
    结果当成功。固定 CPU 模型（不在 METACOMMANDS 集合，如 Windows
    ocr_det）与 cpu/auto 意图不受影响（auto 保留 SDK 可观察回退）。
    返回卸载函数；未安装（非 gpu 意图）返回 None。必须在导入
    api_server/模型 consumer 之前安装。
    """
    import os

    if os.environ.get("MINERU_MODEL_ONNX_DEVICE") != "gpu":
        return None

    import mineru.model.runtime.onnx as mineru_onnx

    eligible = gpu_eligible_model_kinds()
    original = mineru_onnx.ort_session

    def _strict_ort_session(model_path, *args, **kwargs):
        model_kind = str(kwargs.get("model_kind") or "")
        session = original(model_path, *args, **kwargs)
        if model_kind in eligible:
            providers = list(session.get_providers() or [])
            if providers[:1] != ["DmlExecutionProvider"]:
                # ORT 无公开 close；抛错前删除引用，避免 traceback 保留回退 session。

                del session
                raise RuntimeError(
                    "[VibeOCR][MinerU] committed DirectML snapshot but "
                    f"{model_kind or 'model'} session fell back to "
                    f"providers={providers}; refusing implicit CPU result"
                )
        return session

    mineru_onnx.ort_session = _strict_ort_session

    def _uninstall() -> None:
        mineru_onnx.ort_session = original

    return _uninstall


def main() -> None:
    applied = apply_locked_sdk_metacommand_policy()
    print(
        "[VibeOCR][MinerU] metacommands policy applied: "
        + ",".join(f"{key}={value}" for key, value in sorted(applied.items())),
        file=sys.stderr,
        flush=True,
    )
    # 在导入 api_server/模型 consumer 之前安装 factory 守卫：consumer 以
    # ``from ... import ort_session`` 绑定时取到的是守卫包装。
    install_strict_directml_factory_guard()
    import mineru.parser.api_server as api_server

    # click 命令自行解析转发来的 sys.argv：参数与端点保持官方面不变。
    api_server.main()


if __name__ == "__main__":
    main()
