"""Durable local storage for supervisor settings."""

from __future__ import annotations

import json
import os
import shutil
from pathlib import Path
from typing import Any, Protocol
from uuid import uuid4

from vibeocr.runtime_contracts import SettingsSnapshot, parse_pipeline_spec


class RuntimeSettingsStoreError(RuntimeError):
    """Failure retains independently validated non-lifecycle preferences for startup."""

    def __init__(
        self,
        message: str,
        *,
        recovered: SettingsSnapshot | None = None,
        connection_valid: bool = False,
    ) -> None:
        super().__init__(message)
        self.recovered = recovered
        self.connection_valid = connection_valid


class RuntimeSettings(Protocol):
    """The local-substitutable settings seam used by ``SupervisorModule``."""

    def load(self, default: SettingsSnapshot) -> SettingsSnapshot: ...

    def replace(self, snapshot: SettingsSnapshot) -> None: ...


class RuntimeSettingsStore:
    """Atomically persist one Protocol ``SettingsSnapshot`` at a local path."""

    def __init__(self, path: str | Path) -> None:
        self.path = Path(path)
        self._read_failed = False

    def load(self, default: SettingsSnapshot) -> SettingsSnapshot:
        self._read_failed = False
        try:
            return self._load(default)
        except RuntimeSettingsStoreError:
            self._read_failed = True
            raise

    def _load(self, default: SettingsSnapshot) -> SettingsSnapshot:
        if not self.path.exists():
            return default
        try:
            value = json.loads(self.path.read_text(encoding="utf-8"))
        except (OSError, ValueError) as exc:
            raise RuntimeSettingsStoreError("invalid supervisor settings file") from exc
        if (
            isinstance(value, dict)
            and "schema_version" not in value
            and "pipeline_ttls" in value
        ):
            value = {**value, "schema_version": 1}
        if not isinstance(value, dict) or value.get("schema_version") not in (1, 2):
            raise RuntimeSettingsStoreError("unsupported supervisor settings schema")
        if "settings" in value and set(value) - {"schema_version", "settings"}:
            raise RuntimeSettingsStoreError(
                "unrecognized settings envelope; original file preserved"
            )
        payload = value.get("settings")
        if payload is None and ("residency" in value or "pipeline_ttls" in value):
            payload = value
        if not isinstance(payload, dict):
            raise RuntimeSettingsStoreError("invalid supervisor settings payload")
        snapshot = _snapshot_from_payload(payload)
        if "paddle_idle_seconds" not in payload or "mineru_idle_seconds" not in payload:
            backup = self.path.with_name(self.path.name + ".worker-idle.bak")
            try:
                if backup.exists() and backup.read_bytes() != self.path.read_bytes():
                    backup = self.path.with_name(
                        self.path.name + f".worker-idle.{uuid4().hex}.bak"
                    )
                if not backup.exists():
                    shutil.copy2(self.path, backup)
                self.replace(snapshot)
            except OSError as exc:
                raise RuntimeSettingsStoreError(
                    "could not back up worker settings"
                ) from exc
        return snapshot

    def replace(self, snapshot: SettingsSnapshot) -> None:
        if self._read_failed:
            raise RuntimeSettingsStoreError(
                "original settings require repair; file preserved"
            )
        payload = _snapshot_payload(snapshot)
        temporary = self.path.with_name(f".{self.path.name}.{uuid4().hex}.tmp")
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            with temporary.open("w", encoding="utf-8") as stream:
                json.dump(
                    {"schema_version": 1, "settings": payload},
                    stream,
                    ensure_ascii=False,
                    sort_keys=True,
                )
                stream.write("\n")
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, self.path)
        except (OSError, TypeError, ValueError) as exc:
            raise RuntimeSettingsStoreError(
                "could not replace supervisor settings"
            ) from exc
        finally:
            try:
                temporary.unlink(missing_ok=True)
            except OSError:
                pass


def _snapshot_payload(snapshot: SettingsSnapshot) -> dict[str, Any]:
    payload = snapshot.to_payload()
    if not isinstance(payload, dict):
        raise RuntimeSettingsStoreError("invalid supervisor settings payload")
    return payload


