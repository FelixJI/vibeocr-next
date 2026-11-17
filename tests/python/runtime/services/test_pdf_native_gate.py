"""PDF 原生调度优先门：前台优先/后台有界公平/可撤销等待的确定性并发契约。

覆盖 T2 原生调度基座的核心不变量：
- native 从不并行（恰一许可持有者，配合调用方原生锁）；
- 已占门后台安全完成后，新前台越过排队后台；
- 持续前台需求下，有效后台等待者在 4 次连续前台后被放行（有界公平），
  不满 4 次不放行；门空闲且无前台等待时后台可立即进入（不饿死）；
- 取消的等待项不占 native、不阻塞新前台需求、不计公平配额；
- 已进入 native 的后台读不可中断，安全完成；
- /load 每页是独立后台 native 租约，流在页间挂起（网络背压）时不持有
  租约/许可；
- close 在 active native 期间等待，排队后台页观察到 CLOSING 立即退出
  （409，不把取消当检测成功）；cancel 唤醒待入 native 的已取消读；
- T1 写租约：已登记的前台写在 close 期间被等待安全完成，不被拒绝。
- 取消谓词由 should_abort 回调 + wake_all 表达（无额外取消协议包装）。

顺序断言全部基于 Events（谓词驱动的门内决策保证确定性）；``_wait_until``
仅等待线程"到达/注册"这一前置条件，不参与顺序构造。前台链
（``_ForegroundUnit``）通过"下一前台先停靠、再释放当前前台"的交接，
保证门每次空闲瞬间都有前台等待，使有界公平的验证不依赖时序。
"""

from __future__ import annotations

import json
import threading
import time
from typing import TYPE_CHECKING

import pymupdf as fitz
from vibeocr.runtime.documents.pdf_native_gate import (
    PdfNativeGate,
    PdfNativeWaitAborted,
)

if TYPE_CHECKING:
    from pathlib import Path


def _wait_until(predicate, timeout: float = 5.0) -> bool:
    """有界等待线程到达（注册/停靠）；不用于构造顺序。"""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return True
        time.sleep(0.001)
    return predicate()


def _start(thread_target) -> threading.Thread:
    thread = threading.Thread(target=thread_target, daemon=True)
    thread.start()
    return thread


class _ForegroundUnit:
    """单个前台单元：准入后持门直到 finish()，用于构造确定性前台链。"""

    def __init__(self, gate: PdfNativeGate) -> None:
        self._gate = gate
        self.entered = threading.Event()
        self.release = threading.Event()
        self.thread = _start(self._run)

    def _run(self) -> None:
        with self._gate.foreground():
            self.entered.set()
            assert self.release.wait(5)

    def finish(self) -> None:
        self.release.set()
        self.thread.join(5)


# ---- 门级：恰一 native 持有者 ------------------------------------------


class TestSingleNativeHolder:
    def test_mixed_traffic_never_overlaps_in_gate(self):
        """混合前台/后台并发入场，任一时刻至多一个许可持有者。"""
        gate = PdfNativeGate()
        lock = threading.Lock()
        stats = {"inside": 0, "max_inside": 0, "completed": 0}
        barrier = threading.Barrier(15, timeout=5)

        def unit(kind: str) -> None:
            barrier.wait()
            ctx = gate.foreground() if kind == "fg" else gate.background()
            with ctx:
                with lock:
                    stats["inside"] += 1
                    stats["max_inside"] = max(stats["max_inside"], stats["inside"])
                with lock:
                    stats["inside"] -= 1
                    stats["completed"] += 1

        threads = [
            _start((lambda k=kind: unit(k))) for kind in ["fg"] * 10 + ["bg"] * 5
        ]
        for thread in threads:
            thread.join(10)
        assert stats["completed"] == 15
        assert stats["max_inside"] == 1


# ---- 门级：前台越过排队后台 -------------------------------------------


class TestForegroundOvertakesQueuedBackground:
    def test_foreground_overtakes_queued_background_after_holder_releases(self):
        """已占门后台安全完成后，新前台越过排队后台进入。"""
        gate = PdfNativeGate()
        bg1_entered = threading.Event()
        bg1_release = threading.Event()
        fg_entered = threading.Event()
        bg2_entered = threading.Event()
        seq: list[str] = []
        seq_lock = threading.Lock()

        def note(tag: str) -> None:
            with seq_lock:
                seq.append(tag)

        def bg1() -> None:
            with gate.background():
                note("bg1")
                bg1_entered.set()
                assert bg1_release.wait(5)

        def bg2() -> None:
            with gate.background():
                note("bg2")
                bg2_entered.set()

        def fg() -> None:
            with gate.foreground():
                note("fg")
                fg_entered.set()

        t_bg1 = _start(bg1)
        assert bg1_entered.wait(5)
        t_bg2 = _start(bg2)
        assert _wait_until(lambda: gate.background_waiters == 1)
        t_fg = _start(fg)
        assert _wait_until(lambda: gate.foreground_waiters == 1)
        # bg1 持门期间：前台与排队后台都未进入（恰一持有者）。
        assert not fg_entered.is_set()
        assert not bg2_entered.is_set()
        bg1_release.set()
        t_bg1.join(5)
        # 释放后前台越过排队后台。
        assert fg_entered.wait(5)
        t_fg.join(5)
        assert bg2_entered.wait(5)
        t_bg2.join(5)
        assert seq == ["bg1", "fg", "bg2"]


