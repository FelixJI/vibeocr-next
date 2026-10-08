"""Verified provider downloads shared by private, independently writable model views.

Checksums come only from the provider's repository listing. They detect damaged
model downloads; they are never used as an environment or local cache identity.
A frozen listing is not silently updated when a provider's branch moves.
"""

from __future__ import annotations

import argparse
import hashlib
import inspect
import json
import logging
import os
import re
import shutil
import sys
import threading
from collections.abc import Iterator, Sequence
from contextlib import contextmanager
from contextvars import ContextVar
from dataclasses import asdict, dataclass
from importlib.metadata import version
from pathlib import Path, PurePosixPath
from uuid import uuid4

from vibeocr.runtime.environments.runtime_lock import RuntimeStoreLock
from vibeocr.runtime.environments.runtime_maintenance import _atomic_json

_logger = logging.getLogger(__name__)
_PADDLE_CONSTRUCTION_LOCK = threading.RLock()
_SHARED_ENV = "VIBEOCR_SHARED_MODEL_CACHE"
_WINDOWS = sys.platform == "win32"
_PADDLE_LOCAL_ONLY: ContextVar[bool] = ContextVar("paddle_local_only", default=False)


class ModelCacheError(RuntimeError):
    """A model could not be prepared without weakening its download contract."""


class ModelMetadataUnavailable(ModelCacheError):
    """No authority listing was obtained; legacy private reads remain possible."""


class LocalModelsNotPrepared(ModelCacheError):
    """Explicit local-only requests cannot prepare, repair or download models."""


@dataclass(frozen=True)
class ModelAsset:
    namespace: str
    source: str
    repo_id: str
    local_name: str = ""
    required_paths: tuple[str, ...] = ("",)


@dataclass(frozen=True)
class PreparedModels:
    root: Path
    reused_bytes: int
    downloaded_bytes: int | None


def _relative(value: str, *, empty: bool = False) -> str:
    if value == "" and empty:
        return value
    if not isinstance(value, str):
        raise ModelCacheError("Provider model path must be text")
    path = PurePosixPath(value)
    if (
        not value
        or "\\" in value
        or any(char in value for char in '<>:"|?*')
        or any(ord(char) < 32 for char in value)
        or any(
            part.split(".")[0].upper() in {"CON", "PRN", "AUX", "NUL"}
            or re.fullmatch(r"(?:COM|LPT)[1-9]", part.split(".")[0], re.IGNORECASE)
            for part in value.split("/")
        )
        or path.is_absolute()
        or any(part in {".", ".."} for part in value.split("/"))
        or any(not part or part.endswith((".", " ")) for part in value.split("/"))
        or str(path) != value
    ):
        raise ModelCacheError(f"Invalid provider model path: {value!r}")
    return value


def _asset_root(shared: Path, asset: ModelAsset) -> Path:
    if asset.source not in {"huggingface", "modelscope"}:
        raise ModelCacheError(f"Unsupported shared model source: {asset.source}")
    for part in (asset.namespace, *asset.repo_id.split("/")):
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]*", part):
            raise ModelCacheError("Invalid model namespace or repository")
    if len(asset.repo_id.split("/")) != 2:
        raise ModelCacheError("Provider repository must have an owner and name")
    return shared / asset.namespace / asset.source / asset.repo_id


def _valid_file(path: Path, entry: dict) -> bool:
    if not path.is_file() or path.stat().st_size != entry["size"]:
        return False
    with path.open("rb") as stream:
        if entry["algorithm"] == "git-sha1":
            digest = hashlib.sha1(usedforsecurity=False)
            digest.update(f"blob {entry['size']}\0".encode())
            while chunk := stream.read(1024 * 1024):
                digest.update(chunk)
        else:
            digest = hashlib.file_digest(stream, "sha256")
    return digest.hexdigest() == entry["checksum"]


def _validate_manifest(value: object, asset: ModelAsset) -> dict:
    if not isinstance(value, dict) or (
        value.get("source") != asset.source
        or value.get("repo_id") != asset.repo_id
        or value.get("namespace") != asset.namespace
        or not isinstance(value.get("revision"), str)
        or not value["revision"]
        or not isinstance(value.get("files"), list)
        or not value["files"]
    ):
        raise ModelCacheError("Provider model listing is missing or invalid")
    names: set[str] = set()
    for entry in value["files"]:
        if not isinstance(entry, dict):
            raise ModelCacheError("Invalid provider model file entry")
        name = _relative(entry.get("path", ""))
        algorithm = entry.get("algorithm")
        length = 40 if algorithm == "git-sha1" else 64
        if (
            name.casefold() in names
            or type(entry.get("size")) is not int
            or entry["size"] < 0
            or algorithm not in {"git-sha1", "sha256"}
            or not isinstance(entry.get("checksum"), str)
            or not re.fullmatch(rf"[0-9a-f]{{{length}}}", entry["checksum"])
        ):
            raise ModelCacheError("Provider supplied no usable model checksum")
        names.add(name.casefold())
    return value


