from __future__ import annotations

import hashlib
import io
import json
import os
import subprocess
import sys
import tarfile
import threading
import zipfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import pytest
from vibeocr.runtime.environments import runtime_installer as installer


def _wheel(name: str, module: str) -> bytes:
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, "w") as archive:
        archive.writestr(f"{module}.py", "VALUE = 'local'\n")
        archive.writestr(
            f"{module}-1.dist-info/METADATA",
            f"Metadata-Version: 2.1\nName: {name}\nVersion: 1\n",
        )
        archive.writestr(
            f"{module}-1.dist-info/WHEEL",
            "Wheel-Version: 1.0\nGenerator: fixture\nRoot-Is-Purelib: true\nTag: py3-none-any\n",
        )
        archive.writestr(f"{module}-1.dist-info/RECORD", "")
    return stream.getvalue()


def _report(
    root: Path, name: str, filename: str, payload: bytes, url: str
) -> tuple[Path, Path]:
    downloads = root / "downloads" / "artifacts"
    downloads.mkdir(parents=True)
    (downloads / filename).write_bytes(payload)
    report = root / "resolve" / "report.json"
    report.parent.mkdir()
    digest = hashlib.sha256(payload).hexdigest()
    report.write_text(
        json.dumps(
            {
                "install": [
                    {
                        "metadata": {"name": name, "version": "1"},
                        "download_info": {
                            "url": url,
                            "archive_info": {"hashes": {"sha256": digest}},
                        },
                    }
                ]
            }
        ),
        encoding="utf-8",
    )
    return downloads, report


def _pip(
    python: Path, requirements: Path, endpoint: str, target: Path
) -> subprocess.CompletedProcess[str]:
    # Target packages are explicit verified local files; only isolated build
    # requirements may use the selected endpoint.
    env = {
        k: v for k, v in os.environ.items() if not k.upper().startswith(("PIP_", "UV_"))
    }
    env.update(
        PIP_CONFIG_FILE=os.devnull,
        PIP_DISABLE_PIP_VERSION_CHECK="1",
        PIP_NO_INPUT="1",
        PYTHONNOUSERSITE="1",
    )
    return subprocess.run(
        [
            str(python),
            "-m",
            "pip",
            "install",
            "--disable-pip-version-check",
            "--no-deps",
            "--require-hashes",
            "--index-url",
            endpoint,
            "--find-links",
            str(target.parent / "downloads" / "artifacts"),
            "--target",
            str(target),
            "-r",
            str(requirements),
        ],
        env=env,
        capture_output=True,
        text=True,
        timeout=45,
    )


