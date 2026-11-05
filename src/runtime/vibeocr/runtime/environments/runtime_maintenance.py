"""Runtime status/profile descriptors and sanitized failure detail helpers."""

from __future__ import annotations

import importlib.metadata
import json
import os
import re
import subprocess
import sys
import threading
import time
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Any
from uuid import uuid4

from vibeocr.runtime.environments.runtime_manifest import (
    RuntimeComponent,
    RuntimeInstallScope,
    RuntimeProfile,
    covering_profile_id,
    load_runtime_manifest,
    migrate_legacy_component_ids,
    plan_profile_id,
    runtime_component_binding,
    scoped_component_version,
)

_COMPONENT_PROBE_CACHE_TTL_SECONDS = 10.0
_WINDOWS_REPLACE_RETRY_DELAYS = (0.01, 0.02, 0.04, 0.08)
_component_probe_cache_lock = threading.Lock()
_component_probe_cache: dict[
    tuple[str, str, tuple[str, ...], int | None], tuple[float, dict[str, bool]]
] = {}


class RuntimeOperationError(RuntimeError):
    """Base error for Runtime install/environment control."""


class RuntimeInstallPlanStale(RuntimeOperationError):
    pass


class RuntimeInstallPlanBlocked(RuntimeOperationError):
    pass


class RuntimeCapabilityError(RuntimeOperationError):
    """A requested Runtime capability is unavailable."""


class RuntimeInstallFailure(RuntimeOperationError):
    """Runtime installation failed after the request was accepted."""

    def __init__(
        self,
        message: str,
        *,
        reason_code: str = "unknown",
        next_action: str = "inspect_diagnostics",
    ) -> None:
        super().__init__(message)
        self.reason_code = reason_code
        self.next_action = next_action


def safe_runtime_detail(text: str) -> str:
    """Bound public diagnostics and remove locations and credential values."""
    text = re.sub(r"(?i)\b[a-z][a-z0-9+.-]*://[^\s<>\"']+", "[url]", text)
    text = re.sub(r"(?i)(?:[a-z]:[\\/]|\\\\)[^\r\n\"']+", "[path]", text)
    text = re.sub(r"(?<![\w])/(?:[^\s/]+/)*[^\s,;)]+", "[path]", text)
    text = re.sub(
        r"""(?i)\b([\w-]*(?:token|password|secret|authorization|api[_-]?key))["']?\s*[:=]\s*"""
        r"""(?:"(?:\\.|[^"\\\r\n])*"|'(?:\\.|[^'\\\r\n])*'|["'][^\r\n]*|(?:Bearer\s+|Basic\s+)?[^\s,;]+)""",
        r"\1=[redacted]",
        text,
    )
    text = re.sub(r"(?i)\bBearer\s+\S+", "Bearer [redacted]", text)
    return text[-4000:]


class RuntimeSourceIdentityMismatch(RuntimeOperationError):
    """A durable operation is bound to a different verified Runtime source."""


