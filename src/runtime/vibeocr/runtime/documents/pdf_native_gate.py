"""PDF 原生调度优先门 — 全局单 native 持有者的前台优先/后台有界公平调度。

fitz(PyMuPDF) 非线程安全，所有原生调用仍由调用方在既有全局原生锁
(``_FITZ_LOCK`` / ``BackendSession.fitz_lock``) 内执行。本门只做"谁下一个
进入原生锁"的入场排序，不是绕过原生锁的假队列：

- 恰一 native 持有者：同一时刻至多放行一个许可；调用方持许可后获取原生
  锁，许可在原生锁释放之后才归还（由调用方的 with 嵌套顺序保证）。
- 前台优先：默认前台（write/open/model 等不可撤销）与可撤销前台读同一
  优先级；门空闲时前台先于排队后台进入。
- 有界公平：存在有效后台等待者时，最多 ``foreground_burst``（默认 4）次
  连续前台后放行一个后台；过期/取消的等待者不计公平配额。
- 可撤销等待：等待阶段可经 ``should_abort`` 撤销——撤销的等待项真正从
  等待队列移除并唤醒其余等待者（含公平重算），未占 native；已进入
  native 的单元不可中断，安全完成。取消谓词由 ``should_abort`` 回调表达，
  取消方置位后调 ``wake_all()``；后续 viewport/background scoped read epoch
  的 highwater 谓词直接组合进回调，不需要额外取消协议。

锁序：本门互斥锁与调用方的活跃租约锁（``_ops_cond``）互不嵌套——
``should_abort`` 回调在门互斥锁内执行，必须是免锁读（Event.is_set /
状态字符串直读），否则会与"先释放 _ops_cond 再 wake_all"的关闭顺序形成
锁反转。本模块不涉及 wire 协议，也不改变 write lease / cancel_event 语义。
"""

from __future__ import annotations

import threading
from contextlib import contextmanager
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from collections.abc import Callable, Iterator

DEFAULT_FOREGROUND_BURST = 4


class PdfNativeWaitAborted(Exception):
    """等待在进入 native 前被取消；未占用 native，也未登记活跃租约。"""


def _never_aborted() -> bool:
    return False


class PdfNativeGate:
    """fitz 全局串行原生锁的入场优先门。"""

    def __init__(self, *, foreground_burst: int = DEFAULT_FOREGROUND_BURST) -> None:
        self._cond = threading.Condition()
        self._foreground_burst = max(1, int(foreground_burst))
        self._holder = False
        # 前台等待队列：等待中的撤销谓词列表（默认不可撤销项恒 False）。
        self._foreground_waiters: list[Callable[[], bool]] = []
        self._foreground_run = 0
        self._background_waiters: list[Callable[[], bool]] = []

    # ---- 观测（测试/诊断只读 seam）-----------------------------------

    @property
    def foreground_waiters(self) -> int:
        with self._cond:
            return len(self._foreground_waiters)

    @property
    def background_waiters(self) -> int:
        with self._cond:
            return len(self._background_waiters)

    @property
    def holder(self) -> bool:
        """是否有许可持有者（测试/诊断只读 seam）。"""
        with self._cond:
            return self._holder

    # ---- 内部 ----------------------------------------------------------

    def _valid_background_waiter(self) -> bool:
        return any(not should_abort() for should_abort in self._background_waiters)

    def _valid_foreground_waiter(self) -> bool:
        return any(not should_abort() for should_abort in self._foreground_waiters)

    def _admit_foreground(self) -> None:
        if self._valid_background_waiter():
            self._foreground_run += 1
        else:
            self._foreground_run = 0
        self._holder = True

    # ---- 对外 ----------------------------------------------------------

    @contextmanager
    def foreground(
        self, should_abort: Callable[[], bool] | None = None
    ) -> Iterator[None]:
        """前台许可：门空闲时先于排队后台；受有界公平约束。

        ``should_abort`` 返回 True 时在等待阶段撤销：真正移出等待队列并
        唤醒其余等待者（后台据此重算"无前台等待"准入），抛
        :class:`PdfNativeWaitAborted`；取得许可后本段不可中断。默认调用方
        （写/open/model 等租约前置路径）不可撤销。
        """
        check_abort = should_abort if should_abort is not None else _never_aborted
        with self._cond:
            self._foreground_waiters.append(check_abort)
            self._cond.notify_all()
            admitted = False
            try:
                while True:
                    if check_abort():
                        raise PdfNativeWaitAborted
                    if not self._holder and not (
                        self._valid_background_waiter()
                        and self._foreground_run >= self._foreground_burst
                    ):
                        admitted = True
                        break
                    self._cond.wait()
            finally:
                if admitted:
                    self._admit_foreground()
                self._foreground_waiters.remove(check_abort)
                self._cond.notify_all()
        try:
            yield
        finally:
            with self._cond:
                self._holder = False
                self._cond.notify_all()

    @contextmanager
    def background(
        self, should_abort: Callable[[], bool] | None = None
    ) -> Iterator[None]:
        """后台许可：前台优先，连续前台达到 burst 后放行一个后台。

        ``should_abort`` 返回 True 时在进入 native 前撤销等待并抛
        :class:`PdfNativeWaitAborted`；取得许可后本段执行不可中断。
        取消方置位谓词后应调 :meth:`wake_all` 让等待项立即重评退出。
        """
        check_abort = should_abort if should_abort is not None else _never_aborted
        with self._cond:
            self._background_waiters.append(check_abort)
            self._cond.notify_all()
            admitted = False
            try:
                while True:
                    if check_abort():
                        raise PdfNativeWaitAborted
                    # 前台等待者按撤销谓词计有效数：已撤销的等待项不阻塞后台。
                    if not self._holder and (
                        not self._valid_foreground_waiter()
                        or self._foreground_run >= self._foreground_burst
                    ):
                        admitted = True
                        break
                    self._cond.wait()
            finally:
                if admitted:
                    self._foreground_run = 0
                self._background_waiters.remove(check_abort)
                self._cond.notify_all()
            self._holder = True
        try:
            yield
        finally:
            with self._cond:
                self._holder = False
                self._cond.notify_all()

    def wake_all(self) -> None:
        """唤醒全部等待者：让已取消/过期的等待项立即重评并退出。"""
        with self._cond:
            self._cond.notify_all()
