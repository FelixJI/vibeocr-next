"""Cross-process job references for a managed Runtime environment."""

from __future__ import annotations

import json
import os
import re
from pathlib import Path
from uuid import uuid4

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
        selection_revision: int | None = None,
    ) -> None:
        self.registry = registry
        self.store_lock = store_lock
        self.reference_root = reference_root / env_id
        self.env_id = env_id
        self.revision = revision
        self.selection_revision = selection_revision
        self._service: RuntimeStoreLock | None = None
        self._held: dict[str, RuntimeStoreLock] = {}

    @classmethod
    def from_environment(cls) -> ManagedEnvironmentReferences | None:
        env_id = os.getenv("VIBEOCR_MANAGED_ENVIRONMENT_ID")
        if not env_id:
            return None
        revision = int(os.environ["VIBEOCR_MANAGED_ENVIRONMENT_REVISION"])
        references = cls(
            Path(os.environ["VIBEOCR_MANAGED_REGISTRY_PATH"]),
            Path(os.environ["VIBEOCR_MANAGED_STORE_LOCK"]),
            Path(os.environ["VIBEOCR_MANAGED_REFERENCES_ROOT"]),
            env_id,
            revision,
            int(os.environ["VIBEOCR_MANAGED_SELECTION_REVISION"])
            if "VIBEOCR_MANAGED_SELECTION_REVISION" in os.environ
            else None,
        )
        # Installation holds this gate until its complete candidate/rollback transaction ends.
        with (
            maintenance_lock(references.reference_root.parent, env_id),
            RuntimeStoreLock(references.store_lock),
        ):
            data = json.loads(references.registry.read_text(encoding="utf-8"))
            record = data["environments"].get(env_id)
            if (
                references.registry.parent / "environment-swaps" / f"{env_id}.json"
            ).exists():
                raise RuntimeLockTimeout("supervisor environment has a pending swap")
            if record is None or record["revision"] != revision:
                raise RuntimeLockTimeout("supervisor environment revision changed")
            references._service = RuntimeStoreLock(
                references.reference_root / "services" / f"{uuid4().hex}.lock",
                timeout=0,
            )
            references._service.acquire()
        return references

    def admit(self, job_id: str) -> None:
        if not re.fullmatch(r"[0-9a-f-]{36}", job_id):
            raise ValueError("invalid job reference")
        with (
            admission_lock(self.reference_root.parent, self.env_id),
            RuntimeStoreLock(self.store_lock),
        ):
            data = json.loads(self.registry.read_text(encoding="utf-8"))
            record = data["environments"].get(self.env_id)
            if (
                data["active_id"] != self.env_id
                or record is None
                or record["revision"] != self.revision
                or (
                    self.selection_revision is not None
                    and data["active_revision"] != self.selection_revision
                )
            ):
                raise RuntimeLockTimeout("supervisor environment is no longer active")
            lease = RuntimeStoreLock(self.reference_root / f"{job_id}.lock", timeout=0)
            lease.acquire()
            self._held[job_id] = lease

    def release(self, job_id: str) -> None:
        lease = self._held.pop(job_id, None)
        if lease is not None:
            lease.release()
            try:
                lease.path.unlink()
            except (FileNotFoundError, PermissionError):
                pass


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


def maintenance_lock(reference_root: Path, env_id: str) -> RuntimeStoreLock:
    return RuntimeStoreLock(
        reference_root / env_id / "control" / "maintenance.lock", timeout=0
    )


def environment_has_services(reference_root: Path, env_id: str) -> bool:
    directory = reference_root / env_id / "services"
    for path in directory.glob("*.lock"):
        lease = RuntimeStoreLock(path, timeout=0)
        try:
            lease.acquire()
        except RuntimeLockTimeout:
            return True
        else:
            lease.release()
            # OS ownership, not a PID, decides liveness after a crash or PID reuse.
            try:
                path.unlink()
            except (FileNotFoundError, PermissionError):
                pass
    return False


def admission_lock(reference_root: Path, env_id: str) -> RuntimeStoreLock:
    return RuntimeStoreLock(
        reference_root / env_id / "control" / "admission.lock", timeout=0
    )