def _hf_options(asset: ModelAsset) -> dict[str, str]:
    if asset.namespace == "paddlex-3.7.2":
        from paddlex.utils.flags import HUGGING_FACE_ENDPOINT

        # Preserve PaddleX's native endpoint for both metadata and payload.
        # Mirrors select transport, never model identity.
        return {"endpoint": HUGGING_FACE_ENDPOINT}
    return {}


def _remote_manifest(asset: ModelAsset) -> dict:
    if asset.source == "huggingface":
        from huggingface_hub import HfApi

        api = HfApi(**_hf_options(asset))
        revision = api.repo_info(asset.repo_id).sha
        entries = []
        for entry in api.list_repo_tree(
            asset.repo_id, revision=revision, recursive=True
        ):
            if not hasattr(entry, "blob_id"):
                continue
            lfs = entry.lfs
            entries.append(
                {
                    "path": entry.path,
                    "size": entry.size,
                    "algorithm": "sha256" if lfs else "git-sha1",
                    "checksum": lfs.sha256 if lfs else entry.blob_id,
                }
            )
    else:
        from modelscope_hub import HubApi

        # ModelScope does not promise that its branch name is an immutable SHA.
        # Freeze the upstream file checksums; changed content must fail closed.
        revision = "master"
        entries = [
            {
                "path": entry.path,
                "size": entry.size,
                "algorithm": "sha256",
                "checksum": entry.sha256,
            }
            for entry in HubApi().list_repo_files(
                asset.repo_id, "model", revision=revision, recursive=True
            )
            if not entry.is_dir
        ]
    return _validate_manifest(
        {
            "namespace": asset.namespace,
            "source": asset.source,
            "repo_id": asset.repo_id,
            "revision": revision,
            "files": entries,
        },
        asset,
    )


def _legacy_hf_manifest(asset: ModelAsset, legacy_roots: Sequence[Path]) -> dict | None:
    if asset.source != "huggingface":
        return None
    for legacy in legacy_roots:
        local = legacy / asset.local_name
        trees = local / ".cache" / "huggingface" / "trees"
        for tree in sorted(trees.glob("*.json")):
            if not re.fullmatch(r"[0-9a-f]{40}", tree.stem):
                continue
            try:
                raw = json.loads(tree.read_text(encoding="utf-8"))
                if raw.get("format_version") != 1:
                    continue
                files = [
                    {
                        "path": name,
                        "size": entry["size"],
                        "algorithm": "sha256"
                        if entry.get("lfs_sha256")
                        else "git-sha1",
                        "checksum": entry.get("lfs_sha256") or entry["blob_id"],
                    }
                    for name, entry in raw["files"].items()
                ]
                manifest = _validate_manifest(
                    {
                        "namespace": asset.namespace,
                        "source": asset.source,
                        "repo_id": asset.repo_id,
                        "revision": tree.stem,
                        "files": files,
                    },
                    asset,
                )
                if all(
                    _valid_file(local / entry["path"], entry)
                    for entry in _selected_files(manifest, asset)
                ):
                    return manifest
            except (ValueError, KeyError, TypeError, ModelCacheError):
                continue
    return None


def _manifest(root: Path, asset: ModelAsset, legacy_roots: Sequence[Path]) -> dict:
    receipt = root / "provider-files.json"
    if receipt.exists():
        try:
            value = json.loads(receipt.read_text(encoding="utf-8"))
        except (ValueError, OSError) as exc:
            raise ModelCacheError("Saved provider listing is unreadable") from exc
        return _validate_manifest(value, asset)
    try:
        value = _legacy_hf_manifest(asset, legacy_roots) or _remote_manifest(asset)
    except Exception as exc:
        raise ModelMetadataUnavailable(
            f"Provider model listing unavailable: {asset.source}/{asset.repo_id}"
        ) from exc
    _atomic_json(receipt, value)
    return value


def _selected_files(manifest: dict, asset: ModelAsset) -> list[dict]:
    selected: dict[str, dict] = {}
    for raw in asset.required_paths:
        prefix = _relative(raw, empty=True)
        matching = [
            entry
            for entry in manifest["files"]
            if not prefix
            or entry["path"] == prefix
            or entry["path"].startswith(prefix + "/")
        ]
        if not matching:
            raise ModelCacheError(f"Provider listing misses required model path: {raw}")
        selected.update((entry["path"], entry) for entry in matching)
    if not selected:
        raise ModelCacheError("No model files were selected")
    return list(selected.values())


