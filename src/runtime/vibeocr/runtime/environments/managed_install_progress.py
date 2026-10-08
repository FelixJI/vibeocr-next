"""Read-only progress for the existing managed environment transaction."""

from __future__ import annotations

import time
from collections.abc import Callable
from datetime import datetime, timezone
from pathlib import Path
from uuid import uuid4

from packaging.markers import default_environment
from packaging.requirements import InvalidRequirement, Requirement
from packaging.utils import canonicalize_name
from vibeocr.runtime.environments.runtime_maintenance import safe_runtime_detail

CAPABILITY = "environment.install_progress.v1"


class ManagedInstallObserver:
    def __init__(
        self,
        sink: Callable[[dict], None] | None,
        cancel: Callable[[], None],
        plan_id: str,
        environment_id: str,
        revision: int,
        source_ids: list[str],
    ):
        self.sink = sink
        self.cancel = cancel
        self.operation_id = uuid4().hex
        self.snapshot = {"effective_download_source_ids": source_ids}
        self.binding = {
            "attempt_id": self.operation_id,
            "plan_id": plan_id,
            "environment_id": environment_id,
            "environment_revision": revision,
        }
        self.pending_output: dict[str, str] = {}
        self.output_truncated = False
        self.last_output = 0.0
        self.seq = 0
        self.phase = "prepare"
        self.state = "running"
        self.current = ""
        self.dependencies: list[dict] = []
        self.total_known = False
        self.download_total: int | None = None
        self.downloaded_files = 0
        self.bytes_current = 0
        self.bytes_total: int | None = None
        self.emit()

    def emit(self, log: dict | None = None, *, heartbeat: bool = False) -> None:
        self.seq += 1
        event = {
            "event_version": 1,
            "event_kind": "environment_install",
            **self.binding,
            "seq": self.seq,
            "timestamp": datetime.now(timezone.utc).isoformat(),
            "phase": self.phase,
            "state": self.state,
            "current": self.current,
            "dependencies": [dict(item) for item in self.dependencies],
            "dependency_total_known": self.total_known,
            "download_files_total": self.download_total,
            "download_files_completed": self.downloaded_files,
            "bytes_current": self.bytes_current,
            "bytes_total": self.bytes_total,
            "heartbeat": heartbeat,
        }
        if log is not None:
            event["log"] = log
        if self.sink is not None:
            try:
                self.sink(event)
            except Exception:
                # Observation cannot change the transaction or its committed result.
                self.sink = None

    def set_phase(self, phase: str, current: str = "") -> None:
        self.flush_output()
        self.phase, self.current = phase, safe_runtime_detail(current)
        self.emit()

    def requirements(self, path: Path, python_version: str) -> None:
        target = default_environment()
        target.update(
            python_version=".".join(python_version.split(".")[:2]),
            python_full_version=python_version,
            os_name="nt",
            sys_platform="win32",
            platform_system="Windows",
            platform_machine="AMD64",
        )
        unique: dict[tuple[str, str | None], dict] = {}
        known = True
        versions: dict[str, str | None] = {}
        for raw in path.read_text(encoding="utf-8").splitlines():
            text = raw.split(" --hash=", 1)[0].strip().removesuffix(chr(92)).rstrip()
            if not text or text.startswith(
                (
                    "#",
                    "--hash=",
                    "--index-url ",
                    "--extra-index-url ",
                    "--find-links ",
                    "--only-binary ",
                )
            ):
                continue
            try:
                requirement = Requirement(text)
            except InvalidRequirement:
                known = False
                continue
            if requirement.marker and not requirement.marker.evaluate(target):
                continue
            name = canonicalize_name(requirement.name)
            version = next(
                (s.version for s in requirement.specifier if s.operator == "=="), None
            )
            if version is None and requirement.url:
                from urllib.parse import unquote, urlsplit

                from packaging.utils import InvalidWheelFilename, parse_wheel_filename

                try:
                    version = str(
                        parse_wheel_filename(
                            unquote(urlsplit(requirement.url).path.rsplit("/", 1)[-1])
                        )[1]
                    )
                except InvalidWheelFilename:
                    pass
            if name in versions and versions[name] != version:
                known = False
            versions[name] = version
            unique[(name, version)] = {
                "name": name,
                "version": version,
                "download_state": "pending",
                "install_state": "pending",
            }
        self.dependencies = list(unique.values())
        self.total_known = known
        self.emit()

    def bundled(self) -> None:
        self.download_total = 0
        for item in self.dependencies:
            item["download_state"] = "bundled"
        self.emit()

    def installed_batch(self) -> None:
        for item in self.dependencies:
            item["install_state"] = "installed"
        self.emit()

    def finish(self, state: str, detail: str = "") -> None:
        self.flush_output()
        self.state = state
        if state == "succeeded":
            self.phase = "complete"
        self.current = safe_runtime_detail(detail)
        self.emit()

    def output(self, stream: str, text: str, truncated: bool = False) -> None:
        value = self.pending_output.get(stream, "") + safe_runtime_detail(text)
        self.output_truncated |= truncated or len(value) > 4000
        self.pending_output[stream] = value[-4000:]
        if time.monotonic() - self.last_output >= 0.1:
            self.flush_output()

    def flush_output(self) -> None:
        for stream, text in self.pending_output.items():
            self.emit(
                {"stream": stream, "text": text, "truncated": self.output_truncated}
            )
        self.pending_output.clear()
        self.output_truncated = False
        self.last_output = time.monotonic()

    def check_cancelled(self, *, fallback_message: str | None = None) -> None:
        self.cancel()

    def clear_cancellation_detail(self) -> None:
        pass

    def record_activity(self) -> None:
        pass

    def child_status_detail(self, *, message_code: str, fallback_message: str) -> None:
        self.current = safe_runtime_detail(fallback_message)
        self.emit()

    def heartbeat(
        self, *, message_code: str, fallback_message: str | None = None
    ) -> None:
        self.check_cancelled()
        self.flush_output()
        self.emit(heartbeat=True)

    def advance_measured(
        self,
        *,
        phase: str,
        unit: str,
        current: int,
        total: int | None,
        message_code: str,
        message_args: dict[str, str],
    ) -> None:
        self.bytes_current, self.bytes_total = current, total
        name = canonicalize_name(message_args.get("package", ""))
        self.current = name or message_code
        state = {
            "runtime.download_cache_hit": "cached",
            "runtime.download_package": "downloading",
            "runtime.download_verified": "downloaded",
        }.get(message_code)
        if message_code == "runtime.download_verified":
            self.downloaded_files += 1
        if state:
            for item in self.dependencies:
                if item["name"] == name:
                    item["download_state"] = state
        self.emit()
