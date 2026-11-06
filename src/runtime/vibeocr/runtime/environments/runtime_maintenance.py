"""Runtime status/profile descriptors and sanitized failure detail helpers."""

from __future__ import annotations

import importlib.metadata
import json
import os
import re
import subprocess
import threading
import time
from dataclasses import replace
from pathlib import Path
from typing import Any
from uuid import uuid4

from vibeocr.runtime.environments.runtime_manifest import (
    RuntimeInstallScope,
    RuntimeManifest,
    RuntimeProfile,
    covering_profile_id,
    load_runtime_manifest,
    runtime_component_binding,
    scoped_component_version,
)

_COMPONENT_PROBE_CACHE_TTL_SECONDS = 10.0
_WINDOWS_REPLACE_RETRY_DELAYS = (0.01, 0.02, 0.04, 0.08)
_component_probe_cache_lock = threading.Lock()
_component_probe_cache: dict[
    tuple[str, str, tuple[str, ...]], tuple[float, dict[str, bool]]
] = {}


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


def runtime_source_identity(manifest: RuntimeManifest) -> dict[str, str]:
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
    python = runtime_root / "python.exe"
    modules = {
        component_id: runtime_component_binding(profile_id, component_id).import_name
        for component_id in component_ids
    }
    script = (
        "import importlib,json,os,sys\n"
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
    key = (str(runtime_root.resolve()), profile_id, component_ids)
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


def _component_statuses(
    profile: RuntimeProfile,
    scope: RuntimeInstallScope,
    runtime_root: Path,
    required_ids: frozenset[str],
    probe_results: dict[str, bool],
) -> list[dict[str, Any]]:
    python_ready = (runtime_root / "python.exe").is_file()
    versions = _distribution_versions(runtime_root)
    statuses: list[dict[str, Any]] = []
    for component in profile.components:
        component = replace(
            component,
            version=scoped_component_version(profile, scope, component),
        )
        if component.component_id not in required_ids:
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
        if not python_ready:
            actual_state = "missing"
            drift_reason = "missing"
        elif component.version is None:
            actual_state = "ready"
            drift_reason = "none"
        else:
            distribution = runtime_component_binding(
                profile.name, component.component_id
            ).distribution
            actual_version = versions.get(re.sub(r"[-_.]+", "-", distribution).lower())
            if actual_version is None:
                actual_state = "missing"
                drift_reason = "missing"
            elif actual_version != component.version:
                actual_state = "drifted"
                drift_reason = "version_mismatch"
            else:
                actual_state = "ready"
                drift_reason = "none"
        if actual_state == "ready" and not probe_results.get(
            component.component_id, False
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
                "repairable": False,
            }
        )
    return statuses


def _product_profile_status(
    manifest: RuntimeManifest, accelerator: str, tree: Path, scope: RuntimeInstallScope
) -> dict[str, Any]:
    required_ids = frozenset((*scope.component_ids, "runtime_host"))
    profile_id = covering_profile_id(
        manifest, accelerator=accelerator, component_ids=required_ids
    )
    profile = manifest.profiles[profile_id]
    probe = _cached_runtime_component_probe(
        tree,
        tuple(
            c.component_id for c in profile.components if c.component_id in required_ids
        ),
        profile_id=profile_id,
    )
    return {
        "profile_id": profile.name,
        "accelerator": accelerator,
        "components": _component_statuses(profile, scope, tree, required_ids, probe),
    }


def runtime_status_from_environment(
    instance_id: str,
    service_state: str,
) -> dict[str, Any]:
    """Project the bound product tree; never probe registry-selected interpreters."""
    if os.environ.get("VIBEOCR_PRODUCT_RUNTIME") != "1":
        raise RuntimeError("仅能读取随包产品 Runtime 的状态。")
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
    if not runtime_root_value:
        raise RuntimeError("缺少产品解释器树。")
    if manifest.runtime_env is None:
        raise RuntimeError("产品清单缺少预构建 Runtime 绑定。")
    expected_root = manifest.path.parent.parent / "env" / manifest.runtime_env.root
    if Path(runtime_root_value).resolve() != expected_root.resolve():
        raise RuntimeError("产品状态根与随包解释器树不匹配。")
    product_scope = manifest.paddle_onnx_environments.get("paddleocr-ort-directml")
    if product_scope is None:
        raise RuntimeError("产品清单未绑定 Paddle / MinerU 推理依赖。")
    profile = _product_profile_status(
        manifest, accelerator, expected_root, product_scope
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
