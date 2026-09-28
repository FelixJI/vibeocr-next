"""Cross-process job references for a managed Runtime environment."""

from __future__ import annotations

import json
import os
import re
from pathlib import Path

from vibeocr.runtime.environments.runtime_lock import (
    RuntimeLockTimeout,
    RuntimeStoreLock,
)


class ManagedEnvironmentReferences:
    def __init__(
        self,
        registry: Path,
        store_lock: Path,
        reference_root: Path,
        env_id: str,
        revision: int,
    ) -> None:
        self.registry = registry
        self.store_lock = store_lock
        self.reference_root = reference_root / env_id
        self.env_id = env_id
        self.revision = revision
        self._held: dict[str, RuntimeStoreLock] = {}

    @classmethod
    def from_environment(cls) -> ManagedEnvironmentReferences | None:
        env_id = os.getenv("VIBEOCR_MANAGED_ENVIRONMENT_ID")
        if not env_id:
            return None
        revision = int(os.environ["VIBEOCR_MANAGED_ENVIRONMENT_REVISION"])
        return cls(
            Path(os.environ["VIBEOCR_MANAGED_REGISTRY_PATH"]),
            Path(os.environ["VIBEOCR_MANAGED_STORE_LOCK"]),
            Path(os.environ["VIBEOCR_MANAGED_REFERENCES_ROOT"]),
            env_id,
            revision,
        )

    def admit(self, job_id: str) -> None:
        if not re.fullmatch(r"[0-9a-f-]{36}", job_id):
            raise ValueError("invalid job reference")
        with RuntimeStoreLock(self.store_lock):
            data = json.loads(self.registry.read_text(encoding="utf-8"))
            record = data["environments"].get(self.env_id)
            if (
                data["active_id"] != self.env_id
                or record is None
                or record["revision"] != self.revision
            ):
                raise RuntimeLockTimeout("supervisor environment is no longer active")
            lease = RuntimeStoreLock(self.reference_root / f"{job_id}.lock", timeout=0)
            lease.acquire()
            self._held[job_id] = lease

    def release(self, job_id: str) -> None:
        lease = self._held.pop(job_id, None)
        if lease is not None:
            lease.release()


def environment_has_references(reference_root: Path, env_id: str) -> bool:
    directory = reference_root / env_id
    if not directory.is_dir():
        return False
    for path in directory.glob("*.lock"):
        lease = RuntimeStoreLock(path, timeout=0)
        try:
            lease.acquire()
        except RuntimeLockTimeout:
            return True
        else:
            lease.release()
    return False
