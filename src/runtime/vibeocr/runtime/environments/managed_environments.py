"""Named, isolated Python environments owned by the frozen Runtime manager."""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
from contextlib import contextmanager, nullcontext
from dataclasses import replace
from pathlib import Path
from typing import Callable, Iterator
from urllib.error import URLError
from urllib.parse import unquote, urlsplit
from urllib.request import ProxyHandler, build_opener
from uuid import uuid4

from packaging.utils import InvalidWheelFilename, parse_wheel_filename
from vibeocr.runtime.environments.managed_references import environment_has_references
from vibeocr.runtime.environments.runtime_install_plan import (
    RuntimeInstallPlanStale,
)
from vibeocr.runtime.environments.runtime_installer import (
    RuntimeInstallError,
    _extract_python_archive,
    _extract_runtime_pack,
    _load_component_lock,
    _normalize_dist_name,
    _prepare_online_artifacts,
    _python_in,
    _run_install_command,
    probe_nvidia_driver,
)
from vibeocr.runtime.environments.runtime_layout import resolve_runtime_store
from vibeocr.runtime.environments.runtime_lock import (
    RuntimeLockTimeout,
    RuntimeStoreLock,
)
from vibeocr.runtime.environments.runtime_maintenance import (
    _atomic_json,
    failure_guidance,
    safe_runtime_detail,
)
from vibeocr.runtime.environments.runtime_manifest import (
    ACCELERATOR_TO_PLAN,
    RuntimeInstallScope,
    load_runtime_manifest,
    sha256_file,
)
from vibeocr.runtime.environments.runtime_selection import (
    DOWNLOAD_SOURCE_KIND_MINERU_MODEL_REGISTRY,
    DOWNLOAD_SOURCE_KIND_PACKAGE_INDEX,
    DOWNLOAD_SOURCE_KIND_PADDLEOCR_MODEL_REGISTRY,
    RuntimeSelectionError,
    RuntimeSelectionPolicy,
    default_download_sources,
    download_source_catalog_payload,
    download_source_display_name,
    expand_legacy_model_sources,
    sanitize_download_endpoint,
)

_NAME = re.compile(r"^[^\\/\x00-\x1f]{1,80}$")
_SOURCE_ID = re.compile(r"^[a-z0-9][a-z0-9-]{0,40}$")
_SOURCE_KINDS = (
    DOWNLOAD_SOURCE_KIND_PACKAGE_INDEX,
    DOWNLOAD_SOURCE_KIND_PADDLEOCR_MODEL_REGISTRY,
    DOWNLOAD_SOURCE_KIND_MINERU_MODEL_REGISTRY,
)
_UNSET_SOURCE = object()
# 注册表可选新增键：旧 schema_version=1 记录缺失时按空配置/0 读取。
_REGISTRY_OPTIONAL_KEYS = {
    "default_source_ids",
    "source_config_revision",
}
_OPERATION_OPTIONAL_KEYS = {"requested_source_ids", "effective_source_ids"}
_FORBIDDEN_EMPTY = (
    "fastapi",
    "rapidocr",
    "paddleocr",
    "mineru",
    "torch",
    "onnxruntime",
)
# 六种锁定配方是 Runtime 的唯一权威目录：展示名、真实用途（识别类型）与
# 设备要求沿 manifest scope 锁投影；C#/TS 只消费目录，不得重算依赖图。
_RECIPE_IDS = (
    "rapidocr-cpu",
    "paddleocr-cpu",
    "paddleocr-cuda",
    "mineru-cpu",
    "rapidocr+mineru-cpu",
    "rapidocr+mineru-cuda",
)
_RECIPE_DISPLAY_NAMES = {
    "rapidocr-cpu": "RapidOCR · CPU",
    "paddleocr-cpu": "PaddleOCR · CPU",
    "paddleocr-cuda": "PaddleOCR · NVIDIA CUDA",
    "mineru-cpu": "MinerU · CPU",
    "rapidocr+mineru-cpu": "RapidOCR + MinerU · CPU",
    "rapidocr+mineru-cuda": "RapidOCR + MinerU · NVIDIA CUDA",
}
# 探针运行 Supervisor 实际执行的引擎导入：不实例化引擎、不下载模型。
# rapidocr 必须导入真实符号，包级 __init__ 惰性解析无法证明原生闭包可用。
_RECIPE_IMPORT_PROBES = {
    "rapidocr-cpu": "from rapidocr import RapidOCR",
    "paddleocr-cpu": "from paddleocr import PaddleOCR",
    "paddleocr-cuda": "from paddleocr import PaddleOCR",
    "mineru-cpu": "from mineru.parser.api_server import create_app",
    "rapidocr+mineru-cpu": "from mineru.parser.api_server import create_app; from rapidocr import RapidOCR",
    "rapidocr+mineru-cuda": "from mineru.parser.api_server import create_app; from rapidocr import RapidOCR",
}

# 前后端同源一体发布后环境目录不需要 UUID：新环境目录用名称（用户创建）
# 或用途/设备（默认环境）派生的可读 slug，碰撞追加 -2/-3 后缀；历史
# uuid 记录原样兼容，安装事务、manifest 与回滚语义不变。
_ENVIRONMENT_DIRECTORY_FORBIDDEN = re.compile(r'[\\/:*?"<>|\x00-\x1f]')
_ENVIRONMENT_DIRECTORY_MAX = 48
# Windows 保留设备名 + 管理存储自身的固定目录（python-base 解释器池、
# legacy 运行时）不可被环境目录占用。
_ENVIRONMENT_RESERVED_DIRECTORIES = frozenset(
    {
        "legacy",
        "python-base",
        "con",
        "prn",
        "aux",
        "nul",
        *(f"com{index}" for index in range(1, 10)),
        *(f"lpt{index}" for index in range(1, 10)),
    }
)
# 默认环境的用途/设备即目录名：RapidOCR + CPU。
_DEFAULT_ENVIRONMENT_SLUG = "rapidocr-cpu"


def _is_reserved_directory_name(value: str) -> bool:
    """目录名是否占用管理存储固定目录或 Windows 保留设备名。"""
    stem = value.split(".", 1)[0].strip(" .").lower()
    return not stem or stem in _ENVIRONMENT_RESERVED_DIRECTORIES


