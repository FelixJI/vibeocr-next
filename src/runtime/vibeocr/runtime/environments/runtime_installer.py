"""Runtime installer servant for the prebuilt runtime environment tree.

PR2 起解释器与依赖闭包在构建期装配为展开目录随产品分发，运行时零
venv、零 uv、零安装事务：本进程只保留受管环境的注册/launch 信息/
切换与空间清理侍者（``--environment-switch-control`` 预留协议与
``--environment-cancel-control`` 清理取消）。公共面保持 v2 内部 wire：
前端只收路径与健康/能力状态，不接触依赖名、索引 URL 或包管理器参数。
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import threading
import urllib.parse
import urllib.request
from dataclasses import dataclass
from importlib.resources import files
from pathlib import Path
from typing import Any, Callable

from vibeocr.runtime.environments.runtime_lock import RuntimeLockTimeout
from vibeocr.runtime.environments.runtime_maintenance import (
    RuntimeCapabilityError,
    RuntimeInstallFailure,
    RuntimeInstallPlanBlocked,
    RuntimeInstallPlanStale,
    RuntimeSourceIdentityMismatch,
    safe_runtime_detail,
)
from vibeocr.runtime.environments.runtime_manifest import (
    ACCELERATOR_TO_PLAN,
    ManifestError,
)


class RuntimeInstallError(RuntimeInstallFailure):
    pass


class RuntimeIdentityMismatch(RuntimeInstallError, RuntimeSourceIdentityMismatch):
    """The verified Runtime source identity differs from the bound intent."""


class RuntimeCapabilityUnavailable(RuntimeInstallError, RuntimeCapabilityError):
    """A requested capability is absent from the verified Runtime manifest."""


_SHA256_RE = re.compile(r"^[0-9a-f]{64}$")


PROTOCOL_VERSION = 2


def _load_component_lock(path: Path) -> dict[str, Any]:
    try:
        value: Any = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        raise RuntimeInstallError(f"invalid component lock: {path}") from exc
    if not isinstance(value, dict) or value.get("schema_version") != 2:
        raise RuntimeInstallError("component lock schema_version must be 2")
    product = value.get("product")
    if not isinstance(product, dict):
        raise RuntimeInstallError("component lock requires product binding")
    if (
        product.get("component") != "next"
        or product.get("repository") != "FelixJI/vibeocr-next"
    ):
        raise RuntimeInstallError("component lock product identity is invalid")
    version = product.get("version")
    if not isinstance(version, str) or not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise RuntimeInstallError("component lock product version is invalid")
    source_sha = product.get("source_sha")
    if not isinstance(source_sha, str) or not re.fullmatch(r"[0-9a-f]{40}", source_sha):
        raise RuntimeInstallError("component lock product source_sha is invalid")
    manifest_sha = product.get("runtime_manifest_sha256")
    if not isinstance(manifest_sha, str) or not _SHA256_RE.fullmatch(manifest_sha):
        raise RuntimeInstallError("component lock runtime_manifest_sha256 is invalid")
    if product.get("accelerator") not in ACCELERATOR_TO_PLAN:
        raise RuntimeInstallError("component lock product accelerator is invalid")
    capabilities = value.get("required_capabilities")
    if not isinstance(capabilities, list) or not all(
        isinstance(item, str) and item for item in capabilities
    ):
        raise RuntimeInstallError("component lock required_capabilities is invalid")
    return value


def _python_in(runtime_root: Path) -> Path:
    candidates = (
        runtime_root / "Scripts" / "python.exe",
        runtime_root / "python.exe",
        runtime_root / "bin" / "python",
    )
    return next((path for path in candidates if path.is_file()), candidates[0])


_LOCK_DECLARATION_RE = re.compile(
    r"(?m)^([A-Za-z0-9][A-Za-z0-9._-]*)(?:\[[A-Za-z0-9._, -]+\])?\s*(?:==|@)"
)
_LOCK_HASH_RE = re.compile(r"--hash=sha256:([0-9a-f]{64})")
_DOWNLOAD_EVENT_MIN_BYTES = 4 * 1024 * 1024
_DOWNLOAD_CHUNK_BYTES = 1024 * 1024
_DOWNLOAD_HTTP_TIMEOUT_SECONDS = 60.0


def _normalize_dist_name(name: str) -> str:
    return re.sub(r"[-_.]+", "-", name).lower()


@dataclass(frozen=True, slots=True)
class _ResolvedArtifact:
    name: str
    url: str
    sha256: str | None
    filename: str
    version: str | None = None


def _lock_allowed_hashes(lock_path: Path) -> dict[str, set[str]]:
    """Parse the per-distribution sha256 allow-list from an exact lock."""
    allowed: dict[str, set[str]] = {}
    block: list[str] = []
    for raw_line in lock_path.read_text(encoding="utf-8").splitlines():
        line = raw_line.rstrip()
        if line.endswith("\\"):
            block.append(line[:-1])
            continue
        block.append(line)
        joined = " ".join(block)
        block = []
        match = _LOCK_DECLARATION_RE.match(joined)
        if match is None:
            continue
        allowed.setdefault(_normalize_dist_name(match.group(1)), set()).update(
            _LOCK_HASH_RE.findall(joined)
        )
    return allowed


def _parse_resolve_report(
    report_path: Path, download_root: Path | None = None
) -> tuple[_ResolvedArtifact, ...]:
    try:
        document = json.loads(report_path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        raise RuntimeInstallError("package resolve report is unreadable") from exc
    return _parse_resolve_document(document, download_root)


def _parse_resolve_document(
    document: object, download_root: Path | None
) -> tuple[_ResolvedArtifact, ...]:
    items = document.get("install") if isinstance(document, dict) else None
    if not isinstance(items, list) or not items:
        raise RuntimeInstallError("package resolve report has no artifacts")
    artifacts: list[_ResolvedArtifact] = []
    for item in items:
        if not isinstance(item, dict):
            raise RuntimeInstallError("package resolve report artifact is invalid")
        metadata = item.get("metadata")
        download = item.get("download_info")
        archive = download.get("archive_info") if isinstance(download, dict) else None
        hashes = archive.get("hashes") if isinstance(archive, dict) else None
        sha256 = hashes.get("sha256") if isinstance(hashes, dict) else None
        url = download.get("url") if isinstance(download, dict) else None
        name = metadata.get("name") if isinstance(metadata, dict) else None
        if (
            not isinstance(name, str)
            or not name
            or not isinstance(url, str)
            or not url.startswith(("http://", "https://", "file://"))
            or (sha256 is not None and not isinstance(sha256, str))
        ):
            raise RuntimeInstallError("package resolve report artifact is invalid")
        if url.startswith("file://"):
            parsed = urllib.parse.urlsplit(url)
            local = Path(urllib.request.url2pathname(parsed.path)).resolve()
            if (
                download_root is None
                or parsed.netloc not in {"", "localhost"}
                or local.parent != download_root.resolve()
            ):
                raise RuntimeInstallError("resolve artifact is outside download cache")
        filename = urllib.parse.unquote(url.rsplit("/", 1)[-1])
        if (
            not filename
            or "/" in filename
            or "\\" in filename
            or ":" in filename
            or ".." in filename
        ):
            raise RuntimeInstallError("package resolve artifact URL is unsafe")
        artifacts.append(
            _ResolvedArtifact(name, url, sha256, filename, metadata.get("version"))
        )
    return tuple(artifacts)


_OUTPUT_LOCK = threading.Lock()


def _emit(value: object) -> None:
    with _OUTPUT_LOCK:
        print(json.dumps(value, ensure_ascii=False, sort_keys=True), flush=True)


def _environment_cancel_receipt(accepted: bool) -> None:
    value = b"accepted" if accepted else b"rejected"
    # The control daemon must not own a buffered stdout lock during shutdown,
    # including the case where a terminal commit has already won the race.
    with _OUTPUT_LOCK:
        os.write(sys.stdout.fileno(), b'{"environment_cancel":"' + value + b'"}\n')


def _listen_environment_cancel(on_cancel: Callable[[], None]) -> None:
    # Raw fd reads avoid a daemon holding stdin's buffered lock at interpreter
    # shutdown. The inherited pipe also crosses the frozen bootloader boundary.
    command = bytearray()
    try:
        while len(command) < 64:
            value = os.read(sys.stdin.fileno(), 1)
            if not value:
                return
            if value == b"\n":
                if command.strip() == b"cancel":
                    on_cancel()
                return
            command.extend(value)
    except (OSError, ValueError):
        # No receipt means cancellation was not confirmed by this process.
        return


def _configure_utf8_standard_streams() -> None:
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if callable(reconfigure):
            reconfigure(encoding="utf-8")


def _request(value: object) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise RuntimeInstallError("Runtime Host request must be a JSON object")
    binding_fields = {
        "protocol_version",
        "product_root",
        "component_lock",
        "runtime_manifest",
    }
    request_kind = value.get("request_kind")
    if request_kind != "environment":
        # 在线安装/maintenance 的 request_kind（start/command/observe/
        # install_plan）已随预构建运行时树退役：运行时零安装。
        raise RuntimeInstallError("Runtime Host request_kind is invalid")
    required = binding_fields | {"request_kind", "action"}
    allowed = required | {
        "layout_manifest",
        "product_id",
        "device",
        "environment_id",
        "plan_id",
        "item_ids",
        "model_source_id",
        "paddleocr_model_source_id",
        "mineru_model_source_id",
        "prepared",
        "started_health",
    }
    from jsonschema import Draft202012Validator
    from jsonschema.exceptions import ValidationError

    schema = json.loads(
        files("vibeocr.runtime_contracts")
        .joinpath("runtime-host.schema.json")
        .read_text(encoding="utf-8")
    )
    try:
        Draft202012Validator(
            {"$ref": "#/$defs/ManagedEnvironmentRequest", "$defs": schema["$defs"]}
        ).validate(value)
    except ValidationError as exc:
        raise RuntimeInstallError(
            f"invalid environment request: {exc.message}"
        ) from exc
    if set(value).difference(allowed):
        raise RuntimeInstallError("Runtime Host request contains unknown fields")
    if not required.issubset(value) or value["protocol_version"] != PROTOCOL_VERSION:
        raise RuntimeInstallError(
            "Runtime Host request is incompatible with Protocol v2"
        )
    action = value["action"]
    fields = {
        "list": set(),
        "initialize_default": set(),
        "set_sources": set(),
        "prepare_switch": {"environment_id"},
        "commit_switch": {"prepared"},
        "delete": {"environment_id"},
        "preview_cleanup": set(),
        "run_cleanup": {"plan_id", "item_ids"},
    }
    if (
        not isinstance(action, str)
        or action not in fields
        or not fields[action].issubset(value)
    ):
        raise RuntimeInstallError("Runtime environment action is invalid")
    extras = (
        set(value)
        - binding_fields
        - {"request_kind", "action", "layout_manifest", "product_id"}
    )
    if (
        extras
        - fields[action]
        - ({"device"} if action == "prepare_switch" else set())
        - (
            {
                "environment_id",
                "model_source_id",
                "paddleocr_model_source_id",
                "mineru_model_source_id",
            }
            if action == "set_sources"
            else set()
        )
        - ({"started_health"} if action == "commit_switch" else set())
    ):
        raise RuntimeInstallError(
            "Runtime environment action contains unrelated fields"
        )
    if "item_ids" in value and (
        not isinstance(value["item_ids"], list)
        or not value["item_ids"]
        or len(value["item_ids"]) > 2048
        or any(
            not isinstance(item, str) or not item or len(item) > 1024
            for item in value["item_ids"]
        )
        or len(set(value["item_ids"])) != len(value["item_ids"])
    ):
        raise RuntimeInstallError("Runtime cleanup selection is invalid")
    if "device" in value and (
        not isinstance(value["device"], str)
        or value["device"] not in {"cpu", "directml"}
    ):
        raise RuntimeInstallError("Runtime environment device is invalid")
    for field in ("environment_id", "plan_id"):
        if field not in value:
            continue
        item = value[field]
        if action == "set_sources" and field == "environment_id":
            # null/省略 → 全局默认；只拒绝非法非字符串值。
            if item is not None and (not isinstance(item, str) or not item):
                raise RuntimeInstallError(
                    "Runtime environment environment_id is invalid"
                )
            continue
        if not isinstance(item, str) or not item:
            raise RuntimeInstallError(f"Runtime environment {field} is invalid")
    for field in (
        "model_source_id",
        "paddleocr_model_source_id",
        "mineru_model_source_id",
    ):
        if (
            field in value
            and value[field] is not None
            and (
                not isinstance(value[field], str)
                or not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,40}", value[field])
            )
        ):
            raise RuntimeInstallError(f"Runtime environment {field} is invalid")
    if "prepared" in value and not isinstance(value["prepared"], dict):
        raise RuntimeInstallError("Runtime environment prepared switch is invalid")
    if "started_health" in value and not isinstance(value["started_health"], dict):
        raise RuntimeInstallError("Runtime environment health evidence is invalid")
    for field in ("product_root", "component_lock", "runtime_manifest"):
        if not isinstance(value[field], str) or not value[field]:
            raise RuntimeInstallError(f"Runtime Host {field} is invalid")
    return value


def _failure_envelope(exc: Exception, *, operation: str | None) -> dict[str, Any]:
    legacy_code = "install_failed"
    canonical_code = "RUNTIME_INSTALL_FAILED"
    category = "backend_unavailable"
    retryable = False
    detail: dict[str, Any] = {}
    if isinstance(exc, (RuntimeInstallPlanStale, RuntimeInstallPlanBlocked)):
        legacy_code = "invalid_request"
        canonical_code = (
            "RUNTIME_INSTALL_PLAN_STALE"
            if isinstance(exc, RuntimeInstallPlanStale)
            else "RUNTIME_INSTALL_PLAN_BLOCKED"
        )
        category = "conflict"
    elif isinstance(exc, RuntimeLockTimeout):
        legacy_code = "lock_timeout"
        canonical_code = "RUNTIME_BUSY"
        category = "transient"
        retryable = True
    elif isinstance(exc, OSError):
        legacy_code = "io_error"
        canonical_code = "RUNTIME_IO_ERROR"
        category = "transient"
        retryable = True
    elif isinstance(exc, ManifestError):
        legacy_code = "invalid_binding"
        canonical_code = "RUNTIME_IDENTITY_MISMATCH"
        category = "identity"
    elif (
        isinstance(exc, (json.JSONDecodeError, RuntimeInstallFailure))
        and operation is None
    ):
        legacy_code = "invalid_request"
        canonical_code = "VALIDATION_ERROR"
        category = "validation"
    error: dict[str, Any] = {
        "code": legacy_code,
        "canonical_code": canonical_code,
        "category": category,
        "message": str(exc),
        "message_code": "runtime.operation_failed",
        "retryable": retryable,
        "detail": detail,
    }
    if retryable:
        error["retry_after"] = 1
    return {
        "protocol_version": PROTOCOL_VERSION,
        "ok": False,
        "operation": operation,
        "error": error,
    }


def main(argv: list[str] | None = None) -> int:
    _configure_utf8_standard_streams()
    parser = argparse.ArgumentParser(prog="vibeocr-runtime-installer")
    parser.add_argument("--request-json")
    parser.add_argument("--environment-cancel-control", action="store_true")
    parser.add_argument("--environment-switch-control", action="store_true")
    args = parser.parse_args(argv)
    request_kind: str | None = None
    try:
        raw_request = (
            args.request_json if args.request_json is not None else sys.stdin.read()
        )
        request = _request(json.loads(raw_request))
        request_kind = request["request_kind"]
        from vibeocr.runtime.environments.managed_environments import (
            ManagedEnvironmentStore,
        )

        manager = ManagedEnvironmentStore(
            product_root=request["product_root"],
            component_lock=request["component_lock"],
            runtime_manifest=request["runtime_manifest"],
            layout_manifest=request.get("layout_manifest"),
            product_id=request.get("product_id"),
        )
        action = request["action"]
        if args.environment_cancel_control and action == "run_cleanup":
            threading.Thread(
                target=_listen_environment_cancel,
                args=(lambda: manager.cancel_cleanup(_environment_cancel_receipt),),
                daemon=True,
            ).start()
        if action in {"prepare_switch", "commit_switch"}:
            if action != "prepare_switch" or not args.environment_switch_control:
                raise RuntimeInstallError(
                    "environment switch requires a held reservation"
                )
            with manager.switch_reservation(
                request["environment_id"], request.get("device")
            ) as prepared:
                _emit(
                    {
                        "protocol_version": PROTOCOL_VERSION,
                        "response_kind": "environment",
                        "action": "prepare_switch",
                        "result": prepared,
                    }
                )
                line = sys.stdin.readline(65537)
                if not line:
                    return 0  # Host exited: OS target lock is released by the context.
                command = json.loads(line)
                if command == {"action": "abort_switch"}:
                    return 0
                if (
                    not isinstance(command, dict)
                    or set(command) != {"action", "started_health"}
                    or command["action"] != "commit_switch"
                ):
                    raise RuntimeInstallError("switch control command is invalid")
                payload = manager.commit_switch(
                    prepared, started_health=command["started_health"]
                )
            _emit(
                {
                    "protocol_version": PROTOCOL_VERSION,
                    "response_kind": "environment",
                    "action": "commit_switch",
                    "result": payload,
                }
            )
            return 0
        if action == "list":
            payload = manager.list()
        elif action == "initialize_default":
            payload = manager.initialize_default()
        elif action == "set_sources":
            payload = manager.set_sources(
                request.get("environment_id"),
                **{
                    field: request[field]
                    for field in (
                        "model_source_id",
                        "paddleocr_model_source_id",
                        "mineru_model_source_id",
                    )
                    if field in request
                },
            )
        elif action == "preview_cleanup":
            payload = manager.preview_cleanup()
        elif action == "run_cleanup":
            payload = manager.run_cleanup(request["plan_id"], request["item_ids"])
        else:
            manager.delete(request["environment_id"])
            payload = {"deleted_id": request["environment_id"]}
        _emit(
            {
                "protocol_version": PROTOCOL_VERSION,
                "response_kind": "environment",
                "action": action,
                "result": payload,
            }
        )
        return 0
    except (
        json.JSONDecodeError,
        ManifestError,
        RuntimeInstallError,
        RuntimeLockTimeout,
        OSError,
    ) as exc:
        failure = _failure_envelope(exc, operation=None)
        if request_kind == "environment":
            failure["error"]["message"] = safe_runtime_detail(
                failure["error"]["message"]
            )
        _emit(failure)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())


__all__ = [
    "RuntimeCapabilityUnavailable",
    "RuntimeInstallError",
    "RuntimeIdentityMismatch",
    "main",
]
