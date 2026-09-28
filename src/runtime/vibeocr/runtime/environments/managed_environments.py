"""Named, isolated Python environments owned by the frozen Runtime manager."""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
from contextlib import contextmanager
from pathlib import Path
from typing import Callable, Iterator
from urllib.error import URLError
from urllib.request import ProxyHandler, build_opener
from uuid import uuid4

from vibeocr.runtime.environments.managed_references import environment_has_references
from vibeocr.runtime.environments.runtime_install_plan import (
    RuntimeInstallPlanStale,
)
from vibeocr.runtime.environments.runtime_installer import (
    RuntimeInstallError,
    _extract_python_archive,
    _extract_runtime_pack,
    _load_component_lock,
    _prepare_online_artifacts,
    _python_in,
    _run_install_command,
)
from vibeocr.runtime.environments.runtime_layout import resolve_runtime_store
from vibeocr.runtime.environments.runtime_lock import (
    RuntimeLockTimeout,
    RuntimeStoreLock,
)
from vibeocr.runtime.environments.runtime_maintenance import _atomic_json
from vibeocr.runtime.environments.runtime_manifest import (
    ACCELERATOR_TO_PLAN,
    RuntimeInstallScope,
    load_runtime_manifest,
    sha256_file,
)
from vibeocr.runtime.environments.runtime_selection import (
    RuntimeSelectionPolicy,
    download_source_catalog_payload,
)

_NAME = re.compile(r"^[^\\/\x00-\x1f]{1,80}$")
_FORBIDDEN_EMPTY = (
    "fastapi",
    "rapidocr",
    "paddleocr",
    "mineru",
    "torch",
    "onnxruntime",
)


class ManagedEnvironmentError(RuntimeInstallError):
    """A named-environment operation cannot safely complete."""


