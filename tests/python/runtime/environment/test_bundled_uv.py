from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path

import pytest
from vibeocr.runtime.environments import bundled_uv
from vibeocr.runtime.environments import runtime_installer as installer


def _reporter(tmp_path, sink=None):
    from vibeocr.runtime.environments.runtime_maintenance import (
        RuntimeMaintenanceReporter,
        RuntimeProfileDescriptor,
    )

    reporter = RuntimeMaintenanceReporter(
        state_root=tmp_path,
        profile=RuntimeProfileDescriptor("win-x64-cpu", "cpu", ()),
        event_sink=sink,
    )
    reporter.start(
        "ensure",
        total_steps=7,
        operation_id="resolve-test",
        download_source_ids=("paddleocr-modelscope",),
    )
    reporter.advance(
        phase="install_profile",
        current=4,
        total=7,
        message_code="runtime.install_profile",
    )
    return reporter


def _windows_process_running(pid: int) -> bool:
    import ctypes
    import ctypes.wintypes as wintypes

    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.WaitForSingleObject.restype = wintypes.DWORD
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.OpenProcess(0x00100000, False, pid)
    if not handle:
        assert ctypes.get_last_error() == 87  # PID no longer exists.
        return False
    try:
        return kernel.WaitForSingleObject(handle, 2000) == 258
    finally:
        kernel.CloseHandle(handle)


def test_uv_environment_removes_parent_overrides():
    inherited = dict(
        UV_INDEX="hostile",
        PIP_INDEX_URL="hostile",
        VIRTUAL_ENV="hostile",
        CONDA_PREFIX="hostile",
        PYTHONPATH="hostile",
        PYTHONHOME="hostile",
        PATH="retained",
        TEMP="retained",
    )
    assert bundled_uv.uv_environment(inherited) == {
        "PATH": "retained",
        "TEMP": "retained",
    }


@pytest.mark.skipif(os.name != "nt", reason="native Windows uv containment")
def test_native_uv_never_starts_before_job_assignment(tmp_path: Path, monkeypatch):
    pylock = tmp_path / "pylock.toml"
    lock = tmp_path / "requirements.txt"
    lock.write_text("", encoding="utf-8")
    command = installer._uv_command(Path(sys.executable), "compile") + [
        "--offline",
        "--format",
        "pylock.toml",
        "-o",
        str(pylock),
        str(lock),
    ]
    monkeypatch.setattr(
        installer.JobObjectGuard, "assign_from_popen", lambda self, process: False
    )
    with pytest.raises(
        installer.RuntimeInstallError, match="process_containment_failed"
    ):
        installer._run_install_command(
            command,
            timeout=5,
            env=dict(os.environ),
            reporter=None,
            heartbeat_code="runtime.resolve_packages",
            gate_python=Path(sys.executable),
        )
    assert not pylock.exists()


@pytest.mark.skipif(os.name != "nt", reason="native Windows uv build descendants")
@pytest.mark.parametrize("cancel", [False, True])
def test_native_uv_build_descendants_are_owned(tmp_path: Path, cancel: bool):
    import threading

    from vibeocr.runtime.environments.runtime_maintenance import (
        RuntimeOperationCancelled,
        RuntimeOperationStore,
    )

    source = tmp_path / "source"
    source.mkdir()
    (source / "pyproject.toml").write_text(
        '[build-system]\nrequires=[]\nbuild-backend="backend"\nbackend-path=["."]\n',
        encoding="utf-8",
    )
    pid_file = tmp_path / "build-child.pid"
    (source / "backend.py").write_text(
        "import subprocess,sys,pathlib,time\n"
        "def build_wheel(*args, **kwargs):\n"
        " p=subprocess.Popen([sys.executable,'-c','import time; time.sleep(30)'])\n"
        + f" pathlib.Path({str(pid_file)!r}).write_text(str(p.pid))\n"
        + " time.sleep(30)\n",
        encoding="utf-8",
    )
    reporter = _reporter(tmp_path / "state")
    stop = threading.Event()

    def request_cancel():
        while not stop.wait(0.05):
            if pid_file.exists():
                RuntimeOperationStore(tmp_path / "state").request_cancel("resolve-test")
                return

    worker = threading.Thread(target=request_cancel, daemon=True) if cancel else None
    if worker:
        worker.start()
    outsider = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
    try:
        command = installer._uv_command(Path(sys.executable), "install") + [
            "--offline",
            "--no-build-isolation",
            "--no-deps",
            "--target",
            str(tmp_path / "target"),
            str(source),
        ]
        with pytest.raises(
            RuntimeOperationCancelled if cancel else installer.RuntimeInstallError
        ):
            installer._run_install_command(
                command,
                timeout=5,
                env=installer._uv_install_environment(dict(os.environ), tmp_path),
                reporter=reporter,
                heartbeat_code="runtime.install_profile",
                gate_python=Path(sys.executable),
            )
        assert pid_file.exists(), "real uv did not reach the isolated build backend"
        assert not _windows_process_running(int(pid_file.read_text()))
        assert outsider.poll() is None
    finally:
        stop.set()
        if worker:
            worker.join(timeout=1)
        outsider.kill()
        outsider.wait(timeout=5)