def _atomic_json(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{path.name}.{uuid4().hex}.tmp")
    try:
        with temporary.open("w", encoding="utf-8") as stream:
            json.dump(value, stream, ensure_ascii=False, sort_keys=True)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        _replace_transient_windows_lock(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def _replace_transient_windows_lock(source: Path, destination: Path) -> None:
    try:
        os.replace(source, destination)
        return
    except PermissionError as exc:
        if os.name != "nt" or exc.winerror not in {5, 32}:
            raise
        last_error = exc

    for delay in _WINDOWS_REPLACE_RETRY_DELAYS:
        time.sleep(delay)
        try:
            os.replace(source, destination)
            return
        except PermissionError as exc:
            if exc.winerror not in {5, 32}:
                raise
            last_error = exc
    raise last_error


@dataclass(frozen=True, slots=True)
class RuntimeProfileDescriptor:
    profile_id: str
    accelerator: str
    components: tuple[RuntimeComponent, ...]

    def to_payload(self) -> dict[str, Any]:
        return {
            "profile_id": self.profile_id,
            "accelerator": self.accelerator,
            "components": [component.to_payload() for component in self.components],
        }


def profile_descriptor(
    profile: RuntimeProfile,
    *,
    accelerator: str,
) -> RuntimeProfileDescriptor:
    return RuntimeProfileDescriptor(
        profile_id=profile.name,
        accelerator=accelerator,
        components=profile.components,
    )


def runtime_source_identity(manifest: Any) -> dict[str, str]:
    return {
        "backend_version": manifest.backend_version,
        "backend_source_sha": manifest.source_commit,
        "runtime_manifest_sha256": manifest.sha256,
    }


def _distribution_versions(runtime_root: Path) -> dict[str, str]:
    site_packages = [runtime_root / "Lib" / "site-packages"]
    site_packages.extend((runtime_root / "lib").glob("python*/site-packages"))
    versions: dict[str, str] = {}
    for directory in site_packages:
        if not directory.is_dir():
            continue
        for distribution in importlib.metadata.Distribution.discover(
            path=[str(directory)]
        ):
            name = distribution.metadata.get("Name")
            if isinstance(name, str) and name:
                versions[re.sub(r"[-_.]+", "-", name).lower()] = distribution.version
    return versions


def probe_runtime_components(
    runtime_root: Path,
    component_ids: tuple[str, ...],
    *,
    profile_id: str = "win-x64-cpu",
) -> dict[str, bool]:
    paddle_root = runtime_root / "engines" / "paddle"
    paddle_ids = tuple(x for x in component_ids if x.startswith("paddleocr-"))
    if paddle_ids and paddle_root.is_dir():
        host_ids = tuple(x for x in component_ids if x not in paddle_ids)
        return {
            **(
                probe_runtime_components(runtime_root, host_ids, profile_id=profile_id)
                if host_ids
                else {}
            ),
            **probe_runtime_components(paddle_root, paddle_ids, profile_id=profile_id),
        }
    python = next(
        (
            candidate
            for candidate in (
                runtime_root / "Scripts" / "python.exe",
                runtime_root / "python.exe",
                runtime_root / "bin" / "python",
            )
            if candidate.is_file()
        ),
        runtime_root / "python.exe",
    )
    modules = {
        component_id: runtime_component_binding(profile_id, component_id).import_name
        for component_id in component_ids
    }
    script = (
        "import importlib,json,os,sys\n"
        "code_root=os.environ.get('VIBEOCR_PRODUCT_CODE_ROOT')\n"
        "if code_root: sys.path.insert(0,code_root)\n"
        "modules=json.loads(sys.argv[1])\n"
        "result={}\n"
        "for component_id,module in modules.items():\n"
        "  try:\n"
        "    importlib.import_module(module)\n"
        "    if component_id.startswith('paddleocr-'):\n"
        "      paddle=importlib.import_module('paddle')\n"
        "      if component_id.endswith('-cuda'):\n"
        "        assert paddle.device.is_compiled_with_cuda()\n"
        "        paddle.set_device('gpu:0')\n"
        "      else:\n"
        "        paddle.set_device('cpu')\n"
        "      assert float((paddle.to_tensor([1.0])+1).cpu().numpy()[0])==2.0\n"
        "    if component_id in ('gpu_runtime','mineru-cuda'):\n"
        "      torch=importlib.import_module('torch')\n"
        "      assert torch.cuda.is_available()\n"
        "      assert (torch.ones(1,device='cuda')+1).cpu().item()==2.0\n"
        "    if component_id=='mineru-cpu':\n"
        "      importlib.import_module('onnxruntime')\n"
        "      importlib.import_module('mineru_llama_cpp')\n"
        "  except BaseException:\n"
        "    result[component_id]=False\n"
        "  else:\n"
        "    result[component_id]=True\n"
        "print('VIBEOCR_COMPONENT_PROBE='+json.dumps(result,sort_keys=True))\n"
    )
    try:
        completed = subprocess.run(
            [
                str(python),
                "-I",
                "-B",
                "-c",
                script,
                json.dumps(modules, sort_keys=True),
            ],
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=90,
        )
    except (OSError, subprocess.TimeoutExpired):
        return {component_id: False for component_id in component_ids}
    prefix = "VIBEOCR_COMPONENT_PROBE="
    payload = next(
        (
            line[len(prefix) :]
            for line in reversed(completed.stdout.splitlines())
            if line.startswith(prefix)
        ),
        None,
    )
    if completed.returncode != 0 or payload is None:
        return {component_id: False for component_id in component_ids}
    try:
        value = json.loads(payload)
    except ValueError:
        return {component_id: False for component_id in component_ids}
    return {
        component_id: value.get(component_id) is True for component_id in component_ids
    }


def _cached_runtime_component_probe(
    runtime_root: Path, component_ids: tuple[str, ...], *, profile_id: str
) -> dict[str, bool]:
    marker = runtime_root / ".installed.json"
    try:
        marker_mtime = marker.stat().st_mtime_ns
    except OSError:
        marker_mtime = None
    key = (str(runtime_root.resolve()), profile_id, component_ids, marker_mtime)
    now = time.monotonic()
    with _component_probe_cache_lock:
        cached = _component_probe_cache.get(key)
        if cached is not None and cached[0] > now:
            return dict(cached[1])
        result = probe_runtime_components(
            runtime_root, component_ids, profile_id=profile_id
        )
        expired = [
            existing_key
            for existing_key, (expires_at, _) in _component_probe_cache.items()
            if expires_at <= now
        ]
        for existing_key in expired:
            _component_probe_cache.pop(existing_key, None)
        _component_probe_cache[key] = (
            now + _COMPONENT_PROBE_CACHE_TTL_SECONDS,
            dict(result),
        )
        return result


def _installed_marker(runtime_root: Path | None) -> dict[str, Any] | None:
    if runtime_root is None:
        return None
    try:
        value = json.loads(
            (runtime_root / ".installed.json").read_text(encoding="utf-8")
        )
    except (OSError, ValueError):
        return None
    return value if isinstance(value, dict) else None


def _marker_identity_matches(
    marker: dict[str, Any] | None,
    *,
    manifest: Any,
    accelerator: str,
) -> bool:
    return marker is not None and all(
        marker.get(key) == value
        for key, value in (
            ("schema_version", 1),
            ("backend_version", manifest.backend_version),
            ("manifest_sha256", manifest.sha256),
            ("accelerator", accelerator),
        )
    )


def declared_installed_closure(
    marker: dict[str, Any] | None,
    *,
    manifest: Any,
    accelerator: str,
) -> frozenset[str] | None:
    """Read the marker-declared installed closure for display projections.

    The closure participates only when the marker's basic identity matches the
    verified manifest and the declared ids stay inside the accelerator plan;
    whether the closure is an installable scope remains owned by the
    installer's integrity gate.
    """
    if not _marker_identity_matches(marker, manifest=manifest, accelerator=accelerator):
        return None
    installed = marker.get("component_ids")
    if (
        not isinstance(installed, list)
        or not installed
        or any(not isinstance(item, str) or not item for item in installed)
        or len(set(installed)) != len(installed)
    ):
        return None
    # 统一闭包后新 manifest 只产 base 档：旧 marker 的 cpu/cuda 档位 profile
    # 缺席时按 base 投影（与 plan_profile_id/covering_profile_id 同一规则），
    # 否则旧环境状态读取直接 KeyError。
    profile_id = plan_profile_id(manifest, accelerator)
    plan_ids = {
        component.component_id for component in manifest.profiles[profile_id].components
    }
    # 旧 marker 里的退役 id（rapidocr-base/原生 paddle 等）先折叠再比对：
    # 历史闭包应继续投影到覆盖 profile，而不是整体作废。
    closure = frozenset(migrate_legacy_component_ids(profile_id, tuple(installed)))
    return closure if closure <= plan_ids else None


def _legacy_closure_with_current_code(
    marker: dict[str, Any] | None,
    *,
    manifest: Any,
    accelerator: str,
    runtime_root: Path | None,
) -> frozenset[str] | None:
    code_root = os.environ.get("VIBEOCR_PRODUCT_CODE_ROOT")
    if (
        os.environ.get("VIBEOCR_MANAGED_ENVIRONMENT_ID") != "legacy"
        or runtime_root is None
        or not code_root
        or not Path(__file__).resolve().is_relative_to(Path(code_root).resolve())
        or not isinstance(marker, dict)
        or marker.get("schema_version") != 1
        or marker.get("accelerator") != accelerator
        or not isinstance(marker.get("backend_version"), str)
        or not isinstance(marker.get("manifest_sha256"), str)
        or not re.fullmatch(r"[0-9a-f]{64}", marker["manifest_sha256"])
        or marker["backend_version"]
        != _distribution_versions(runtime_root).get("vibeocr-next-runtime")
        or Path(sys.prefix).resolve() != runtime_root.resolve()
        or tuple(sys.version_info[:3])
        != tuple(int(part) for part in manifest.python.version.split("."))
    ):
        return None
    ids = marker.get("component_ids")
    if (
        not isinstance(ids, list)
        or not ids
        or any(not isinstance(item, str) or not item for item in ids)
        or len(set(ids)) != len(ids)
    ):
        return None
    # 与 declared_installed_closure 同一档位解析与退役折叠规则：新
    # manifest 缺历史档位 profile 时按 base 投影，保证旧闭包可读取。
    profile_id = plan_profile_id(manifest, accelerator)
    plan_ids = {
        component.component_id for component in manifest.profiles[profile_id].components
    }
    closure = frozenset(migrate_legacy_component_ids(profile_id, tuple(ids)))
    return closure if closure <= plan_ids else None


def _component_statuses(
    descriptor: RuntimeProfileDescriptor,
    *,
    manifest: Any,
    runtime_root: Path | None,
    probe_results: dict[str, bool] | None = None,
    marker: dict[str, Any] | None = None,
    managed_scope: RuntimeInstallScope | None = None,
    managed_closure: frozenset[str] | None = None,
) -> list[dict[str, Any]]:
    python_ready = runtime_root is not None and any(
        candidate.is_file()
        for candidate in (
            runtime_root / "Scripts" / "python.exe",
            runtime_root / "python.exe",
            runtime_root / "bin" / "python",
        )
    )
    # marker 的基础字段逐项比较：component_ids（安装闭包）是 2.7.0 新增
    # 字段，由 installer 的 integrity gate 负责判定，不参与展示层 drift；
    # 闭包外的可选组件按 not_required 投影，不得谎报 desired ready。
    legacy_closure = _legacy_closure_with_current_code(
        marker,
        manifest=manifest,
        accelerator=descriptor.accelerator,
        runtime_root=runtime_root,
    )
    marker_matches = (
        managed_scope is not None
        or managed_closure is not None
        or legacy_closure is not None
        or _marker_identity_matches(
            marker, manifest=manifest, accelerator=descriptor.accelerator
        )
    )
    required_ids = (
        frozenset((*managed_scope.component_ids, "runtime_host"))
        if managed_scope is not None
        else managed_closure
        if managed_closure is not None
        else legacy_closure
        or declared_installed_closure(
            marker, manifest=manifest, accelerator=descriptor.accelerator
        )
    )
    versions = _distribution_versions(runtime_root) if runtime_root is not None else {}
    product_code = os.environ.get("VIBEOCR_PRODUCT_CODE_ROOT")
    if product_code and Path(__file__).resolve().is_relative_to(
        Path(product_code).resolve()
    ):
        for distribution in importlib.metadata.Distribution.discover(
            path=[product_code]
        ):
            name = distribution.metadata.get("Name")
            if name and re.sub(r"[-_.]+", "-", name).lower() == "vibeocr-next-runtime":
                versions["vibeocr-next-runtime"] = distribution.version
                break
    paddle_versions = (
        _distribution_versions(runtime_root / "engines" / "paddle")
        if runtime_root is not None and (runtime_root / "engines" / "paddle").is_dir()
        else versions
    )
    profile = manifest.profiles[descriptor.profile_id]
    installed_scope = managed_scope or next(
        (scope for scope in profile.scopes if set(scope.component_ids) == required_ids),
        None,
    )
    statuses: list[dict[str, Any]] = []
    for component in descriptor.components:
        component = replace(
            component,
            version=scoped_component_version(profile, installed_scope, component),
        )
        if required_ids is not None and component.component_id not in required_ids:
            # base-only / 精确 scope 安装不含该可选组件：缺席是合法状态，
            # 不是 drift，也不可 repair（扩闭包属于 ensure 的选择面）。
            statuses.append(
                {
                    **component.to_payload(),
                    "state": "not_required",
                    "desired_state": "not_required",
                    "desired_version": None,
                    "actual_state": "missing",
                    "actual_version": None,
                    "drift_reason": "none",
                    "repairable": False,
                }
            )
            continue
        actual_version: str | None = None
        if (
            marker is None and managed_scope is None and managed_closure is None
        ) or not python_ready:
            actual_state = "missing"
            drift_reason = "missing"
        elif not marker_matches:
            actual_state = "drifted"
            drift_reason = "identity_mismatch"
        elif component.version is None:
            actual_state = "ready"
            drift_reason = "none"
        else:
            distribution = runtime_component_binding(
                descriptor.profile_id, component.component_id
            ).distribution
            environment_versions = (
                paddle_versions
                if component.component_id.startswith("paddleocr-")
                else versions
            )
            actual_version = environment_versions.get(
                re.sub(r"[-_.]+", "-", distribution).lower()
            )
            if actual_version is None:
                actual_state = "missing"
                drift_reason = "missing"
            elif actual_version != component.version:
                actual_state = "drifted"
                drift_reason = "version_mismatch"
            else:
                actual_state = "ready"
                drift_reason = "none"
        if (
            actual_state == "ready"
            and probe_results is not None
            and not probe_results.get(component.component_id, False)
        ):
            actual_state = "drifted"
            drift_reason = "integrity_failed"
        statuses.append(
            {
                **component.to_payload(),
                "state": "ready" if actual_state == "ready" else "failed",
                "desired_state": "ready",
                "desired_version": component.version,
                "actual_state": actual_state,
                "actual_version": actual_version,
                "drift_reason": drift_reason,
                "repairable": actual_state != "ready",
            }
        )
    return statuses


def runtime_profile_status(
    manifest: Any,
    *,
    accelerator: str,
    runtime_root: Path | None,
    probe_results: dict[str, bool] | None = None,
    profile_id: str | None = None,
    managed_scope: RuntimeInstallScope | None = None,
    managed_closure: frozenset[str] | None = None,
) -> dict[str, Any]:
    """Project one desired/actual component view for every transport.

    ``profile_id`` pins the projected component set, declared versions, and
    import bindings; the installer's drift evaluation pins it to the covering
    profile of the *installed* closure. When omitted, the projection follows
    that same rule: the covering profile of the marker-declared installed
    closure when one is trusted, and the historical accelerator-plan
    projection otherwise, so a base-only installation reports
    ``win-x64-base`` instead of a plan view it does not satisfy.

    ``managed_closure`` 是 #270 为退役原生 Paddle 环境保留的只读已安装闭包
    投影：与真实安装 scope 一样作为可信闭包参与覆盖 profile/探针范围，
    但不携带锁或安装身份（版本漂移按历史 manifest 元数据评估）。
    """
    trusted_closure = managed_scope is not None or managed_closure is not None
    marker = None if trusted_closure else _installed_marker(runtime_root)
    if profile_id is None:
        legacy_closure = (
            _legacy_closure_with_current_code(
                marker,
                manifest=manifest,
                accelerator=accelerator,
                runtime_root=runtime_root,
            )
            if not trusted_closure
            else None
        )
        closure = (
            frozenset((*managed_scope.component_ids, "runtime_host"))
            if managed_scope is not None
            else managed_closure
            if managed_closure is not None
            else legacy_closure
            or declared_installed_closure(
                marker, manifest=manifest, accelerator=accelerator
            )
        )
        profile_id = (
            covering_profile_id(
                manifest, accelerator=accelerator, component_ids=closure
            )
            if closure is not None
            # 无闭包证据（空 runtime root）时按 accelerator 档位投影；统一
            # 闭包后新 manifest 只产 base 档，历史 accelerator 档位缺失时
            # 回退 base，与 covering_profile_id 同一读取规则。
            else plan_profile_id(manifest, accelerator)
        )
    descriptor = profile_descriptor(
        manifest.profiles[profile_id], accelerator=accelerator
    )
    if probe_results is None and runtime_root is not None:
        required_ids = (
            frozenset((*managed_scope.component_ids, "runtime_host"))
            if managed_scope is not None
            else managed_closure
            if managed_closure is not None
            else None
        )
        probe_results = _cached_runtime_component_probe(
            runtime_root,
            tuple(
                component.component_id
                for component in descriptor.components
                if required_ids is None or component.component_id in required_ids
            ),
            profile_id=profile_id,
        )
    return {
        "profile_id": descriptor.profile_id,
        "accelerator": descriptor.accelerator,
        "components": _component_statuses(
            descriptor,
            manifest=manifest,
            runtime_root=runtime_root,
            probe_results=probe_results,
            marker=marker,
            managed_scope=managed_scope,
            managed_closure=managed_closure,
        ),
    }


def runtime_status_from_environment(
    instance_id: str,
    service_state: str,
) -> dict[str, Any]:
    """Build the HTTP status snapshot from installer-owned environment/state."""
    manifest_value = os.environ.get("VIBEOCR_RUNTIME_MANIFEST")
    accelerator = os.environ.get("VIBEOCR_RUNTIME_ACCELERATOR")
    state_root_value = os.environ.get("VIBEOCR_RUNTIME_STATE_ROOT")
    runtime_root_value = os.environ.get("VIBEOCR_RUNTIME_ROOT")
    if not manifest_value or not accelerator or not state_root_value:
        raise RuntimeError("Runtime status environment is incomplete")
    # Runtime installation verified the full release asset set before launch.
    # Status polling only needs the immutable descriptor and must not rehash the
    # large Python/runtime archives on every HTTP request.
    manifest = load_runtime_manifest(manifest_value, verify_artifacts=False)
    managed_scope = None
    managed_closure: frozenset[str] | None = None
    managed_id = os.environ.get("VIBEOCR_MANAGED_ENVIRONMENT_ID")
    managed_recipe = os.environ.get("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE")
    if managed_id and managed_id != "legacy":
        from vibeocr.runtime.environments.managed_environments import (
            ManagedEnvironmentStore,
        )

        if not runtime_root_value or not managed_recipe:
            raise RuntimeError("managed Runtime status environment is incomplete")
        manager = ManagedEnvironmentStore(
            product_root=os.environ["VIBEOCR_PRODUCT_ROOT"],
            component_lock=os.environ["VIBEOCR_COMPONENT_LOCK"],
            runtime_manifest=manifest_value,
            layout_manifest=os.environ.get("VIBEOCR_LAYOUT_MANIFEST") or None,
            product_id=os.environ.get("VIBEOCR_PRODUCT_ID") or None,
        )
        managed_scope, managed_closure, managed_accelerator = manager.status_scope(
            managed_id,
            int(os.environ["VIBEOCR_MANAGED_ENVIRONMENT_REVISION"]),
            Path(runtime_root_value),
            managed_recipe,
            os.environ.get("VIBEOCR_MANAGED_ENVIRONMENT_DEVICE"),
        )
        if managed_accelerator != accelerator:
            raise RuntimeError("managed Runtime accelerator differs from registry")
    profile = runtime_profile_status(
        manifest,
        accelerator=accelerator,
        runtime_root=Path(runtime_root_value) if runtime_root_value else None,
        managed_scope=managed_scope,
        managed_closure=managed_closure,
    )
    if service_state != "maintenance" and any(
        # not_required 组件缺席是合法状态（base-only / 精确 scope 安装），
        # 不得把健康的运行时判成 degraded。
        component.get("desired_state") != "not_required"
        and component.get("actual_state") != "ready"
        for component in profile["components"]
    ):
        service_state = "degraded"
    from vibeocr.runtime import __version__

    return {
        "schema_version": 2,
        "instance_id": instance_id,
        "service_state": service_state,
        "backend_version": __version__,
        "source": runtime_source_identity(manifest),
        "profile": profile,
    }


__all__ = [
    "RuntimeCapabilityError",
    "RuntimeInstallFailure",
    "RuntimeInstallPlanBlocked",
    "RuntimeInstallPlanStale",
    "RuntimeOperationError",
    "RuntimeProfileDescriptor",
    "RuntimeSourceIdentityMismatch",
    "declared_installed_closure",
    "profile_descriptor",
    "runtime_source_identity",
    "runtime_profile_status",
    "runtime_status_from_environment",
    "safe_runtime_detail",
]
