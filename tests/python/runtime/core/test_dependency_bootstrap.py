"""dependency_bootstrap CLI 入口的边缘用例测试。

通过 monkeypatch 隔离 GPU 检测与依赖安装内核，验证 profile 解析与退出码语义。
"""

from __future__ import annotations

from pathlib import Path

import pytest
from vibeocr.runtime.environments import dependency_bootstrap


class TestMainArgv:
    """main() 的 argparse 与退出码语义。"""

    def test_success_returns_zero(
        self,
        monkeypatch: pytest.MonkeyPatch,
        capsys: pytest.CaptureFixture[str],
    ):
        """install 返回成功时 main 退出码为 0，消息进 stdout。"""
        monkeypatch.setattr(
            dependency_bootstrap,
            "install_backend_dependencies",
            lambda *a, **k: (True, "installed ok"),
        )

        code = dependency_bootstrap.main(["--profile", "cpu", "--network", "domestic"])

        assert code == 0
        captured = capsys.readouterr()
        assert "installed ok" in captured.out

    def test_failure_returns_one_and_writes_stderr(
        self,
        monkeypatch: pytest.MonkeyPatch,
        capsys: pytest.CaptureFixture[str],
    ):
        """install 返回失败时 main 退出码为 1，消息进 stderr。"""
        monkeypatch.setattr(
            dependency_bootstrap,
            "install_backend_dependencies",
            lambda *a, **k: (False, "boom"),
        )

        code = dependency_bootstrap.main(
            ["--profile", "cpu", "--network", "international"]
        )

        assert code == 1
        captured = capsys.readouterr()
        assert "boom" in captured.err

    def test_progress_callback_reports_stages(
        self,
        monkeypatch: pytest.MonkeyPatch,
        capsys: pytest.CaptureFixture[str],
    ):
        """main 注入的 report 回调把阶段消息打印到 stdout。"""
        captured_stages: list[str] = []

        def fake_install(python_exe, *, profile, network_type, progress_callback):
            progress_callback("resolve", "starting")
            progress_callback("install", "done")
            captured_stages.append(profile)
            return True, "all good"

        monkeypatch.setattr(
            dependency_bootstrap, "install_backend_dependencies", fake_install
        )

        code = dependency_bootstrap.main(["--profile", "gpu-cu126"])

        assert code == 0
        out = capsys.readouterr().out
        assert "[resolve] starting" in out
        assert "[install] done" in out
        assert captured_stages == ["gpu-cu126"]

    def test_explicit_python_path_forwarded(
        self, monkeypatch: pytest.MonkeyPatch, tmp_path: Path
    ):
        """--python 指定的解释器路径被透传给安装内核。"""
        seen_python: dict[str, Path] = {}
        target = tmp_path / "custompython"

        def fake_install(python_exe, *, profile, network_type, progress_callback):
            seen_python["python"] = python_exe
            return True, "ok"

        monkeypatch.setattr(
            dependency_bootstrap, "install_backend_dependencies", fake_install
        )

        dependency_bootstrap.main(["--profile", "cpu", "--python", str(target)])

        assert seen_python["python"] == target

    def test_auto_profile_does_not_probe_gpu_before_retired_diagnostic(
        self,
        monkeypatch: pytest.MonkeyPatch,
        capsys: pytest.CaptureFixture[str],
    ):
        """旧 auto 参数只透传诊断，不为退役请求探测 GPU。"""
        from vibeocr.runtime.environments import env_manager

        def forbidden():
            raise AssertionError("retired CLI probed GPU")

        monkeypatch.setattr(env_manager, "detect_gpu", forbidden)
        seen_profile: list[str] = []

        def fake_install(python_exe, *, profile, network_type, progress_callback):
            seen_profile.append(profile)
            return True, "ok"

        monkeypatch.setattr(
            dependency_bootstrap, "install_backend_dependencies", fake_install
        )

        code = dependency_bootstrap.main(["--profile", "auto"])

        assert code == 0
        assert seen_profile == ["auto"]

    def test_invalid_profile_choice_rejected(self, monkeypatch: pytest.MonkeyPatch):
        """argparse 拒绝非法 profile 选项（SystemExit）。"""
        monkeypatch.setattr(
            dependency_bootstrap,
            "install_backend_dependencies",
            lambda *a, **k: (True, ""),
        )

        with pytest.raises(SystemExit):
            dependency_bootstrap.main(["--profile", "bogus"])


@pytest.mark.parametrize("operation", ["install", "switch"])
def test_retired_native_entrypoints_reject_before_environment_side_effects(
    tmp_path, monkeypatch, operation
):
    from vibeocr.runtime.environments import env_manager

    def forbidden(*args, **kwargs):
        raise AssertionError("retired request reached environment mutation")

    monkeypatch.setattr(env_manager, "_run_pip", forbidden)
    monkeypatch.setattr(env_manager, "_load_dep_specs", forbidden)
    monkeypatch.setattr(env_manager.subprocess, "Popen", forbidden)
    marker = tmp_path / "healthy.txt"
    marker.write_text("old healthy environment", encoding="utf-8")
    if operation == "install":
        success, message = env_manager.install_backend_dependencies(
            tmp_path / "python.exe"
        )
    else:
        success, message = env_manager.switch_paddle_backend(tmp_path, "cpu")
    assert not success
    assert "已退役" in message and "ONNX" in message
    assert marker.read_text(encoding="utf-8") == "old healthy environment"
