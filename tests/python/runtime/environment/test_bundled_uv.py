from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

import pytest
from test_runtime_local_artifacts import _wheel
from vibeocr.runtime.environments import bundled_uv
from vibeocr.runtime.environments import runtime_installer as installer


def test_real_pylock_adapts_direct_wheel_with_markers_and_extras(tmp_path: Path):
    import hashlib

    downloads = tmp_path / "downloads"
    downloads.mkdir()
    payload = _wheel("target", "target")
    wheel = downloads / "target-1-py3-none-any.whl"
    wheel.write_bytes(payload)
    lock = tmp_path / "requirements.lock"
    digest = hashlib.sha256(payload).hexdigest()
    lock.write_text(
        f'target[feature] @ {wheel.as_uri()} ; sys_platform == "win32" --hash=sha256:{digest}\n'
        + f'absent==1; sys_platform == "linux" --hash=sha256:{digest}\n',
        encoding="utf-8",
    )
    pylock = tmp_path / "pylock.toml"
    command = installer._uv_command(Path(sys.executable), "compile") + [
        "--offline",
        "--no-index",
        "--format",
        "pylock.toml",
        "--generate-hashes",
        "--python-platform",
        "x86_64-pc-windows-msvc",
        "-o",
        str(pylock),
        str(lock),
    ]
    installer._run_install_command(
        command,
        timeout=30,
        env=installer._uv_install_environment(dict(os.environ), tmp_path),
        reporter=None,
        heartbeat_code="runtime.resolve_packages",
        gate_python=Path(sys.executable),
    )
    report = installer._pylock_report(pylock, lock, "3.13.16", downloads)
    assert len(report["install"]) == 1
    assert report["install"][0]["metadata"] == {"name": "target", "version": "1"}
    report_path = tmp_path / "report.json"
    report_path.write_text(json.dumps(report), encoding="utf-8")
    local = installer._local_install_requirements(
        lock, downloads, report_path, "3.13.16"
    )
    assert "target[feature] @ file:///" in local.read_text()


def test_real_pylock_keeps_percent_encoded_local_version_direct_url(tmp_path: Path):
    # A torch-style local version (`2.x+cu126`) reaches uv through a direct URL
    # whose `+` stays percent-encoded; real uv compile must preserve that exact
    # authoritative URL and hash in pylock and the local install input.
    import hashlib
    import threading
    import urllib.parse
    from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

    version = "2.0.1+cu126"
    filename = f"cu_target-{version}-py3-none-any.whl"
    encoded = filename.replace("+", "%2B")
    payload = _wheel("cu-target", "cu_target", version)
    digest = hashlib.sha256(payload).hexdigest()
    requests: list[str] = []

    class Handler(BaseHTTPRequestHandler):
        def do_GET(self) -> None:
            requests.append(self.path)
            body = (
                payload
                if urllib.parse.unquote(self.path.rsplit("/", 1)[-1]) == filename
                else b""
            )
            self.send_response(200 if body else 404)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *args) -> None:
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    url = f"http://127.0.0.1:{server.server_port}/wheels/{encoded}"
    lock = tmp_path / "requirements.lock"
    lock.write_text(f"cu-target @ {url} --hash=sha256:{digest}\n", encoding="utf-8")
    downloads = tmp_path / "downloads"
    downloads.mkdir()
    pylock = tmp_path / "pylock.toml"
    command = installer._uv_command(Path(sys.executable), "compile") + [
        "--no-index",
        "--format",
        "pylock.toml",
        "--generate-hashes",
        "--python-platform",
        "x86_64-pc-windows-msvc",
        "-o",
        str(pylock),
        str(lock),
    ]
    try:
        installer._run_install_command(
            command,
            timeout=45,
            env=installer._uv_install_environment(dict(os.environ), tmp_path),
            reporter=None,
            heartbeat_code="runtime.resolve_packages",
            gate_python=Path(sys.executable),
        )
        assert requests, "real uv did not fetch the localhost wheel"
        assert any(encoded in path for path in requests)
        (downloads / filename).write_bytes(payload)
        report = installer._pylock_report(pylock, lock, "3.13.16", downloads)
        entry = report["install"][0]
        assert entry["metadata"] == {"name": "cu-target", "version": version}
        assert entry["download_info"]["url"] == url
        assert entry["download_info"]["archive_info"]["hashes"]["sha256"] == digest
        report_path = tmp_path / "report.json"
        report_path.write_text(json.dumps(report), encoding="utf-8")
        local = installer._local_install_requirements(
            lock, downloads, report_path, "3.13.16"
        )
        text = local.read_text(encoding="utf-8")
        assert "cu-target @ file:///" in text
        assert encoded in text
        assert f"--hash=sha256:{digest}" in text
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=2)