# ---- 门级：4 次有界公平 -----------------------------------------------


class TestBoundedFairness:
    def test_background_admitted_after_exactly_four_consecutive_foreground(self):
        """有效后台等待者存在时，恰好 4 次连续前台后放行该后台。"""
        gate = PdfNativeGate()  # 默认 burst=4（与冻结预算一致）
        bg_admitted = threading.Event()
        bg_release = threading.Event()

        def bg() -> None:
            with gate.background():
                bg_admitted.set()
                assert bg_release.wait(5)

        # 前台 #0 持门（准入时尚无后台等待者，不计公平配额）。
        outer = _ForegroundUnit(gate)
        assert outer.entered.wait(5)
        # 后台在 outer 持门期间注册等待。
        t_bg = _start(bg)
        assert _wait_until(lambda: gate.background_waiters == 1)
        # 前台 #1..#4 链式交接：门每次空闲瞬间都有前台停靠，后台只能等配额。
        units: list[_ForegroundUnit] = []
        current = outer
        for _ in range(4):
            nxt = _ForegroundUnit(gate)
            assert _wait_until(lambda: gate.foreground_waiters == 1)
            current.finish()
            assert nxt.entered.wait(5)
            units.append(nxt)
            current = nxt
            if len(units) < 4:
                assert not bg_admitted.is_set(), "不满 4 次连续前台不得放行后台"
        # 第 5 个前台先停靠，再释放第 4 个前台：fg_run=4 且后台仍有效等待，
        # 第 5 个前台必须让位。
        fifth = _ForegroundUnit(gate)
        assert _wait_until(lambda: gate.foreground_waiters == 1)
        assert not fifth.entered.is_set()
        current.finish()
        assert bg_admitted.wait(5), "4 次连续前台后必须放行一个后台"
        assert not fifth.entered.is_set(), "后台持门期间第 5 个前台不得进入"
        bg_release.set()
        t_bg.join(5)
        assert fifth.entered.wait(5)
        fifth.finish()


# ---- 门级：取消等待项 -------------------------------------------------


class TestCancelledWaiters:
    def test_cancelled_waiter_exits_without_native_and_frees_foreground(self):
        """取消的等待项立即退出（未占 native），且不再阻塞前台公平配额。"""
        gate = PdfNativeGate()
        cancelled = threading.Event()
        outcome: dict[str, str] = {}

        def bg() -> None:
            try:
                with gate.background(should_abort=cancelled.is_set):
                    outcome["result"] = "entered"
            except PdfNativeWaitAborted:
                outcome["result"] = "aborted"

        # 前台 #0 持门；后台注册等待；随后 4 次连续前台把配额用满。
        outer = _ForegroundUnit(gate)
        assert outer.entered.wait(5)
        t_bg = _start(bg)
        assert _wait_until(lambda: gate.background_waiters == 1)
        current = outer
        for _ in range(4):
            nxt = _ForegroundUnit(gate)
            assert _wait_until(lambda: gate.foreground_waiters == 1)
            current.finish()
            assert nxt.entered.wait(5)
            current = nxt
        # 第 4 个前台持门期间取消（置位 + 唤醒）：等待中的后台退出，未进入
        # native，也不占公平配额。
        cancelled.set()
        gate.wake_all()
        assert _wait_until(lambda: gate.background_waiters == 0)
        t_bg.join(5)
        assert outcome.get("result") == "aborted"
        current.finish()
        # 配额已满（4 次前台）但有效等待者已消失：第 5 个前台立即进入。
        fifth = _ForegroundUnit(gate)
        assert fifth.entered.wait(5), "取消的等待项不得阻塞新的前台需求"
        fifth.finish()

    def test_cancel_during_in_native_background_completes_safely(self):
        """已进入 native 的后台读不可中断：取消只影响等待阶段。"""
        gate = PdfNativeGate()
        cancelled = threading.Event()
        entered = threading.Event()
        release = threading.Event()
        done = threading.Event()
        errors: list[BaseException] = []

        def bg() -> None:
            try:
                with gate.background(should_abort=cancelled.is_set):
                    entered.set()
                    assert release.wait(5)
                done.set()
            except BaseException as error:  # noqa: BLE001 - 测试记录用
                errors.append(error)

        thread = _start(bg)
        assert entered.wait(5)
        cancelled.set()
        gate.wake_all()  # 已在 native 内：不影响本段安全完成
        release.set()
        thread.join(5)
        assert done.is_set()
        assert not errors


