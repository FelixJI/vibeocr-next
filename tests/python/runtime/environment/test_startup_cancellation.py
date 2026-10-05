"""Issue #172: maintenance start 注册前取消 fence 的生命周期契约。

owned run 的取消握手只在 pre_registration（fence 封死、无任何 durable
写）时授权宿主终止；已注册/部分注册失败的取消必须走 durable
request_cancel 协作路径，真实终态保持权威。
"""

from __future__ import annotations

import json
import os
import queue
import subprocess
import sys
import threading
import time
from pathlib import Path

import pytest
from vibeocr.runtime.environments.runtime_maintenance import (
    RuntimeMaintenanceReporter,
    RuntimeOperationCancelled,
    RuntimeOperationNotFound,
    RuntimeOperationStore,
    RuntimeProfileDescriptor,
    StartupCancellation,
)


def test_startup_cancellation_pending_seals_and_is_idempotent() -> None:
    guard = StartupCancellation()

    assert guard.cancel() == "pre_registration"
    assert guard.state == "cancelled"
    # 重复取消保持 pre_registration：sealed fence 永远可终止，不翻转为
    # registered。
    assert guard.cancel() == "pre_registration"


def test_startup_cancellation_registered_never_authorizes_termination() -> None:
    guard = StartupCancellation()
    with guard.registration():
        pass

    assert guard.state == "registered"
    assert guard.cancel() == "registered"
    assert guard.cancel() == "registered"


def test_registration_after_cancel_raises_before_entering() -> None:
    guard = StartupCancellation()
    guard.cancel()

    with pytest.raises(RuntimeOperationCancelled):
        with guard.registration():
            raise AssertionError("fence 已封死，注册不得进入")


def test_partial_registration_failure_never_falls_back_to_pending() -> None:
    guard = StartupCancellation()

    with pytest.raises(OSError):
        with guard.registration():
            raise OSError("partial durable write")

    # durable 写尝试后即使失败也不能回到可终止状态。
    assert guard.state == "registered"
    assert guard.cancel() == "registered"