def _download(root: Path, asset: ModelAsset, manifest: dict, entry: dict) -> Path:
    cache = root / "downloads"
    local = root / "payload"
    if asset.source == "huggingface":
        from huggingface_hub import hf_hub_download

        return Path(
            hf_hub_download(
                asset.repo_id,
                entry["path"],
                revision=manifest["revision"],
                cache_dir=cache,
                local_dir=local,
                force_download=(local / entry["path"]).is_file(),
                **_hf_options(asset),
            )
        )
    from modelscope_hub import HubApi

    return HubApi().download_file(
        asset.repo_id,
        "model",
        entry["path"],
        revision=manifest["revision"],
        cache_dir=cache,
        local_dir=local,
        expected_sha256=entry["checksum"],
        force=True,
    )


def prepare_models(
    assets: Sequence[ModelAsset],
    private_root: Path,
    *,
    shared_root: Path | None = None,
    legacy_roots: Sequence[Path] = (),
    completion_marker: str | None = None,
) -> PreparedModels:
    """Prepare exactly the requested resources, then publish a private view.

    The provider listing is the authority. Missing/corrupt objects alone go to
    the native downloader. Old private views and incomplete candidates survive
    failure; no model/environment garbage collection runs here.
    """
    shared = shared_root or Path(os.environ[_SHARED_ENV])
    private_root = private_root.resolve()
    binding = private_root / "current.json"
    request = {
        "assets": [
            {**asdict(asset), "required_paths": list(asset.required_paths)}
            for asset in assets
        ],
        "completion_marker": completion_marker,
    }
    if completion_marker is not None:
        _relative(completion_marker)
    # ponytail: serialize model preparation; per-repo locks only if contention matters.
    with RuntimeStoreLock(shared / "prepare.lock", timeout=1800):
        files: list[tuple[Path, str, dict]] = []
        reused = 0
        downloaded = False
        for asset in assets:
            local_name = _relative(asset.local_name, empty=True)
            root = _asset_root(shared, asset)
            manifest = _manifest(root, asset, legacy_roots)
            for entry in _selected_files(manifest, asset):
                relative = str(PurePosixPath(local_name) / entry["path"])
                payload = root / "payload" / entry["path"]
                if not _valid_file(payload, entry):
                    imported = next(
                        (
                            old / relative
                            for old in legacy_roots
                            if _valid_file(old / relative, entry)
                        ),
                        None,
                    )
                    if imported is None:
                        imported = _download(root, asset, manifest, entry)
                        downloaded = True
                        if not _valid_file(imported, entry):
                            raise ModelCacheError(
                                f"Downloaded model does not match frozen provider listing: {entry['path']}"
                            )
                    else:
                        reused += entry["size"]
                    if imported != payload:
                        payload.parent.mkdir(parents=True, exist_ok=True)
                        temporary = payload.with_name(
                            f".{payload.name}.{uuid4().hex}.tmp"
                        )
                        shutil.copy2(imported, temporary)
                        os.replace(temporary, payload)
                else:
                    reused += entry["size"]
                files.append((payload, relative, entry))
        if not files or len({relative for _, relative, _ in files}) != len(files):
            raise ModelCacheError("Empty or overlapping model selection")
        try:
            current = json.loads(binding.read_text(encoding="utf-8"))
        except (FileNotFoundError, ValueError):
            current = {}
        if not isinstance(current, dict):
            raise ModelCacheError("Invalid private model binding")

        def markers(candidate: Path) -> list[Path]:
            if completion_marker is None:
                return []
            return [
                candidate / asset.local_name / relative / completion_marker
                for asset in assets
                for relative in asset.required_paths
                if (candidate / asset.local_name / relative).is_dir()
            ]

        generation = current.get("generation")
        if (
            isinstance(generation, str)
            and re.fullmatch(r"[0-9a-f]{32}", generation)
            and current.get("request") == request
        ):
            candidate = private_root / "revisions" / generation
            if all(
                _valid_file(candidate / relative, entry) for _, relative, entry in files
            ) and all(marker.is_file() for marker in markers(candidate)):
                return PreparedModels(candidate, reused, None if downloaded else 0)
        generation = uuid4().hex
        candidate = private_root / "revisions" / generation
        candidate.mkdir(parents=True)
        for payload, relative, entry in files:
            destination = candidate / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(payload, destination)
        for marker in markers(candidate):
            marker.touch()
        _atomic_json(binding, {"generation": generation, "request": request})
        _logger.info(
            "Model preparation: reused_bytes=%d downloaded_bytes=%s private_root=%s",
            reused,
            "unknown" if downloaded else 0,
            candidate,
        )
        return PreparedModels(candidate, reused, None if downloaded else 0)


