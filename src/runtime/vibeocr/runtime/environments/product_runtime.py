"""Product-owned runtime binding and read-only import of old profile preferences."""

from __future__ import annotations

import json
import ntpath
import os
import sys
from dataclasses import replace
from importlib.metadata import version
from pathlib import Path, PureWindowsPath

from vibeocr.runtime.environments.model_cache import seed_bundled_paddle_onnx_models
from vibeocr.runtime.environments.runtime_layout import resolve_runtime_store
from vibeocr.runtime.environments.runtime_manifest import load_runtime_manifest
from vibeocr.runtime.environments.runtime_selection import (
    expand_legacy_model_sources,
    normalize_download_source_ids,
)
from vibeocr.runtime.environments.settings_store import (
    RuntimeSettingsStore,
    RuntimeSettingsStoreError,
)
from vibeocr.runtime_contracts import SettingsSnapshot


def _cache_root(state: Path, profile: object) -> Path:
    if profile is None:
        return state / "recognition"
    if (
        not isinstance(profile, str)
        or not profile
        or PureWindowsPath(profile).name != profile
        or profile in {".", ".."}
        or profile.endswith((" ", "."))
        or any(c in '<>:"/\\\\|?*' or ord(c) < 32 for c in profile)
        or ntpath.isreserved(profile)
    ):
        raise ValueError("旧配置中的 active_id 无效，原文件已保留。")
    candidate = state / "environments" / profile
    if candidate.resolve() != candidate:
        raise ValueError("旧设置目录包含重解析点，原文件已保留。")
    return candidate


def _legacy_snapshot(state: Path, product: Path) -> SettingsSnapshot:
    registry = state / "environments.json"
    record = {}
    defaults = []
    profile = None
    if registry.exists():
        try:
            data = json.loads(registry.read_bytes())
            if data["schema_version"] != 1 or not isinstance(
                data["environments"], dict
            ):
                raise ValueError("registry schema")
            profile = data["active_id"]
            if profile is not None:
                _cache_root(state, profile)
                record = data["environments"][profile]
                if not isinstance(record, dict) or record["id"] != profile:
                    raise ValueError("active record")
            defaults = data.get("default_source_ids") or []
        except (KeyError, TypeError, ValueError, OSError) as exc:
            raise ValueError(
                f"无法读取旧识别设置，请检查 {registry}；原文件已保留。"
            ) from exc
    elif (state / "environments" / "product" / "supervisor-settings.json").is_file():
        profile = "product"
    legacy_cache = None if record.get("kind") == "legacy" else profile
    source_paths = []
    if legacy_cache:
        source_paths.append(
            _cache_root(state, legacy_cache) / "supervisor-settings.json"
        )
    source_paths.append(product / "state" / "supervisor-settings.json")
    snapshot = SettingsSnapshot()
    recovery_error = None
    for source in source_paths:
        if source.is_file():
            if source.resolve() != source:
                raise ValueError("旧设置文件包含重解析点，原文件已保留。")
            try:
                snapshot = RuntimeSettingsStore(source).read_legacy(snapshot)
            except RuntimeSettingsStoreError as exc:
                if exc.recovered is None:
                    raise
                snapshot = exc.recovered
                recovery_error = exc
            break
    sources = {}
    for values in (
        defaults,
        record.get("override_source_ids") or [],
        snapshot.download_source_ids,
    ):
        if not isinstance(values, (tuple, list)) or not all(
            isinstance(v, str) for v in values
        ):
            raise ValueError("旧模型来源配置无效，原文件已保留。")
        ids = expand_legacy_model_sources(values)
        if ids:
            for value in normalize_download_source_ids(ids):
                sources[value.partition("-")[0]] = value
    device = record.get("selected_device")
    if device not in {"cpu", "directml"}:
        recipe = record.get("recipe", "")
        if not isinstance(recipe, str):
            raise ValueError(
                f"旧配置中的设备类型无效，请检查 {registry}；原文件已保留。"
            )
        device = "cpu" if recipe.endswith("-cpu") else "auto"
    snapshot = replace(
        snapshot,
        extra={
            **snapshot.extra,
            "legacy_cache_profile": legacy_cache,
            **(
                {"legacy_cache_at_state_root": True}
                if record.get("kind") == "legacy"
                else {}
            ),
        },
        recognition_device=device,
        download_source_ids=tuple(sources.values()),
    )
    if recovery_error is not None:
        raise RuntimeSettingsStoreError(
            str(recovery_error),
            recovered=snapshot,
            connection_valid=recovery_error.connection_valid,
        ) from recovery_error
    return snapshot


