"""Worker deadlines use completed work and retained process ownership."""

from __future__ import annotations

import json
import os
import subprocess
import sys
import threading
from pathlib import Path

import pytest
from vibeocr.runtime.environments.settings_store import (
    RuntimeSettingsStore,
    RuntimeSettingsStoreError,
)
from vibeocr.runtime.recognition.paddle_process_adapter import PaddleProcessAdapter
from vibeocr.runtime.recognition.worker_idle import WorkerIdleOwner
from vibeocr.runtime_contracts import SettingsSnapshot


def owner_fixture(stop=lambda: None):
    now = [100.0]
    owner = WorkerIdleOwner("test", stop, lock=threading.RLock(), clock=lambda: now[0])
    return owner, now


def worked(owner):
    release = owner.reserve()
    owner.starting()
    owner.started()
    owner.working()
    release()


def test_default_deadline_boundary_and_status_do_not_touch_clock():
    stops = []
    owner, now = owner_fixture(lambda: stops.append(True))
    worked(owner)
    for _ in range(5):
        assert owner.snapshot()["remaining_seconds"] == 600
    now[0] += 599
    owner.check_idle()
    assert not stops
    now[0] += 1
    owner.check_idle()
    assert stops == [True]
    assert owner.state == "idle_stopped"
    owner.close()


def test_reservation_protects_batches_queue_and_cancellation_cleanup():
    stops = []
    owner, now = owner_fixture(lambda: stops.append(True))
    queued = owner.reserve()
    assert owner.generation == 0 and not owner.running
    worked(owner)
    now[0] += 10000
    owner.check_idle()
    assert not stops
    assert owner.last_finished is None
    queued()
    assert owner.last_finished == now[0]
    now[0] += 600
    owner.check_idle()
    assert stops == [True]
    owner.close()


def test_shortening_and_persistent_policy_recompute_from_completed_work():
    owner, now = owner_fixture()
    worked(owner)
    now[0] += 700
    owner.configure(0)
    owner.check_idle()
    assert owner.running
    assert owner.last_finished == 100
    owner.configure(800)
    owner.check_idle()
    assert owner.running
    owner.configure(600)
    owner.check_idle()
    assert owner.state == "idle_stopped"
    owner.close()


def test_late_check_cannot_stop_new_generation_or_active_work():
    owner, now = owner_fixture()
    worked(owner)
    now[0] += 600
    owner.check_idle()
    release = owner.reserve()
    owner.starting()
    owner.started()
    owner.working()
    owner.check_idle()
    assert owner.generation == 2 and owner.running
    release()
    owner.check_idle()
    assert owner.running
    owner.close()


def test_failed_stop_retains_ownership_and_rejects_restart_until_confirmed():
    fail = [True]

    def stop():
        if fail[0]:
            raise OSError("secret must not reach diagnostics")

    owner, now = owner_fixture(stop)
    worked(owner)
    now[0] += 600
    with pytest.raises(OSError):
        owner.check_idle()
    assert owner.running and owner.state == "stop_failed"
    assert owner.snapshot()["error"] == "OSError"
    with pytest.raises(RuntimeError, match="ownership retained"):
        owner.reserve()
    fail[0] = False
    owner.stop("explicit_release")
    assert not owner.running
    owner.close()


@pytest.mark.parametrize("value", [-1, True, False, 1.5, "600", None, 2147483648])
def test_policy_rejects_invalid_integer_seconds(value):
    with pytest.raises(ValueError):
        SettingsSnapshot(paddle_idle_seconds=value)
    with pytest.raises(ValueError):
        SettingsSnapshot(mineru_idle_seconds=value)