def _windows_short_path(path: str) -> str:
    """Return the 8.3 alias Windows already keeps for one existing path."""
    import ctypes

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    get_short_path_name = kernel32.GetShortPathNameW
    get_short_path_name.argtypes = (ctypes.c_wchar_p, ctypes.c_wchar_p, ctypes.c_uint32)
    get_short_path_name.restype = ctypes.c_uint32
    needed = get_short_path_name(path, None, 0)
    if needed == 0:
        raise OSError(ctypes.get_last_error(), "GetShortPathNameW failed", path)
    buffer = ctypes.create_unicode_buffer(needed)
    written = get_short_path_name(path, buffer, needed)
    if written == 0 or written >= needed:
        raise OSError(ctypes.get_last_error(), "GetShortPathNameW failed", path)
    return buffer.value


def _paddle_native_model_dir(root: Path) -> Path:
    """Alias one non-ASCII Windows model dir to its existing 8.3 ASCII name.

    Paddle 3.3.1's native layer cannot read non-ASCII model paths. The alias
    names the same already-prepared directory (nothing is copied, moved or
    re-downloaded) and is never persisted; ASCII paths and other platforms
    keep existing behavior, and a missing alias fails closed.
    """
    text = str(root)
    if not _WINDOWS or text.isascii():
        return root
    try:
        alias = _windows_short_path(text)
    except OSError as exc:
        raise ModelCacheError(
            f"Windows short path unavailable for Paddle model directory: {text}"
        ) from exc
    if not alias.isascii():
        raise ModelCacheError(
            "Paddle cannot read the model directory and Windows provided no "
            "ASCII short alias for it; use a local volume with 8.3 short names "
            f"enabled or an ASCII cache path instead: {text}"
        )
    return Path(alias)


def _local_paddle_model(asset: ModelAsset, private: Path) -> Path:
    try:
        shared = Path(os.environ[_SHARED_ENV])
        manifest = _validate_manifest(
            json.loads(
                (_asset_root(shared, asset) / "provider-files.json").read_text(
                    encoding="utf-8"
                )
            ),
            asset,
        )
        current = json.loads((private / "current.json").read_text(encoding="utf-8"))
        expected = {
            "assets": [{**asdict(asset), "required_paths": list(asset.required_paths)}],
            "completion_marker": None,
        }
        if not isinstance(current, dict) or current.get("request") != expected:
            raise ModelCacheError("Private model binding does not match request")
        generation = current.get("generation")
        if not isinstance(generation, str) or not re.fullmatch(
            r"[0-9a-f]{32}", generation
        ):
            raise ModelCacheError("Invalid private model generation")
        candidate = private / "revisions" / generation
        if not all(
            _valid_file(candidate / entry["path"], entry)
            for entry in _selected_files(manifest, asset)
        ):
            raise ModelCacheError("Prepared model is missing or damaged")
        return candidate
    except (OSError, ValueError, KeyError, TypeError, ModelCacheError) as exc:
        raise LocalModelsNotPrepared(
            "Local Paddle models are not prepared; explicitly prepare them with ordinary Paddle OCR"
        ) from exc


@contextmanager
def paddle_model_cache(*, local_only: bool = False) -> Iterator[None]:
    # A nested construction must not weaken the calling recognition request.
    token = _PADDLE_LOCAL_ONLY.set(local_only or _PADDLE_LOCAL_ONLY.get())
    try:
        with _paddle_model_cache():
            yield
    finally:
        _PADDLE_LOCAL_ONLY.reset(token)


