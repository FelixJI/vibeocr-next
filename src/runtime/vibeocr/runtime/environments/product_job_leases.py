"""Cross-process ownership of jobs while changing the product's device setting."""

from __future__ import annotations

import re
from collections.abc import Callable
from contextlib import contextmanager
from pathlib import Path

from vibeocr.runtime.environments.runtime_lock import (
    RuntimeLockTimeout,
    RuntimeStoreLock,
)


class ProductJobLeases:
    def __init__(
        self, locks: Path, require_current: Callable[[], None] | None = None
    ) -> None:
        self._require_current = require_current
        self.root = locks / "recognition-jobs"
        self._held: dict[str, RuntimeStoreLock] = {}

    def admit(self, job_id: str) -> None:
        if not re.fullmatch(r"[0-9a-f-]{36}", job_id):
            raise ValueError("invalid job id")
        with RuntimeStoreLock(self.root / "admission.lock"):
            if self._require_current is not None:
                self._require_current()
            lease = RuntimeStoreLock(self.root / "active" / f"{job_id}.lock", timeout=0)
            lease.acquire()
            self._held[job_id] = lease

    def release(self, job_id: str) -> None:
        if lease := self._held.pop(job_id, None):
            lease.release()
            try:
                lease.path.unlink(missing_ok=True)
            except PermissionError:
                pass  # Windows may still hold a maintenance probe on the released file.

    @contextmanager
    def device_change(self):
        with RuntimeStoreLock(self.root / "admission.lock"):
            for path in (self.root / "active").glob("*.lock"):
                try:
                    with RuntimeStoreLock(path, timeout=0):
                        pass
                except RuntimeLockTimeout as exc:
                    raise ValueError(
                        "识别任务仍在进行，请在完成后更改计算设备。"
                    ) from exc
            yield