@pytest.mark.parametrize(
    "pipelines,expected",
    [
        ([], 300),
        ([{"name": "OCR", "ttl_seconds": 120, "pinned": False}], 300),
        ([{"name": "OCR", "ttl_seconds": 700, "pinned": False}], 700),
        ([{"name": "OCR", "ttl_seconds": 0, "pinned": False}], 0),
        ([{"name": "OCR", "ttl_seconds": None, "pinned": True}], 0),
        ([{"name": "PaddleOCR-VL", "ttl_seconds": 0, "pinned": True}], 300),
    ],
)
def test_saved_v2_preferences_migrate_once_with_backup_and_unrelated_values(
    tmp_path, pipelines, expected
):
    path = tmp_path / "settings.json"
    raw = {
        "schema_version": 1,
        "settings": {
            "schema_version": 2,
            "residency": {"default_ttl_seconds": 300, "pipelines": pipelines},
            "extra": {
                "mineru_connection": {"mode": "remote", "api_key": "kept"},
                "unrelated": 123,
            },
            "download_source_ids": ["pypi"],
        },
    }
    path.write_text(json.dumps(raw), encoding="utf-8")
    before = path.read_bytes()
    store = RuntimeSettingsStore(path)
    value = store.load(SettingsSnapshot())
    assert value.paddle_idle_seconds == expected
    assert value.mineru_idle_seconds == 300
    assert value.extra == raw["settings"]["extra"]
    assert value.download_source_ids == ("pypi",)
    assert path.with_name(path.name + ".worker-idle.bak").read_bytes() == before
    assert store.load(SettingsSnapshot()) == value


@pytest.mark.parametrize(
    "mode",
    [
        "rapid_text",
        "windows_text",
        "paddle_document_vl",
        "paddle_formula",
        "rapid_table",
    ],
)
def test_retired_or_unmanaged_ocr_pin_does_not_pin_paddle(tmp_path, mode):
    path = tmp_path / "settings.json"
    raw = {
        "schema_version": 1,
        "settings": {
            "schema_version": 2,
            "residency": {
                "default_ttl_seconds": 300,
                "pipelines": [
                    {
                        "name": "OCR",
                        "recognition_mode": mode,
                        "ttl_seconds": 0,
                        "pinned": True,
                    }
                ],
            },
            "extra": {},
        },
    }
    path.write_text(json.dumps(raw), encoding="utf-8")
    assert (
        RuntimeSettingsStore(path).load(SettingsSnapshot()).paddle_idle_seconds == 300
    )


def test_v1_zero_inherits_and_new_policy_wins(tmp_path):
    path = tmp_path / "settings.json"
    raw = {
        "schema_version": 1,
        "settings": {
            "schema_version": 1,
            "pipeline_ttls": {"OCR": 0, "MinerU": 120},
            "extra": {},
        },
    }
    path.write_text(json.dumps(raw), encoding="utf-8")
    store = RuntimeSettingsStore(path)
    value = store.load(SettingsSnapshot())
    assert value.paddle_idle_seconds == 300 and value.mineru_idle_seconds == 120
    store.replace(
        SettingsSnapshot(paddle_idle_seconds=0, mineru_idle_seconds=2147483647)
    )
    assert store.load(SettingsSnapshot()).paddle_idle_seconds == 0


def test_migration_write_failure_keeps_original_and_backup(tmp_path, monkeypatch):
    path = tmp_path / "settings.json"
    raw = {
        "schema_version": 1,
        "settings": {
            "schema_version": 2,
            "residency": {"default_ttl_seconds": 300, "pipelines": []},
            "extra": {},
        },
    }
    path.write_text(json.dumps(raw), encoding="utf-8")
    before = path.read_bytes()
    monkeypatch.setattr(
        "vibeocr.runtime.environments.settings_store.os.replace",
        lambda *_: (_ for _ in ()).throw(OSError("disk busy")),
    )
    with pytest.raises(RuntimeSettingsStoreError):
        RuntimeSettingsStore(path).load(SettingsSnapshot())
    assert path.read_bytes() == before
    assert path.with_name(path.name + ".worker-idle.bak").read_bytes() == before


