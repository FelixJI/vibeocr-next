from __future__ import annotations

import json
import os
import sys
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


def test_real_command_output_arrives_before_exit_and_is_redacted(
    tmp_path: Path,
) -> None:
    release = tmp_path / "release"
    observed: list[dict] = []
    streams: set[str] = set()

    def receive(event: dict) -> None:
        observed.append(event)
        if log := event.get("log"):
            if log["stream"] == "stderr" or "Collecting sample" in log["text"]:
                streams.add(log["stream"])
            if streams == {"stdout", "stderr"}:
                release.touch()

    observer = ManagedInstallObserver(receive, lambda: None, "b" * 32, "env", 1, [])
    installer._run_install_command(
        [
            sys._base_executable,
            "-u",
            "-c",
            "import pathlib,sys,time; "
            'print("Collecting sample", flush=True); '
            'print("https://user:password@example.invalid/a?token=secret", file=sys.stderr, flush=True); '
            'print("x"*5000, flush=True); '
            "release=pathlib.Path(sys.argv[1]); deadline=time.monotonic()+10; "
            "exec('while not release.exists():\\n"
            " if time.monotonic() >= deadline: sys.exit(7)\\n"
            " time.sleep(0.01)'); "
            'print("Successfully installed sample-1")',
            str(release),
        ],
        timeout=15,
        env=dict(os.environ),
        reporter=observer,
        heartbeat_code="runtime.install_profile",
    )
    logs = [event["log"] for event in observed if "log" in event]
    # A buffered-to-exit observer cannot release the waiting child successfully.
    assert release.is_file() and streams == {"stdout", "stderr"}
    assert any("Collecting sample" in log["text"] for log in logs)
    assert any(log["truncated"] for log in logs)
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


@pytest.mark.parametrize("resolver_fails", [False, True])
def test_online_artifacts_keep_resolve_phase_until_resolution_succeeds(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, resolver_fails: bool
) -> None:
    lock = tmp_path / "requirements.txt"
    lock.write_text("a==1\n", encoding="utf-8")
    report = tmp_path / "report.json"
    report.write_text(
        json.dumps(
            {
                "install": [
                    {
                        "metadata": {"name": "a", "version": "1"},
                        "download_info": {"url": "https://example.invalid/a.whl"},
                    }
                ]
            }
        ),
        encoding="utf-8",
    )
    events: list[dict] = []
    observer = _observer(events)
    observer.set_phase("resolve")
    resolving_phases: list[str] = []
    downloaded = False
    original_parse = installer._parse_resolve_report

    def resolve(*args) -> Path:
        resolving_phases.append(observer.phase)
        if resolver_fails:
            raise installer.RuntimeInstallError("resolver failed")
        return report

    def parse(path: Path, root: Path) -> tuple[installer._ResolvedArtifact, ...]:
        resolving_phases.append(observer.phase)
        return original_parse(path, root)

    def download(artifacts, allowed, root: Path, reporter) -> Path:
        nonlocal downloaded
        assert reporter is observer and observer.phase == "download"
        downloaded = True
        return root

    monkeypatch.setattr(installer, "_resolve_online_report", resolve)
    monkeypatch.setattr(installer, "_parse_resolve_report", parse)
    monkeypatch.setattr(installer, "_download_resolved_artifacts", download)
    if resolver_fails:
        with pytest.raises(installer.RuntimeInstallError, match="resolver failed"):
            installer._prepare_online_artifacts(
                Path(sys._base_executable),
                lock,
                "https://example.invalid",
                tmp_path,
                observer,
                {},
            )
        assert not downloaded and observer.phase == "resolve"
        assert resolving_phases == ["resolve"]
        assert all(event["phase"] != "download" for event in events)
    else:
        installer._prepare_online_artifacts(
            Path(sys._base_executable),
            lock,
            "https://example.invalid",
            tmp_path,
            observer,
            {},
        )
        assert downloaded and resolving_phases == ["resolve", "resolve"]