# ---- 进程级：/load 每页租约与关闭同步 ----------------------------------


def _create_text_pdf(path: Path, pages: int = 3) -> Path:
    doc = fitz.open()
    for i in range(pages):
        page = doc.new_page(width=612, height=792)
        page.insert_text((72, 72), f"Page {i + 1}", fontsize=12)
    doc.save(str(path))
    doc.close()
    return path


class TestLoadPageLease:
    async def test_load_releases_lease_while_stream_parks_between_pages(
        self, tmp_path, monkeypatch
    ):
        """/load 每页独立租约：流在页间挂起（网络背压）时不持有租约/许可。"""
        from vibeocr.runtime.documents import pdf_backend_process as backend

        reg = backend.SessionRegistry()
        backend._REGISTRY = reg
        session = None
        try:
            path = _create_text_pdf(tmp_path / "lease.pdf", pages=3)
            session = reg.add(str(path))
            sid = session.session_id

            response = backend.session_load(sid)
            iterator = response.body_iterator
            first_chunk = await iterator.__anext__()
            # 生成器此刻挂在第 0 页的 yield 上（消费者未拉取＝背压）：
            # 无活跃租约、优先门空闲，前台操作可即时进入。
            assert session.active_ops == 0
            probe = backend.session_model(sid)
            assert probe.file_path == str(path)

            chunks = [first_chunk]
            async for chunk in iterator:
                chunks.append(chunk)
            lines = [json.loads(chunk.decode()) for chunk in chunks]
            assert len(lines) == 4  # 3 页 + 结束哨兵
            assert lines[-1]["message"] == "done"
            assert lines[-1]["current"] == 3
            # 每页真实检测完成（已知状态不因背压/取消被伪造）。
            for i in range(3):
                assert lines[i]["page_index"] == i
                assert lines[i]["page_payload"]["has_text_layer"] is True
            assert session.active_ops == 0
        finally:
            backend._REGISTRY = None
            if session is not None:
                reg.remove(session.session_id)