def test_paddle_settings_capabilities_and_status_do_not_spawn(monkeypatch):
    adapter = PaddleProcessAdapter(Path(sys.executable))
    monkeypatch.setattr(
        adapter, "_start", lambda: pytest.fail("observation started worker")
    )
    adapter.configure_settings(SettingsSnapshot(paddle_idle_seconds=0))
    assert adapter.capabilities().name == "OCR"
    assert adapter.residency_status().entries[0].worker["state"] == "not_started"
    adapter.close()


@pytest.mark.skipif(os.name != "nt", reason="Windows Job Object ownership contract")
def test_paddle_stop_confirms_owned_descendant_exit_without_killing_external_process():
    import ctypes

    from vibeocr.runtime.processes.utils.job_object import (
        CREATE_SUSPENDED,
        JobObjectGuard,
        _get_kernel32,
    )

    code = "import subprocess,sys;sys.stdin.readline();p=subprocess.Popen([sys.executable,'-c','import time;time.sleep(60)']);print(p.pid,flush=True);sys.stdin.read()"
    adapter = PaddleProcessAdapter(Path(sys.executable))
    external = subprocess.Popen(
        [sys._base_executable, "-c", "import time;time.sleep(60)"]
    )
    process = subprocess.Popen(
        [sys.executable, "-u", "-c", code],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        text=True,
        creationflags=CREATE_SUSPENDED,
    )
    guard = JobObjectGuard(allow_breakaway=False)
    child_handle = None
    try:
        assert guard.assign_from_popen(process, suspended=True)
        adapter._process = process
        adapter._guard = guard
        process.stdin.write("start\n")
        process.stdin.flush()
        child_pid = int(process.stdout.readline())
        kernel32 = _get_kernel32()
        child_handle = kernel32.OpenProcess(0x00100000, False, child_pid)
        assert child_handle
        adapter._idle.started()
        adapter.release_idle()
        kernel32.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
        assert kernel32.WaitForSingleObject(child_handle, 0) == 0
        assert process.poll() is not None
        assert adapter._process is None and adapter._guard is None
        assert external.poll() is None
    finally:
        if child_handle:
            kernel32.CloseHandle(child_handle)
        adapter.close()
        if process.poll() is None:
            process.terminate()
            process.wait(timeout=5)
        external.terminate()
        external.wait(timeout=5)


def test_expiry_and_new_reservation_are_serialized_until_stop_confirmation():
    stopping, confirm, reserved = (
        threading.Event(),
        threading.Event(),
        threading.Event(),
    )

    def stop():
        stopping.set()
        assert confirm.wait(2)

    owner, now = owner_fixture(stop)
    worked(owner)
    now[0] += 600
    expiry = threading.Thread(target=owner.check_idle)
    expiry.start()
    assert stopping.wait(1)
    releases = []

    def accept():
        releases.append(owner.reserve())
        reserved.set()

    acceptance = threading.Thread(target=accept)
    acceptance.start()
    assert not reserved.wait(0.05)
    confirm.set()
    expiry.join(1)
    acceptance.join(1)
    assert reserved.is_set() and owner.generation == 1 and not owner.running
    releases[0]()
    owner.close()


def test_job_reservation_is_taken_before_dispatch_and_covers_queue_gap(tmp_path):
    from vibeocr.runtime.jobs.module import SupervisorModule, SupervisorOptions
    from vibeocr.runtime_contracts import (
        JobKind,
        JobPriority,
        JobState,
        ResidencyStatus,
    )

    stopped, entered, proceed, released = (
        [],
        threading.Event(),
        threading.Event(),
        threading.Event(),
    )
    owner, now = owner_fixture(lambda: stopped.append(True))
    worked(owner)

    class Executor:
        def reserve(self, record):
            release = owner.reserve()

            def finished():
                release()
                released.set()

            return finished

        def execute(self, record, staged):
            entered.set()
            assert proceed.wait(2)
            owner.working()
            record.transition(JobState.RUNNING)
            record.transition(JobState.COMPLETED)

        def residency_status(self):
            return ResidencyStatus()

    module = SupervisorModule(
        options=SupervisorOptions(instance_id="idle-queue-test"),
        stager_root=tmp_path / "stage",
        executor=Executor(),
    )
    module.submit(
        kind=JobKind.RECOGNITION,
        priority=JobPriority.INTERACTIVE,
        uploads=[("input.png", "image/png", b"synthetic")],
    )
    assert entered.wait(1)
    now[0] += 1000
    owner.check_idle()
    assert owner.active == 1 and stopped == []
    proceed.set()
    assert released.wait(1)
    assert owner.last_finished == now[0] and owner.active == 0
    owner.check_idle()
    assert not stopped
    owner.close()


