"""Named, isolated Python environments owned by the frozen Runtime manager."""

from __future__ import annotations

import json
import logging
import os
import re
import subprocess
import sys
import threading
import time
from contextlib import contextmanager, nullcontext
from dataclasses import replace
from pathlib import Path
from typing import Callable, Iterator
from urllib.error import URLError
from urllib.parse import unquote, urlsplit
from urllib.request import ProxyHandler, build_opener

from packaging.utils import InvalidWheelFilename, parse_wheel_filename
from vibeocr.runtime.environments import managed_cleanup
from vibeocr.runtime.environments.managed_references import (
    environment_has_references,
    environment_has_services,
)
from vibeocr.runtime.environments.model_cache import (
    seed_bundled_paddle_onnx_models,
)
from vibeocr.runtime.environments.runtime_installer import (
    RuntimeInstallError,
    _load_component_lock,
    _normalize_dist_name,
)
from vibeocr.runtime.environments.runtime_layout import resolve_runtime_store
from vibeocr.runtime.environments.runtime_lock import (
    RuntimeLockTimeout,
    RuntimeStoreLock,
)
from vibeocr.runtime.environments.runtime_maintenance import (
    RuntimeInstallPlanStale,
    _atomic_json,
    safe_runtime_detail,
)
from vibeocr.runtime.environments.runtime_manifest import (
    ACCELERATOR_TO_PLAN,
    RuntimeInstallScope,
    load_runtime_manifest,
)
from vibeocr.runtime.environments.runtime_selection import (
    DOWNLOAD_SOURCE_KIND_MINERU_MODEL_REGISTRY,
    DOWNLOAD_SOURCE_KIND_PADDLEOCR_MODEL_REGISTRY,
    RuntimeSelectionError,
    RuntimeSelectionPolicy,
    default_download_sources,
    download_source_catalog_payload,
    download_source_display_name,
    download_source_provider,
    expand_legacy_model_sources,
    sanitize_download_endpoint,
)

_NAME = re.compile(r"^[^\\/\x00-\x1f]{1,80}$")
_SOURCE_ID = re.compile(r"^[a-z0-9][a-z0-9-]{0,40}$")
_SOURCE_KINDS = (
    DOWNLOAD_SOURCE_KIND_PADDLEOCR_MODEL_REGISTRY,
    DOWNLOAD_SOURCE_KIND_MINERU_MODEL_REGISTRY,
)
_UNSET_SOURCE = object()
# 注册表可选新增键：旧 schema_version=1 记录缺失时按空配置/0 读取。
_REGISTRY_OPTIONAL_KEYS = {
    "default_source_ids",
    "source_config_revision",
    "prepare_targets",
}
_OPERATION_OPTIONAL_KEYS = {"requested_source_ids", "effective_source_ids"}
_FORBIDDEN_EMPTY = (
    "fastapi",
    "rapidocr",
    "rapid-table",
    "paddleocr",
    "mineru",
    "torch",
    "onnxruntime",
)
# 锁定配方是 Runtime 的唯一权威目录：展示名、真实用途（识别类型）与
# 设备要求沿 manifest scope 锁投影；C#/TS 只消费目录，不得重算依赖图。
# Rapid 系配方（rapidocr-* / rapidocr+mineru-*）已随引擎退役：不再进入
# 可安装目录，旧记录按数据保护原则保留并继续健康探测（不自动卸载/
# 删除），提示改用对应 Paddle ONNX 环境。
_RECIPE_IDS = (
    "rapidocr-cpu",
    "rapidocr-cuda",
    "paddleocr-cpu",
    "paddleocr-cuda",
    "paddleocr-ort-cpu",
    "paddleocr-ort-cuda",
    "paddleocr-ort-directml",
    "mineru-cpu",
    "mineru-cuda",
    "rapidocr+mineru-cpu",
    "rapidocr+mineru-cuda",
)
# 显式产品能力；旧 profile/配方标识的兼容仅在 Runtime 内解析。
# 统一推理闭包：paddleocr-ort-directml 是唯一可安装配方（Paddle ONNX +
# MinerU 随同一闭包携带，DirectML EP 覆盖全 D3D12 GPU，CPU 推理走同闭包
# CPUExecutionProvider 的设备切换）；paddleocr-ort-cpu/cuda 与独立
# mineru 配方已退役，按数据保护原则保留读取。
_RECIPE_METADATA = {
    "paddleocr-ort-directml": (
        "paddle",
        "paddle-onnx",
        "PaddleOCR ONNX + MinerU",
        "directml",
        "DmlExecutionProvider",
    ),
}


def _recipe_metadata(recipe: str) -> dict:
    metadata = _RECIPE_METADATA.get(recipe)
    if metadata is None:
        return {}
    engine, family, title, device, provider = metadata
    return {
        "engine": engine,
        "family": family,
        "family_display_name": title,
        "target_device": device,
        "provider": provider,
        "supports_remote_host": True,
        # 统一闭包是唯一可安装配方，也是远程 MinerU 模式的推荐宿主
        # （桌面"启用远程模式"按 DefaultRemoteHost && SupportsRemoteHost
        # 唯一命中选择宿主配方）。
        "default_remote_host": recipe == "paddleocr-ort-directml",
    }


