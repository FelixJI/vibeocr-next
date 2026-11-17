"""PDF 原生调度优先门 — 全局单 native 持有者的前台优先/后台有界公平调度。

fitz(PyMuPDF) 非线程安全，所有原生调用仍由调用方在既有全局原生锁
(``_FITZ_LOCK`` / ``BackendSession.fitz_lock``) 内执行。本门只做"谁下一个
进入原生锁"的入场排序，不是绕过原生锁的假队列：

- 恰一 native 持有者：同一时刻至多放行一个许可；调用方持许可后获取原生
  锁，许可在原生锁释放之后才归还（由 ``_fitz_op`` / 后台逐页租约的
  with 嵌套顺序保证）。
- 前台优先：既有全部 with 调用点默认前台（write/open/model/render 等同一
  优先级）；门空闲时前台先于排队后台进入。
- 有界公平：存在有效后台等待者时，最多 ``foreground_burst``（默认 4）次
  连续前台后放行一个后台；过期/取消的后台等待者不计公平配额。
- 可撤销等待：后台等待只覆盖"尚未进入 native"的阶段，取消后立即在唤醒时
  退出且不占用 native；已进入 native 的读不可中断，安全完成。取消谓词
  由 ``should_abort`` 回调表达，取消方置位后调 ``wake_all()`` 唤醒；后续
  viewport/background scoped read epoch 的 highwater 谓词可直接组合进
  ``should_abort``，不需要额外的取消协议。

本模块不涉及 wire 协议，也不改变 write lease / cancel_event 的既有语义。
"""

from __future__ import annotations

import threading
from contextlib import contextmanager
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from collections.abc import Callable, Iterator

DEFAULT_FOREGROUND_BURST = 4


class PdfNativeWaitAborted(Exception):
    """后台等待在进入 native 前被取消；未占用 native，也未登记活跃租约。"""


def _never_aborted() -> bool:
    return False


class PdfNativeGate:
    """fitz 全局串行原生锁的入场优先门。

    锁序说明：本门互斥锁与调用方的活跃租约锁（``_ops_cond``）互不嵌套——
    ``should_abort`` 回调在门互斥锁内执行，必须是免锁读（Event.is_set /
    状态字符串直读），否则会与"先释放 _ops_cond 再 wake_all"的关闭顺序
    形成锁反转。
    """

    def __init__(self, *, foreground_burst: int = DEFAULT_FOREGROUND_BURST) -> None:
        self._cond = threading.Condition()
        self._foreground_burst = max(1, int(foreground_burst))
        self._holder = False
        self._foreground_waiters = 0
        self._foreground_run = 0
        self._background_waiters: list[Callable[[], bool]] = []

    # ---- 观测（测试/诊断只读 seam）-----------------------------------

    @property
    def foreground_waiters(self) -> int:
        with self._cond:
            return self._foreground_waiters

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
        for should_abort in self._background_waiters:
            if not should_abort():
                return True
        return False

    # ---- 对外 ----------------------------------------------------------

    @contextmanager
    def foreground(self) -> Iterator[None]:
        """前台许可：门空闲时先于排队后台；受有界公平约束。"""
        with self._cond:
            self._foreground_waiters += 1
            try:
                while self._holder or (
                    self._valid_background_waiter()
                    and self._foreground_run >= self._foreground_burst
                ):
                    self._cond.wait()
            finally:
                self._foreground_waiters -= 1
            if self._valid_background_waiter():
                self._foreground_run += 1
            else:
                # 无有效后台等待者（含全部过期/取消）时不累计公平配额。
                self._foreground_run = 0
            self._holder = True
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
                    if not self._holder and (
                        self._foreground_waiters == 0
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