@pytest.mark.parametrize("direct_url", [False, True])
def test_verified_local_wheel_never_fetches_same_version_or_original_direct_url(
    tmp_path: Path, direct_url: bool
) -> None:
    payload = _wheel("local-target", "local_target")
    requests: list[str] = []

    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            requests.append(self.path)
            self.send_response(200)
            self.send_header(
                "Content-Type",
                "application/octet-stream"
                if self.path.endswith(".whl")
                else "text/html",
            )
            self.end_headers()
            self.wfile.write(
                payload
                if self.path.endswith(".whl")
                else f'<a href="/local_target-1-py3-none-any.whl#sha256={digest}">wheel</a>'.encode()
            )

        def log_message(self, *args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    endpoint = f"http://127.0.0.1:{server.server_port}/simple/"
    url = f"http://127.0.0.1:{server.server_port}/local_target-1-py3-none-any.whl"
    downloads, report = _report(
        tmp_path, "local-target", "local_target-1-py3-none-any.whl", payload, url
    )
    lock = tmp_path / "requirements.lock"
    digest = hashlib.sha256(payload).hexdigest()
    declaration = f"local-target @ {url}" if direct_url else "local-target==1"
    lock.write_text(f"{declaration} --hash=sha256:{digest}\n", encoding="utf-8")
    try:
        local = installer._local_install_requirements(
            lock, downloads, report, "3.13.16"
        )
        result = _pip(
            Path(sys._base_executable), local, endpoint, tmp_path / "installed"
        )
        assert result.returncode == 0, result.stdout + result.stderr
        assert (
            tmp_path / "installed" / "local_target.py"
        ).read_text() == "VALUE = 'local'\n"
        assert requests == []
        print(
            f"local-wheel direct_url={direct_url} target_requests={len(requests)} installed=local"
        )
        assert "file:///" in local.read_text() and url not in local.read_text()
    finally:
        server.shutdown()
        server.server_close()
        thread.join(2)


def test_local_requirements_reject_incomplete_closure_and_honor_windows_markers(
    tmp_path: Path,
) -> None:
    payload = _wheel("local-target", "local_target")
    downloads, report = _report(
        tmp_path,
        "local-target",
        "local_target-1-py3-none-any.whl",
        payload,
        "https://example.invalid/local_target-1-py3-none-any.whl",
    )
    lock = tmp_path / "requirements.lock"
    digest = hashlib.sha256(payload).hexdigest()
    lock.write_text(
        f'local-target==1 --hash=sha256:{digest}\nmissing==1; sys_platform == "win32" --hash=sha256:{digest}\n',
        encoding="utf-8",
    )
    with pytest.raises(installer.RuntimeInstallError, match="closure"):
        installer._local_install_requirements(lock, downloads, report, "3.13.16")
    lock.write_text(
        f'local-target==1 --hash=sha256:{digest}\nmissing==1; sys_platform == "linux" --hash=sha256:{digest}\n',
        encoding="utf-8",
    )
    assert (
        "missing"
        not in installer._local_install_requirements(
            lock, downloads, report, "3.13.16"
        ).read_text()
    )
    report.write_text(report.read_text().replace(digest, "0" * 64))
    with pytest.raises(installer.RuntimeInstallError, match="hash"):
        installer._local_install_requirements(lock, downloads, report, "3.13.16")


def test_local_sdist_preserves_isolated_build_dependency_source(tmp_path: Path) -> None:
    build_wheel = _wheel("build-tool", "build_tool")
    backend = """import pathlib, zipfile, build_tool

def build_wheel(wheel_directory, config_settings=None, metadata_directory=None):
    name = 'source_target-1-py3-none-any.whl'
    with zipfile.ZipFile(pathlib.Path(wheel_directory) / name, 'w') as z:
        z.writestr('source_target.py', 'VALUE = ' + repr(build_tool.VALUE))
        z.writestr('source_target-1.dist-info/METADATA', 'Metadata-Version: 2.1\\nName: source-target\\nVersion: 1\\n')
        z.writestr('source_target-1.dist-info/WHEEL', 'Wheel-Version: 1.0\\nRoot-Is-Purelib: true\\nTag: py3-none-any\\n')
        z.writestr('source_target-1.dist-info/RECORD', '')
    return name
"""
    data = io.BytesIO()
    with tarfile.open(fileobj=data, mode="w:gz") as archive:
        for name, text in {
            "pyproject.toml": '[build-system]\nrequires = ["build-tool==1"]\nbuild-backend = "backend"\nbackend-path = ["."]\n',
            "backend.py": backend,
        }.items():
            content = text.encode()
            info = tarfile.TarInfo(f"source_target-1/{name}")
            info.size = len(content)
            archive.addfile(info, io.BytesIO(content))
    payload = data.getvalue()
    requests: list[str] = []

    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            requests.append(self.path)
            self.send_response(200)
            self.send_header(
                "Content-Type",
                "application/octet-stream"
                if self.path.endswith(".whl")
                else "text/html",
            )
            self.end_headers()
            self.wfile.write(
                build_wheel
                if self.path.endswith(".whl")
                else b'<a href="/build_tool-1-py3-none-any.whl">build wheel</a>'
            )

        def log_message(self, *args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    url = f"http://127.0.0.1:{server.server_port}/source_target-1.tar.gz"
    downloads, report = _report(
        tmp_path, "source-target", "source_target-1.tar.gz", payload, url
    )
    lock = tmp_path / "requirements.lock"
    lock.write_text(
        f"source-target @ {url} --hash=sha256:{hashlib.sha256(payload).hexdigest()}\n",
        encoding="utf-8",
    )
    try:
        local = installer._local_install_requirements(
            lock, downloads, report, "3.13.16"
        )
        result = _pip(
            Path(sys._base_executable),
            local,
            f"http://127.0.0.1:{server.server_port}/simple/",
            tmp_path / "installed",
        )
        assert result.returncode == 0, result.stdout + result.stderr
        assert (
            tmp_path / "installed" / "source_target.py"
        ).read_text() == "VALUE = 'local'"
        assert "/source_target-1.tar.gz" not in requests
        print(f"local-sdist target_requests=0 isolated_build_requests={requests}")
        assert (
            "/simple/build-tool/" in requests
            and "/build_tool-1-py3-none-any.whl" in requests
        )
    finally:
        server.shutdown()
        server.server_close()
        thread.join(2)


@pytest.mark.parametrize("entry", ["managed", "maintenance"])
def test_both_product_install_entries_bind_verified_local_targets(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, entry: str
) -> None:
    from dataclasses import replace

    from test_runtime_installer import _pypi_source, _release
    from vibeocr.runtime.environments import managed_environments
    from vibeocr.runtime.environments.runtime_manifest import load_runtime_manifest

    manifest_path, component = _release(tmp_path / "release")
    manifest = load_runtime_manifest(manifest_path)
    payload = _wheel("local-target", "local_target")
    lock = tmp_path / "target.lock"
    lock.write_text(
        f"local-target @ https://example.invalid/local_target-1-py3-none-any.whl --hash=sha256:{hashlib.sha256(payload).hexdigest()}\n",
        encoding="utf-8",
    )
    scope = replace(
        manifest.profiles["win-x64-base"].scopes[0],
        lock_path=lock,
        sha256=hashlib.sha256(lock.read_bytes()).hexdigest(),
    )
    commands: list[list[str]] = []

    def prepare(python, lock, endpoint, cache, reporter, env):
        downloads, report = _report(
            cache,
            "local-target",
            "local_target-1-py3-none-any.whl",
            payload,
            "https://example.invalid/local_target-1-py3-none-any.whl",
        )
        report.rename(report.with_name(f"{lock.stem}-report.json"))
        return downloads

    module = managed_environments if entry == "managed" else installer
    monkeypatch.setattr(module, "_prepare_online_artifacts", prepare)
    monkeypatch.setattr(
        module,
        "_run_install_command",
        lambda command, **kwargs: commands.append(command),
    )
    if entry == "managed":
        store = managed_environments.ManagedEnvironmentStore(
            product_root=tmp_path / "product",
            component_lock=component,
            runtime_manifest=manifest_path,
            base_python=sys._base_executable,
        )
        store._install_scope(Path("python"), scope, "https://example.invalid/simple/")
    else:
        root = tmp_path / "candidate"
        root.mkdir()
        monkeypatch.setattr(
            installer,
            "_extract_python_archive",
            lambda archive, destination, **kwargs: (
                destination / "python.exe"
            ).write_bytes(b"python"),
        )
        installer._default_install_runner(root, manifest, scope, _pypi_source())
    command = commands[0]
    local = Path(command[command.index("-r") + 1])
    assert "--no-deps" in command and "--require-hashes" in command
    assert (
        local != lock
        and "file:///" in local.read_text()
        and "example.invalid" not in local.read_text()
    )
    assert (
        "--no-build-isolation" not in command and "--only-binary=:all:" not in command
    )


@pytest.mark.parametrize("complete_inputs", [False, True])
def test_resolve_cache_evolution_refreshes_report_but_keeps_verified_artifacts(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, complete_inputs: bool
) -> None:
    payload = _wheel("local-target", "local_target")
    downloads, report = _report(
        tmp_path,
        "local-target",
        "local_target-1-py3-none-any.whl",
        payload,
        "https://example.invalid/local_target-1-py3-none-any.whl",
    )
    lock = tmp_path / "target.lock"
    lock.write_text(
        f"local-target==1 --hash=sha256:{hashlib.sha256(payload).hexdigest()}\n",
        encoding="utf-8",
    )
    report = report.rename(report.with_name(f"{lock.stem}-report.json"))
    endpoint = "https://example.invalid/simple/"
    inputs: dict[str, str | bool] = {"lock": lock.read_text(), "endpoint": endpoint}
    if complete_inputs:
        inputs["ignore_installed"] = True
    report.with_suffix(".inputs.json").write_text(json.dumps(inputs), encoding="utf-8")
    commands: list[list[str]] = []
    monkeypatch.setattr(
        installer,
        "_run_install_command",
        lambda command, **kwargs: commands.append(command),
    )
    assert (
        installer._resolve_online_report(
            Path("python"), lock, endpoint, tmp_path, None, {}
        )
        == report
    )
    assert len(commands) == (0 if complete_inputs else 1)
    if commands:
        assert "--ignore-installed" in commands[0]
    assert (
        json.loads(report.with_suffix(".inputs.json").read_text())["ignore_installed"]
        is True
    )
    assert (downloads / "local_target-1-py3-none-any.whl").read_bytes() == payload


@pytest.mark.parametrize("publish_fails", [False, True])
def test_shared_local_input_is_published_whole_and_preserves_previous_on_failure(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, publish_fails: bool
) -> None:
    payload = _wheel("local-target", "local_target")
    downloads, report = _report(
        tmp_path,
        "local-target",
        "local_target-1-py3-none-any.whl",
        payload,
        "https://example.invalid/local_target-1-py3-none-any.whl",
    )
    lock = tmp_path / "target.lock"
    lock.write_text(
        f"local-target==1 --hash=sha256:{hashlib.sha256(payload).hexdigest()}\n",
        encoding="utf-8",
    )
    local = report.with_name("target-local.txt")
    local.write_text("previous complete input\n", encoding="utf-8")
    original = installer._replace_transient_windows_lock
    observed = []

    def publish(source: Path, destination: Path) -> None:
        # Another environment reading the shared final name still sees the old
        # complete input while the new file is fully written elsewhere.
        assert destination.read_text() == "previous complete input\n"
        assert source != destination and source.read_text().startswith(
            "local-target @ file:///"
        )
        assert source.read_text().endswith("\n")
        observed.append(True)
        if publish_fails:
            raise OSError("publication interrupted")
        original(source, destination)

    monkeypatch.setattr(installer, "_replace_transient_windows_lock", publish)
    if publish_fails:
        with pytest.raises(OSError, match="publication interrupted"):
            installer._local_install_requirements(lock, downloads, report, "3.13.16")
        assert local.read_text() == "previous complete input\n"
    else:
        assert (
            installer._local_install_requirements(lock, downloads, report, "3.13.16")
            == local
        )
        assert local.read_text().startswith("local-target @ file:///")
    assert observed == [True] and not list(report.parent.glob("*.tmp"))