class ManagedEnvironmentStore:
    def __init__(
        self,
        *,
        product_root: str | Path,
        component_lock: str | Path,
        runtime_manifest: str | Path,
        layout_manifest: str | Path | None = None,
        product_id: str | None = None,
        base_python: str | Path | None = None,
        install_runner: Callable[[Path, RuntimeInstallScope, str], None] | None = None,
    ) -> None:
        self.product_root = Path(product_root).resolve()
        self.component_lock_path = Path(component_lock).resolve()
        self.layout_manifest = (
            str(Path(layout_manifest).resolve()) if layout_manifest else ""
        )
        self.product_id = product_id or ""
        self.manifest = load_runtime_manifest(runtime_manifest, verify_artifacts=False)
        binding = _load_component_lock(self.component_lock_path)
        product = binding["product"]
        if (
            product["version"] != self.manifest.backend_version
            or product["source_sha"] != self.manifest.source_commit
            or product["runtime_manifest_sha256"] != self.manifest.sha256
            or not set(binding["required_capabilities"]).issubset(
                self.manifest.capabilities
            )
        ):
            raise ManagedEnvironmentError("component lock and Runtime manifest differ")
        self.paths = resolve_runtime_store(
            product_root,
            manifest_sha256=self.manifest.sha256,
            layout_manifest=layout_manifest,
            product_id=product_id,
        )
        self.root = self.paths.store_root / "environments"
        self._registry = self.paths.state_root / "environments.json"
        self._lock = self.paths.locks_root / "runtime-store.lock"
        self._references = self.paths.locks_root / "environment-references"
        # Tests may supply a synthetic trusted interpreter. Production extracts
        # the manifest-bound archive into a reusable, read-only base location.
        self._base_override = Path(base_python).resolve() if base_python else None
        self._install_runner = install_runner or self._install_scope

    def _launch(self, record: dict, python: str) -> dict | None:
        if record["status"] == "empty":
            return None
        root = self._safe_path(record)
        state = (
            self.paths.state_root
            if record["kind"] == "legacy"
            else self.paths.state_root / "environments" / record["id"]
        )
        settings = (
            self.product_root / "state" / "supervisor-settings.json"
            if record["kind"] == "legacy"
            else state / "supervisor-settings.json"
        )
        if record["kind"] == "legacy":
            marker = json.loads((root / ".installed.json").read_text(encoding="utf-8"))
            accelerator = marker["accelerator"]
        else:
            accelerator = (
                "nvidia_cuda" if record.get("recipe", "").endswith("cuda") else "cpu"
            )
        environment = {
            "VIBEOCR_PRODUCT_ROOT": str(self.product_root),
            "VIBEOCR_LAYOUT_MANIFEST": self.layout_manifest,
            "VIBEOCR_PRODUCT_ID": self.product_id,
            "VIBEOCR_RUNTIME_ROOT": str(root),
            "VIBEOCR_RUNTIME_MANIFEST": str(self.manifest.path),
            "VIBEOCR_COMPONENT_LOCK": str(self.component_lock_path),
            "VIBEOCR_RUNTIME_ACCELERATOR": accelerator,
            "VIBEOCR_USE_GPU": "true" if accelerator == "nvidia_cuda" else "false",
            "VIBEOCR_RUNTIME_STATE_ROOT": str(state),
            "VIBEOCR_SUPERVISOR_SETTINGS": str(settings),
            "VIBEOCR_MANAGED_ENVIRONMENT_ID": record["id"],
            "VIBEOCR_MANAGED_ENVIRONMENT_REVISION": str(record["revision"]),
            "VIBEOCR_MANAGED_REGISTRY_PATH": str(self._registry),
            "VIBEOCR_MANAGED_STORE_LOCK": str(self._lock),
            "VIBEOCR_MANAGED_REFERENCES_ROOT": str(self._references),
            "PIP_CACHE_DIR": str(state / "cache" / "pip"),
            "UV_CACHE_DIR": str(state / "cache" / "uv"),
            "HF_HOME": str(state / "cache" / "huggingface"),
            "MODELSCOPE_CACHE": str(state / "cache" / "modelscope"),
            "PADDLE_PDX_CACHE_HOME": str(state / "cache" / "paddlex"),
            "MINERU_HOME": str(state / "mineru4"),
            "TEMP": str(state / "temp"),
            "TMP": str(state / "temp"),
            "PIP_CONFIG_FILE": os.devnull,
            "PIP_DISABLE_PIP_VERSION_CHECK": "1",
            "PIP_NO_INPUT": "1",
            "PYTHONNOUSERSITE": "1",
            "PYTHONUTF8": "1",
        }
        code_root = self.product_root / "runtime" / "backend" / "runtime-code"
        if self.layout_manifest or code_root.is_dir():
            for relative in (
                "vibeocr/runtime/__init__.py",
                "vibeocr/runtime/host/main.py",
                "vibeocr/runtime/recognition/paddle_worker.py",
                "vibeocr/runtime/documents/pdf_backend_process.py",
                "vibeocr/runtime/environments/dependency_profiles.json",
                "vibeocr/runtime_contracts/__init__.py",
                f"vibeocr_next_runtime-{self.manifest.backend_version}.dist-info/METADATA",
            ):
                if not (code_root / relative).is_file():
                    raise ManagedEnvironmentError("product Runtime code is unavailable")
            environment["VIBEOCR_PRODUCT_CODE_ROOT"] = str(code_root)
        if record["kind"] == "venv":
            environment["VIBEOCR_MANAGED_ENVIRONMENT_RECIPE"] = record["recipe"]
        return {
            "python_executable": python,
            "supervisor_module": "vibeocr.runtime.host.main",
            "working_directory": str(self.product_root),
            "model_root": str(state / "models"),
            "environment": environment,
        }

    def _reject_referenced(self, data: dict) -> None:
        if any(
            environment_has_references(self._references, env_id)
            for env_id in data["environments"]
        ):
            raise ManagedEnvironmentError("environment has active jobs")

    @contextmanager
    def _target_operation(self, env_id: str) -> Iterator[None]:
        if env_id != "legacy" and not re.fullmatch(r"[0-9a-f]{32}", env_id):
            raise ManagedEnvironmentError("unknown environment")
        lock = RuntimeStoreLock(
            self.paths.locks_root / "environment-operations" / f"{env_id}.lock",
            timeout=0,
        )
        try:
            lock.acquire()
        except RuntimeLockTimeout as exc:
            raise ManagedEnvironmentError("environment operation in progress") from exc
        try:
            yield
        finally:
            lock.release()

    def _read(self) -> dict:
        if self._registry.is_file():
            data = json.loads(self._registry.read_text(encoding="utf-8"))
            self._validate_registry(data)
            return data
        data = {
            "schema_version": 1,
            "active_id": None,
            "active_revision": 0,
            "environments": {},
        }
        legacy = self.paths.runtime_root
        if (legacy / ".installed.json").is_file() and _python_in(legacy).is_file():
            data["environments"]["legacy"] = {
                "id": "legacy",
                "name": "原有环境",
                "revision": 1,
                "kind": "legacy",
                "path": "runtime",
                "status": "installed",
                "python_version": self.manifest.python.version,
                "abi": self.manifest.python.abi,
            }
            data["active_id"] = "legacy"
            data["active_revision"] = 1
        return data

    @staticmethod
    def _is_reparse(path: Path) -> bool:
        try:
            attributes = path.lstat().st_file_attributes
        except (AttributeError, OSError):
            return path.is_symlink()
        return bool(attributes & 0x400)

    def _safe_path(self, record: dict) -> Path:
        env_id = record["id"]
        if record["kind"] == "legacy":
            expected = self.paths.runtime_root
            relative = Path("runtime")
        else:
            relative = Path(record["path"])
            if (
                len(relative.parts) != 3
                or relative.parts[:2] != (env_id, "revisions")
                or not re.fullmatch(
                    rf"{record['revision']}(?:-[0-9a-f]{{16}}|-[0-9a-f]{{32}})?",
                    relative.parts[2],
                )
            ):
                raise ManagedEnvironmentError(
                    "environment path is outside managed store"
                )
            expected = self.root / relative
        declared = Path(record["path"])
        if declared.is_absolute() or declared != relative:
            raise ManagedEnvironmentError("environment path is outside managed store")
        current = expected
        boundary = self.paths.store_root.resolve()
        while current != boundary:
            if (current.exists() or current.is_symlink()) and self._is_reparse(current):
                raise ManagedEnvironmentError("environment path contains reparse point")
            parent = current.parent
            if parent == current or not parent.resolve().is_relative_to(boundary):
                raise ManagedEnvironmentError("environment path escapes store")
            current = parent
        return expected

    def _validate_registry(self, data: object) -> None:
        if (
            not isinstance(data, dict)
            or set(data)
            != {"schema_version", "active_id", "active_revision", "environments"}
            or data["schema_version"] != 1
            or type(data["active_revision"]) is not int
            or data["active_revision"] < 0
            or not isinstance(data["environments"], dict)
        ):
            raise ManagedEnvironmentError("environment registry is invalid")
        environments = data["environments"]
        active = data["active_id"]
        if active is not None and (
            not isinstance(active, str) or active not in environments
        ):
            raise ManagedEnvironmentError("active environment is not registered")
        for env_id, record in environments.items():
            if (
                not isinstance(env_id, str)
                or (env_id != "legacy" and not re.fullmatch(r"[0-9a-f]{32}", env_id))
                or not isinstance(record, dict)
                or set(record)
                - {
                    "id",
                    "name",
                    "revision",
                    "kind",
                    "path",
                    "status",
                    "python_version",
                    "abi",
                    "recipe",
                    "source_ids",
                }
                or not {
                    "id",
                    "name",
                    "revision",
                    "kind",
                    "path",
                    "status",
                    "python_version",
                    "abi",
                }.issubset(record)
                or record["id"] != env_id
                or not isinstance(record["name"], str)
                or not _NAME.fullmatch(record["name"])
                or type(record["revision"]) is not int
                or record["revision"] < 1
                or not isinstance(record["kind"], str)
                or record["kind"] not in {"legacy", "venv"}
                or (env_id == "legacy") != (record["kind"] == "legacy")
                or not isinstance(record["status"], str)
                or record["status"] not in {"empty", "installed"}
                or not isinstance(record["path"], str)
                or not isinstance(record["python_version"], str)
                or not re.fullmatch(r"\d+\.\d+\.\d+", record["python_version"])
                or not isinstance(record["abi"], str)
                or not re.fullmatch(r"cp\d+", record["abi"])
            ):
                raise ManagedEnvironmentError("environment record is invalid")
            if "recipe" in record and (
                not isinstance(record["recipe"], str)
                or record["recipe"]
                not in {
                    "rapidocr-cpu",
                    "paddleocr-cpu",
                    "paddleocr-cuda",
                    "mineru-cpu",
                    "rapidocr+mineru-cpu",
                    "rapidocr+mineru-cuda",
                }
            ):
                raise ManagedEnvironmentError("environment recipe is invalid")
            if "source_ids" in record and (
                not isinstance(record["source_ids"], list)
                or any(not isinstance(source, str) for source in record["source_ids"])
            ):
                raise ManagedEnvironmentError("environment source selection is invalid")
            self._safe_path(record)

    def _expected_base_python(self) -> Path:
        if self._base_override is not None:
            return self._base_override
        base = self.root / "python-base" / self.manifest.python.version
        return base / "python.exe"

    def _base_python(self) -> Path:
        python = self._expected_base_python()
        if python.is_file():
            return python
        with RuntimeStoreLock(self.paths.locks_root / "python-base.lock"):
            if python.is_file():
                return python
            base = python.parent
            archive = self.manifest.python.archive_path
            if (
                not archive.is_file()
                or sha256_file(archive) != self.manifest.python.sha256
            ):
                raise ManagedEnvironmentError(
                    "bound Python archive is missing or invalid"
                )
            partial = base.with_name(f".{base.name}.{uuid4().hex}.installing")
            _extract_python_archive(archive, partial)
            if not _python_in(partial).is_file():
                raise ManagedEnvironmentError("bound Python archive has no executable")
            base.parent.mkdir(parents=True, exist_ok=True)
            partial.rename(base)
        return python

    @staticmethod
    def _venv_python(root: Path) -> Path:
        return root / (
            "Scripts/python.exe" if sys.platform == "win32" else "bin/python"
        )

    def _probe(self, record: dict) -> dict:
        root = self._safe_path(record)
        python = (
            _python_in(root) if record["kind"] == "legacy" else self._venv_python(root)
        )
        if (
            record["python_version"] != self.manifest.python.version
            or record["abi"] != self.manifest.python.abi
        ):
            return {
                "healthy": False,
                "reason": "base_or_abi_changed",
                "python": str(python),
            }
        if not python.is_file():
            return {"healthy": False, "reason": "python_missing", "python": str(python)}
        code = (
            "import json,sys; from importlib.metadata import distributions; "
            "installed=list(distributions()); "
            "print(json.dumps({'prefix':sys.prefix,'base_prefix':sys.base_prefix,"
            "'version':sys.version_info[:3],'packages':sorted(d.metadata['Name'].lower() "
            "for d in installed),'runtime_version':next((d.version for d in installed "
            "if d.metadata['Name'].lower()=='vibeocr-next-runtime'),None)}))"
        )
        try:
            result = subprocess.run(
                [str(python), "-I", "-c", code],
                capture_output=True,
                text=True,
                timeout=20,
                check=True,
            )
            details = json.loads(result.stdout)
        except (OSError, subprocess.SubprocessError, ValueError, KeyError):
            return {
                "healthy": False,
                "reason": "python_probe_failed",
                "python": str(python),
            }
        if Path(details["prefix"]).resolve() != root.resolve():
            return {
                "healthy": False,
                "reason": "prefix_mismatch",
                "python": str(python),
            }
        expected_version = [
            int(part) for part in self.manifest.python.version.split(".")
        ]
        actual_version = ".".join(str(part) for part in details["version"])
        actual_abi = f"cp{details['version'][0]}{details['version'][1]}"
        if record["kind"] == "legacy":
            try:
                marker = json.loads(
                    (root / ".installed.json").read_text(encoding="utf-8")
                )
            except (OSError, ValueError):
                marker = None
            if (
                not isinstance(marker, dict)
                or marker.get("schema_version") != 1
                or not isinstance(marker.get("backend_version"), str)
                or marker["backend_version"] != details.get("runtime_version")
                or not isinstance(marker.get("manifest_sha256"), str)
                or not re.fullmatch(r"[0-9a-f]{64}", marker["manifest_sha256"])
                or marker.get("accelerator") not in ACCELERATOR_TO_PLAN
                or not isinstance(marker.get("component_ids"), list)
                or not marker["component_ids"]
                or any(
                    not isinstance(component, str) or not component
                    for component in marker["component_ids"]
                )
            ):
                return {
                    "healthy": False,
                    "reason": "legacy_runtime_marker_invalid",
                    "python": str(python),
                    "python_version": actual_version,
                    "abi": actual_abi,
                }
        if details["version"] != expected_version or (
            record["kind"] == "venv"
            and Path(details["base_prefix"]).resolve()
            != self._expected_base_python().parent.resolve()
        ):
            return {
                "healthy": False,
                "reason": "base_or_abi_changed",
                "python": str(python),
                "python_version": actual_version,
                "abi": actual_abi,
            }
        packages = set(details["packages"])
        if record["status"] == "empty" and packages.intersection(_FORBIDDEN_EMPTY):
            return {
                "healthy": False,
                "reason": "empty_environment_contaminated",
                "python": str(python),
            }
        if record["status"] == "installed":
            expected = {"fastapi"}
            recipe = record.get("recipe")
            if recipe == "rapidocr-cpu":
                expected.update({"rapidocr", "onnxruntime"})
            elif recipe == "paddleocr-cpu":
                expected.update({"paddleocr", "paddlepaddle"})
            elif recipe == "paddleocr-cuda":
                expected.update({"paddleocr", "paddlepaddle-gpu"})
            elif recipe in {"rapidocr+mineru-cpu", "rapidocr+mineru-cuda"}:
                expected.update({"rapidocr", "onnxruntime", "mineru"})
                if recipe.endswith("cuda"):
                    expected.add("torch")
            elif recipe == "mineru-cpu":
                expected.add("mineru")
            if not expected.issubset(packages):
                return {
                    "healthy": False,
                    "reason": "engine_packages_missing",
                    "python": str(python),
                }
            if recipe in {
                "rapidocr-cpu",
                "rapidocr+mineru-cpu",
                "rapidocr+mineru-cuda",
            }:
                try:
                    subprocess.run(
                        [
                            str(python),
                            "-I",
                            "-B",
                            "-c",
                            "import pyclipper, onnxruntime",
                        ],
                        capture_output=True,
                        text=True,
                        timeout=20,
                        check=True,
                    )
                except (OSError, subprocess.SubprocessError):
                    return {
                        "healthy": False,
                        "reason": "engine_import_failed",
                        "python": str(python),
                        "python_version": actual_version,
                        "abi": actual_abi,
                        "packages": sorted(packages),
                    }
        return {
            "healthy": True,
            "reason": None,
            "python": str(python),
            "python_version": actual_version,
            "abi": actual_abi,
            "packages": sorted(packages),
        }

    def _public_record(self, record: dict, probe: dict) -> dict:
        installed = record["status"] == "installed"
        python_ok = probe["reason"] not in {
            "python_missing",
            "python_probe_failed",
            "prefix_mismatch",
            "base_or_abi_changed",
            "legacy_runtime_marker_invalid",
        }
        dependency_state = (
            "missing"
            if probe["reason"]
            in {"engine_packages_missing", "empty_environment_contaminated"}
            else "unavailable"
            if probe["reason"] == "engine_import_failed"
            else record["status"]
            if python_ok
            else "unknown"
        )
        recipe = record.get("recipe", "")
        if recipe.startswith("paddleocr"):
            modes = ["text", "table", "formula", "structure", "document_vl"]
        elif recipe.startswith("rapidocr+mineru"):
            modes = ["text", "document"]
        elif recipe == "rapidocr-cpu":
            modes = ["text"]
        elif recipe == "mineru-cpu":
            modes = ["document"]
        else:
            modes = []
        return {
            **record,
            "path": str(self._safe_path(record)),
            "python": probe["python"],
            "python_version": probe.get("python_version"),
            "abi": probe.get("abi"),
            "disk_bytes": self._disk_usage(self._safe_path(record)),
            "python_state": "ready" if python_ok else "unavailable",
            "dependency_state": dependency_state,
            "engine_state": "unverified"
            if installed and dependency_state == "installed"
            else "unavailable",
            "model_state": "not_checked" if installed else "not_applicable",
            "service_state": "not_started",
            "configured_recognition_types": modes,
            "target_device": "cuda"
            if recipe.endswith("cuda")
            else "cpu"
            if recipe
            else None,
            "actual_device": None,
            "reason": (
                "engine_import_failed: Native OCR modules could not load; "
                "use a shorter Portable location or reinstall this environment"
                if probe["reason"] == "engine_import_failed"
                else probe["reason"]
            ),
            "packages": probe.get("packages", []),
        }

    def _disk_usage(self, root: Path) -> int:
        total = 0
        for directory, children, files in os.walk(root, followlinks=False):
            parent = Path(directory)
            children[:] = [
                child for child in children if not self._is_reparse(parent / child)
            ]
            for name in files:
                path = parent / name
                if self._is_reparse(path):
                    continue
                try:
                    total += path.stat().st_size
                except OSError:
                    continue
        return total

    def list(self) -> dict:
        with RuntimeStoreLock(self._lock):
            data = self._read()
            return {
                "active_id": data["active_id"],
                "active_revision": data["active_revision"],
                "package_source_ids": [
                    source["id"]
                    for source in download_source_catalog_payload()["sources"]
                    if source["kind"] == "package_index"
                ],
                "environments": [
                    self._public_record(record, self._probe(record))
                    for record in data["environments"].values()
                ],
            }

    def create(self, name: str) -> dict:
        name = name.strip()
        if not _NAME.fullmatch(name) or name in {".", ".."}:
            raise ManagedEnvironmentError("environment name is invalid")
        with RuntimeStoreLock(self._lock):
            data = self._read()
            if any(
                item["name"].casefold() == name.casefold()
                for item in data["environments"].values()
            ):
                raise ManagedEnvironmentError("environment name already exists")
            env_id = uuid4().hex
            root = self.root / env_id / "revisions" / "1"
            base = self._base_python()
            result = subprocess.run(
                [str(base), "-I", "-m", "venv", "--copies", "--without-pip", str(root)],
                capture_output=True,
                text=True,
                timeout=120,
            )
            if result.returncode:
                raise ManagedEnvironmentError(
                    "could not create isolated Python environment"
                )
            record = {
                "id": env_id,
                "name": name,
                "revision": 1,
                "kind": "venv",
                "path": str(Path(env_id) / "revisions" / "1"),
                "status": "empty",
                "python_version": self.manifest.python.version,
                "abi": self.manifest.python.abi,
            }
            probe = self._probe(record)
            if not probe["healthy"]:
                raise ManagedEnvironmentError(
                    f"new environment failed probe: {probe['reason']}"
                )
            data["environments"][env_id] = record
            _atomic_json(self._registry, data)
            return self._public_record(record, probe)

    def repair_empty(self, env_id: str) -> dict:
        """Recreate only an empty venv after a Portable path or Python ABI change."""
        with self._target_operation(env_id), RuntimeStoreLock(self._lock):
            data = self._read()
            if environment_has_references(self._references, env_id):
                raise ManagedEnvironmentError("environment has active jobs")
            record = data["environments"].get(env_id)
            if (
                record is None
                or record["kind"] != "venv"
                or record["status"] != "empty"
            ):
                raise ManagedEnvironmentError(
                    "only a managed empty environment can be repaired"
                )
            if self._probe(record)["healthy"]:
                return self._public_record(record, self._probe(record))
            revision = record["revision"] + 1
            directory = f"{revision}-{uuid4().hex[:16]}"
            root = self.root / env_id / "revisions" / directory
            result = subprocess.run(
                [
                    str(self._base_python()),
                    "-I",
                    "-m",
                    "venv",
                    "--copies",
                    "--without-pip",
                    str(root),
                ],
                capture_output=True,
                text=True,
                timeout=120,
            )
            if result.returncode:
                raise ManagedEnvironmentError("could not repair empty environment")
            replacement = {
                **record,
                "revision": revision,
                "path": str(Path(env_id) / "revisions" / directory),
                "python_version": self.manifest.python.version,
                "abi": self.manifest.python.abi,
            }
            probe = self._probe(replacement)
            if not probe["healthy"]:
                raise ManagedEnvironmentError(
                    f"repaired environment failed probe: {probe['reason']}"
                )
            data["environments"][env_id] = replacement
            if data["active_id"] == env_id:
                data["active_revision"] += 1
            _atomic_json(self._registry, data)
            return self._public_record(replacement, probe)

    def _recipe(self, recipe: str) -> tuple[RuntimeInstallScope, str]:
        if recipe == "rapidocr-cpu":
            return self.manifest.profiles["win-x64-base"].scopes[0], "cpu"
        if recipe == "mineru-cpu":
            scope = next(
                (
                    item
                    for item in self.manifest.profiles["win-x64-cpu"].scopes
                    if item.scope_id == "mineru-standalone"
                ),
                None,
            )
            if scope is None:
                raise ManagedEnvironmentError(
                    "standalone MinerU recipe is absent from manifest"
                )
            return scope, "cpu"
        if recipe in {"rapidocr+mineru-cpu", "rapidocr+mineru-cuda"}:
            accelerator = "cpu" if recipe.endswith("cpu") else "nvidia_cuda"
            profile_id = "win-x64-cpu" if accelerator == "cpu" else "win-x64-cu126"
            scope = next(
                (
                    item
                    for item in self.manifest.profiles[profile_id].scopes
                    if item.scope_id == "mineru"
                ),
                None,
            )
            if scope is None:
                raise ManagedEnvironmentError("MinerU recipe is absent from manifest")
            return scope, accelerator
        if recipe in {"paddleocr-cpu", "paddleocr-cuda"}:
            profile_id = "win-x64-cpu" if recipe.endswith("cpu") else "win-x64-cu126"
            bound = self.manifest.profiles[profile_id].scopes[0].paddle_environment
            if bound is None:
                raise ManagedEnvironmentError("Paddle recipe is absent from manifest")
            return RuntimeInstallScope(
                scope_id=recipe,
                component_ids=(recipe,),
                lock_path=bound.lock_path,
                sha256=bound.sha256,
                runtime_pack=(),
                runtime_pack_sha256=(),
                paddle_environment=None,
            ), "cpu" if recipe.endswith("cpu") else "nvidia_cuda"
        raise ManagedEnvironmentError("unknown engine recipe")

    def status_scope(
        self, env_id: str, revision: int, runtime_root: Path, recipe: str
    ) -> tuple[RuntimeInstallScope, str]:
        """Resolve the running revision's installed scope from the registry."""
        data = self._read()
        record = data["environments"].get(env_id)
        if (
            record is None
            or record["kind"] != "venv"
            or record["status"] != "installed"
            or record["revision"] != revision
            or record.get("recipe") != recipe
            or self._safe_path(record).resolve() != runtime_root.resolve()
        ):
            raise ManagedEnvironmentError("running environment revision is stale")
        return self._recipe(recipe)

    def preview_install(
        self, env_id: str, recipe: str, source_ids: tuple[str, ...] = ("tuna-pypi",)
    ) -> dict:
        with self._target_operation(env_id), RuntimeStoreLock(self._lock):
            data = self._read()
            record = data["environments"].get(env_id)
            if record is None or record["kind"] != "venv":
                raise ManagedEnvironmentError(
                    "target must be a managed virtual environment"
                )
            if env_id == data["active_id"] and record["status"] != "empty":
                raise ManagedEnvironmentError("active environment cannot be modified")
            requested_recipe = recipe
            current = record.get("recipe")
            if (current == "rapidocr-cpu" and recipe == "mineru-cpu") or (
                current == "mineru-cpu" and recipe == "rapidocr-cpu"
            ):
                recipe = "rapidocr+mineru-cpu"
            elif (
                current is not None
                and current != recipe
                and not (
                    current in {"rapidocr-cpu", "mineru-cpu"}
                    and recipe == "rapidocr+mineru-cpu"
                )
            ):
                raise ManagedEnvironmentError(
                    "engine combination has no compatible locked recipe; "
                    "create another environment"
                )
            elif current is None and recipe == "mineru-cuda":
                raise ManagedEnvironmentError(
                    "standalone CUDA MinerU recipe is not bound by the current release"
                )
            scope, accelerator = self._recipe(recipe)
            selection = RuntimeSelectionPolicy.from_manifest(self.manifest).plan_start(
                accelerator=accelerator,
                install_component_ids=(),
                download_source_ids=source_ids,
            )
            if (
                len(
                    [
                        source
                        for source in selection.effective_download_sources
                        if source.kind == "package_index"
                    ]
                )
                != 1
            ):
                raise ManagedEnvironmentError("one package index source is required")
            plan = {
                "plan_id": uuid4().hex,
                "environment_id": env_id,
                "environment_revision": record["revision"],
                "active_revision": data["active_revision"],
                "requested_recipe": requested_recipe,
                "recipe": recipe,
                "recipe_lock": scope.sha256,
                "source_ids": list(source_ids),
                "runtime_manifest": self.manifest.sha256,
            }
            dependencies = []
            for line in scope.lock_path.read_text(encoding="utf-8").splitlines():
                if match := re.match(r"^([A-Za-z0-9_.-]+)==([^\s\\]+)", line):
                    dependencies.append(f"{match.group(1)}=={match.group(2)}")
                elif match := re.match(r"^([A-Za-z0-9_.-]+)\s+@\s+([^\s\\]+)", line):
                    dependencies.append(f"{match.group(1)} @ {match.group(2)}")
            _atomic_json(
                self.paths.state_root / "environment-plans" / f"{env_id}.json",
                plan,
            )
            return {**plan, "dependencies": dependencies}

    def install(
        self, plan_id: str, env_id: str, recipe: str, source_ids: tuple[str, ...]
    ) -> dict:
        if not re.fullmatch(r"[0-9a-f]{32}", plan_id) or not re.fullmatch(
            r"[0-9a-f]{32}", env_id
        ):
            raise RuntimeInstallPlanStale("invalid environment plan")
        with self._target_operation(env_id):
            plan_path = self.paths.state_root / "environment-plans" / f"{env_id}.json"
            with RuntimeStoreLock(self._lock):
                try:
                    plan = json.loads(plan_path.read_text(encoding="utf-8"))
                except (OSError, ValueError) as exc:
                    raise RuntimeInstallPlanStale(
                        "environment plan is unavailable"
                    ) from exc
                if (
                    not isinstance(plan, dict)
                    or not isinstance(plan.get("recipe"), str)
                    or not isinstance(plan.get("environment_id"), str)
                    or not isinstance(plan.get("source_ids"), list)
                    or any(not isinstance(item, str) for item in plan["source_ids"])
                ):
                    raise RuntimeInstallPlanStale("environment plan is invalid")
                data = self._read()
                record = data["environments"].get(plan.get("environment_id"))
                scope, accelerator = self._recipe(plan["recipe"])
                if (
                    plan.get("plan_id") != plan_id
                    or plan.get("environment_id") != env_id
                    or plan.get("recipe") != recipe
                    or plan.get("source_ids") != list(source_ids)
                    or record is None
                    or record["kind"] != "venv"
                    or record["revision"] != plan.get("environment_revision")
                    or data["active_revision"] != plan.get("active_revision")
                    or (data["active_id"] == env_id and record["status"] != "empty")
                    or plan.get("recipe_lock") != scope.sha256
                    or plan.get("runtime_manifest") != self.manifest.sha256
                ):
                    raise RuntimeInstallPlanStale(
                        "environment or recipe changed; preview again"
                    )
                if environment_has_references(self._references, env_id):
                    raise ManagedEnvironmentError("environment has active jobs")
                selection = RuntimeSelectionPolicy.from_manifest(
                    self.manifest
                ).plan_start(
                    accelerator=accelerator,
                    install_component_ids=(),
                    download_source_ids=tuple(plan["source_ids"]),
                )
                source = next(
                    source
                    for source in selection.effective_download_sources
                    if source.kind == "package_index"
                )
                revision = record["revision"] + 1
                directory = f"{revision}-{uuid4().hex[:16]}"
                root = self.root / env_id / "revisions" / directory
                if root.exists():
                    raise ManagedEnvironmentError("candidate revision already exists")
            base = self._base_python()
            result = subprocess.run(
                [str(base), "-I", "-m", "venv", "--copies", str(root)],
                capture_output=True,
                text=True,
                timeout=120,
            )
            if result.returncode:
                raise ManagedEnvironmentError("could not prepare candidate environment")
            self._install_runner(self._venv_python(root), scope, source.endpoint)
            candidate = {
                **record,
                "revision": revision,
                "path": str(Path(env_id) / "revisions" / directory),
                "status": "installed",
                "recipe": plan["recipe"],
                "source_ids": plan["source_ids"],
                "python_version": self.manifest.python.version,
                "abi": self.manifest.python.abi,
            }
            probe = self._probe(candidate)
            if not probe["healthy"]:
                raise ManagedEnvironmentError(
                    f"installed environment failed probe: {probe['reason']}; "
                    "check native dependencies and Portable path length"
                )
            with RuntimeStoreLock(self._lock):
                latest = self._read()
                try:
                    current_plan = json.loads(plan_path.read_text(encoding="utf-8"))
                except (OSError, ValueError) as exc:
                    raise RuntimeInstallPlanStale(
                        "environment plan is unavailable"
                    ) from exc
                if (
                    current_plan != plan
                    or latest["environments"].get(env_id) != record
                    or latest["active_revision"] != plan["active_revision"]
                ):
                    raise RuntimeInstallPlanStale(
                        "environment or recipe changed; preview again"
                    )
                if environment_has_references(self._references, env_id):
                    raise ManagedEnvironmentError("environment has active jobs")
                latest["environments"][env_id] = candidate
                if latest["active_id"] == env_id:
                    latest["active_revision"] += 1
                _atomic_json(self._registry, latest)
            return self._public_record(candidate, probe)

    def _install_scope(
        self, python: Path, scope: RuntimeInstallScope, endpoint: str
    ) -> None:
        if sha256_file(scope.lock_path) != scope.sha256:
            raise ManagedEnvironmentError("engine recipe lock does not match manifest")
        wheel = self.manifest.path.parent / self.manifest.runtime_wheel
        if sha256_file(wheel) != self.manifest.runtime_sha256:
            raise ManagedEnvironmentError("Runtime wheel does not match manifest")
        cache = self.paths.state_root / "installer-cache"
        env = {
            key: value
            for key, value in os.environ.items()
            if not key.upper().startswith(("PIP_", "UV_"))
        }
        env.update(
            {
                "PIP_CACHE_DIR": str(cache / "pip"),
                "UV_CACHE_DIR": str(cache / "uv"),
                "HF_HOME": str(cache / "huggingface"),
                "TEMP": str(cache / "temp"),
                "TMP": str(cache / "temp"),
                "PIP_CONFIG_FILE": os.devnull,
                "PIP_NO_INPUT": "1",
                "PYTHONNOUSERSITE": "1",
                "PYTHONUTF8": "1",
            }
        )
        (cache / "pip").mkdir(parents=True, exist_ok=True)
        (cache / "temp").mkdir(parents=True, exist_ok=True)
        pack_files = [self.manifest.path.parent / name for name in scope.runtime_pack]
        if pack_files and not all(path.is_file() for path in pack_files):
            raise ManagedEnvironmentError("bound offline engine pack is missing")
        command = [str(python), "-m", "pip", "install"]
        if pack_files and all(path.is_file() for path in pack_files):
            pack_dir = _extract_runtime_pack(
                pack_files,
                cache / "runtime-packs",
                expected_sha256=scope.runtime_pack_sha256,
            )
            command += [
                "--no-index",
                "--find-links",
                str(pack_dir),
                "--only-binary=:all:",
                "-r",
                str(pack_dir / "pack-requirements.txt"),
            ]
        else:
            downloaded = _prepare_online_artifacts(
                python, scope.lock_path, endpoint, cache, None, env
            )
            command += [
                "--index-url",
                endpoint,
                "--find-links",
                str(downloaded),
                "--require-hashes",
                "-r",
                str(scope.lock_path),
            ]
        _run_install_command(
            command,
            timeout=3600,
            env=env,
            reporter=None,
            heartbeat_code="runtime.install_profile",
        )
        _run_install_command(
            [
                str(python),
                "-m",
                "pip",
                "install",
                "--no-deps",
                "--force-reinstall",
                str(wheel),
            ],
            timeout=600,
            env=env,
            reporter=None,
            heartbeat_code="runtime.install_backend",
        )
        _run_install_command(
            [str(python), "-c", "import vibeocr.runtime.host.main"],
            timeout=60,
            env=env,
            reporter=None,
            heartbeat_code="runtime.verify_runtime",
        )
        _run_install_command(
            [str(python), "-m", "pip", "check"],
            timeout=60,
            env=env,
            reporter=None,
            heartbeat_code="runtime.verify_runtime",
        )

    def prepare_switch(self, env_id: str) -> dict:
        with self._target_operation(env_id), RuntimeStoreLock(self._lock):
            data = self._read()
            self._reject_referenced(data)
            try:
                record = data["environments"][env_id]
            except KeyError as exc:
                raise ManagedEnvironmentError("unknown environment") from exc
            probe = self._probe(record)
            if not probe["healthy"]:
                raise ManagedEnvironmentError(
                    f"target environment is unhealthy: {probe['reason']}"
                )
            return {
                "environment_id": env_id,
                "environment_revision": record["revision"],
                "active_id": data["active_id"],
                "active_revision": data["active_revision"],
                "python": probe["python"],
                "requires_supervisor": record["status"] == "installed",
                "launch": self._launch(record, probe["python"]),
            }

    def commit_switch(
        self, prepared: dict, *, started_health: dict | None = None
    ) -> dict:
        if (
            not isinstance(prepared, dict)
            or set(prepared)
            != {
                "environment_id",
                "environment_revision",
                "active_id",
                "active_revision",
                "python",
                "requires_supervisor",
                "launch",
            }
            or not isinstance(prepared["environment_id"], str)
            or type(prepared["environment_revision"]) is not int
            or type(prepared["active_revision"]) is not int
            or (
                prepared["active_id"] is not None
                and not isinstance(prepared["active_id"], str)
            )
            or not isinstance(prepared["python"], str)
            or type(prepared["requires_supervisor"]) is not bool
            or (
                prepared["launch"] is not None
                and not isinstance(prepared["launch"], dict)
            )
        ):
            raise ManagedEnvironmentError("prepared environment switch is invalid")
        with (
            self._target_operation(prepared["environment_id"]),
            RuntimeStoreLock(self._lock),
        ):
            data = self._read()
            self._reject_referenced(data)
            env_id = prepared["environment_id"]
            record = data["environments"].get(env_id)
            if (
                record is None
                or record["revision"] != prepared["environment_revision"]
                or data["active_revision"] != prepared["active_revision"]
                or data["active_id"] != prepared["active_id"]
            ):
                raise ManagedEnvironmentError("environment switch is stale")
            probe = self._probe(record)
            if not probe["healthy"] or probe["python"] != prepared["python"]:
                raise ManagedEnvironmentError(
                    "target environment changed before switch"
                )
            if prepared["launch"] != self._launch(record, probe["python"]):
                raise ManagedEnvironmentError("target launch changed before switch")
            if record["status"] == "installed" and not self._verified_health(
                started_health
            ):
                raise ManagedEnvironmentError("target Supervisor is not healthy")
            if record["status"] == "empty" and started_health is not None:
                raise ManagedEnvironmentError(
                    "empty environment must not launch Supervisor"
                )
            if data["active_id"] != env_id:
                data["active_id"] = env_id
                data["active_revision"] += 1
                _atomic_json(self._registry, data)
            elif not self._registry.is_file():
                _atomic_json(self._registry, data)
            return {
                "active_id": data["active_id"],
                "active_revision": data["active_revision"],
            }

    @staticmethod
    def _verified_health(evidence: dict | None) -> bool:
        if (
            not isinstance(evidence, dict)
            or set(evidence) != {"port", "instance_id"}
            or type(evidence["port"]) is not int
            or evidence["port"] < 1
            or evidence["port"] > 65535
            or not isinstance(evidence["instance_id"], str)
            or not evidence["instance_id"]
        ):
            return False
        try:
            opener = build_opener(ProxyHandler({}))
            with opener.open(
                f"http://127.0.0.1:{evidence['port']}/v2/health", timeout=3
            ) as response:
                health = json.load(response)
        except (OSError, URLError, ValueError):
            return False
        return (
            isinstance(health, dict)
            and health.get("ready") is True
            and health.get("draining") is False
            and health.get("instance_id") == evidence["instance_id"]
            and health.get("protocol_version") == 2
            and health.get("schema_version") == 2
        )

    def delete(self, env_id: str, *, referenced: bool = False) -> None:
        with self._target_operation(env_id), RuntimeStoreLock(self._lock):
            data = self._read()
            if environment_has_references(self._references, env_id):
                raise ManagedEnvironmentError("environment has active jobs")
            record = data["environments"].get(env_id)
            if record is None:
                raise ManagedEnvironmentError("unknown environment")
            if env_id == data["active_id"] or referenced or record["kind"] == "legacy":
                raise ManagedEnvironmentError(
                    "active, referenced or legacy environment cannot be deleted"
                )
            # Remove the registry entry first. An interrupted removal may leave
            # an orphaned directory, but cannot resurrect a deleted environment.
            del data["environments"][env_id]
            _atomic_json(self._registry, data)
            import shutil

            root = (self.root / env_id).resolve()
            if root.parent != self.root.resolve():
                raise ManagedEnvironmentError("environment path escapes store")
            shutil.rmtree(root)
