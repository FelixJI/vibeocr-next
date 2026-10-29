"""MinerUProcessAdapter: supervisor-owned MinerU API subprocess.

Plan §4 Phase 5 goals addressed by this seam:

* Supervisor owns the MinerU API subprocess lifecycle (start/health/stop).
* ``recognize_many`` issues ONE budgeted multi-file native parse job
  with unique internal stems; results map back to stable input order.
* Default backend does NOT promise cross-document compute batching — the
  capability reports ``real_batch=False`` so metrics can distinguish HTTP
  batching from compute batching.
* Idle release stops the API subprocess (disk models are not deleted).
* Cancel: cooperative stop of subsequent chunks; exclusive MinerU jobs may
  escalate to hard termination after a grace period.

The actual HTTP call to mineru-api and result parsing reuse
``services/mineru_service.py``; this adapter is the ownership seam and the
unique-stem mapper.
"""

from __future__ import annotations

import logging
import re
import threading
import time
import uuid
from dataclasses import dataclass, field
from pathlib import Path
from typing import TYPE_CHECKING, Any, Protocol

from vibeocr.runtime.documents.utils.mime_types import (
    extension_to_mime,
    mime_to_extension,
)
from vibeocr.runtime.jobs.budgets import AdapterCapability, InputItem
from vibeocr.runtime.recognition.mineru_api import MineruCancelled
from vibeocr.runtime.recognition.worker_idle import WorkerIdleOwner
from vibeocr.runtime_contracts import ErrorCode, PipelineSelection

if TYPE_CHECKING:
    from collections.abc import Callable

    from vibeocr.runtime_contracts import ResidencyStatus

_SAFE_RE = re.compile(r"[^A-Za-z0-9._-]+")
logger = logging.getLogger(__name__)


def unique_stem(original: str, index: int, content_type: str = "") -> str:
    """Build a unique, filesystem-safe stem for one input in a batch.

    Duplicate stems are disambiguated by an index + short random token so a
    single multi-file native parse job cannot collide on result keys.

    The stem must carry a MinerU-supported extension: mineru-api routes
    parsing (and PDF page ranges) by the upload filename. An extension the
    shared MIME map already recognises in ``original`` wins; otherwise the
    item's transport-declared ``content_type`` resolves one through the same
    map. There is deliberately no ``.pdf`` fallback here: without a trusted
    source we refuse the upload (fail closed) instead of guessing a type.
    """
    p = Path(original or "")
    name = p.stem
    ext = p.suffix
    if extension_to_mime(ext) is None:
        mime = content_type.split(";", 1)[0].strip().lower()
        resolved = mime_to_extension(mime) if mime else None
        if resolved is None:
            raise ValueError(
                f"unsupported file type for MinerU upload: no known extension "
                f"from display_name {original!r} or content_type {content_type!r}"
            )
        ext = resolved
    cleaned = _SAFE_RE.sub("_", name).strip("._-") or "input"
    cleaned = cleaned[:48]
    return f"{index:04d}-{cleaned}-{uuid.uuid4().hex[:6]}{ext}"


class _MinerUClientLike(Protocol):
    """Minimal slice of the MinerU API client we depend on."""

    def file_parse(self, files: list[tuple[str, bytes]], **kwargs: Any) -> Any: ...


class _MinerULifecycle(Protocol):
    """Owns the mineru-api subprocess start/stop.

    ``MinerUService`` is a singleton whose ``__init__`` blocks until the API is
    up, so in production the lifecycle just ensures the singleton exists (start)
    and tears it down (stop). Tests inject a no-op lifecycle or leave it None.
    """

    def start(self) -> None: ...

    def stop(self) -> None: ...