def test_real_stdio_worker_idle_exit_and_concurrent_next_request_start_once(
    monkeypatch,
):
    from vibeocr.runtime.recognition import paddle_process_adapter as module

    original_popen = subprocess.Popen
    code = "import json,sys\nfor line in sys.stdin:\n r=json.loads(line);print(json.dumps({'result':[] if r['operation']=='recognize' else {}}),flush=True)"
    starts = []

    def spawn(_arguments, **kwargs):
        process = original_popen([sys.executable, "-u", "-c", code], **kwargs)
        starts.append(process.pid)
        return process

    monkeypatch.setattr(module.subprocess, "Popen", spawn)
    adapter = PaddleProcessAdapter(Path(sys.executable))
    now = [100.0]
    adapter._idle.clock = lambda: now[0]
    adapter.configure_settings(SettingsSnapshot(paddle_idle_seconds=1))
    try:
        assert adapter._request("recognize") == []
        first = adapter._process
        now[0] += 1
        adapter._idle.check_idle()
        assert first.poll() is not None and adapter._process is None
        results = []
        threads = [
            threading.Thread(
                target=lambda: results.append(adapter._request("recognize"))
            )
            for _ in range(2)
        ]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join(2)
        assert all(not thread.is_alive() for thread in threads)
        assert results == [[], []] and len(starts) == 2
        assert adapter._idle.generation == 2
    finally:
        adapter.close()


def test_flat_legacy_unrelated_fields_are_diagnosed_without_loss(tmp_path):
    from vibeocr.runtime.jobs.module import SupervisorModule, SupervisorOptions

    path = tmp_path / "legacy.json"
    path.write_text(
        json.dumps(
            {"theme": "dark", "pipeline_ttls": {"OCR": 0}, "extra": {"keep": 1}}
        ),
        encoding="utf-8",
    )
    before = path.read_bytes()
    store = RuntimeSettingsStore(path)
    with pytest.raises(RuntimeSettingsStoreError, match="invalid"):
        store.load(SettingsSnapshot())
    assert path.read_bytes() == before
    module = SupervisorModule(
        options=SupervisorOptions(instance_id="legacy-preserved"),
        stager_root=tmp_path / "staging",
        executor=object(),
        settings_store=store,
    )
    assert "worker_settings_error" in module.settings().extra
    assert path.read_bytes() == before
    with pytest.raises(RuntimeSettingsStoreError, match="preserved"):
        store.replace(SettingsSnapshot())


def test_flat_v1_without_unrelated_fields_migrates_zero_as_inheritance(tmp_path):
    path = tmp_path / "legacy.json"
    path.write_text(
        json.dumps({"pipeline_ttls": {"OCR": 0}, "extra": {"keep": 1}}),
        encoding="utf-8",
    )
    value = RuntimeSettingsStore(path).load(SettingsSnapshot())
    assert value.paddle_idle_seconds == 300 and value.extra == {"keep": 1}


