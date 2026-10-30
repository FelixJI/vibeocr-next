"""One idle deadline per owned worker; reservations never start a process."""

from __future__ import annotations

import threading
import time
from collections.abc import Callable


class WorkerBusyError(RuntimeError):
    """Accepted work still protects the owned worker."""


class WorkerIdleOwner:
    def __init__(
        self,
        name: str,
        stop: Callable[[], None],
        *,
        lock: threading.RLock,
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        self.name = name
        self.lock = lock
        self.operation_lock = threading.RLock()
        self.clock = clock
        self._stop = stop
        self.timeout = 600
        self.generation = 0
        self.active = 0
        self.running = False
        self.state = "not_started"
        self.reason = "none"
        self.error: str | None = None
        self.last_finished: float | None = None
        self.elapsed_seconds: float | None = None
        self._worked = False
        self._closed = False
        self._retire = False
        self._wakeup = threading.Event()
        self._thread: threading.Thread | None = None
        self._publish()

    def reserve(self, *, allow_stop_failed: bool = False) -> Callable[[], None]:
        with self.lock:
            if self._closed:
                raise RuntimeError(f"{self.name} owner is closed")
            if self.state == "stop_failed" and not allow_stop_failed:
                raise RuntimeError(
                    f"{self.name} worker stop failed; ownership retained"
                )
            self.active += 1
            self._publish()
        released = False

        def release() -> None:
            nonlocal released
            with self.lock:
                if released:
                    return
                released = True
                self.active -= 1
                if self.active == 0 and self._worked:
                    self.last_finished = self.clock()
                    self._worked = False
                    if self.running and self.state not in (
                        "starting",
                        "stopping",
                        "stop_failed",
                    ):
                        self.state = "idle"
                self._publish()
                self._wakeup.set()

        return release

    @property
    def closed(self) -> bool:
        return self._closed

    def starting(self) -> None:
        with self.lock:
            if self._closed:
                raise RuntimeError(f"{self.name} owner is closed")
            if self.state == "stop_failed":
                raise RuntimeError(f"{self.name} worker still owned after stop failure")
            self.state = "starting"
            self._start_began = self.clock()
            self._worked = True
            self._publish()

    def started(self) -> None:
        with self.lock:
            self.generation += 1
            self.elapsed_seconds = max(
                0, self.clock() - getattr(self, "_start_began", self.clock())
            )
            self.running = True
            self.state = "busy" if self.active else "idle"
            self.error = None
            self.reason = "none"
            self._retire = False
            self._publish()
            if self._thread is None:
                self._thread = threading.Thread(
                    target=self._watch, daemon=True, name=f"{self.name}-worker-idle"
                )
                self._thread.start()

    def working(self) -> None:
        with self.lock:
            if self._closed:
                raise RuntimeError(f"{self.name} owner is closed")
            if self.state == "stop_failed":
                raise RuntimeError(
                    f"{self.name} worker stop failed; ownership retained"
                )
            self._worked = True
            if self.running and self.state not in (
                "starting",
                "stopping",
                "stop_failed",
            ):
                self.state = "busy"
            self._publish()

    def update_generation(self, generation: int) -> None:
        with self.lock:
            self.generation = generation
            self._publish()

    def record_error(self, error: Exception, reason: str) -> None:
        with self.lock:
            if self.state != "stop_failed":
                self.error = type(error).__name__
                self.reason = reason
                if self.state == "starting":
                    self.state = "not_started"
                self._publish()

    def configure(self, timeout: int) -> None:
        if type(timeout) is not int or not 0 <= timeout <= 2147483647:
            raise ValueError("worker idle seconds must be an integer in 0..2147483647")
        with self.lock:
            self.timeout = timeout
            self._publish()
            self._wakeup.set()

    def stop(
        self, reason: str, *, force: bool = False, _idle_check: bool = False
    ) -> None:
        # Busy rejection never waits for a long startup/stop; recheck after serialization.
        with self.lock:
            if self.active and not force:
                raise WorkerBusyError(
                    f"{self.name} worker is protected by accepted work"
                )
        # Long operations serialize before taking the short reservation/settings lock.
        with self.operation_lock:
            with self.lock:
                if _idle_check and self._idle_reason() != reason:
                    return
                if self.active and not force:
                    raise WorkerBusyError(
                        f"{self.name} worker is protected by accepted work"
                    )
                started = self.clock()
                self.state = "stopping"
                self.reason = reason
                self._publish()
            try:
                self._stop()
            except Exception as exc:
                with self.lock:
                    self.state = "stop_failed"
                    self.error = type(exc).__name__
                raise
            else:
                with self.lock:
                    self.running = False
                    self.state = "idle_stopped" if reason == "idle" else "not_started"
                    self.error = None
            finally:
                with self.lock:
                    self.elapsed_seconds = max(0, self.clock() - started)
                    self._publish()

    def retire_when_idle(self, retire: bool) -> None:
        with self.lock:
            self._retire = retire
            self._wakeup.set()

    def _idle_reason(self) -> str | None:
        if self.running and self.state != "stop_failed" and self.active == 0:
            if self._retire:
                return "connection_changed"
            if (
                self.timeout > 0
                and self.last_finished is not None
                and self.clock() - self.last_finished >= self.timeout
            ):
                return "idle"
        return None

    def check_idle(self) -> None:
        with self.operation_lock:
            with self.lock:
                reason = self._idle_reason()
            if reason is not None:
                self.stop(reason, _idle_check=True)

    def _publish(self) -> None:
        self._published = dict(
            owner=self.name,
            generation=self.generation,
            state=self.state,
            reason=self.reason,
            idle_seconds=self.timeout,
            active_leases=self.active,
            error=self.error,
            elapsed_seconds=self.elapsed_seconds,
            last_finished=self.last_finished,
            running=self.running,
        )

    def snapshot(self) -> dict:
        # Immutable publication keeps observations coherent without waiting for IPC/stop.
        value = dict(self._published)
        last_finished = value.pop("last_finished")
        running = value.pop("running")
        value["remaining_seconds"] = None
        if (
            running
            and not value["active_leases"]
            and value["idle_seconds"]
            and last_finished is not None
        ):
            value["remaining_seconds"] = max(
                0, int(value["idle_seconds"] - (self.clock() - last_finished))
            )
        return value

    def _watch(self) -> None:
        while not self._closed:
            self._wakeup.wait(0.25)
            self._wakeup.clear()
            if self._closed:
                return
            try:
                self.check_idle()
            except Exception:
                # Failure is retained in the authoritative snapshot; no restart/replay.
                pass

    def close(self) -> None:
        self._closed = True
        self._wakeup.set()
        thread = self._thread
        if thread is not None and thread is not threading.current_thread():
            thread.join(timeout=1)
        self.stop("shutdown", force=True)