@dataclass
class MinerUProcessAdapter:
    """Supervisor-owned MinerU adapter."""

    client_factory: Callable[[], _MinerUClientLike]
    backend: str = "hybrid-engine"
    # Default does NOT promise cross-document compute batching.
    capability: AdapterCapability = field(
        default_factory=lambda: AdapterCapability(
            name="MinerU", real_batch=False, max_compute_batch=1
        )
    )
    # Optional real subprocess lifecycle. When None the adapter falls back to a
    # flag-flip (kept for unit tests that inject a fake client_factory without a
    # backing process). Production wiring injects a lifecycle over MinerUService.
    lifecycle: _MinerULifecycle | None = None
    _process_started: bool = False
    _process_lock: threading.RLock = field(default_factory=threading.RLock, repr=False)
    _settings: Any = None
    _idle: WorkerIdleOwner = field(init=False, repr=False)

    def __post_init__(self) -> None:
        self._idle = WorkerIdleOwner(
            "MinerU", self._stop_process, lock=self._process_lock
        )

    @property
    def _active_leases(self) -> int:
        return self._idle.active

    def reserve(self):
        return self._idle.reserve(allow_stop_failed=self._remote_locked())

    def capabilities(self, options: Any | None = None) -> AdapterCapability:
        del options
        return self.capability

    # ------------------------------------------------------------------
    # Process lifecycle (ownership seam)
    # ------------------------------------------------------------------

    def _sync_generation(self) -> None:
        generation = getattr(self.lifecycle, "generation", None)
        if callable(generation):
            self._idle.update_generation(generation())

    def ensure_started(self) -> None:
        with self._process_lock:
            if self._idle.closed:
                raise RuntimeError("MinerU owner is closed")
            if self._remote_locked():
                return
            if self._process_started:
                return
            prepare_start = getattr(self.lifecycle, "prepare_start", None)
            if callable(prepare_start):
                prepare_start()
            self._idle.starting()
            try:
                if self.lifecycle is not None:
                    self.lifecycle.start()
            except Exception as exc:
                # A failed start can still own a partially created local process.
                self._process_started = True
                self._idle.stop("start_failed", force=True)
                self._idle.record_error(exc, "start_failed")
                raise
            self._process_started = True
            self._idle.started()
            self._sync_generation()

    @staticmethod
    def _remote_locked() -> bool:
        from vibeocr.runtime.recognition.mineru_readiness import (
            CONNECTION_REMOTE,
            current_connection,
        )

        connection = current_connection()
        if connection.mode == "unavailable":
            from vibeocr.runtime.recognition.mineru_config import MineruConfigError

            raise MineruConfigError(
                ErrorCode.MINERU_CONFIG_UNAVAILABLE, "mineru_connection_invalid"
            )
        return connection.mode == CONNECTION_REMOTE

    def _stop_process(self) -> None:
        owned = getattr(self.lifecycle, "owns_resources", None)
        if self.lifecycle is not None and (
            self._process_started or (callable(owned) and owned())
        ):
            # A lifecycle token also finds resources from an unfinished start.
            self.lifecycle.stop()
        self._process_started = False

    def stop(self, *, reason: Any = None) -> None:
        self._idle.stop(str(reason or "explicit_release"))

    # ------------------------------------------------------------------
    # recognize_many — budgeted multi-file request
    # ------------------------------------------------------------------

    def _service_options(self, options: Any | None) -> Any | None:
        """Convert a wire ``PipelineSelection`` into backend ``OCROptions``.

        Same boundary conversion as the paddle adapter: the wire DTO stays
        contracts-owned and strictly validated, while the Backend package
        owns the option semantics (``lang_list``, ``backend``, page ranges,
        formula/table toggles, ...).
        """
        if not isinstance(options, PipelineSelection):
            return options
        from vibeocr.runtime.recognition.mineru_config import resolve_mineru_config

        return resolve_mineru_config(options)

    def recognize_many(
        self,
        items: list[InputItem],
        *,
        options: Any | None = None,
        cancelled: Callable[[], bool] = lambda: False,
    ) -> list[dict[str, Any]]:
        if not items:
            return []
        started = time.perf_counter()
        release = self.reserve()
        try:
            service_options = self._service_options(options)
            if not self._remote_locked():
                self._idle.working()
            self.ensure_started()
            client = self.client_factory()
            # Build unique-stem file list preserving input order.
            files: list[tuple[str, bytes]] = []
            stem_to_index: dict[str, int] = {}
            for idx, item in enumerate(items):
                raw = getattr(item, "data", b"")
                if not isinstance(raw, (bytes, bytearray)) or len(raw) == 0:
                    raise ValueError(
                        f"InputItem {item.item_id} has no raw bytes for MinerU upload"
                    )
                display = getattr(item, "display_name", None) or f"input-{idx}"
                content_type = getattr(item, "content_type", "") or ""
                stem = unique_stem(display, idx, content_type=content_type)
                stem_to_index[stem] = idx
                stem_to_index[Path(stem).stem] = idx
                files.append((stem, bytes(raw)))
            # A request-provided backend (user-selected pipeline option) wins
            # over the adapter default: file_parse's ``backend`` kwarg would
            # override options.backend, so pass None when the request already
            # carries one.
            backend_override = (
                None if getattr(service_options, "backend", None) else self.backend
            )
            raw_results = client.file_parse(
                files,
                options=service_options,
                backend=backend_override,
                cancelled=lambda: cancelled() or self._idle.closed,
            )
            payloads = self._map_results_back(raw_results, stem_to_index, len(items))
        except MineruCancelled:
            # 取消不是故障：只记一行 info，不用 logger.exception 打错误栈。
            logger.info(
                "[Supervisor][Recognize] action=recognize pipeline=MinerU "
                "items=%d result=cancelled elapsed_ms=%.1f",
                len(items),
                (time.perf_counter() - started) * 1000,
            )
            raise
        except Exception:
            logger.exception(
                "[Supervisor][Recognize] action=recognize pipeline=MinerU "
                "items=%d result=failed elapsed_ms=%.1f",
                len(items),
                (time.perf_counter() - started) * 1000,
            )
            raise
        else:
            logger.info(
                "[Supervisor][Recognize] action=recognize pipeline=MinerU "
                "items=%d result=success elapsed_ms=%.1f",
                len(items),
                (time.perf_counter() - started) * 1000,
            )
            return payloads
        finally:
            self._sync_generation()
            release()

    @staticmethod
    def _map_results_back(
        raw_results: Any, stem_to_index: dict[str, int], expected: int
    ) -> list[dict[str, Any]]:
        """Restore stable input order from a MinerU multi-file result dict.

        MinerU returns results keyed by filename; we map each stem back to its
        original index. Missing stems yield an empty dict (the caller decides
        whether to mark the item failed).
        """
        out: list[dict[str, Any]] = [{} for _ in range(expected)]
        if isinstance(raw_results, dict):
            for key, value in raw_results.items():
                idx = stem_to_index.get(key)
                if idx is not None:
                    payload = value if isinstance(value, dict) else {"raw": value}
                    out[idx] = payload
        elif isinstance(raw_results, list):
            # If the API returned a list in the same order, use positional mapping.
            for i, value in enumerate(raw_results):
                if i < expected:
                    out[i] = value if isinstance(value, dict) else {"raw": value}
        return out

    # ------------------------------------------------------------------
    # Residency passthrough (idle release stops the subprocess)
    # ------------------------------------------------------------------

    def preload(self, pipelines: tuple[str, ...]) -> ResidencyStatus:
        if "MinerU" not in pipelines:
            return self.residency_status()
        started = time.perf_counter()
        release = self.reserve()
        try:
            if not self._remote_locked():
                self._idle.working()
            self.ensure_started()
            prepare = getattr(self.client_factory(), "prepare", None)
            if prepare is not None:
                prepare(cancelled=lambda: self._idle.closed)
        except Exception:
            logger.error(
                "[Supervisor][Preload] pipeline=MinerU result=failed elapsed_ms=%.1f",
                (time.perf_counter() - started) * 1000,
            )
            raise
        finally:
            self._sync_generation()
            release()
        logger.info(
            "[Supervisor][Preload] pipeline=MinerU result=success elapsed_ms=%.1f",
            (time.perf_counter() - started) * 1000,
        )
        return self.residency_status()

    def residency_status(self) -> ResidencyStatus:
        from vibeocr.runtime_contracts import (
            RecognitionResourceKind,
            ResidencyEntry,
            ResidencyKind,
            ResidencyStatus,
        )

        worker = self._idle.snapshot()
        if self._remote_locked():
            worker = {
                **worker,
                "local_owner_state": worker["state"],
                "state": "remote",
                "remaining_seconds": None,
            }
        return ResidencyStatus(
            entries=(
                ResidencyEntry(
                    pipeline="MinerU",
                    resource_kind=RecognitionResourceKind.PROCESS,
                    resource_id="mineru",
                    kind=ResidencyKind.IDLE
                    if self._idle.running and not self._remote_locked()
                    else ResidencyKind.EVICTED,
                    active_leases=worker["active_leases"],
                    remaining_ttl_seconds=worker["remaining_seconds"],
                    worker=worker,
                ),
            )
        )

    def release_idle(self, pipeline: str | None = None) -> ResidencyStatus:
        if pipeline in (None, "MinerU"):
            self._idle.stop("explicit_release")
        return self.residency_status()

    def settings_guard(self):
        return self._process_lock

    def validate_settings(self, snapshot: Any) -> None:
        from vibeocr.runtime.recognition.mineru_config import MineruConfigError
        from vibeocr.runtime.recognition.mineru_readiness import (
            connection_from_extra,
            current_connection,
        )

        connection = connection_from_extra(getattr(snapshot, "extra", None))
        with self._process_lock:
            if connection != current_connection() and self._active_leases > 0:
                raise MineruConfigError(
                    ErrorCode.VALIDATION_ERROR,
                    "mineru_connection_change_blocked_by_active_tasks",
                )

    def configure_settings(self, snapshot: Any) -> None:
        from vibeocr.runtime.recognition.mineru_config import MineruConfigError
        from vibeocr.runtime.recognition.mineru_readiness import (
            configure_connection,
            connection_from_extra,
            current_connection,
        )

        self.validate_settings(snapshot)
        # 非法连接配置 fail closed；合法变更在无活动 MinerU 任务时原子应用。
        connection = connection_from_extra(getattr(snapshot, "extra", None))
        with self._process_lock:
            if connection != current_connection() and self._active_leases > 0:
                # 在途解析固定原端点：活动任务期间拒绝改连接，避免串用/
                # 旧任务 mark_executed 污染新连接的 tier 观察集。
                raise MineruConfigError(
                    ErrorCode.VALIDATION_ERROR,
                    "mineru_connection_change_blocked_by_active_tasks",
                )
            configure_connection(connection)
            self._idle.retire_when_idle(
                connection.mode == "remote" and self._process_started
            )
            self._settings = snapshot
            self._idle.configure(snapshot.mineru_idle_seconds)

    def close(self) -> None:
        cancel_start = getattr(self.lifecycle, "cancel_start", None)
        if callable(cancel_start):
            cancel_start()  # Signal before waiting for the owner lock held by startup.
        self._idle.close()


__all__ = ["MinerUProcessAdapter", "unique_stem"]