def test_paddle_stop_failure_keeps_process_and_guard_until_retry_confirms_exit():
    from unittest.mock import MagicMock

    adapter = PaddleProcessAdapter(Path(sys.executable))
    process, guard = MagicMock(), MagicMock()
    process.poll.return_value = None
    process.wait.side_effect = subprocess.TimeoutExpired("owned", 5)
    guard.terminate_and_wait.side_effect = OSError("owned tree failed")
    adapter._process, adapter._guard = process, guard
    adapter._idle.started()
    with pytest.raises(OSError):
        adapter.release_idle()
    assert adapter._process is process and adapter._guard is guard
    assert adapter.residency_status().entries[0].worker["state"] == "stop_failed"
    with pytest.raises(RuntimeError, match="ownership retained"):
        adapter.reserve()
    process.poll.return_value = 0
    guard.terminate_and_wait.side_effect = None
    adapter.release_idle()
    assert adapter._process is None and adapter._guard is None
    adapter.close()


@pytest.mark.parametrize("mode", ["startup_no_reply", "blocked_large_write"])
@pytest.mark.parametrize("managed", [False, True])
def test_close_is_bounded_during_real_worker_startup_or_blocked_pipe(
    monkeypatch, mode, managed
):
    import time

    from vibeocr.runtime.recognition import paddle_process_adapter as module

    if managed:
        monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "paddleocr-ort-cpu")
    original_popen = subprocess.Popen
    code = (
        "import sys,time;sys.stdin.readline();time.sleep(60)"
        if mode == "startup_no_reply"
        else "import json,sys,time;sys.stdin.readline();print(json.dumps({'result':{}}),flush=True);time.sleep(60)"
    )

    def spawn(_arguments, **kwargs):
        return original_popen([sys.executable, "-u", "-c", code], **kwargs)

    monkeypatch.setattr(module.subprocess, "Popen", spawn)
    adapter = PaddleProcessAdapter(Path(sys.executable))
    entered, errors = threading.Event(), []
    exchange = adapter._exchange

    def observed(request, **kwargs):
        if request["operation"] == (
            "settings" if mode == "startup_no_reply" else "recognize"
        ):
            entered.set()
        return exchange(request, **kwargs)

    monkeypatch.setattr(adapter, "_exchange", observed)

    def request():
        try:
            adapter._request("recognize", frame="x" * (4 * 1024 * 1024))
        except Exception as exc:
            errors.append(exc)

    requester = threading.Thread(target=request)
    requester.start()
    assert entered.wait(2)
    if mode == "blocked_large_write":
        time.sleep(
            0.05
        )  # The frame exceeds the native pipe capacity; the child never reads it.
    process = adapter._process
    closer = threading.Thread(target=adapter.close)
    closer.start()
    closer.join(3)
    requester.join(3)
    assert not closer.is_alive(), (
        "close blocked on a live writer or startup lifecycle lock"
    )
    assert not requester.is_alive() and errors
    assert process.poll() is not None and adapter._process is None


def test_settings_commit_guards_due_timers_and_applies_both_owner_policies(tmp_path):
    from contextlib import ExitStack, contextmanager

    from vibeocr.runtime.jobs.module import SupervisorModule, SupervisorOptions
    from vibeocr.runtime_contracts import ResidencyStatus

    stops = []
    paddle, now = owner_fixture(lambda: stops.append("paddle"))
    mineru = WorkerIdleOwner(
        "mineru",
        lambda: stops.append("mineru"),
        lock=threading.RLock(),
        clock=lambda: now[0],
    )
    entered, finish, committed = threading.Event(), threading.Event(), []

    class Store:
        def load(self, default):
            return default

        def replace(self, snapshot):
            entered.set()
            assert finish.wait(2)
            committed.append(snapshot)

    class Executor:
        @contextmanager
        def settings_guard(self):
            with ExitStack() as stack:
                stack.enter_context(paddle.lock)
                stack.enter_context(mineru.lock)
                yield

        def configure_settings(self, snapshot):
            paddle.configure(snapshot.paddle_idle_seconds)
            mineru.configure(snapshot.mineru_idle_seconds)
            return ResidencyStatus()

    module = SupervisorModule(
        options=SupervisorOptions(instance_id="settings-atomic"),
        stager_root=tmp_path / "stage",
        executor=Executor(),
        settings_store=Store(),
    )
    worked(paddle)
    worked(mineru)
    requested = SettingsSnapshot(paddle_idle_seconds=0, mineru_idle_seconds=1200)
    updater = threading.Thread(target=lambda: module.update_settings(requested))
    updater.start()
    assert entered.wait(1)
    now[0] += 600
    due = threading.Thread(target=lambda: (paddle.check_idle(), mineru.check_idle()))
    due.start()
    assert paddle.timeout == mineru.timeout == 600
    finish.set()
    updater.join(2)
    due.join(2)
    assert not updater.is_alive() and not due.is_alive()
    assert committed == [requested] and module.settings() is requested
    assert (paddle.timeout, mineru.timeout) == (0, 1200) and stops == []
    paddle.close()
    mineru.close()