class TestCloseSyncWithGate:
    def _blocked_render(self, monkeypatch, backend):
        entered = threading.Event()
        release = threading.Event()
        calls: list[int] = []

        def rasterize(doc, page, dpi):
            calls.append(page)
            entered.set()
            assert release.wait(5)
            return b"\x00\x00\x00", 1, 1

        monkeypatch.setattr(backend, "_render_page_pixels", rasterize)
        return entered, release, calls

    def test_close_waits_for_active_native_and_aborts_pending_background_page(
        self, tmp_path, monkeypatch
    ):
        """close 等待 active native 完成；排队后台页观察到 CLOSING 立即 409。"""
        from fastapi import HTTPException
        from vibeocr.runtime.documents import pdf_backend_process as backend
        from vibeocr.runtime.documents.wire_schemas import RenderPreviewRequest

        reg = backend.SessionRegistry()
        backend._REGISTRY = reg
        try:
            path = _create_text_pdf(tmp_path / "close_wait.pdf", pages=2)
            session = reg.add(str(path))
            sid = session.session_id
            entered, release, calls = self._blocked_render(monkeypatch, backend)

            render_done = {}

            def do_render() -> None:
                backend.render_preview(sid, RenderPreviewRequest(page=0, dpi=72))
                render_done["ok"] = True

            outcome: dict[str, str] = {}

            def bg_page() -> None:
                try:
                    backend._detect_one_page(session, 1)
                    outcome["result"] = "completed"
                except HTTPException as error:
                    outcome["result"] = f"http:{error.status_code}"
                except BaseException as error:  # noqa: BLE001 - 测试记录用
                    outcome["result"] = f"error:{type(error).__name__}"

            t_render = _start(do_render)
            assert entered.wait(5)
            t_bg = _start(bg_page)
            assert _wait_until(lambda: session.native_gate.background_waiters == 1)
            # 排队后台页不计活跃租约，也不占 native。
            assert session.active_ops == 1

            close_done = threading.Event()

            def do_close() -> None:
                reg.remove(sid)
                close_done.set()

            t_close = _start(do_close)
            t_bg.join(5)
            # close 置 CLOSING 后唤醒优先门：排队后台页在 native 仍被占时
            # 立即以 409 退出（取消不等于检测成功）。
            assert outcome.get("result") == "http:409"
            assert not close_done.is_set(), "close 必须等待 active native 完成"
            release.set()
            t_render.join(5)
            t_close.join(5)
            assert render_done.get("ok") is True
            assert calls == [0]
            assert close_done.is_set()
            assert session.state == "CLOSED"
            assert reg.count() == 0
        finally:
            backend._REGISTRY = None

    def test_registered_foreground_write_completes_during_close(
        self, tmp_path, monkeypatch
    ):
        """T1 写租约：已登记的前台写在 close 期间被等待安全完成，不被拒绝。"""
        from vibeocr.runtime.documents import pdf_backend_process as backend
        from vibeocr.runtime.documents.wire_schemas import RotateRequest

        reg = backend.SessionRegistry()
        backend._REGISTRY = reg
        try:
            path = _create_text_pdf(tmp_path / "write_close.pdf", pages=2)
            session = reg.add(str(path))
            sid = session.session_id

            # 后台检测页占据 native：page_rotation 首次调用挂起。
            bg_started = threading.Event()
            bg_release = threading.Event()
            real_page_rotation = backend.PdfService.page_rotation

            def held_page_rotation(doc, i):
                if not bg_started.is_set():
                    bg_started.set()
                    assert bg_release.wait(5)
                return real_page_rotation(doc, i)

            monkeypatch.setattr(backend.PdfService, "page_rotation", held_page_rotation)

            # 前台写：进入 native 后挂起。
            write_entered = threading.Event()
            write_release = threading.Event()
            real_rotate = backend.PdfService.rotate_pages

            def held_rotate(doc, pdf_document, pages, angle):
                write_entered.set()
                assert write_release.wait(5)
                return real_rotate(doc, pdf_document, pages, angle)

            monkeypatch.setattr(backend.PdfService, "rotate_pages", held_rotate)

            write_result = {}

            def do_write():
                try:
                    result = backend.rotate_pages(
                        sid, RotateRequest(pages=[1], angle=90)
                    )
                    write_result["ok"] = result.diff is not None
                except BaseException as error:  # noqa: BLE001 - 测试记录用
                    write_result["error"] = error

            t_bg = _start(lambda: backend._detect_one_page(session, 0))
            assert bg_started.wait(5)
            assert session.active_ops == 1

            # 前台写先登记租约（T1 语义），再在门内排队等待。
            t_write = _start(do_write)
            assert _wait_until(lambda: session.native_gate.foreground_waiters == 1)
            assert session.active_ops == 2

            close_done = threading.Event()

            def do_close():
                reg.remove(sid)
                close_done.set()

            t_close = _start(do_close)
            # close 已置 CLOSING，但必须等待已登记写完成，而不是拒绝它。
            assert not close_done.is_set()
            assert not write_entered.is_set()
            bg_release.set()
            assert write_entered.wait(5)
            write_release.set()
            t_write.join(5)
            t_bg.join(5)
            t_close.join(5)
            assert write_result.get("ok") is True, write_result
            assert close_done.is_set()
            assert session.state == "CLOSED"
            assert reg.count() == 0
        finally:
            backend._REGISTRY = None

    def test_session_cancel_wakes_pending_background_page(self, tmp_path, monkeypatch):
        """cancel 唤醒门内等待的后台页：待入 native 的已取消读立即退出。"""
        from fastapi import HTTPException
        from vibeocr.runtime.documents import pdf_backend_process as backend
        from vibeocr.runtime.documents.wire_schemas import RenderPreviewRequest

        reg = backend.SessionRegistry()
        backend._REGISTRY = reg
        try:
            path = _create_text_pdf(tmp_path / "cancel_wait.pdf", pages=2)
            session = reg.add(str(path))
            sid = session.session_id
            entered, release, calls = self._blocked_render(monkeypatch, backend)

            render_done = {}

            def do_render() -> None:
                backend.render_preview(sid, RenderPreviewRequest(page=0, dpi=72))
                render_done["ok"] = True

            outcome: dict[str, str] = {}

            def bg_page() -> None:
                try:
                    backend._detect_one_page(session, 1)
                    outcome["result"] = "completed"
                except HTTPException as error:
                    outcome["result"] = f"http:{error.status_code}"
                except BaseException as error:  # noqa: BLE001 - 测试记录用
                    outcome["result"] = f"error:{type(error).__name__}"

            t_render = _start(do_render)
            assert entered.wait(5)
            t_bg = _start(bg_page)
            assert _wait_until(lambda: session.native_gate.background_waiters == 1)

            backend.cancel(sid)
            t_bg.join(5)
            # 待入 native 的已取消读在唤醒后立即退出（409），未占 native。
            assert outcome.get("result") == "http:409"
            assert not release.is_set()
            # 已进入 native 的前台渲染不受取消影响，安全完成。
            release.set()
            t_render.join(5)
            assert render_done.get("ok") is True
            assert calls == [0]
            assert session.state == "OPEN"
            session.cancel_event.clear()
            reg.remove(sid)
        finally:
            backend._REGISTRY = None