def test_main_waits_for_receipt_publication_before_registration_observes_cancel(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # P1 行为回归（main seam）：pending 封死与回执发布必须同锁协调。发布
    # gate 卡住回执时，main 的注册路径不得观察到取消并先行退出——“回执
    # 在锁外发布”的旧实现会在窗口内返回（红），当前实现等回执发布完成
    # （绿）。factory 不构造真实 control，直接进入同一 fence。
    from types import SimpleNamespace

    from vibeocr.runtime.environments import runtime_installer

    product = tmp_path / "product"
    product.mkdir()
    request = {
        "protocol_version": 2,
        "operation": "ensure",
        "operation_id": "receipt-order-op",
        "accepted_event_streams": ["ndjson.v2"],
        "product_root": str(product),
        "component_lock": str(tmp_path / "component-lock.json"),
        "runtime_manifest": str(tmp_path / "runtime-manifest.json"),
        "accelerator": "cpu",
    }
    incoming_read, incoming_write = os.pipe()
    ack_started = threading.Event()
    release = threading.Event()
    published: list[str] = []
    envelopes: list[dict[str, object]] = []
    thread_errors: list[BaseException] = []

    def gated_receipt(state: str) -> None:
        if state != "pre_registration":
            published.append(state)
            return
        ack_started.set()
        if not release.wait(timeout=10):
            thread_errors.append(AssertionError("receipt gate never released"))
            return
        published.append(state)

    def collect_emit(value: object) -> None:
        assert isinstance(value, dict)
        envelopes.append(value)

    monkeypatch.setattr(runtime_installer, "_maintenance_cancel_receipt", gated_receipt)
    monkeypatch.setattr(runtime_installer, "_emit", collect_emit)

    def gated_control_factory(req, *, event_sink, startup_cancellation=None):
        assert startup_cancellation is not None
        if not ack_started.wait(timeout=30):
            raise AssertionError("listener never requested cancellation")
        with startup_cancellation.registration():
            raise AssertionError("cancelled fence entered")

    monkeypatch.setattr(
        runtime_installer, "_runtime_control_from_request", gated_control_factory
    )
    stdin_reader = os.fdopen(incoming_read, "rb")
    monkeypatch.setattr(
        runtime_installer,
        "sys",
        SimpleNamespace(
            stdin=stdin_reader,
            stdout=SimpleNamespace(),
            stderr=SimpleNamespace(),
        ),
    )
    writer = os.fdopen(incoming_write, "wb")
    try:
        writer.write(b"cancel\n")
        writer.flush()
        main_result: list[int] = []
        main_done = threading.Event()

        def run_main() -> None:
            try:
                main_result.append(
                    runtime_installer.main(
                        [
                            "--maintenance-cancel-control",
                            "--request-json",
                            json.dumps(request),
                        ]
                    )
                )
            except BaseException as exc:
                thread_errors.append(exc)
            finally:
                main_done.set()

        main_thread = threading.Thread(target=run_main, daemon=True)
        try:
            main_thread.start()
            assert ack_started.wait(timeout=30)
            # 回执发布未完成前，main 不得观察到取消并退出。
            assert not main_done.wait(timeout=0.5), (
                "main 在回执发布完成前已退出：注册观察早于回执发布"
            )
            release.set()
            assert main_done.wait(timeout=30)
            assert main_result == [1]
            assert published == ["pre_registration"]
            assert thread_errors == []
            failure = envelopes[0]
            assert failure["ok"] is False
            error = failure["error"]
            assert isinstance(error, dict)
            assert error["canonical_code"] == "CANCELLED"
        finally:
            release.set()
            main_thread.join(timeout=10)
            assert not main_thread.is_alive()
    finally:
        # 关闭本测试自有的 stdin 管道，不遗留句柄。
        writer.close()
        stdin_reader.close()


def _reporter(
    tmp_path: Path,
    guard: StartupCancellation | None = None,
) -> RuntimeMaintenanceReporter:
    return RuntimeMaintenanceReporter(
        state_root=tmp_path,
        profile=RuntimeProfileDescriptor("win-x64-cpu", "cpu", ()),
        startup_cancellation=guard,
    )


def test_cancelled_guard_blocks_registration_and_binding(tmp_path: Path) -> None:
    guard = StartupCancellation()
    guard.cancel()
    reporter = _reporter(tmp_path, guard)
    bound: list[str] = []

    with pytest.raises(RuntimeOperationCancelled):
        reporter.start(
            "ensure",
            total_steps=7,
            operation_id="sealed-op",
            before_registration=lambda: bound.append("bind"),
        )

    assert bound == []
    with pytest.raises(RuntimeOperationNotFound):
        RuntimeOperationStore(tmp_path).snapshot("sealed-op")


def test_reporter_without_guard_still_runs_before_registration(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # 无 flag 的普通 HTTP/CLI 调用必须保持原有语义：注册前绑定仍执行。
    order: list[str] = []
    original_start = RuntimeOperationStore.start

    def ordered_start(self, operation_id, intent, **kwargs):
        order.append("start")
        return original_start(self, operation_id, intent, **kwargs)

    monkeypatch.setattr(RuntimeOperationStore, "start", ordered_start)

    reporter = _reporter(tmp_path)
    reporter.start(
        "ensure",
        total_steps=7,
        operation_id="plain-op",
        before_registration=lambda: order.append("bind"),
    )

    assert order == ["bind", "start"]
    assert RuntimeOperationStore(tmp_path).snapshot("plain-op") is not None


def test_cancel_between_bind_and_store_start_waits_for_durable_record(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # bind 与 store.start 在同一个 fence 边界内：窗口内到达的取消被锁
    # 串行化，直到记录 durable 后才观察到 registered，request_cancel 可以
    # 落地——取消不再丢失。
    guard = StartupCancellation()
    entered = threading.Event()
    release = threading.Event()
    original_start = RuntimeOperationStore.start

    def slow_start(self, operation_id, intent, **kwargs):
        entered.set()
        assert release.wait(timeout=10)
        return original_start(self, operation_id, intent, **kwargs)

    monkeypatch.setattr(RuntimeOperationStore, "start", slow_start)

    reporter = _reporter(tmp_path, guard)
    bound: list[str] = []
    finished = threading.Event()
    failures: list[BaseException] = []

    def run_start() -> None:
        try:
            reporter.start(
                "ensure",
                total_steps=7,
                operation_id="fence-op",
                before_registration=lambda: bound.append("bind"),
            )
        except BaseException as exc:  # noqa: BLE001 - 线程内异常由断言回收
            failures.append(exc)
        finally:
            finished.set()

    thread = threading.Thread(target=run_start)
    thread.start()
    assert entered.wait(timeout=10)
    assert bound == ["bind"]

    states: list[str] = []
    cancel_done = threading.Event()

    def run_cancel() -> None:
        states.append(guard.cancel())
        cancel_done.set()

    cancel_thread = threading.Thread(target=run_cancel)
    cancel_thread.start()
    # 取消决策停在 fence 锁上：store.start 完成前不得返回。
    assert not cancel_done.wait(timeout=0.3)

    release.set()
    assert finished.wait(timeout=10)
    assert cancel_done.wait(timeout=10)
    thread.join(timeout=10)
    cancel_thread.join(timeout=10)
    assert failures == []
    assert states == ["registered"]

    snapshot = RuntimeOperationStore(tmp_path).request_cancel("fence-op")
    assert snapshot["operation_id"] == "fence-op"


def test_maintenance_cancel_listener_emits_state_receipt() -> None:
    # stdin 控制通道复用 environment listener，receipt 是本地进程控制协议
    # （非 v2 wire schema）。
    script = (
        "from vibeocr.runtime.environments.runtime_installer import "
        "_listen_environment_cancel, _maintenance_cancel_receipt\n"
        "_listen_environment_cancel("
        "lambda: _maintenance_cancel_receipt('pre_registration'))\n"
    )
    result = subprocess.run(
        [sys.executable, "-c", script],
        input=b"cancel\n",
        capture_output=True,
        timeout=30,
        check=True,
    )
    assert b'{"maintenance_cancel":"pre_registration"}' in result.stdout


def test_real_main_startup_cancel_handshake_is_deterministic_pre_registration(
    tmp_path: Path,
) -> None:
    # 真实 runtime_installer main() + 注册 fence listener + stdin 握手：
    # 测试进程先持有 runtime-store 锁，把 ensure 封在注册前的锁等待上，
    # 因此 cancel 必然得到 pre_registration 回执；释放锁后注册被 fence
    # 拦下，进程以 CANCELLED envelope 有界退出，且不留下 durable 记录
    # （也不会启动任何安装）。读取用有界队列，不依赖阻塞 readline。
    from test_runtime_installer import _release
    from vibeocr.runtime.environments.runtime_layout import resolve_runtime_store
    from vibeocr.runtime.environments.runtime_lock import RuntimeStoreLock
    from vibeocr.runtime.environments.runtime_manifest import load_runtime_manifest

    manifest, component = _release(tmp_path / "release", with_base_pack=True)
    product = tmp_path / "product"
    product.mkdir()
    request = {
        "protocol_version": 2,
        "operation": "ensure",
        "operation_id": "e2e-cancel-op",
        "accepted_event_streams": ["ndjson.v2"],
        "product_root": str(product),
        "component_lock": str(component),
        "runtime_manifest": str(manifest),
        "accelerator": "cpu",
    }
    loaded = load_runtime_manifest(manifest)
    paths = resolve_runtime_store(product, manifest_sha256=loaded.sha256)

    process: subprocess.Popen[bytes] | None = None
    reader_thread: threading.Thread | None = None
    lines: queue.Queue[bytes] = queue.Queue()
    output: list[bytes] = []
    try:
        with RuntimeStoreLock(paths.locks_root / "runtime-store.lock", timeout=30):
            process = subprocess.Popen(
                [
                    sys.executable,
                    "-m",
                    "vibeocr.runtime.environments.runtime_installer",
                    "--maintenance-cancel-control",
                    "--request-json",
                    json.dumps(request),
                ],
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL,
            )
            assert process.stdout is not None
            stdout = process.stdout

            def pump() -> None:
                try:
                    for line in stdout:
                        lines.put(line)
                finally:
                    lines.put(b"")

            reader_thread = threading.Thread(target=pump, daemon=True)
            reader_thread.start()
            assert process.stdin is not None
            process.stdin.write(b"cancel\n")
            process.stdin.flush()
            receipt: str | None = None
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                try:
                    line = lines.get(timeout=0.5)
                except queue.Empty:
                    continue
                if not line:
                    break
                output.append(line)
                if b"maintenance_cancel" in line:
                    receipt = json.loads(line)["maintenance_cancel"]
                    break
            assert receipt == "pre_registration"

        # 释放锁后：注册被 fence 拦下，进程以 CANCELLED envelope 退出。
        deadline = time.monotonic() + 30
        saw_eof = False
        while time.monotonic() < deadline:
            try:
                line = lines.get(timeout=0.5)
            except queue.Empty:
                continue
            if not line:
                saw_eof = True
                break
            output.append(line)
        assert saw_eof, "cancelled host did not close stdout within 30 seconds"
        assert process.wait(timeout=10) == 1
        envelope = json.loads(output[-1].strip())
        assert envelope["ok"] is False
        assert envelope["error"]["canonical_code"] == "CANCELLED"
        with pytest.raises(RuntimeOperationNotFound):
            RuntimeOperationStore(paths.state_root).snapshot("e2e-cancel-op")
    finally:
        if process is not None:
            if process.poll() is None:
                process.kill()
            process.wait(timeout=10)
            if reader_thread is not None:
                reader_thread.join(timeout=10)
                assert not reader_thread.is_alive()
            if process.stdin is not None:
                process.stdin.close()
            if process.stdout is not None:
                process.stdout.close()