def test_mineru_startup_close_cancels_its_generation_and_reaps_owned_process(
    monkeypatch,
):
    from vibeocr.runtime.host.composition import _MinerUServiceLifecycle
    from vibeocr.runtime.processes.utils.job_object import (
        CREATE_SUSPENDED,
        JobObjectGuard,
    )
    from vibeocr.runtime.recognition import mineru_service as module
    from vibeocr.runtime.recognition.mineru_adapter import MinerUProcessAdapter
    from vibeocr.runtime.recognition.mineru_api import MineruCancelled

    entered, errors = threading.Event(), []

    class Service:
        _startup_cancel = None
        _process_generation = 0
        process = None
        guard = None

        def _ensure_api_running(self):
            cls = type(self)
            cls.process = subprocess.Popen(
                [sys._base_executable, "-c", "import time;time.sleep(60)"],
                creationflags=CREATE_SUSPENDED if os.name == "nt" else 0,
            )
            cls.guard = JobObjectGuard(allow_breakaway=False)
            if os.name == "nt":
                assert cls.guard.assign_from_popen(cls.process, suspended=True)
            cls._process_generation += 1
            entered.set()
            assert cls._startup_cancel.wait(3)
            raise MineruCancelled("MinerU startup canceled")

        @classmethod
        def shutdown_owned(cls):
            if cls.process is None:
                return
            cls.guard.terminate_and_wait(timeout=2)
            if cls.process.poll() is None:
                cls.process.terminate()
            cls.process.wait(timeout=2)
            cls.guard.close()

    monkeypatch.setattr(module, "MinerUService", Service)
    lifecycle = _MinerUServiceLifecycle()
    adapter = MinerUProcessAdapter(client_factory=Service, lifecycle=lifecycle)

    def start():
        try:
            adapter.preload(("MinerU",))
        except Exception as exc:
            errors.append(exc)

    starter = threading.Thread(target=start)
    starter.start()
    assert entered.wait(2)
    cancel = lifecycle._startup_cancel
    closer = threading.Thread(target=adapter.close)
    closer.start()
    closer.join(3)
    starter.join(3)
    assert not closer.is_alive() and not starter.is_alive()
    assert cancel.is_set() and Service.process.poll() is not None
    assert len(errors) == 1 and isinstance(errors[0], MineruCancelled)
    assert not adapter._process_started
    with pytest.raises(RuntimeError, match="closed"):
        lifecycle.prepare_start()
    assert lifecycle._startup_cancel is cancel


def test_migration_keeps_an_earlier_backup_and_backs_up_a_changed_legacy_file(tmp_path):
    path = tmp_path / "settings.json"
    backup = path.with_name(path.name + ".worker-idle.bak")
    backup.write_text("earlier backup remains untouched", encoding="utf-8")
    path.write_text(
        json.dumps({"pipeline_ttls": {"OCR": 120}, "extra": {}}), encoding="utf-8"
    )
    original = path.read_bytes()
    RuntimeSettingsStore(path).load(SettingsSnapshot())
    assert backup.read_text(encoding="utf-8") == "earlier backup remains untouched"
    newer = list(tmp_path.glob("settings.json.worker-idle.*.bak"))
    assert len(newer) == 1 and newer[0].read_bytes() == original
