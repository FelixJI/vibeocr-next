"""Issue #172: maintenance start 注册前取消 fence 的生命周期契约。

owned run 的取消握手只在 pre_registration（fence 封死、无任何 durable
写）时授权宿主终止；已注册/部分注册失败的取消必须走 durable
request_cancel 协作路径，真实终态保持权威。
"""

from __future__ import annotations

import threading
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