def _snapshot_from_payload(payload: dict[str, Any]) -> SettingsSnapshot:
    try:
        known = {
            "schema_version",
            "residency",
            "pipeline_ttls",
            "default_ttl_seconds",
            "extra",
            "download_source_ids",
            "paddle_idle_seconds",
            "mineru_idle_seconds",
        }
        if set(payload) - known:
            raise ValueError("unrecognized settings fields")
        if payload.get("schema_version") != SettingsSnapshot().schema_version and not (
            payload.get("schema_version") == 1 and "pipeline_ttls" in payload
        ):
            raise ValueError("settings schema")
        has_legacy = "residency" in payload or "pipeline_ttls" in payload
        residency = payload.get("residency")
        if residency is None and "pipeline_ttls" in payload:
            legacy = payload["pipeline_ttls"]
            if not isinstance(legacy, dict) or any(
                type(value) is not int or value < 0 for value in legacy.values()
            ):
                raise ValueError("legacy pipeline TTL")
            residency = {
                "default_ttl_seconds": payload.get("default_ttl_seconds", 300),
                "pipelines": [
                    {"name": name, "ttl_seconds": value or None, "pinned": False}
                    for name, value in legacy.items()
                ],
            }
        if residency is None and not has_legacy:
            residency = {"default_ttl_seconds": 300, "pipelines": []}
        if not isinstance(residency, dict):
            raise ValueError("residency")
        ttl = residency["default_ttl_seconds"]
        pipelines = residency["pipelines"]
        extra = payload.get("extra", {})
        source_ids = payload.get("download_source_ids", [])
        if (
            type(ttl) is not int
            or ttl < 0
            or not isinstance(pipelines, list)
            or not isinstance(extra, dict)
            or not isinstance(source_ids, list)
            or any(
                not isinstance(source_id, str) or not source_id
                for source_id in source_ids
            )
            or len(set(source_ids)) != len(source_ids)
        ):
            raise ValueError("settings shape")
        return SettingsSnapshot(
            default_ttl_seconds=ttl,
            paddle_idle_seconds=payload["paddle_idle_seconds"]
            if "paddle_idle_seconds" in payload
            else _worker_timeout(residency, "PaddleOCR")
            if has_legacy
            else 600,
            mineru_idle_seconds=payload["mineru_idle_seconds"]
            if "mineru_idle_seconds" in payload
            else _worker_timeout(residency, "MinerU")
            if has_legacy
            else 600,
            pipelines=tuple(parse_pipeline_spec(item) for item in pipelines),
            extra=extra,
            download_source_ids=tuple(source_ids),
        )
    except (KeyError, TypeError, ValueError) as exc:
        recovered, connection_valid = _recover_non_lifecycle(payload)
        raise RuntimeSettingsStoreError(
            "invalid supervisor settings payload",
            recovered=recovered,
            connection_valid=connection_valid,
        ) from exc


def _recover_non_lifecycle(
    payload: dict[str, Any],
) -> tuple[SettingsSnapshot | None, bool]:
    if payload.get("schema_version") not in (1, 2):
        return None, False
    extra = payload.get("extra")
    if not isinstance(extra, dict):
        return None, False
    from vibeocr.runtime.environments.runtime_selection import (
        RuntimeSelectionError,
        normalize_download_source_ids,
    )
    from vibeocr.runtime.recognition.mineru_config import MineruConfigError
    from vibeocr.runtime.recognition.mineru_readiness import connection_from_extra

    try:
        connection_from_extra(extra)
        connection_valid = True
    except MineruConfigError:
        connection_valid = False
    try:
        sources = (
            normalize_download_source_ids(payload.get("download_source_ids"))
            if payload.get("download_source_ids")
            else ()
        )
    except (RuntimeSelectionError, TypeError, ValueError):
        sources = ()
    return SettingsSnapshot(
        extra=dict(extra), download_source_ids=tuple(sources)
    ), connection_valid


def _worker_timeout(residency: dict[str, Any], owner: str) -> int:
    """Migrate effective policies of supported modes, never a retired OCR alias."""
    mode_pipelines = {
        "paddle_text": "OCR",
        "paddle_structure": "PP-StructureV3",
        "paddle_table": "TABLE_RECOGNITION",
        "mineru_document": "MinerU",
    }
    retired_modes = {
        "rapid_text",
        "rapid_table",
        "paddle_document_vl",
        "paddle_formula",
        "windows_text",
    }
    retired_pipelines = {"PaddleOCR-VL", "FORMULA_RECOGNITION", "RAPID_TABLE"}
    supported = (
        {"OCR", "TABLE_RECOGNITION", "PP-StructureV3"}
        if owner == "PaddleOCR"
        else {"MinerU"}
    )
    default = residency["default_ttl_seconds"]
    if type(default) is not int or not 0 <= default <= 2147483647:
        raise ValueError("invalid legacy default TTL")
    policies = {}
    for entry in residency["pipelines"]:
        if not isinstance(entry, dict):
            raise ValueError("invalid legacy pipeline")
        name, mode = entry.get("name"), entry.get("recognition_mode")
        if not isinstance(name, str):
            raise ValueError("invalid legacy pipeline name")
        if mode in retired_modes or name in retired_pipelines:
            continue
        if mode is not None and (
            mode not in mode_pipelines or mode_pipelines[mode] != name
        ):
            raise ValueError("unknown legacy recognition mode")
        if name not in set(mode_pipelines.values()):
            raise ValueError("unknown legacy pipeline")
        value, pinned = entry.get("ttl_seconds"), entry.get("pinned", False)
        if (
            type(pinned) is not bool
            or value is not None
            and (type(value) is not int or not 0 <= value <= 2147483647)
        ):
            raise ValueError("invalid legacy pipeline policy")
        if name in supported:
            if name in policies:
                raise ValueError("duplicate legacy pipeline policy")
            policies[name] = (
                0
                if pinned and owner == "PaddleOCR"
                else default
                if value is None
                else value
            )
    effective = [policies.get(name, default) for name in supported]
    return 0 if 0 in effective else max(effective)


__all__ = ["RuntimeSettings", "RuntimeSettingsStore", "RuntimeSettingsStoreError"]