_RECIPE_DISPLAY_NAMES = {
    "rapidocr-cpu": "RapidOCR + RapidTable · CPU（已退役）",
    "rapidocr-cuda": "RapidOCR + RapidTable · NVIDIA CUDA（已退役）",
    "paddleocr-cpu": "PaddleOCR · CPU（已退役）",
    "paddleocr-cuda": "PaddleOCR · NVIDIA CUDA（已退役）",
    "paddleocr-ort-cpu": "PaddleOCR ONNX · CPU（已退役）",
    "paddleocr-ort-cuda": "PaddleOCR ONNX · NVIDIA CUDA（已退役）",
    "paddleocr-ort-directml": "PaddleOCR ONNX + MinerU · Windows DirectML",
    "mineru-cpu": "MinerU · CPU（已退役）",
    "mineru-cuda": "MinerU · NVIDIA CUDA（已退役）",
    "rapidocr+mineru-cpu": "RapidOCR + RapidTable + MinerU · CPU（已退役）",
    "rapidocr+mineru-cuda": "RapidOCR + RapidTable + MinerU · NVIDIA CUDA（已退役）",
}
# #270：旧原生 Paddle 配方退役；本仓已全面转向 PaddleOCR 后 Rapid 系配方
# 同步退役：不再进入可安装目录、不再宣称任何识别类型，也不再作为保留
# 模式的后端；已安装记录按数据保护原则保留并继续健康探测（不自动
# 卸载/删除），提示改用对应 Paddle ONNX 环境。
_RETIRED_NATIVE_PADDLE_RECIPES = frozenset({"paddleocr-cpu", "paddleocr-cuda"})
_RETIRED_RAPID_RECIPES = frozenset(
    {
        "rapidocr-cpu",
        "rapidocr-cuda",
        "rapidocr+mineru-cpu",
        "rapidocr+mineru-cuda",
    }
)
# 统一推理闭包退役的分设备配方：CPU/CUDA paddle-ort 与独立 MinerU 环境
# 全部并入 paddleocr-ort-directml；已安装记录按数据保护原则保留并继续
# 健康探测（不自动卸载/删除）。
_RETIRED_SPLIT_DEVICE_RECIPES = frozenset(
    {
        "paddleocr-ort-cpu",
        "paddleocr-ort-cuda",
        "mineru-cpu",
        "mineru-cuda",
    }
)
_RETIRED_ENGINE_RECIPES = (
    _RETIRED_NATIVE_PADDLE_RECIPES
    | _RETIRED_RAPID_RECIPES
    | _RETIRED_SPLIT_DEVICE_RECIPES
)
# 探针运行 Supervisor 实际执行的引擎导入：不实例化引擎、不下载模型。
# 只为可安装配方保留引擎 import 探针；退役配方（Rapid 系 / 原生 Paddle）
# 不再加载任何原生引擎库，读取时按 engine_recipe_retired 直接投影不可用。
# GPU 硬件检查按所选设备门控：统一闭包选 CPU（无 DXGI 适配器的 Windows
# 虚拟机等）时只验证 CPUExecutionProvider 闭包可导入，不要求存在 D3D12
# 适配器；DmlExecutionProvider 是 onnxruntime-directml 包的静态能力，与
# 硬件无关，保持无条件断言。
_RECIPE_IMPORT_PROBES = {
    "paddleocr-ort-directml": (
        "from paddleocr import PaddleOCR; import onnxruntime; "
        "from mineru.parser.api_server import create_app; "
        "from vibeocr.runtime.recognition.directml_devices import "
        "hardware_adapters; "
        "assert 'DmlExecutionProvider' in "
        "onnxruntime.get_available_providers(); import os; "
        "assert os.environ.get("
        "'VIBEOCR_MANAGED_ENVIRONMENT_DEVICE', 'directml'"
        ") == 'cpu' or hardware_adapters()"
    ),
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
# 默认环境的用途/设备即目录名：统一推理闭包（Paddle ONNX + MinerU，
# DirectML/CPU 设备切换）随包预装。
_DEFAULT_ENVIRONMENT_SLUG = "paddleocr-ort-directml"
# 旧版默认环境 slug：仅用于识别旧初始化 marker 并迁到新 slug；从未完成
# 的旧默认环境不迁移数据，只改用新目录名重建。
_LEGACY_DEFAULT_ENVIRONMENT_SLUGS = ("rapidocr-cpu", "paddleocr-ort-cpu")
# 固定路径布局：每个历史 venv 环境直接位于 environments/<id>。预构建
# 运行时树（PR2）随产品分发后不再有安装事务：新环境 kind=product，树
# 位置以 manifest runtime_env 绑定锚定（backend 的兄弟 env 目录），不进
# store_root/environments 边界。.staging/.retired 换入槽位与 swap journal
# 不再产生新写入，常量与路径 helper 保留供历史记录/ledger 校验和空间
# 清理回收旧残留。
_SWAP_STAGING_ROOT = ".staging"
_SWAP_RETIRED_ROOT = ".retired"
# 预构建树在产品内的固定落点（product_layout 置于 runtime/env/<root>，
# root 由 manifest runtime_env 绑定声明）；注册表 product 记录的 path
# 字段固定为该标记值，真实树路径在 _safe_path 里以 manifest 重新锚定。
_PRODUCT_ENVIRONMENT_PATH = "env/runtime-env"


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


class ManagedEnvironmentStore:
    def __init__(
        self,
        *,
        product_root: str | Path,
        component_lock: str | Path,
        runtime_manifest: str | Path,
        layout_manifest: str | Path | None = None,
        product_id: str | None = None,
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
        self._swap_journal_root = self.paths.state_root / "environment-swaps"
        self._lock = self.paths.locks_root / "runtime-store.lock"
        self._references = self.paths.locks_root / "environment-references"
        self._cleanup_cancelled = threading.Event()
        self._cleanup_terminal = False
        self._cleanup_cancel_gate = threading.Lock()
        self._switch_guarded: str | None = None
        self._switch_committed = False

    @staticmethod
    def _selected_device(record: dict) -> str:
        return record.get("selected_device") or _recipe_metadata(
            record.get("recipe", "")
        ).get("target_device", "cpu")

    @staticmethod
    def _paddle_shared(recipe: str | None) -> bool:
        """统一推理闭包的设备共享族：directml 环境可在 directml/cpu 间切换。

        原 cpu/cuda 同闭包互换语义（#299）已随分设备配方退役；directml
        统一闭包保留同样的同环境设备切换能力（DirectML EP ↔ CPU EP）。
        """
        return recipe == "paddleocr-ort-directml"

    def _with_device(self, record: dict, device: str | None) -> dict:
        device = device or self._selected_device(record)
        declared = _recipe_metadata(record.get("recipe", "")).get(
            "target_device", "cpu"
        )
        allowed = (
            {declared, "cpu"}
            if record.get("recipe", "").startswith("paddleocr-ort-")
            else {declared}
        )
        if device not in allowed:
            raise ManagedEnvironmentError(
                "requested device is not installed in this environment"
            )
        return {**record, "selected_device": device}

    def _probe_device(self, record: dict, device: str | None = None) -> dict:
        selected = self._with_device(record, device)
        return self._probe({**selected, "_device_probe": selected["selected_device"]})

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
            # CUDA 档位已退役：managed 环境一律按 CPU 档位投影，设备
            # （DirectML/CPU EP）由 VIBEOCR_MANAGED_ENVIRONMENT_DEVICE 表达。
            accelerator = "cpu"
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
            "VIBEOCR_MANAGED_SELECTION_REVISION": str(self._read()["active_revision"]),
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
        # 预构建树自带完整 site-packages（-I 不排除解释器自身 site-packages），
        # 入口统一 ``python -m``，不再需要产品代码根注入；Runtime code 的
        # 可用性由树清单与 _probe 的 import 探针兜底。
        if record["kind"] in {"venv", "product"}:
            environment["VIBEOCR_MANAGED_ENVIRONMENT_RECIPE"] = record["recipe"]
            environment["VIBEOCR_MANAGED_ENVIRONMENT_DEVICE"] = self._selected_device(
                record
            )
            if record["recipe"].startswith("paddleocr-ort-"):
                # Frozen source manifests own downloads; importing the catalog must stay offline.
                environment["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] = "True"
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

        目录只剩模型源：PaddleOCR 与 MinerU 各自默认 ModelScope；历史
        package_index 选择在读取时过滤（依赖全部离线随包，无 pip 索引语义）。
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
            # 受管环境的计划只解析来源与 base 闭包：组件闭包由
            # paddle_onnx_environments 的 scope 提供，不依赖 accelerator
            # 档位 profile。统一闭包后新 manifest 只产 base 档，历史
            # accelerator（cpu/nvidia_cuda，退役配方与 legacy 记录）在
            # 档位 profile 缺席时按 base 规划；plan_start 对产品级组件
            # 选择仍保持严格校验。
            plan_profile = ACCELERATOR_TO_PLAN.get(accelerator)
            if plan_profile is None or plan_profile not in self.manifest.profiles:
                accelerator = "base"
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
            # 目录只剩模型源：偏好投影；原生 downloader 的真实端点未知。
            entries.append(
                {
                    "id": source.source_id,
                    "kind": source.kind,
                    "display_name": download_source_display_name(source.source_id),
                    "endpoint": sanitize_download_endpoint(source.endpoint),
                    "requested": (
                        requested is not None and source.source_id in requested
                    ),
                    "inherited_from": self._source_origin(
                        data, record, source.source_id
                    ),
                    "usage": "model_preference",
                    "actual_endpoint": None,
                }
            )
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
        model_source_id: str | None | object = _UNSET_SOURCE,
        *,
        paddleocr_model_source_id: str | None | object = _UNSET_SOURCE,
        mineru_model_source_id: str | None | object = _UNSET_SOURCE,
    ) -> dict:
        """保存全局默认或单环境override；null 表示清除该 kind 回退继承。

        只写配置：不下载、不安装、不重启；同环境在途操作会被 target
        operation lock 拒绝，保存 A 不会修改 B。目录只剩模型源：
        PaddleOCR 与 MinerU 各自选择提供方。

        显式保存全局默认（``env_id is None``）时，本次提交的 kinds 会
        同步清除所有环境的旧 override：新 UI 只有一个全局设置入口，
        残留的旧 override 会隐性遮蔽新全局默认。清除与全局保存在同一
        次 store 锁内原子提交，``source_config_revision`` 只递增一次，
        旧预览随之失效；在途安装的来源已在操作启动时冻结，不受影响。
        未知来源 id 无法判定 kind，保留并沿用旧标注语义。环境级
        ``env_id`` 接口保留供旧数据/兼容；未显式提交的 kinds 不动旧配置。
        """
        catalog = {source["id"]: source for source in self._source_catalog()}
        requested: dict[str, str | None | object] = {}
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
        if (legacy / ".installed.json").is_file() and self._interpreter_python(
            legacy
        ).is_file():
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
        elif record["kind"] == "product":
            # 预构建树随产品分发：注册表 path 是固定标记，真实树位置以
            # 已验证 manifest 的 runtime_env 绑定锚定（backend 的兄弟 env
            # 目录），不进 store_root/environments 边界，cleanup 也不把
            # 产品树列为可清理对象。
            binding = self.manifest.runtime_env
            if (
                binding is None
                or record["path"] != _PRODUCT_ENVIRONMENT_PATH
                or Path(binding.root).name != binding.root
                or binding.root in {".", ".."}
            ):
                raise ManagedEnvironmentError(
                    "environment path is outside managed store"
                )
            relative = Path(record["path"])
            expected = self.manifest.path.parent.parent / "env" / binding.root
            declared = Path(record["path"])
            if declared.is_absolute() or declared != relative:
                raise ManagedEnvironmentError(
                    "environment path is outside managed store"
                )
            # 边界锚定产品 runtime 目录（manifest 的父级）：树内任何一级
            # reparse/symlink 都会把目录语义移出产品树，拒绝。
            boundary = self.manifest.path.parent.parent.resolve()
            current = expected
            while current != boundary:
                if (current.exists() or current.is_symlink()) and self._is_reparse(
                    current
                ):
                    raise ManagedEnvironmentError(
                        "environment path contains reparse point"
                    )
                parent = current.parent
                if parent == current or not parent.resolve().is_relative_to(boundary):
                    raise ManagedEnvironmentError("environment path escapes store")
                current = parent
            return expected
        else:
            relative = Path(record["path"])
            parts = relative.parts
            if parts == (env_id,):
                # 新固定路径：venv 就是 environments/<id> 本身。
                pass
            elif (
                len(parts) >= 2
                and parts[0] in (_SWAP_STAGING_ROOT, _SWAP_RETIRED_ROOT)
                and parts[1] == env_id
                and ".." not in parts
                and all(parts)
            ):
                # 换入事务的候选/退役槽位（含退役镜像子路径）：供 cleanup
                # ledger 与 staging 探针校验；注册表记录由
                # _validate_registry 显式拒绝槽位路径。预构建树后不再有
                # 新写入，仅历史残留与空间清理读取。
                pass
            elif (
                len(parts) != 3
                or parts[:2] != (env_id, "revisions")
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
        targets = data.get("prepare_targets", {})
        if not isinstance(targets, dict) or any(
            recipe not in _RECIPE_METADATA
            or not isinstance(target, dict)
            or set(target) != {"environment_id", "name"}
            or not isinstance(target["environment_id"], str)
            or not _is_valid_environment_id(target["environment_id"])
            or not isinstance(target["name"], str)
            or not _NAME.fullmatch(target["name"])
            for recipe, target in targets.items()
        ):
            raise ManagedEnvironmentError(
                "environment preparation ownership is invalid"
            )
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
                    "selected_device",
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
                or record["kind"] not in {"legacy", "venv", "product"}
                or (env_id == "legacy") != (record["kind"] == "legacy")
                # 注册表记录必须指向存活路径：固定路径或 legacy revisions
                # 布局；换入槽位只属于事务与 ledger，不得成为环境记录。
                # 类型前置：path 非 str 时直接落入后续结构化校验，不在
                # Path() 上抛 TypeError。
                or (
                    record["kind"] == "venv"
                    and isinstance(record["path"], str)
                    and Path(record["path"]).parts[:1]
                    in ((_SWAP_STAGING_ROOT,), (_SWAP_RETIRED_ROOT,))
                )
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
            if record["kind"] == "product" and (
                record["status"] != "installed"
                or record.get("recipe") != _DEFAULT_ENVIRONMENT_SLUG
                or record["path"] != _PRODUCT_ENVIRONMENT_PATH
                or "last_install_operation" in record
            ):
                # 预构建产品环境：随包分发、零安装，只允许固定形态；
                # 安装操作证据只属于历史 venv 记录。
                raise ManagedEnvironmentError("product environment record is invalid")
            if "selected_device" in record:
                if not isinstance(record["selected_device"], str) or record[
                    "selected_device"
                ] not in {"cpu", "cuda", "directml"}:
                    raise ManagedEnvironmentError(
                        "environment selected device is invalid"
                    )
                self._with_device(record, record["selected_device"])
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
        """历史 venv 记录探测用的基础解释器位置（只读，不再解包）。"""
        base = self.root / "python-base" / self.manifest.python.version
        return base / "python.exe"

    @staticmethod
    def _interpreter_python(root: Path) -> Path:
        """解释器目录布局（legacy 运行时与预构建树）：python.exe 位于根下。"""
        candidates = (
            root / "Scripts" / "python.exe",
            root / "python.exe",
            root / "bin" / "python",
        )
        return next((path for path in candidates if path.is_file()), candidates[1])

    @staticmethod
    def _venv_python(root: Path) -> Path:
        return root / (
            "Scripts/python.exe" if sys.platform == "win32" else "bin/python"
        )

    def _record_python(self, record: dict) -> Path:
        root = self._safe_path(record)
        if record["kind"] == "venv":
            return self._venv_python(root)
        return self._interpreter_python(root)

    def _probe(self, record: dict) -> dict:
        if record["id"] in managed_cleanup.read_ledger(self)["pending"]:
            return {
                "healthy": False,
                "reason": "cleanup_pending",
                "python": str(self._record_python(record)),
            }
        root = self._safe_path(record)
        python = self._record_python(record)
        if record["kind"] == "product":
            # 预构建树的健康门：树目录与确定性树清单随产品分发且必须
            # 存在（缺失代表发布闭包损坏，fail closed 不重建）；解释器
            # 与包集合/版本门与历史环境同源校验。
            binding = self.manifest.runtime_env
            if binding is None:
                return {
                    "healthy": False,
                    "reason": "product_runtime_env_unbound",
                    "python": str(python),
                }
            if not root.is_dir() or not binding.manifest_path.is_file():
                return {
                    "healthy": False,
                    "reason": "product_runtime_env_missing",
                    "python": str(python),
                }
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
                # 历史 marker 可能携带 nvidia_cuda（CUDA 档位退役前的安装
                # 记录）：读取层按已知历史值放行，投影保留原值。
                or marker.get("accelerator")
                not in (
                    *ACCELERATOR_TO_PLAN,
                    "nvidia_cuda",
                )
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
            recipe = record.get("recipe")
            if recipe in _RETIRED_ENGINE_RECIPES:
                # 退役配方（Rapid 系 / 原生 Paddle）：不再加载任何原生引擎
                # 库做健康探测，直接投影不可用；记录与数据保留，切换入口
                # fail closed。
                return {
                    "healthy": False,
                    "reason": "engine_recipe_retired",
                    "python": str(python),
                }
            expected = {"fastapi"}
            if recipe in self.manifest.paddle_onnx_environments:
                # 统一推理闭包：onnxruntime-directml 单一 ORT 命名空间，
                # MinerU 随包携带（缺包即闭包不完整）。
                expected.update(
                    {"paddleocr", "paddlex", "onnxruntime-directml", "mineru"}
                )
                forbidden = {
                    "paddlepaddle",
                    "paddlepaddle-gpu",
                    "torch",
                    "torchvision",
                    "transformers",
                    "paddle2onnx",
                }
                ort_packages = packages & {
                    "onnxruntime",
                    "onnxruntime-gpu",
                    "onnxruntime-directml",
                }
                if packages & forbidden or (
                    ort_packages and ort_packages != {"onnxruntime-directml"}
                ):
                    return {
                        "healthy": False,
                        "reason": "engine_environment_contaminated",
                        "python": str(python),
                    }
            if not expected.issubset(packages):
                return {
                    "healthy": False,
                    "reason": "engine_packages_missing",
                    "python": str(python),
                }
            import_probe = _RECIPE_IMPORT_PROBES.get(recipe)
            if import_probe is not None:
                # Probe the exact import the Supervisor performs at engine
                # init, for every engine the recipe activates: importing the
                # real symbols loads the native transitive closure without
                # instantiating the engine or downloading models.
                probe_started = time.monotonic()
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
                        # 首次导入 Paddle 的原生闭包可能超过 20 秒，缓存预热
                        # 后仍较重；保持有界探针，不把冷启动误判为安装损坏。
                        timeout=60
                        if recipe in self.manifest.paddle_onnx_environments
                        else 20,
                        check=True,
                    )
                except (OSError, subprocess.SubprocessError) as exc:
                    detail = (
                        f"{type(exc).__name__} after "
                        f"{time.monotonic() - probe_started:.1f}s: {exc}"
                    )
                    stderr = getattr(exc, "stderr", None)
                    if stderr:
                        if isinstance(stderr, bytes):
                            stderr = stderr.decode("utf-8", errors="replace")
                        detail += f"\n{stderr[-2000:]}"
                    return {
                        "healthy": False,
                        "reason": "engine_import_failed",
                        "detail": detail,
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
        if recipe == "paddleocr-ort-directml":
            # 统一推理闭包：OCR/表格/结构随 Paddle ONNX，文档解析随包内
            # MinerU 服务；document 与 text/table/structure 同环境可用，
            # 设置页 MinerU 本地引擎分区按该能力识别共享闭包。
            return ["text", "table", "structure", "document"]
        if recipe in _RETIRED_ENGINE_RECIPES:
            # 退役配方（原生 Paddle / Rapid 系 / 分设备 paddle-ort、独立
            # MinerU）不再宣称任何识别类型；记录与健康状态仍保留。
            return []
        return []

    @staticmethod
    def _supported_devices(recipe: str) -> list[str]:
        """配方 → 可选设备全集（与 _with_device 的允许集一致）。

        统一闭包（paddleocr-ort-*）安装档为声明的 target_device，但同闭包
        携带 CPUExecutionProvider，可切换 CPU 推理；无元数据的退役配方不
        出现在目录中，返回空集只作防御。
        """
        declared = _recipe_metadata(recipe).get("target_device")
        if not isinstance(declared, str) or not declared:
            return []
        return [declared, "cpu"] if recipe.startswith("paddleocr-ort-") else [declared]

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
            in {
                "engine_packages_missing",
                "empty_environment_contaminated",
            }
            else record["status"]
            if python_ok and probe["reason"] != "engine_recipe_retired"
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
            "engine_state": "unavailable"
            if probe["reason"] in {"engine_import_failed", "engine_recipe_retired"}
            else "unverified"
            if installed and dependency_state == "installed"
            else "unavailable",
            "model_state": "missing"
            if probe["reason"] == "engine_model_missing"
            else "not_checked"
            if installed
            else "not_applicable",
            "service_state": "not_started",
            "configured_recognition_types": modes,
            **_recipe_metadata(recipe),
            "target_device": self._selected_device(record),
            "provider": _recipe_metadata(
                f"paddleocr-ort-{self._selected_device(record)}"
            ).get("provider")
            if recipe.startswith("paddleocr-ort-")
            else _recipe_metadata(recipe).get("provider"),
            "actual_device": None,
            "operation_blocks": self._operation_blocks(record, probe, data),
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

    def _operation_blocks(self, record: dict, probe: dict, data: dict | None) -> dict:
        """只读动作快照；真正执行仍由既有锁、引用和 CAS 再核验。"""
        referenced = environment_has_references(self._references, record["id"])
        busy = data is not None and any(
            environment_has_references(self._references, env_id)
            for env_id in data["environments"]
        )
        active = data is None or record["id"] == data["active_id"]
        switch = (
            "environment_has_active_jobs"
            if busy
            else (
                probe["reason"] or "environment_unhealthy"
                if not probe["healthy"]
                else None
            )
        )
        if record["kind"] == "product":
            # 预构建树随产品分发：随 Velopack 更新整体替换，不可删除、
            # 无安装/修复语义，只参与健康切换与设备切换。
            return {"switch": switch, "delete": "product_environment"}
        owned = record["kind"] == "venv"
        return {
            "switch": switch,
            "delete": "legacy_environment"
            if not owned
            else (
                "active_environment"
                if active
                else "environment_has_active_jobs"
                if referenced
                else "environment_has_live_service"
                if environment_has_services(self._references, record["id"])
                else None
            ),
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
        # CUDA 支持已退役：不再探测 NVIDIA 驱动（DirectML 由引擎按设备
        # 可用性自行回退），列表不携带硬件投影。
        with RuntimeStoreLock(self._lock):
            data = self._read()
            catalog = self._source_catalog()
            default_ids = list(
                expand_legacy_model_sources(data.get("default_source_ids") or ())
            )
            return {
                "active_id": data["active_id"],
                "active_revision": data["active_revision"],
                "capabilities": [
                    managed_cleanup.CAPABILITY,
                    managed_cleanup.SWITCH_CAPABILITY,
                ],
                "recipes": self.recipe_catalog(),
                "sources": [
                    {
                        "id": source["id"],
                        "kind": source["kind"],
                        "provider": download_source_provider(source["id"]),
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
                "environments": [
                    self._public_record(record, self._probe(record), data)
                    for record in data["environments"].values()
                ],
            }

    def initialize_default(self) -> dict:
        """幂等注册随产品分发的预构建默认环境并布置随包默认模型。

        预构建运行时树（PR2）零安装：注册只是把固定 product 记录写入
        注册表；依赖闭包已随产品分发，缺树/缺绑定代表发布闭包错误，
        fail closed 不重建。旧 venv/退役记录按数据保护原则保留只读；
        旧退役默认环境的迁移只登记归属并切换 active，不重装，旧目录在
        健康使用 product 环境后由 _finish_default_migration 回收。
        """
        env_id = _DEFAULT_ENVIRONMENT_SLUG
        binding = self.manifest.runtime_env
        if binding is None:
            raise ManagedEnvironmentError(
                "product runtime environment tree is not bound by manifest"
            )
        tree = self.manifest.path.parent.parent / "env" / binding.root
        if not self._interpreter_python(tree).is_file():
            raise ManagedEnvironmentError("product runtime environment tree is missing")
        scope, _accelerator = self._recipe(env_id)
        with RuntimeStoreLock(self.paths.locks_root / "default-environment.lock"):
            with RuntimeStoreLock(self._lock):
                data = self._read()
                active = data["environments"].get(data["active_id"])
                retired_default = (
                    active is not None
                    and active["kind"] == "venv"
                    and active["status"] == "installed"
                    and (
                        active["id"] in _LEGACY_DEFAULT_ENVIRONMENT_SLUGS
                        or (
                            re.fullmatch(r"[0-9a-f]{32}", active["id"]) is not None
                            and active["name"] == "默认环境"
                        )
                    )
                    and active.get("recipe") in _RETIRED_ENGINE_RECIPES
                )
                previous_selection = (data["active_id"], data["active_revision"])
                if data["environments"].get(env_id) is None:
                    data["environments"][env_id] = {
                        "id": env_id,
                        "name": "默认环境",
                        "revision": 1,
                        "kind": "product",
                        "path": _PRODUCT_ENVIRONMENT_PATH,
                        "status": "installed",
                        "python_version": self.manifest.python.version,
                        "abi": self.manifest.python.abi,
                        "recipe": env_id,
                        "recipe_lock": scope.sha256,
                        "selected_device": "directml",
                    }
                    _atomic_json(self._registry, data)
            if retired_default and previous_selection[0] not in (None, env_id):
                _atomic_json(
                    self.paths.state_root / "default-environment-migration.json",
                    {
                        "retired_id": active["id"],
                        "retired_revision": active["revision"],
                        "replacement_id": env_id,
                    },
                )
        # 随包默认模型幂等布置（全命中时走快速路径直接返回）；模型包
        # 损坏按发布闭包错误 fail closed，传播给调用方。
        seed_bundled_paddle_onnx_models(
            self.manifest,
            self.paths.state_root / "model-cache",
            lambda: None,
        )
        with RuntimeStoreLock(self._lock):
            data = self._read()
            if data["environments"].get(env_id) is not None and (
                data["active_id"] is None
                or (
                    data["active_id"] != env_id
                    and retired_default
                    and (data["active_id"], data["active_revision"])
                    == previous_selection
                )
            ):
                self._reject_referenced(data)
                data["active_id"] = env_id
                data["active_revision"] += 1
                _atomic_json(self._registry, data)
        self._finish_default_migration(env_id)
        return self.list()

    def _staging_path(self, env_id: str) -> Path:
        return self.root / _SWAP_STAGING_ROOT / env_id

    def _retired_path(self, env_id: str) -> Path:
        return self.root / _SWAP_RETIRED_ROOT / env_id

    def _journal_path(self, env_id: str) -> Path:
        return self._swap_journal_root / f"{env_id}.json"

    def _recipe(self, recipe: str) -> tuple[RuntimeInstallScope, str]:
        if _recipe_metadata(recipe).get("family") == "paddle-onnx":
            scope = self.manifest.paddle_onnx_environments.get(recipe)
            if scope is None:
                raise ManagedEnvironmentError(
                    "Paddle ONNX recipe is absent from manifest"
                )
            # 统一推理闭包只有 directml 一个档位；directml EP 覆盖全 D3D12
            # GPU，不绑定 NVIDIA 专属加速器计划。
            return scope, "cpu"
        if recipe in _RETIRED_ENGINE_RECIPES:
            # 原生 Paddle / Rapid 系 / 分设备（CPU/CUDA paddle-ort、独立
            # MinerU）配方退役。新 manifest 不再绑定对应 scope，也不能伪造
            # 安装 scope：预览/确认安装在此明确拒绝；历史记录读取走
            # status_scope 的只读闭包投影。
            raise ManagedEnvironmentError(
                "engine recipe is retired; prepare a Paddle ONNX environment",
                reason_code="engine_recipe_retired",
                next_action="prepare_paddle_onnx",
            )
        raise ManagedEnvironmentError("unknown engine recipe")

    def status_scope(
        self,
        env_id: str,
        revision: int,
        runtime_root: Path,
        recipe: str,
        device: str | None = None,
    ) -> tuple[RuntimeInstallScope | None, frozenset[str] | None, str]:
        """Resolve the running revision's installed scope from the registry.

        返回 (install_scope, trusted_closure, accelerator)：正常配方返回
        真实安装 scope；旧原生 Paddle 配方不再有可安装 scope，改用只读的
        已安装组件 ID 投影（recipe 组件 + runtime_host）供状态页展示，
        不调用 _recipe、不伪造锁。
        """
        data = self._read()
        record = data["environments"].get(env_id)
        if (
            record is None
            or record["kind"] not in {"venv", "product"}
            or record["status"] != "installed"
            or record["revision"] != revision
            or record.get("recipe") != recipe
            or self._safe_path(record).resolve() != runtime_root.resolve()
        ):
            raise ManagedEnvironmentError("running environment revision is stale")
        if recipe in _RETIRED_ENGINE_RECIPES:
            accelerator = "nvidia_cuda" if recipe.endswith("cuda") else "cpu"
            return None, frozenset({recipe, "runtime_host"}), accelerator
        scope, accelerator = self._recipe(recipe)
        if self._paddle_shared(recipe):
            # 校验设备仍在已安装集合内；统一推理闭包内 directml/cpu 切换
            # 不改变 scope（同一闭包同时携带 DmlExecutionProvider 与
            # CPUExecutionProvider），也不绑定 NVIDIA 专属加速器计划。
            self._with_device(record, device)
            accelerator = "cpu"
        return scope, None, accelerator

    def recipe_catalog(self) -> list[dict]:
        """唯一权威配方目录：沿当前 manifest 绑定的 scope 锁直接投影。

        未被 manifest 绑定的配方不进入目录（唯一真相源），
        前台推荐配置只消费本目录，不得自行推导依赖或来源。
        """
        entries: list[dict] = []
        for recipe in _RECIPE_IDS:
            if recipe in _RETIRED_ENGINE_RECIPES:
                continue
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
                    # 目录同时投影配方支持的设备全集：统一闭包安装档为
                    # directml，但同闭包可切换 CPU 推理（_with_device 的
                    # 允许集），桌面设备选择与安装配方分离消费本字段。
                    "supported_devices": self._supported_devices(recipe),
                    **_recipe_metadata(recipe),
                    "python_version": self.manifest.python.version,
                    "abi": self.manifest.python.abi,
                    "platform": self.manifest.python.platform,
                    "scope_id": scope.scope_id,
                    "component_ids": list(scope.component_ids),
                    "recipe_lock": scope.sha256,
                    "dependencies": [
                        display for _name, display, _version in self._scope_pins(scope)
                    ],
                    "dependency_origin": "bundled_pack",
                    "python_origin": "product_bundle",
                    "runtime_wheel_origin": "product_bundle",
                }
            )
        return entries

    @contextmanager
    def switch_reservation(
        self, env_id: str, device: str | None = None
    ) -> Iterator[dict]:
        with self._target_operation(env_id):
            self._switch_guarded = env_id
            self._switch_committed = False
            try:
                yield self.prepare_switch(env_id, device)
            finally:
                self._switch_guarded = None
        if self._switch_committed:
            self._finish_default_migration(env_id)

    def prepare_switch(self, env_id: str, device: str | None = None) -> dict:
        with (
            nullcontext()
            if self._switch_guarded == env_id
            else self._target_operation(env_id)
        ):
            with RuntimeStoreLock(self._lock):
                data = self._read()
                self._reject_referenced(data)
                try:
                    record = data["environments"][env_id]
                except KeyError as exc:
                    raise ManagedEnvironmentError("unknown environment") from exc
                record = self._with_device(record, device)
                probe = self._probe_device(record)
                if not probe["healthy"]:
                    raise ManagedEnvironmentError(
                        f"target environment is unhealthy: {probe['reason']}"
                    )
                # 无注册表的旧 legacy 用户：候选服务的启动租约必须读到
                # 注册表，而首次落盘原本要等 commit_switch 的健康检查。
                if not self._registry.is_file() and data["environments"]:
                    _atomic_json(self._registry, data)
                return {
                    "environment_id": env_id,
                    "environment_revision": record["revision"],
                    "active_id": data["active_id"],
                    "active_revision": data["active_revision"],
                    "python": probe["python"],
                    "requires_supervisor": record["status"] == "installed",
                    "launch": self._switch_launch(record, probe["python"], data),
                    "selected_device": record["selected_device"],
                }

    def _switch_launch(self, record: dict, python: str, data: dict) -> dict | None:
        launch = self._launch(record, python)
        if launch is not None:
            changed = data["active_id"] != record["id"] or self._selected_device(
                data["environments"][record["id"]]
            ) != self._selected_device(record)
            launch["environment"]["VIBEOCR_MANAGED_SELECTION_REVISION"] = str(
                data["active_revision"] + int(changed)
            )
        return launch

    def commit_switch(
        self, prepared: dict, *, started_health: dict | None = None
    ) -> dict:
        if (
            not isinstance(prepared, dict)
            or set(prepared) - {"selected_device"}
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
            nullcontext()
            if self._switch_guarded == prepared["environment_id"]
            else self._target_operation(prepared["environment_id"])
        ):
            with RuntimeStoreLock(self._lock):
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
                selected = self._with_device(record, prepared.get("selected_device"))
                probe = self._probe_device(selected)
                if not probe["healthy"] or probe["python"] != prepared["python"]:
                    raise ManagedEnvironmentError(
                        "target environment changed before switch"
                    )
                if prepared["launch"] != self._switch_launch(
                    selected, probe["python"], data
                ):
                    raise ManagedEnvironmentError("target launch changed before switch")
                if record["status"] == "installed" and not self._verified_health(
                    started_health
                ):
                    raise ManagedEnvironmentError("target Supervisor is not healthy")
                if record["status"] == "empty" and started_health is not None:
                    raise ManagedEnvironmentError(
                        "empty environment must not launch Supervisor"
                    )
                if data["active_id"] != env_id or self._selected_device(
                    record
                ) != self._selected_device(selected):
                    data["environments"][env_id] = selected
                    data["active_id"] = env_id
                    data["active_revision"] += 1
                    _atomic_json(self._registry, data)
                elif not self._registry.is_file():
                    _atomic_json(self._registry, data)
                result = {
                    "active_id": data["active_id"],
                    "active_revision": data["active_revision"],
                }
            if self._switch_guarded == env_id:
                self._switch_committed = record["status"] == "installed"
        if self._switch_guarded is None and record["status"] == "installed":
            self._finish_default_migration(env_id)
        return result

    def _finish_default_migration(self, env_id: str) -> None:
        """健康切换后续做归属清理；清理失败不撤销已健康的服务。"""
        marker = self.paths.state_root / "default-environment-migration.json"
        try:
            with RuntimeStoreLock(self.paths.locks_root / "default-environment.lock"):
                if not marker.is_file():
                    return
                migration = json.loads(marker.read_text(encoding="utf-8"))
                if (
                    not isinstance(migration, dict)
                    or set(migration)
                    != {"retired_id", "retired_revision", "replacement_id"}
                    or any(
                        not isinstance(migration[key], str)
                        or not _is_valid_environment_id(migration[key])
                        for key in ("retired_id", "replacement_id")
                    )
                    or type(migration["retired_revision"]) is not int
                    or migration["retired_revision"] < 1
                    or migration["retired_id"] == migration["replacement_id"]
                ):
                    raise ManagedEnvironmentError(
                        "default environment migration is invalid"
                    )
                if migration["replacement_id"] != env_id:
                    return
                plan = managed_cleanup.preview_cleanup(self)
                with RuntimeStoreLock(self._lock):
                    data = self._read()
                    old = data["environments"].get(migration["retired_id"])
                    if data["active_id"] != env_id:
                        return
                    if old is not None and (
                        old["revision"] != migration["retired_revision"]
                        or old.get("recipe") not in _RETIRED_ENGINE_RECIPES
                    ):
                        return
                    pending = (
                        migration["retired_id"]
                        in managed_cleanup.read_ledger(self)["pending"]
                    )
                if old is not None or pending:
                    item_id = (
                        f"environment:{migration['retired_id']}"
                        if old is not None
                        else f"pending:{migration['retired_id']}"
                    )
                    result = managed_cleanup.run_cleanup(
                        self, plan["plan_id"], [item_id]
                    )
                    if result["items"][0]["state"] != "deleted":
                        raise ManagedEnvironmentError(result["items"][0]["detail"])
                marker.unlink()
        except (
            OSError,
            ValueError,
            RuntimeInstallError,
            RuntimeLockTimeout,
            RuntimeInstallPlanStale,
        ) as exc:
            logging.getLogger(__name__).warning(
                "Default environment cleanup pending: %s", safe_runtime_detail(str(exc))
            )

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

    def preview_cleanup(self) -> dict:
        return managed_cleanup.preview_cleanup(self)

    def run_cleanup(self, plan_id: str, item_ids: list[str]) -> dict:
        return managed_cleanup.run_cleanup(self, plan_id, item_ids)

    def cancel_cleanup(self, acknowledge: Callable[[bool], None]) -> None:
        # Cancellation never waits for the store lock held by filesystem deletion.
        with self._cleanup_cancel_gate:
            accepted = not self._cleanup_terminal
            if accepted:
                self._cleanup_cancelled.set()
            acknowledge(accepted)

    def delete(self, env_id: str, *, referenced: bool = False) -> None:
        if referenced:
            raise ManagedEnvironmentError("environment has active jobs")
        try:
            managed_cleanup.delete_environment(self, env_id)
        except RuntimeInstallError as exc:
            raise ManagedEnvironmentError(str(exc)) from exc
