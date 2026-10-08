from __future__ import annotations

import json
import os
import sys
import time
from importlib.resources import files
from pathlib import Path

import httpx
import jsonschema
import pytest
from test_runtime_download_progress import _artifact, _transport
from vibeocr.runtime.environments import runtime_installer as installer
from vibeocr.runtime.environments.managed_install_progress import ManagedInstallObserver


def _observer(events: list[dict]) -> ManagedInstallObserver:
    return ManagedInstallObserver(
        events.append, lambda: None, "a" * 32, "test-env", 2, ["tuna-pypi"]
    )


def test_effective_packages_are_deduplicated_and_marker_filtered(
    tmp_path: Path,
) -> None:
    lock = tmp_path / "requirements.txt"
    lock.write_text(
        'Foo.Bar==1.0\nfoo_bar==1.0\nother==2; python_version < "3.0"\n',
        encoding="utf-8",
    )
    events: list[dict] = []
    observer = _observer(events)
    observer.requirements(lock, "3.13.7")
    observer.bundled()
    observer.installed_batch()
    observer.finish("failed", "verification failed")
    last = events[-1]
    assert last["state"] == "failed"
    assert last["download_files_total"] == 0
    assert last["dependencies"] == [
        {
            "name": "foo-bar",
            "version": "1.0",
            "download_state": "bundled",
            "install_state": "installed",
        }
    ]
    schema = json.loads(
        files("vibeocr.runtime_contracts")
        .joinpath("runtime-host.schema.json")
        .read_text(encoding="utf-8")
    )
    for event in events:
        jsonschema.validate(event, schema)
    assert [event["seq"] for event in events] == list(range(1, len(events) + 1))


def test_unparsed_requirements_and_conflicting_pins_keep_total_unknown(
    tmp_path: Path,
) -> None:
    lock = tmp_path / "requirements.txt"
    lock.write_text(
        "a==1\na==2\nnot a requirement ???\n --hash=sha256:abc\n# comment\n",
        encoding="utf-8",
    )
    observer = _observer([])
    observer.requirements(lock, "3.13.7")
    assert not observer.total_known
    assert len(observer.dependencies) == 2
    lock.write_text(
        'a @ https://example.invalid/a-1.0-py3-none-any.whl ; sys_platform == "win32"\nlinux==1 ; sys_platform == "linux"\n',
        encoding="utf-8",
    )
    observer.requirements(lock, "3.13.7")
    assert observer.total_known
    assert [item["name"] for item in observer.dependencies] == ["a"]
    assert observer.dependencies[0]["version"] == "1.0"


def test_cached_files_are_not_new_downloads(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    a, allowed_a = _artifact(b"cached")
    b, allowed_b = _artifact(b"new", "https://example.invalid/b.whl")
    b = installer._ResolvedArtifact("b", b.url, b.sha256, "b.whl")
    (tmp_path / a.filename).write_bytes(b"cached")
    lock = tmp_path / "requirements.txt"
    lock.write_text("a==1\nb==2\n", encoding="utf-8")
    events: list[dict] = []
    observer = _observer(events)
    observer.requirements(lock, "3.13.7")
    _transport(
        monkeypatch,
        lambda request: httpx.Response(
            200,
            headers={"Content-Length": "3"},
            stream=httpx.ByteStream(b"" if request.method == "HEAD" else b"new"),
        ),
    )
    installer._download_resolved_artifacts(
        (a, b), {**allowed_a, "b": allowed_b["a"]}, tmp_path, observer
    )
    assert events[0]["download_files_total"] is None
    assert events[-1]["download_files_total"] == 1
    assert events[-1]["download_files_completed"] == 1
    assert [item["download_state"] for item in events[-1]["dependencies"]] == [
        "cached",
        "downloaded",
    ]
    assert all(
        item["install_state"] == "pending" for item in events[-1]["dependencies"]
    )
    second: list[dict] = []
    cached = _observer(second)
    cached.requirements(lock, "3.13.7")
    installer._download_resolved_artifacts(
        (a, b), {**allowed_a, "b": allowed_b["a"]}, tmp_path, cached
    )
    assert (
        second[-1]["download_files_total"]
        == second[-1]["download_files_completed"]
        == 0
    )
    assert all(
        item["download_state"] == "cached" for item in second[-1]["dependencies"]
    )


def test_real_command_output_arrives_before_exit_and_is_redacted() -> None:
    observed: list[tuple[float, dict]] = []
    observer = ManagedInstallObserver(
        lambda event: observed.append((time.monotonic(), event)),
        lambda: None,
        "b" * 32,
        "env",
        1,
        [],
    )
    installer._run_install_command(
        [
            sys._base_executable,
            "-u",
            "-c",
            'import sys,time; print("Collecting sample"); print("https://user:password@example.invalid/a?token=secret", file=sys.stderr); print("x"*5000); time.sleep(0.5); print("Successfully installed sample-1")',
        ],
        timeout=10,
        env=dict(os.environ),
        reporter=observer,
        heartbeat_code="runtime.install_profile",
    )
    finished = time.monotonic()
    logs = [(when, event["log"]) for when, event in observed if "log" in event]
    assert any(
        finished - when > 0.3 and "Collecting sample" in log["text"]
        for when, log in logs
    )
    assert any(log["stream"] == "stderr" for _, log in logs)
    assert any(log["truncated"] for _, log in logs)
    assert "password" not in json.dumps(observed) and "secret" not in json.dumps(
        observed
    )
    assert observer.state == "running"


def test_observer_failure_does_not_break_command_or_cancellation() -> None:
    def broken(event: dict) -> None:
        raise RuntimeError("unavailable UI")

    observer = ManagedInstallObserver(broken, lambda: None, "c" * 32, "env", 1, [])
    installer._run_install_command(
        [sys._base_executable, "-c", 'print("Collecting sample")'],
        timeout=10,
        env=dict(os.environ),
        reporter=observer,
        heartbeat_code="runtime.install_profile",
    )

    def cancelled() -> None:
        raise installer.RuntimeOperationCancelled("cancelled")

    observer.cancel = cancelled
    with pytest.raises(installer.RuntimeOperationCancelled):
        installer._run_install_command(
            [sys._base_executable, "-c", 'print("never")'],
            timeout=10,
            env=dict(os.environ),
            reporter=observer,
            heartbeat_code="runtime.install_profile",
        )