def _environment_directory_slug(name: str) -> str | None:
    """从环境名称派生安全可读的存储目录名；无法派生时返回 None。"""
    slug = _ENVIRONMENT_DIRECTORY_FORBIDDEN.sub("-", name.strip())
    slug = re.sub(r"\s+", " ", slug).strip(" .")
    if len(slug) > _ENVIRONMENT_DIRECTORY_MAX:
        slug = slug[:_ENVIRONMENT_DIRECTORY_MAX].strip(" .")
    return slug or None


def _is_valid_environment_id(value: str) -> bool:
    """环境 id 接受历史 uuid 与可读 slug 两种形式（slug 须是安全目录名）。"""
    if re.fullmatch(r"[0-9a-f]{32}", value):
        return True
    if (
        not value.strip()
        or len(value) > _ENVIRONMENT_DIRECTORY_MAX + len("-100")
        or value != value.strip()
        or value != value.strip(" .")
        or _ENVIRONMENT_DIRECTORY_FORBIDDEN.search(value)
        or _is_reserved_directory_name(value)
    ):
        return False
    return True


class ManagedEnvironmentError(RuntimeInstallError):
    """A named-environment operation cannot safely complete."""


class ManagedEnvironmentInstallCancelled(ManagedEnvironmentError):
    """Cancellation was accepted before the installation's terminal commit."""


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
        self._install_cancelled = False
        self._install_terminal = False

    def cancel_install(self, acknowledge: Callable[[bool], None]) -> None:
        """Acknowledge before kill, serialized with both terminal registry writes.

        This manager belongs to one CLI process. The in-memory flag prevents its
        worker from replacing the durable installing journal after cancellation;
        no new registry field is needed by older versions after rollback.
        """
        with RuntimeStoreLock(self._lock):
            accepted = not self._install_terminal
            if accepted:
                self._install_cancelled = True
            acknowledge(accepted)

    def _check_install_cancelled(self) -> None:
        if self._install_cancelled:
            raise ManagedEnvironmentInstallCancelled(
                "environment installation was cancelled",
                reason_code="install_interrupted",
                next_action="preview_again",
            )

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
            "MODELSCOPE_HOME": str(state / "cache" / "modelscope-home"),
            "MODELSCOPE_CREDENTIALS_PATH": str(state / "cache" / "modelscope-home"),
            "VIBEOCR_SHARED_MODEL_CACHE": str(self.paths.state_root / "model-cache"),
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
        # runtime-code 与 runtime-manifest 由同一构建步骤同目录产出；
        # Velopack current/ 布局下 product_root 是 bundle 根（只有
        # current/packages/state），不能从 product_root 硬拼，以已验证的
        # manifest 位置为权威锚点（与 _install_scope 取 wheel 同一模式）。
        code_root = self.manifest.path.parent / "runtime-code"
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
        # 模型来源偏好按既有官方 env 投影给 _launch 消费；解析只读注册表
        # （调用方已持有 store 锁）。原生 downloader 的实际端点未知，不伪造。
        environment.update(
            self._plan_selection(
                self._read(), record, accelerator, None
            ).model_source_environment()
        )
        return {
            "python_executable": python,
            "supervisor_module": "vibeocr.runtime.host.main",
            "working_directory": str(self.product_root),
            "model_root": str(state / "models"),
            "environment": environment,
        }

    def _source_catalog(self) -> list[dict[str, str]]:
        return download_source_catalog_payload()["sources"]

    def _kind_source_map(
        self, source_ids: tuple[str, ...] | list[str]
    ) -> dict[str, str]:
        """已知目录的 id 列表 → kind→id；未知 id 交给上层标注，不在此 fail closed。"""
        catalog = {source["id"]: source for source in self._source_catalog()}
        resolved: dict[str, str] = {}
        for source_id in expand_legacy_model_sources(source_ids):
            source = catalog.get(source_id)
            if source is not None and source["kind"] not in resolved:
                resolved[source["kind"]] = source_id
        return resolved

    def _unknown_source_ids(self, source_ids: tuple[str, ...] | list[str]) -> list[str]:
        catalog = {source["id"] for source in self._source_catalog()}
        return [source_id for source_id in source_ids if source_id not in catalog]

    def _resolved_defaults(
        self, data: dict, record: dict | None = None
    ) -> dict[str, str | None]:
        """全局默认 → 环境override 的每 kind 解析；未覆盖 kind 回退产品默认。

        默认依赖包源是 TUNA，PaddleOCR 与 MinerU 各自默认 ModelScope。
        来源只代表安装依赖与下载模型；旧共享模型偏好在读取时展开。
        """
        merged: dict[str, str] = {}
        merged.update(self._kind_source_map(data.get("default_source_ids") or ()))
        if record is not None:
            merged.update(
                self._kind_source_map(record.get("override_source_ids") or ())
            )
        resolved: dict[str, str | None] = {
            kind: merged.get(kind) for kind in _SOURCE_KINDS
        }
        for source in default_download_sources(include_model_sources=True):
            if resolved.get(source["kind"]) is None:
                resolved[source["kind"]] = source["id"]
        return resolved

    def _policy_for(
        self, data: dict, record: dict | None = None
    ) -> RuntimeSelectionPolicy:
        resolved = self._resolved_defaults(data, record)
        catalog = self._source_catalog()
        defaults = tuple(
            source["id"]
            for source in catalog
            if resolved.get(source["kind"]) == source["id"]
        )
        return (
            RuntimeSelectionPolicy.from_manifest(
                self.manifest, default_download_source_ids=defaults
            )
            if defaults
            else RuntimeSelectionPolicy.from_manifest(self.manifest)
        )

    def _plan_selection(
        self,
        data: dict,
        record: dict,
        accelerator: str,
        requested: tuple[str, ...] | None,
    ):
        try:
            if requested is not None and len(set(requested)) != len(requested):
                raise ManagedEnvironmentError(
                    "download_source_ids must not contain duplicates"
                )
            selection = self._policy_for(data, record).plan_start(
                accelerator=accelerator,
                install_component_ids=(),
                download_source_ids=None
                if requested is None
                else expand_legacy_model_sources(requested),
            )
            return replace(selection, requested_download_source_ids=requested)
        except RuntimeSelectionError as error:
            raise ManagedEnvironmentError(str(error)) from error

    def _source_origin(self, data: dict, record: dict | None, source_id: str) -> str:
        overrides = (
            self._kind_source_map(record.get("override_source_ids") or ())
            if record is not None
            else {}
        )
        defaults = self._kind_source_map(data.get("default_source_ids") or ())
        catalog = {source["id"]: source for source in self._source_catalog()}
        kind = catalog[source_id]["kind"]
        if overrides.get(kind) == source_id:
            return "environment_override"
        if defaults.get(kind) == source_id:
            return "global_default"
        return "product_default"

    def _source_entries(self, data: dict, record: dict | None, selection) -> list[dict]:
        """计划/环境解析的来源投影：名称/类型/脱敏端点/继承来源/用途。"""
        requested = selection.requested_download_source_ids
        requested = (
            None if requested is None else expand_legacy_model_sources(requested)
        )
        entries: list[dict] = []
        for source in selection.effective_download_sources:
            entry = {
                "id": source.source_id,
                "kind": source.kind,
                "display_name": download_source_display_name(source.source_id),
                "endpoint": sanitize_download_endpoint(source.endpoint),
                "requested": (requested is not None and source.source_id in requested),
                "inherited_from": self._source_origin(data, record, source.source_id),
            }
            if source.kind != DOWNLOAD_SOURCE_KIND_PACKAGE_INDEX:
                # 模型源只是偏好投影；原生 downloader 的真实端点未知。
                entry["usage"] = "model_preference"
                entry["actual_endpoint"] = None
            else:
                entry["usage"] = "online_index"
            entries.append(entry)
        return entries

    def _resolved_source_entries(self, data: dict, record: dict | None) -> list[dict]:
        resolved = self._resolved_defaults(data, record)
        entries: list[dict] = []
        for kind in _SOURCE_KINDS:
            source_id = resolved.get(kind)
            entry: dict = {"kind": kind, "id": source_id, "origin": "product_default"}
            if source_id is not None:
                entry["display_name"] = download_source_display_name(source_id)
                entry["origin"] = self._source_origin(data, record, source_id)
            else:
                # 仅 model_registry 可能：官方原生默认，无目录 id 可指认。
                entry["display_name"] = None
            entries.append(entry)
        return entries

    def set_sources(
        self,
        env_id: str | None,
        package_source_id: str | None,
        model_source_id: str | None | object = _UNSET_SOURCE,
        *,
        paddleocr_model_source_id: str | None | object = _UNSET_SOURCE,
        mineru_model_source_id: str | None | object = _UNSET_SOURCE,
    ) -> dict:
        """保存全局默认或单环境override；null 表示清除该 kind 回退继承。

        只写配置：不下载、不安装、不重启；同环境在途操作会被 target
        operation lock 拒绝，保存 A 不会修改 B。

        显式保存全局默认（``env_id is None``）时，本次提交的 kinds 会
        同步清除所有环境的旧 override：新 UI 只有一个全局设置入口，
        残留的旧 override 会隐性遮蔽新全局默认。清除与全局保存在同一
        次 store 锁内原子提交，``source_config_revision`` 只递增一次，
        旧预览随之失效；在途安装的来源已在操作启动时冻结，不受影响。
        未知来源 id 无法判定 kind，保留并沿用旧标注语义。环境级
        ``env_id`` 接口保留供旧数据/兼容；未显式提交的 kinds 不动旧配置。
        """
        catalog = {source["id"]: source for source in self._source_catalog()}
        requested: dict[str, str | None | object] = {
            DOWNLOAD_SOURCE_KIND_PACKAGE_INDEX: package_source_id,
        }
        if model_source_id is not _UNSET_SOURCE:
            if model_source_id is not None and model_source_id not in {
                "huggingface",
                "modelscope",
            }:
                raise ManagedEnvironmentError(
                    f"unknown model_registry download source: {model_source_id}"
                )
            for engine in ("paddleocr", "mineru"):
                requested[f"{engine}_model_registry"] = (
                    None if model_source_id is None else f"{engine}-{model_source_id}"
                )
        for kind, source_id in (
            (DOWNLOAD_SOURCE_KIND_PADDLEOCR_MODEL_REGISTRY, paddleocr_model_source_id),
            (DOWNLOAD_SOURCE_KIND_MINERU_MODEL_REGISTRY, mineru_model_source_id),
        ):
            if source_id is not _UNSET_SOURCE:
                requested[kind] = source_id
        for kind, source_id in requested.items():
            if source_id is None:
                continue
            if not isinstance(source_id, str):
                raise ManagedEnvironmentError(f"invalid {kind} download source")
            source = catalog.get(source_id)
            if source is None or source["kind"] != kind:
                raise ManagedEnvironmentError(
                    f"unknown {kind} download source: {source_id}"
                )
        target_lock = (
            self._target_operation(env_id) if env_id is not None else nullcontext()
        )
        with target_lock, RuntimeStoreLock(self._lock):
            data = self._read()
            key = "default_source_ids" if env_id is None else "override_source_ids"
            target: dict = data
            if env_id is not None:
                record = data["environments"].get(env_id)
                if record is None:
                    raise ManagedEnvironmentError("unknown environment")
                target = record
            current = self._kind_source_map(target.get(key) or ())
            for kind, source_id in requested.items():
                if source_id is None:
                    current.pop(kind, None)
                elif isinstance(source_id, str):
                    current[kind] = source_id
            if env_id is None:
                submitted_kinds = set(requested)
                kind_by_id = {
                    source_id: source["kind"] for source_id, source in catalog.items()
                }
                for record in data["environments"].values():
                    raw_overrides = record.get("override_source_ids")
                    if not raw_overrides:
                        continue
                    # 历史共享模型 id（裸 huggingface/modelscope）先展开成
                    # 每引擎标准形式再判 kind，否则会逃过 submitted kinds
                    # 清除并在读取时继续遮蔽新全局默认；保存也用标准形式。
                    expanded = list(expand_legacy_model_sources(raw_overrides))
                    kept = [
                        source_id
                        for source_id in expanded
                        if kind_by_id.get(source_id) not in submitted_kinds
                    ]
                    if kept != list(raw_overrides):
                        if kept:
                            record["override_source_ids"] = kept
                        else:
                            record.pop("override_source_ids", None)
            merged_ids = tuple(
                source["id"]
                for source in self._source_catalog()
                if current.get(source["kind"]) == source["id"]
            )
            data["source_config_revision"] = (
                int(data.get("source_config_revision") or 0) + 1
            )
            if merged_ids:
                target[key] = list(merged_ids)
            else:
                target.pop(key, None)
            _atomic_json(self._registry, data)
        return self.list()

    def _reject_referenced(self, data: dict) -> None:
        if any(
            environment_has_references(self._references, env_id)
            for env_id in data["environments"]
        ):
            raise ManagedEnvironmentError("environment has active jobs")

    @contextmanager
    def _target_operation(self, env_id: str) -> Iterator[None]:
        if env_id != "legacy" and not _is_valid_environment_id(env_id):
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

    def _last_install_failure(self, record: dict) -> dict | None:
        operation = record.get("last_install_operation")
        if operation is None:
            return None
        # 旧记录没有来源绑定；进程中断与显式失败共用已冻结的来源投影。
        source_evidence = (
            {
                "requested_source_ids": operation["requested_source_ids"],
                "effective_source_ids": operation["effective_source_ids"],
            }
            if "effective_source_ids" in operation
            else {}
        )
        # 只读投影操作已冻结的 plan_id：调用方用它把读回的失败/中断终态
        # 绑定回发起的安装计划，避免同 revision/recipe 的旧记录被冒认。
        # 历史记录缺失该键时不输出，调用方按不可归属处理。
        plan_evidence = (
            {"plan_id": operation["plan_id"]} if "plan_id" in operation else {}
        )
        if operation["phase"] == "installing":
            lock = RuntimeStoreLock(
                self.paths.locks_root
                / "environment-operations"
                / f"{record['id']}.lock",
                timeout=0,
            )
            try:
                lock.acquire()
            except RuntimeLockTimeout:
                reason_code = "install_in_progress"
                detail = "Dependency installation is still running."
            else:
                lock.release()
                reason_code = "install_interrupted"
                detail = "Dependency installation was interrupted; the previous environment remains available."
            return {
                "phase": "installing"
                if reason_code == "install_in_progress"
                else "failed",
                "environment_revision": operation["environment_revision"],
                "recipe": operation["recipe"],
                "reason_code": reason_code,
                "next_action": "wait"
                if reason_code == "install_in_progress"
                else "preview_again",
                "detail": detail,
                **plan_evidence,
                **source_evidence,
            }
        return {
            "phase": "failed",
            "environment_revision": operation["environment_revision"],
            "recipe": operation["recipe"],
            "reason_code": operation["reason_code"],
            "next_action": operation["next_action"],
            "detail": safe_runtime_detail(operation["detail"]),
            **plan_evidence,
            **source_evidence,
        }

    def _read(self) -> dict:
        if self._registry.is_file():
            data = json.loads(self._registry.read_text(encoding="utf-8"))
            self._validate_registry(data)
            return data
        data = {
            "schema_version": 1,
            "active_id": None,
            "active_revision": 0,
            "default_source_ids": [],
            "source_config_revision": 0,
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
            or not {
                "schema_version",
                "active_id",
                "active_revision",
                "environments",
            }.union(_REGISTRY_OPTIONAL_KEYS).issuperset(set(data))
            or not {
                "schema_version",
                "active_id",
                "active_revision",
                "environments",
            }.issubset(data)
            or data["schema_version"] != 1
            or type(data["active_revision"]) is not int
            or data["active_revision"] < 0
            or not isinstance(data["environments"], dict)
        ):
            raise ManagedEnvironmentError("environment registry is invalid")
        self._validate_source_ids(data.get("default_source_ids"), "default sources")
        if "source_config_revision" in data and (
            type(data["source_config_revision"]) is not int
            or data["source_config_revision"] < 0
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
                or (env_id != "legacy" and not _is_valid_environment_id(env_id))
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
                    "recipe_lock",
                    "source_ids",
                    "override_source_ids",
                    "last_install_operation",
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
                or record["recipe"] not in _RECIPE_IDS
            ):
                raise ManagedEnvironmentError("environment recipe is invalid")
            # 安装时冻结的 manifest scope 锁证据（旧 schema_version=1 记录
            # 缺失该键：list/手工切换不受影响，兼容查询按未验证处理）。
            if "recipe_lock" in record and (
                not isinstance(record["recipe_lock"], str)
                or not re.fullmatch(r"[0-9a-f]{64}", record["recipe_lock"])
            ):
                raise ManagedEnvironmentError("environment recipe lock is invalid")
            if "source_ids" in record and (
                not isinstance(record["source_ids"], list)
                or any(not isinstance(source, str) for source in record["source_ids"])
            ):
                raise ManagedEnvironmentError("environment source selection is invalid")
            if "override_source_ids" in record:
                self._validate_source_ids(
                    record["override_source_ids"], "environment source overrides"
                )
            if "last_install_operation" in record:
                operation = record["last_install_operation"]
                if (
                    record["kind"] != "venv"
                    or not isinstance(operation, dict)
                    or not {
                        "environment_revision",
                        "plan_id",
                        "recipe",
                        "phase",
                        "reason_code",
                        "next_action",
                        "detail",
                    }.issubset(operation)
                    or not {
                        "environment_revision",
                        "plan_id",
                        "recipe",
                        "phase",
                        "reason_code",
                        "next_action",
                        "detail",
                    }.union(_OPERATION_OPTIONAL_KEYS).issuperset(set(operation))
                    or type(operation["environment_revision"]) is not int
                    or operation["environment_revision"] != record["revision"]
                    or not isinstance(operation["plan_id"], str)
                    or not re.fullmatch(r"[0-9a-f]{32}", operation["plan_id"])
                    or not isinstance(operation["recipe"], str)
                    or operation["recipe"] not in _RECIPE_IDS
                    or not isinstance(operation["phase"], str)
                    or operation["phase"] not in {"installing", "failed"}
                    or not isinstance(operation["reason_code"], str)
                    or not re.fullmatch(r"[a-z0-9_.-]{0,80}", operation["reason_code"])
                    or not isinstance(operation["next_action"], str)
                    or not re.fullmatch(r"[a-z0-9_.-]{0,80}", operation["next_action"])
                    or not isinstance(operation["detail"], str)
                    or len(operation["detail"]) > 4000
                    or (
                        operation["phase"] == "failed"
                        and (
                            not operation["reason_code"] or not operation["next_action"]
                        )
                    )
                    or not self._valid_operation_sources(operation)
                ):
                    raise ManagedEnvironmentError(
                        "environment installation record is invalid"
                    )
            self._safe_path(record)

    @staticmethod
    def _validate_source_ids(value: object, label: str) -> None:
        """来源偏好列表的结构校验：目录语义（未知 id、同 kind 多选）在解析时标注。"""
        if value is None:
            return
        if not isinstance(value, list) or not (
            all(isinstance(item, str) and _SOURCE_ID.fullmatch(item) for item in value)
        ):
            raise ManagedEnvironmentError(f"{label} are invalid")
        if len(set(value)) != len(value):
            raise ManagedEnvironmentError(f"{label} are invalid")

    @staticmethod
    def _valid_operation_sources(operation: dict) -> bool:
        requested = operation.get("requested_source_ids")
        if requested is not None and (
            not isinstance(requested, list)
            or not all(isinstance(item, str) for item in requested)
        ):
            return False
        effective = operation.get("effective_source_ids")
        return effective is None or (
            isinstance(effective, list)
            and all(isinstance(item, str) for item in effective)
            and bool(effective)
        )

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
            "import json,sys,sysconfig; from importlib.metadata import distributions; "
            "installed=list(distributions()); "
            "print(json.dumps({'prefix':sys.prefix,'base_prefix':sys.base_prefix,"
            "'platform':sysconfig.get_platform(),"
            "'version':sys.version_info[:3],'packages':sorted(d.metadata['Name'].lower() "
            "for d in installed),'versions':{d.metadata['Name'].lower():d.version "
            "for d in installed},'runtime_version':next((d.version for d in installed "
            "if d.metadata['Name'].lower()=='vibeocr-next-runtime'),None)}))"
        )
        try:
            result = subprocess.run(
                [str(python), "-I", "-c", code],
                stdin=subprocess.DEVNULL,
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
        packages = {_normalize_dist_name(name) for name in details["packages"]}
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
            import_probe = _RECIPE_IMPORT_PROBES.get(recipe)
            if import_probe is not None:
                # Probe the exact import the Supervisor performs at engine
                # init, for every engine the recipe activates. rapidocr's
                # package __init__ resolves RapidOCR lazily, so bare package
                # imports (or leaf modules such as pyclipper/onnxruntime)
                # prove nothing about the native transitive closure
                # (rapidocr.main -> ch_ppocr_det -> shapely); importing the
                # real symbols loads that closure without instantiating the
                # engine or downloading models.
                try:
                    subprocess.run(
                        [
                            str(python),
                            "-I",
                            "-B",
                            "-c",
                            import_probe,
                        ],
                        # 与实际 Supervisor 使用同一私有配置/环境投影。
                        # _launch 只投影参数；不启动进程、创建模型或写入缓存。
                        env={
                            **os.environ,
                            **self._launch(record, str(python))["environment"],
                        },
                        stdin=subprocess.DEVNULL,
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
            "versions": {
                _normalize_dist_name(name): version
                for name, version in (details.get("versions") or {}).items()
            },
            "platform": str(details.get("platform") or "").replace("-", "_"),
        }

    @staticmethod
    def _recipe_modes(recipe: str) -> list[str]:
        """配方 → 真实可用的识别类型（目录与公共记录共用同一投影）。"""
        if recipe.startswith("paddleocr"):
            return ["text", "table", "formula", "structure", "document_vl"]
        if recipe.startswith("rapidocr+mineru"):
            return ["text", "document"]
        if recipe == "rapidocr-cpu":
            return ["text"]
        if recipe == "mineru-cpu":
            return ["document"]
        return []

    @staticmethod
    def _scope_pins(scope: RuntimeInstallScope) -> list[tuple[str, str, str | None]]:
        """锁文件 pin 列表：(规范小写名, 展示 pin, 锁定版本或 None)。

        直接 URL 只接受可解析且包名一致的 wheel 版本；未知版本不参与复用。
        """
        pins: list[tuple[str, str, str | None]] = []
        for line in scope.lock_path.read_text(encoding="utf-8").splitlines():
            if match := re.match(r"^([A-Za-z0-9_.-]+)==([^\s\\]+)", line):
                name, version = match.group(1), match.group(2)
                pins.append((_normalize_dist_name(name), f"{name}=={version}", version))
            elif match := re.match(r"^([A-Za-z0-9_.-]+)\s+@\s+([^\s\\]+)", line):
                name, url = match.group(1), match.group(2)
                version = None
                try:
                    wheel_name, wheel_version, _build, _tags = parse_wheel_filename(
                        unquote(urlsplit(url).path.rsplit("/", 1)[-1])
                    )
                    if _normalize_dist_name(name) == wheel_name:
                        version = str(wheel_version)
                except InvalidWheelFilename:
                    pass
                pins.append((_normalize_dist_name(name), f"{name} @ {url}", version))
        return pins

    def _public_record(
        self, record: dict, probe: dict, data: dict | None = None
    ) -> dict:
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
        modes = self._recipe_modes(recipe)
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
            "last_install_failure": self._last_install_failure(record),
            "packages": probe.get("packages", []),
            **self._source_projection(data, record),
        }

    def _source_projection(self, data: dict | None, record: dict) -> dict:
        """环境记录的来源解析投影；无注册表上下文时只回退安装证据。"""
        if data is None:
            return {
                "override_source_ids": list(
                    expand_legacy_model_sources(record.get("override_source_ids") or ())
                )
            }
        resolved = self._resolved_source_entries(data, record)
        return {
            "override_source_ids": list(
                expand_legacy_model_sources(record.get("override_source_ids") or ())
            ),
            "unknown_source_ids": self._unknown_source_ids(
                record.get("override_source_ids") or ()
            ),
            "resolved_sources": resolved,
            "resolved_source_ids": [
                entry["id"] for entry in resolved if entry["id"] is not None
            ],
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
        # 硬件真值只由 Runtime 探测一次（与安装预检同一 helper）：
        # unsupported 携带原因，unknown 只代表探测超时/未完成。探测不触碰
        # 注册表，在锁外执行，避免把 nvidia-smi 的超时预算计入存储锁。
        hardware = {"nvidia_driver": probe_nvidia_driver()}
        with RuntimeStoreLock(self._lock):
            data = self._read()
            catalog = self._source_catalog()
            default_ids = list(
                expand_legacy_model_sources(data.get("default_source_ids") or ())
            )
            return {
                "active_id": data["active_id"],
                "active_revision": data["active_revision"],
                "recipes": self.recipe_catalog(),
                "hardware": hardware,
                "sources": [
                    {
                        "id": source["id"],
                        "kind": source["kind"],
                        "display_name": download_source_display_name(source["id"]),
                        "endpoint": sanitize_download_endpoint(source["endpoint"]),
                        "is_default": source
                        in default_download_sources(include_model_sources=True),
                    }
                    for source in catalog
                ],
                "default_source_ids": default_ids,
                "resolved_default_sources": self._resolved_source_entries(data, None),
                "unknown_default_source_ids": self._unknown_source_ids(default_ids),
                "source_config_revision": int(data.get("source_config_revision") or 0),
                "package_source_ids": [
                    source["id"]
                    for source in catalog
                    if source["kind"] == DOWNLOAD_SOURCE_KIND_PACKAGE_INDEX
                ],
                "environments": [
                    self._public_record(record, self._probe(record), data)
                    for record in data["environments"].values()
                ],
            }

    def initialize_default(self) -> dict:
        """首次无环境时安装离线基础配方；升级沿用已有环境，失败可续装。

        初始化状态独立保存，避免改变旧版仍需读取的注册表格式。独立锁
        覆盖创建/安装，store 锁只覆盖注册表提交；完成后不自动重建。

        默认环境固定为 RapidOCR · CPU，依赖必须来自随包离线 base pack：
        缺离线包代表发布闭包错误，保持 fail closed，不静默改用网络源。
        只有真实安装或探针失败才保留可重试的失败证据。
        """
        marker = self.paths.state_root / "default-environment.json"
        with RuntimeStoreLock(self.paths.locks_root / "default-environment.lock"):
            if marker.is_file():
                state = json.loads(marker.read_text(encoding="utf-8"))
                if (
                    not isinstance(state, dict)
                    or set(state) != {"environment_id", "created"}
                    or type(state["created"]) is not bool
                    or (
                        state["environment_id"] is not None
                        and (
                            not isinstance(state["environment_id"], str)
                            or not _is_valid_environment_id(state["environment_id"])
                        )
                    )
                ):
                    raise ManagedEnvironmentError(
                        "default environment initialization is invalid"
                    )
            else:
                with RuntimeStoreLock(self._lock):
                    existing = bool(self._read()["environments"])
                state = {
                    "environment_id": (None if existing else _DEFAULT_ENVIRONMENT_SLUG),
                    "created": existing,
                }
                _atomic_json(marker, state)
            default_id = state["environment_id"]
            if default_id is not None:
                with RuntimeStoreLock(self._lock):
                    record = self._read()["environments"].get(default_id)
                if record is None and not state["created"]:
                    record = self._create(
                        "默认环境", as_default=True, environment_id=default_id
                    )
                if record is not None:
                    state["created"] = True
                    _atomic_json(marker, state)
                    if record["status"] == "empty":
                        scope, _accelerator = self._recipe("rapidocr-cpu")
                        if not scope.runtime_pack:
                            raise ManagedEnvironmentError(
                                "default environment requires the bundled offline base pack"
                            )
                        plan = self.preview_install(default_id, "rapidocr-cpu")
                        self.install(plan["plan_id"], default_id, "rapidocr-cpu")
                    with RuntimeStoreLock(self._lock):
                        data = self._read()
                        record = data["environments"].get(default_id)
                        if record is not None and record["status"] == "installed":
                            if data["active_id"] is None:
                                data["active_id"] = default_id
                                data["active_revision"] += 1
                                _atomic_json(self._registry, data)
                # 已创建但被用户删除、或创建前已有其他环境时，尊重用户选择。
                _atomic_json(marker, {"environment_id": None, "created": True})
        return self.list()

    def create(self, name: str) -> dict:
        created = self._create(name)
        assert created is not None
        return created

    def _new_environment_id(self, name: str, data: dict) -> str:
        """为新环境选择可读目录 id；永不回退 uuid。

        无法从名称派生时用 ``environment``；保留目录名加 ``environment-``
        前缀；碰撞按 -2/-3 顺序递增并截断保持长度有界。目录名在
        Windows 大小写不敏感：与既有 id（casefold）与磁盘现状双重判重。
        """
        base = _environment_directory_slug(name) or "environment"
        if _is_reserved_directory_name(base):
            base = f"environment-{base}"
        base = base[:_ENVIRONMENT_DIRECTORY_MAX].strip(" .") or "environment"
        taken = {record_id.casefold() for record_id in data["environments"]}
        candidate = base
        suffix = 1
        while (
            candidate.casefold() in taken
            or _is_reserved_directory_name(candidate)
            or (self.root / candidate).exists()
        ):
            suffix += 1
            tail = f"-{suffix}"
            trimmed = (
                base[: _ENVIRONMENT_DIRECTORY_MAX - len(tail)].strip(" .")
                or "environment"
            )
            candidate = f"{trimmed}{tail}"
        return candidate

    def _create(
        self, name: str, *, as_default: bool = False, environment_id: str | None = None
    ) -> dict | None:
        name = name.strip()
        if not _NAME.fullmatch(name) or name in {".", ".."}:
            raise ManagedEnvironmentError("environment name is invalid")
        with RuntimeStoreLock(self._lock):
            data = self._read()
            if as_default and data["environments"]:
                return None
            if any(
                item["name"].casefold() == name.casefold()
                for item in data["environments"].values()
            ):
                raise ManagedEnvironmentError("environment name already exists")
            env_id = environment_id or self._new_environment_id(name, data)
            root = self._safe_path(
                {
                    "id": env_id,
                    "kind": "venv",
                    "path": str(Path(env_id) / "revisions" / "1"),
                    "revision": 1,
                }
            )
            base = self._base_python()
            result = subprocess.run(
                [str(base), "-I", "-m", "venv", "--copies", "--without-pip", str(root)],
                stdin=subprocess.DEVNULL,
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
            return self._public_record(record, probe, data)

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
                return self._public_record(record, self._probe(record), data)
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
                stdin=subprocess.DEVNULL,
                capture_output=True,
                text=True,
                timeout=120,
            )
            if result.returncode:
                raise ManagedEnvironmentError("could not repair empty environment")
            replacement = {
                **{
                    key: value
                    for key, value in record.items()
                    if key != "last_install_operation"
                },
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
            return self._public_record(replacement, probe, data)

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
        self, env_id: str, recipe: str, source_ids: tuple[str, ...] | None = None
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
                    and recipe in {"rapidocr+mineru-cpu", "rapidocr+mineru-cuda"}
                )
            ):
                # 单 venv 同时装 base 与 Paddle 隔离锁会让 opencv-python 与
                # opencv-contrib-python 冲突（旧安装器为此用独立解释器），
                # 未绑定的组合如实拒绝，不声称可用。
                raise ManagedEnvironmentError(
                    "engine combination has no compatible locked recipe; "
                    "create another environment"
                )
            elif current is None and recipe == "mineru-cuda":
                raise ManagedEnvironmentError(
                    "standalone CUDA MinerU recipe is not bound by the current release"
                )
            scope, accelerator = self._recipe(recipe)
            selection = self._plan_selection(data, record, accelerator, source_ids)
            if (
                len(
                    [
                        source
                        for source in selection.effective_download_sources
                        if source.kind == DOWNLOAD_SOURCE_KIND_PACKAGE_INDEX
                    ]
                )
                != 1
            ):
                raise ManagedEnvironmentError("one package index source is required")
            sources = self._source_entries(data, record, selection)
            for source in sources:
                if source["kind"] == DOWNLOAD_SOURCE_KIND_PACKAGE_INDEX:
                    # 随包离线包存在时依赖不走在线索引；缺失离线包安装时会
                    # fail closed，不静默改用网络源。
                    source["usage"] = (
                        "bundled_pack" if scope.runtime_pack else "online_index"
                    )
            plan = {
                "plan_id": uuid4().hex,
                "environment_id": env_id,
                "environment_revision": record["revision"],
                "active_revision": data["active_revision"],
                "requested_recipe": requested_recipe,
                "recipe": recipe,
                "recipe_lock": scope.sha256,
                # 请求源（本次显式）与生效源（显式+继承叠加）分开冻结；
                # None 表示本次未显式指定、完全继承环境/全局配置。
                "requested_source_ids": (
                    None
                    if selection.requested_download_source_ids is None
                    else sorted(selection.requested_download_source_ids)
                ),
                "source_ids": [
                    source.source_id for source in selection.effective_download_sources
                ],
                "effective_source_ids": [
                    source.source_id for source in selection.effective_download_sources
                ],
                "source_config_revision": int(data.get("source_config_revision") or 0),
                "runtime_manifest": self.manifest.sha256,
            }
            dependencies = [
                display for _name, display, _version in self._scope_pins(scope)
            ]
            _atomic_json(
                self.paths.state_root / "environment-plans" / f"{env_id}.json",
                plan,
            )
            return {
                **plan,
                "dependencies": dependencies,
                "sources": sources,
                # 资产来源分类：固定解释器归档与内部 wheel 随产品分发；
                # 依赖来自随包离线包或锁定的在线索引；模型只是偏好，
                # 实际下载端点未知，缓存命中不计为新下载。
                "python_origin": "product_bundle",
                "runtime_wheel_origin": "product_bundle",
                "dependency_origin": (
                    "bundled_pack" if scope.runtime_pack else "online_index"
                ),
            }

    def recipe_catalog(self) -> list[dict]:
        """唯一权威配方目录：沿当前 manifest 绑定的 scope 锁直接投影。

        未被 manifest 绑定的配方不进入目录（与 preview_install 同一真相源），
        前台推荐配置只消费本目录，不得自行推导依赖或来源。
        """
        entries: list[dict] = []
        for recipe in _RECIPE_IDS:
            try:
                scope, accelerator = self._recipe(recipe)
            except ManagedEnvironmentError:
                continue
            entries.append(
                {
                    "id": recipe,
                    "display_name": _RECIPE_DISPLAY_NAMES[recipe],
                    "configured_recognition_types": self._recipe_modes(recipe),
                    "accelerator": accelerator,
                    "target_device": (
                        "cuda" if accelerator == "nvidia_cuda" else "cpu"
                    ),
                    "python_version": self.manifest.python.version,
                    "abi": self.manifest.python.abi,
                    "platform": self.manifest.python.platform,
                    "scope_id": scope.scope_id,
                    "component_ids": list(scope.component_ids),
                    "recipe_lock": scope.sha256,
                    "dependencies": [
                        display for _name, display, _version in self._scope_pins(scope)
                    ],
                    "dependency_origin": (
                        "bundled_pack" if scope.runtime_pack else "online_index"
                    ),
                    "python_origin": "product_bundle",
                    "runtime_wheel_origin": "product_bundle",
                }
            )
        return entries

    def find_compatible(self, recipe: str) -> dict:
        """按配方寻找可直接复用的已安装健康环境；只读，不安装/不切换。

        身份只认真实 scope 锁（manifest scope.sha256 字节契约 + 安装时
        冻结的锁证据）、ABI/Python 基线/平台与设备要求；名称与下载镜像
        不参与。选择优先当前活动环境，其余按稳定 id 序。旧记录缺少锁
        证据时如实标记 query 不验证，list/手工切换保持可用。
        """
        with RuntimeStoreLock(self._lock):
            data = self._read()
            scope, _accelerator = self._recipe(recipe)
            pins = {
                name: version for name, _display, version in self._scope_pins(scope)
            }
            ordered = sorted(
                data["environments"].values(),
                key=lambda record: (
                    record["id"] != data["active_id"],
                    record["id"],
                ),
            )
            evaluations: list[dict] = []
            selected: dict | None = None
            for record in ordered:
                evaluation = {
                    "environment_id": record["id"],
                    "name": record["name"],
                    "revision": record["revision"],
                    "status": record["status"],
                    "recipe": record.get("recipe")
                    if record["kind"] == "venv"
                    else None,
                    "active": record["id"] == data["active_id"],
                    "selected": False,
                    "reason_code": None,
                }
                reason: str | None
                if record["kind"] == "legacy":
                    reason = "legacy_environment"
                elif record["status"] != "installed":
                    reason = "environment_empty"
                elif record.get("recipe") != recipe:
                    reason = "recipe_mismatch"
                elif "recipe_lock" not in record:
                    reason = "lock_evidence_missing"
                elif record["recipe_lock"] != scope.sha256:
                    reason = "lock_evidence_mismatch"
                else:
                    reason = None
                if reason is None:
                    probe = self._probe(record)
                    if not probe["healthy"]:
                        reason = probe["reason"]
                    elif probe.get("platform") != self.manifest.python.platform:
                        reason = "platform_mismatch"
                    elif any(version is None for version in pins.values()):
                        reason = "locked_version_unknown"
                    else:
                        installed_versions = probe.get("versions") or {}
                        if any(
                            installed_versions.get(name) != version
                            for name, version in pins.items()
                        ):
                            reason = "locked_version_mismatch"
                if reason is None and selected is not None:
                    reason = "compatible_lower_priority"
                evaluation["reason_code"] = reason
                if reason is None:
                    evaluation["selected"] = True
                    selection_reason = (
                        "active_environment"
                        if record["id"] == data["active_id"]
                        else "deterministic_id_order"
                    )
                    evaluation["reason_code"] = (
                        "selected_active_environment"
                        if selection_reason == "active_environment"
                        else "selected_compatible_environment"
                    )
                    selected = {
                        "environment_id": record["id"],
                        "environment_revision": record["revision"],
                        "recipe": recipe,
                        "selection_reason": selection_reason,
                    }
                evaluations.append(evaluation)
            return {
                "recipe": recipe,
                "recipe_lock": scope.sha256,
                "recipes": self.recipe_catalog(),
                "selected": selected,
                "environments": evaluations,
            }

    def install(
        self,
        plan_id: str,
        env_id: str,
        recipe: str,
        source_ids: tuple[str, ...] | None = None,
    ) -> dict:
        if not re.fullmatch(r"[0-9a-f]{32}", plan_id) or not _is_valid_environment_id(
            env_id
        ):
            raise RuntimeInstallPlanStale("invalid environment plan")
        requested_ids = None if source_ids is None else tuple(sorted(set(source_ids)))
        with self._target_operation(env_id):
            plan_path = self.paths.state_root / "environment-plans" / f"{env_id}.json"
            with RuntimeStoreLock(self._lock):
                self._check_install_cancelled()
                self._install_terminal = False
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
                    or plan.get("requested_source_ids")
                    != (None if requested_ids is None else list(requested_ids))
                    or record is None
                    or record["kind"] != "venv"
                    or record["revision"] != plan.get("environment_revision")
                    or data["active_revision"] != plan.get("active_revision")
                    or (data["active_id"] == env_id and record["status"] != "empty")
                    or plan.get("recipe_lock") != scope.sha256
                    or plan.get("runtime_manifest") != self.manifest.sha256
                    # 来源配置修订变化 → 旧计划失效；在途操作不受影响
                    # （这里发生在冻结前，配置变化后 confirm 必须重新预览）。
                    or plan.get("source_config_revision")
                    != int(data.get("source_config_revision") or 0)
                ):
                    raise RuntimeInstallPlanStale(
                        "environment or recipe changed; preview again"
                    )
                if environment_has_references(self._references, env_id):
                    raise ManagedEnvironmentError("environment has active jobs")
                selection = self._plan_selection(
                    data, record, accelerator, requested_ids
                )
                if [
                    source.source_id for source in selection.effective_download_sources
                ] != list(plan["source_ids"]):
                    raise RuntimeInstallPlanStale(
                        "source configuration changed; preview again"
                    )
                source = next(
                    source
                    for source in selection.effective_download_sources
                    if source.kind == DOWNLOAD_SOURCE_KIND_PACKAGE_INDEX
                )
                revision = record["revision"] + 1
                directory = f"{revision}-{uuid4().hex[:16]}"
                root = self.root / env_id / "revisions" / directory
                if root.exists():
                    raise ManagedEnvironmentError("candidate revision already exists")
                operation = {
                    "environment_revision": record["revision"],
                    "plan_id": plan_id,
                    "recipe": recipe,
                    "phase": "installing",
                    "reason_code": "",
                    "next_action": "",
                    "detail": "",
                    # 操作启动即冻结请求/生效来源；失败/取消/重启后仍可查。
                    "requested_source_ids": plan.get("requested_source_ids"),
                    "effective_source_ids": list(plan["source_ids"]),
                }
                record = {**record, "last_install_operation": operation}
                data["environments"][env_id] = record
                _atomic_json(self._registry, data)
            try:
                base = self._base_python()
                result = subprocess.run(
                    [str(base), "-I", "-m", "venv", "--copies", str(root)],
                    stdin=subprocess.DEVNULL,
                    capture_output=True,
                    text=True,
                    timeout=120,
                )
                if result.returncode:
                    raise ManagedEnvironmentError(
                        "could not prepare candidate environment:\n"
                        f"{safe_runtime_detail(result.stderr)}\nvenv exit {result.returncode}",
                        reason_code="venv_creation_failed",
                        next_action="check_directory_permissions",
                    )
                self._check_install_cancelled()
                self._install_runner(self._venv_python(root), scope, source.endpoint)
                self._check_install_cancelled()
                candidate = {
                    key: value
                    for key, value in record.items()
                    if key != "last_install_operation"
                }
                candidate.update(
                    {
                        "revision": revision,
                        "path": str(Path(env_id) / "revisions" / directory),
                        "status": "installed",
                        "recipe": plan["recipe"],
                        # 安装时冻结的 scope 锁证据（旧记录缺省该键）。
                        "recipe_lock": scope.sha256,
                        "source_ids": plan["source_ids"],
                        "python_version": self.manifest.python.version,
                        "abi": self.manifest.python.abi,
                    }
                )
                probe = self._probe(candidate)
                if not probe["healthy"]:
                    raise ManagedEnvironmentError(
                        f"installed environment failed probe: {probe['reason']}; "
                        "check native dependencies and Portable path length",
                        reason_code=probe["reason"],
                        next_action="repair_environment",
                    )
                with RuntimeStoreLock(self._lock):
                    self._check_install_cancelled()
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
                    self._install_terminal = True
            except Exception as error:
                guidance = failure_guidance(error)
                if isinstance(error, RuntimeInstallPlanStale):
                    guidance = {
                        "reason_code": "plan_stale",
                        "next_action": "preview_again",
                    }
                reason_code = guidance["reason_code"]
                next_action = guidance["next_action"]
                failed = {
                    **operation,
                    "phase": "failed",
                    "reason_code": reason_code
                    if re.fullmatch(r"[a-z0-9_.-]{1,80}", reason_code)
                    else "unknown",
                    "next_action": next_action
                    if re.fullmatch(r"[a-z0-9_.-]{1,80}", next_action)
                    else "inspect_diagnostics",
                    "detail": safe_runtime_detail(str(error)),
                }
                try:
                    with RuntimeStoreLock(self._lock):
                        self._check_install_cancelled()
                        latest = self._read()
                        if latest["environments"].get(env_id) == record:
                            latest["environments"][env_id] = {
                                **record,
                                "last_install_operation": failed,
                            }
                            _atomic_json(self._registry, latest)
                        self._install_terminal = True
                except OSError:
                    # The accepted marker remains durable if a full disk blocks
                    # recording the more specific failure detail.
                    pass
                raise
            return self._public_record(candidate, probe, latest)

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