@contextmanager
def _paddle_model_cache() -> Iterator[None]:
    """Use PaddleX's actual submodel selection during one serialized construction."""
    with _PADDLE_CONSTRUCTION_LOCK:
        local_only = _PADDLE_LOCAL_ONLY.get()
        source = os.environ.get("PADDLE_PDX_MODEL_SOURCE", "huggingface")
        if not os.environ.get(_SHARED_ENV):
            if local_only:
                raise LocalModelsNotPrepared("Shared model cache is unavailable")
            yield
            return
        try:
            supported = (
                source in {"huggingface", "modelscope"}
                and version("paddlex") == "3.7.2"
            )
        except Exception as exc:
            if local_only:
                raise LocalModelsNotPrepared(
                    "Local-only Paddle resolver version is unavailable"
                ) from exc
            raise
        if not supported:
            if local_only:
                raise LocalModelsNotPrepared(
                    "Local-only Paddle resolver version or source is unsupported"
                )
            _logger.info(
                "Paddle shared model cache unsupported; using private native cache"
            )
            yield
            return
        from paddlex.inference.utils.official_models import official_models

        original = getattr(official_models, "_get_model_local_path", None)
        if not callable(original) or len(inspect.signature(original).parameters) != 1:
            if local_only:
                raise LocalModelsNotPrepared(
                    "Local-only Paddle resolver shape is unsupported"
                )
            _logger.warning(
                "Unsupported PaddleX resolver shape; using private native cache"
            )
            yield
            return
        if local_only and not os.environ.get("PADDLE_PDX_CACHE_HOME"):
            raise LocalModelsNotPrepared("Private model cache is unavailable")
        private = Path(os.environ["PADDLE_PDX_CACHE_HOME"])

        def resolve(model_names: str | tuple[str, ...]) -> Path:
            names = (model_names,) if isinstance(model_names, str) else model_names
            if not names or not all(isinstance(name, str) for name in names):
                raise ModelCacheError("Unsupported PaddleX model resolver arguments")
            for index, requested in enumerate(names):
                name = (
                    requested.replace("-0.9B", "")
                    if "PaddleOCR-VL" in requested
                    else requested
                )
                old = private / "official_models" / name
                asset = ModelAsset("paddlex-3.7.2", source, f"PaddlePaddle/{name}")
                if local_only:
                    try:
                        root = _local_paddle_model(
                            asset, private / "prepared-models" / name
                        )
                    except LocalModelsNotPrepared:
                        if index + 1 < len(names):
                            continue
                        raise
                    vl = root / "PaddleOCR-VL-0.9B"
                    target = vl if name == "PaddleOCR-VL" and vl.is_dir() else root
                    return _paddle_native_model_dir(target)
                try:
                    prepared = prepare_models(
                        [asset], private / "prepared-models" / name, legacy_roots=(old,)
                    )
                    root = prepared.root
                except ModelMetadataUnavailable as exc:
                    if old.is_dir():
                        _logger.warning(
                            "Using unverified legacy private Paddle model: %s", old
                        )
                        root = old
                    else:
                        cause = exc.__cause__
                        response = getattr(cause, "response", None)
                        missing = getattr(response, "status_code", None) == 404
                        if source == "modelscope":
                            from modelscope_hub.errors import NotExistError

                            missing = isinstance(cause, NotExistError)
                        if missing and index + 1 < len(names):
                            continue
                        raise
                vl = root / "PaddleOCR-VL-0.9B"
                target = vl if name == "PaddleOCR-VL" and vl.is_dir() else root
                return _paddle_native_model_dir(target)
            raise ModelCacheError("Paddle did not select an available model")

        official_models._get_model_local_path = resolve
        try:
            yield
        finally:
            official_models._get_model_local_path = original


def prepare_mineru(tier: str) -> Path:
    """Executed in the target MinerU interpreter, never in a health probe."""
    from mineru.config import config
    from mineru.model.download import (
        MODEL_COMPLETE_MARKER,
        resolve_model_source,
        verify_model_repo,
    )
    from mineru.model.registry import model_repos_for_tier

    source = resolve_model_source(allow_auto=True)
    if source == "local":
        return Path(config.model.base_dir)
    repos = model_repos_for_tier(tier)
    assets = [
        ModelAsset(
            f"mineru-{version('mineru')}",
            source,
            repo.repos[source],
            repo.local_name,
            tuple(path.relative_path for path in repo.required_paths())
            if repo.download_mode == "required_paths"
            else ("",),
        )
        for repo in repos
    ]
    try:
        prepared = prepare_models(
            assets,
            Path(os.environ["MINERU_HOME"]) / "prepared-models" / tier,
            legacy_roots=(Path(config.model.base_dir),),
            completion_marker=MODEL_COMPLETE_MARKER,
        )
    except ModelMetadataUnavailable:
        if not all(verify_model_repo(repo).ready for repo in repos):
            raise
        _logger.warning(
            "Using unverified legacy private MinerU models: %s", config.model.base_dir
        )
        return Path(config.model.base_dir)
    return prepared.root


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("tier", choices=("basic", "standard"))
    args = parser.parse_args()
    root = prepare_mineru(args.tier)
    print(json.dumps({"model_root": str(root)}))


if __name__ == "__main__":
    main()