def test_no_pip_venv_offline_install_and_check_ignore_parent_overrides(tmp_path: Path):
    import io
    import zipfile

    candidate = tmp_path / "candidate"
    subprocess.run(
        [sys.executable, "-I", "-m", "venv", "--without-pip", str(candidate)],
        check=True,
        capture_output=True,
    )
    python = candidate / ("Scripts/python.exe" if os.name == "nt" else "bin/python")
    (tmp_path / "uv.toml").write_text(
        "this is deliberately invalid TOML", encoding="utf-8"
    )
    wheel = tmp_path / "broken-1-py3-none-any.whl"
    data = io.BytesIO()
    with zipfile.ZipFile(data, "w") as archive:
        archive.writestr("broken.py", "VALUE=1\n")
        archive.writestr(
            "broken-1.dist-info/METADATA",
            "Metadata-Version: 2.1\nName: broken\nVersion: 1\nRequires-Dist: missing-fixture-dependency==1\n",
        )
        archive.writestr(
            "broken-1.dist-info/WHEEL",
            "Wheel-Version: 1.0\nRoot-Is-Purelib: true\nTag: py3-none-any\n",
        )
        archive.writestr("broken-1.dist-info/RECORD", "")
    wheel.write_bytes(data.getvalue())
    inherited = {
        **os.environ,
        "UV_INDEX": "https://unconfirmed.invalid",
        "UV_PYTHON": "missing",
        "PIP_INDEX_URL": "https://unconfirmed.invalid",
        "VIRTUAL_ENV": str(tmp_path / "other"),
        "CONDA_PREFIX": str(tmp_path / "other"),
        "PYTHONPATH": "missing",
    }
    env = installer._uv_install_environment(inherited, tmp_path)
    command = installer._uv_command(python, "install") + [
        "--directory",
        str(tmp_path),
        "--offline",
        "--no-deps",
        str(wheel),
    ]
    installer._run_install_command(
        command,
        timeout=30,
        env=env,
        reporter=None,
        heartbeat_code="runtime.install_profile",
        gate_python=python,
    )
    installer._run_install_command(
        [
            str(python),
            "-I",
            "-c",
            "import broken,importlib.util; assert importlib.util.find_spec('pip') is None",
        ],
        timeout=10,
        env=env,
        reporter=None,
        heartbeat_code="runtime.verify_runtime",
    )
    with pytest.raises(
        installer.RuntimeInstallError, match="missing-fixture-dependency"
    ):
        installer._run_install_command(
            installer._uv_command(python, "check") + ["--offline"],
            timeout=10,
            env=env,
            reporter=None,
            heartbeat_code="runtime.verify_runtime",
            gate_python=python,
        )


def test_development_tool_does_not_fall_back_to_another_installation(
    tmp_path, monkeypatch
):
    from importlib.metadata import PackagePath
    from types import SimpleNamespace

    owned = PackagePath("../../Scripts/uv.exe" if os.name == "nt" else "../../bin/uv")
    package = SimpleNamespace(
        version=bundled_uv.UV_VERSION,
        files=[owned],
        locate_file=lambda entry: tmp_path / "missing-owned-uv",
    )
    monkeypatch.setattr(bundled_uv, "distribution", lambda name: package)
    with pytest.raises(ValueError, match="bundled uv is missing"):
        bundled_uv.uv_executable()
