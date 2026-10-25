"""CPU 推理的通用线程数预算；不加载模型框架。"""

from __future__ import annotations

import logging
import os

logger = logging.getLogger(__name__)

#: CPU 线程数回退值（探测失败时用，保守）。
FALLBACK_CPU_THREADS = 4
#: 单进程 CPU 线程上限（paddle/OpenMP 线程过多反而因 NUMA/调度抖动变慢，
#: OCR 推理以卷积+矩阵乘为主，经验上限 16）。
CPU_THREADS_CAP = 16


def get_cpu_thread_count() -> int:
    """返回推荐的 CPU 推理线程数，范围 [1, CPU_THREADS_CAP]。

    策略：取逻辑核数，夹到上限；探测失败回退 FALLBACK_CPU_THREADS。
    优先读 ``VIBEOCR_CPU_THREADS`` 环境变量（用户显式覆盖）。

    Returns:
        推荐线程数（正整数）。
    """
    # 用户显式覆盖优先
    override = os.environ.get("VIBEOCR_CPU_THREADS", "").strip()
    if override:
        try:
            n = int(override)
            if n > 0:
                return n
        except ValueError:
            logger.warning("[cpu_info] VIBEOCR_CPU_THREADS=%r 非整数，忽略", override)

    try:
        n = os.cpu_count() or FALLBACK_CPU_THREADS
    except Exception:  # 系统调用失败兜底
        n = FALLBACK_CPU_THREADS
    return max(1, min(n, CPU_THREADS_CAP))