def prepare_product_runtime() -> RuntimeSettingsStore:
    product = Path(os.environ["VIBEOCR_PRODUCT_ROOT"]).resolve()
    manifest = load_runtime_manifest(
        os.environ["VIBEOCR_RUNTIME_MANIFEST"], verify_artifacts=False
    )
    lock = json.loads(Path(os.environ["VIBEOCR_COMPONENT_LOCK"]).read_bytes())
    binding = lock["product"]
    if (
        lock["schema_version"] != 2
        or binding["component"] != "next"
        or binding["repository"] != "FelixJI/vibeocr-next"
        or binding["version"] != manifest.backend_version
        or binding["source_sha"] != manifest.source_commit
        or binding["runtime_manifest_sha256"] != manifest.sha256
        or not set(lock["required_capabilities"]).issubset(manifest.capabilities)
        or not manifest.runtime_env
        or manifest.runtime_env.root != "runtime-env"
    ):
        raise ValueError("产品运行时与版本清单不匹配，请重新解压完整产品包。")
    tree = manifest.path.parent.parent / "env" / manifest.runtime_env.root
    if (tree / "python.exe").resolve() != Path(sys.executable).resolve() or (
        version("vibeocr-next-runtime") != manifest.backend_version
        or ".".join(map(str, sys.version_info[:3])) != manifest.python.version
    ):
        raise ValueError("产品解释器或 Runtime 版本不匹配，请重新解压完整产品包。")
    layout = os.environ.get("VIBEOCR_LAYOUT_MANIFEST") or None
    paths = resolve_runtime_store(
        product,
        manifest_sha256=manifest.sha256,
        layout_manifest=layout,
        product_id="next" if layout else None,
    )
    state = paths.state_root
    store = RuntimeSettingsStore(state / "recognition" / "supervisor-settings.json")
    if store.path.resolve() != store.path:
        raise ValueError("产品设置目录不可包含重解析点。")
    try:
        if store.path.exists():
            snapshot = store.load(SettingsSnapshot())
        else:
            snapshot = _legacy_snapshot(state, product)
            store.replace(snapshot)
    except RuntimeSettingsStoreError as exc:
        if exc.recovered is None:
            raise
        snapshot = exc.recovered
        store.preserve_startup_error(exc)
    cache = (
        state
        if snapshot.extra.get("legacy_cache_at_state_root") is True
        else _cache_root(state, snapshot.extra.get("legacy_cache_profile"))
    )
    device = snapshot.recognition_device
    if device not in {"cpu", "directml", "auto"}:
        raise ValueError("产品计算设备配置无效，请检查识别设置。")
    os.environ.update(
        {
            "VIBEOCR_PRODUCT_RUNTIME": "1",
            "VIBEOCR_RECOGNITION_DEVICE": device,
            "VIBEOCR_RUNTIME_ROOT": str(tree),
            "VIBEOCR_RUNTIME_STATE_ROOT": str(state),
            "VIBEOCR_RUNTIME_ACCELERATOR": "cpu",
            "VIBEOCR_SHARED_MODEL_CACHE": str(state / "model-cache"),
            "HF_HOME": str(cache / "cache" / "huggingface"),
            "MODELSCOPE_CACHE": str(cache / "cache" / "modelscope"),
            "MODELSCOPE_HOME": str(cache / "cache" / "modelscope-home"),
            "MODELSCOPE_CREDENTIALS_PATH": str(cache / "cache" / "modelscope-home"),
            "PADDLE_PDX_CACHE_HOME": str(cache / "cache" / "paddlex"),
            "PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK": "True",
            "MINERU_HOME": str(cache / "mineru4"),
            "TEMP": str(cache / "temp"),
            "TMP": str(cache / "temp"),
        }
    )
    (cache / "temp").mkdir(parents=True, exist_ok=True)
    seed_bundled_paddle_onnx_models(manifest, state / "model-cache", lambda: None)
    return store