@pytest.mark.parametrize(
    "fault", ["version", "hash", "missing", "extra", "incompatible", "direct_url"]
)
def test_pylock_rejects_changes_outside_authoritative_lock(tmp_path: Path, fault: str):
    digest = "a" * 64
    lock = tmp_path / "requirements.lock"
    lock.write_text(f"target==1 --hash=sha256:{digest}\n", encoding="utf-8")
    version = "2" if fault == "version" else "1"
    wheel = (
        f"target-{version}-cp313-cp313-"
        + ("manylinux_2_17_x86_64" if fault == "incompatible" else "win_amd64")
        + ".whl"
    )
    if fault == "direct_url":
        lock.write_text(
            f"target @ https://trusted.invalid/{wheel} --hash=sha256:{digest}\n",
            encoding="utf-8",
        )
    entry = f'[[packages]]\nname = "target"\nversion = "{version}"\nwheels = [{{url="https://index.invalid/{wheel}", hashes={{sha256="{("b" * 64) if fault == "hash" else digest}"}}}}]\n'
    if fault == "missing":
        entry = "packages = []\n"
    if fault == "extra":
        entry += f'[[packages]]\nname="extra"\nversion="1"\nwheels=[{{url="https://index.invalid/extra-1-py3-none-any.whl",hashes={{sha256="{digest}"}}}}]\n'
    pylock = tmp_path / "pylock.toml"
    pylock.write_text('lock-version="1.0"\n' + entry, encoding="utf-8")
    with pytest.raises(installer.RuntimeInstallError):
        installer._pylock_report(pylock, lock, "3.13.16", tmp_path)


def test_pylock_selects_allowed_windows_wheel(tmp_path: Path):
    digest = "a" * 64
    lock = tmp_path / "requirements.lock"
    lock.write_text(f"target==1 --hash=sha256:{digest}\n", encoding="utf-8")
    pylock = tmp_path / "pylock.toml"
    pylock.write_text(
        'lock-version="1.0"\n[[packages]]\nname="target"\nversion="1"\nwheels=['
        + f'{{url="https://index.invalid/target-1-cp313-cp313-manylinux_2_17_x86_64.whl",hashes={{sha256="{digest}"}}}},'
        + f'{{url="https://index.invalid/target-1-cp313-cp313-win_amd64.whl",hashes={{sha256="{digest}"}}}}]\n',
        encoding="utf-8",
    )
    report = installer._pylock_report(pylock, lock, "3.13.16", tmp_path)
    assert report["install"][0]["download_info"]["url"].endswith("win_amd64.whl")


@pytest.mark.parametrize("fault", ["missing", "corrupt", "version"])
def test_frozen_tool_failures_are_explicit(tmp_path: Path, monkeypatch, fault: str):
    monkeypatch.setattr(sys, "frozen", True, raising=False)
    monkeypatch.setattr(sys, "_MEIPASS", str(tmp_path), raising=False)
    if fault != "missing":
        executable = tmp_path / "uv" / "uv.exe"
        executable.parent.mkdir()
        executable.write_bytes(b"corrupt")
    if fault == "version":
        monkeypatch.setattr(
            bundled_uv.subprocess,
            "run",
            lambda *args, **kwargs: subprocess.CompletedProcess(
                args, 0, "uv 0.12.21", ""
            ),
        )
    with pytest.raises(ValueError, match="restore the Portable product"):
        bundled_uv.uv_executable()


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

    from test_resolver_process import _reporter, _windows_process_running
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
