"""cpu_info 通用线程数预算边缘用例测试。"""

from __future__ import annotations

import pytest
from vibeocr.runtime.processes.utils import cpu_info


class TestGetCpuThreadCount:
    """get_cpu_thread_count 边缘用例。"""

    def test_env_override_used(self, monkeypatch: pytest.MonkeyPatch):
        """合法 VIBEOCR_CPU_THREADS 优先于 os.cpu_count。"""
        monkeypatch.setenv("VIBEOCR_CPU_THREADS", "7")
        assert cpu_info.get_cpu_thread_count() == 7

    def test_env_override_with_whitespace(self, monkeypatch: pytest.MonkeyPatch):
        """带空白的覆盖值被 strip 后采用。"""
        monkeypatch.setenv("VIBEOCR_CPU_THREADS", "  3  ")
        assert cpu_info.get_cpu_thread_count() == 3

    def test_env_override_zero_ignored(self, monkeypatch: pytest.MonkeyPatch):
        """非正值忽略，回退 os.cpu_count。"""
        monkeypatch.setenv("VIBEOCR_CPU_THREADS", "0")
        assert cpu_info.get_cpu_thread_count() == cpu_info.get_cpu_thread_count()

    def test_env_override_negative_ignored(self, monkeypatch: pytest.MonkeyPatch):
        """负值忽略，回退。"""
        monkeypatch.setenv("VIBEOCR_CPU_THREADS", "-4")
        # 至少为正
        assert cpu_info.get_cpu_thread_count() >= 1

    def test_env_override_non_int_ignored(self, monkeypatch: pytest.MonkeyPatch):
        """非整数值忽略并回退。"""
        monkeypatch.setenv("VIBEOCR_CPU_THREADS", "abc")
        assert cpu_info.get_cpu_thread_count() >= 1

    def test_cpu_count_capped(self, monkeypatch: pytest.MonkeyPatch):
        """os.cpu_count 超过上限时裁剪到 CPU_THREADS_CAP。"""
        monkeypatch.delenv("VIBEOCR_CPU_THREADS", raising=False)
        monkeypatch.setattr(cpu_info.os, "cpu_count", lambda: 1024)
        assert cpu_info.get_cpu_thread_count() == cpu_info.CPU_THREADS_CAP

    def test_result_within_valid_range(self, monkeypatch: pytest.MonkeyPatch):
        """无覆盖时结果落在 [1, CAP]。"""
        monkeypatch.delenv("VIBEOCR_CPU_THREADS", raising=False)
        n = cpu_info.get_cpu_thread_count()
        assert 1 <= n <= cpu_info.CPU_THREADS_CAP

    def test_probe_failure_falls_back(self, monkeypatch: pytest.MonkeyPatch):
        """系统 CPU 数探针失败时使用保守回退值。"""
        monkeypatch.delenv("VIBEOCR_CPU_THREADS", raising=False)

        def raise_probe_error() -> int:
            raise OSError("probe failed")

        monkeypatch.setattr(cpu_info.os, "cpu_count", raise_probe_error)
        assert cpu_info.get_cpu_thread_count() == cpu_info.FALLBACK_CPU_THREADS
