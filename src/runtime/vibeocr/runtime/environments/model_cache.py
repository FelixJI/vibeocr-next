"""Verified provider downloads shared by private, independently writable model views.

Checksums come only from the provider's repository listing. They detect damaged
model downloads; they are never used as an environment or local cache identity.
A frozen listing is not silently updated when a provider's branch moves.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import logging
import os
import re
import shutil
import zipfile
from collections.abc import Callable, Sequence
from dataclasses import asdict, dataclass
from importlib.metadata import version
from importlib.resources import files
from pathlib import Path, PurePosixPath
from uuid import uuid4

from vibeocr.runtime.environments.runtime_lock import RuntimeStoreLock
from vibeocr.runtime.environments.runtime_maintenance import _atomic_json

_logger = logging.getLogger(__name__)
_SHARED_ENV = "VIBEOCR_SHARED_MODEL_CACHE"

# 随产品 model pack 携带的默认 Paddle ONNX 模型：small 检测/识别（默认
# preset）与默认文档方向分类。tiny/medium/表格/结构大模型不随包，
# 按需走冻结清单下载。
BUNDLED_PADDLE_ONNX_MODELS = (
    "PP-OCRv6_small_det",
    "PP-OCRv6_small_rec",
    "PP-LCNet_x1_0_doc_ori",
)
_PADDLE_ONNX_NAMESPACE = "paddlex-onnx-3.7.2"


def _paddle_onnx_catalog() -> dict:
    return json.loads(
        files("vibeocr.runtime.environments")
        .joinpath("paddle_onnx_models.json")
        .read_text(encoding="utf-8")
    )["models"]


def seed_bundled_paddle_onnx_models(
    manifest: object,
    shared_root: Path,
    check_cancelled: Callable[[], None],
    *,
    models: Sequence[str] = BUNDLED_PADDLE_ONNX_MODELS,
) -> None:
    """把随包 model pack 的默认模型布置进共享资产缓存的可消费位置。

    布置目标即 ``prepare_models`` 的共享根（``<shared>/paddlex-onnx-3.7.2/
    <source>/<repo>/payload/…``），source/revision 身份与冻结清单一致：
    已存在且校验通过的文件不重写；包内字节与冻结清单不匹配时 fail
    closed，不静默降级。CPU/CUDA/DirectML 环境共用同一共享缓存。
    """
    from vibeocr.runtime.environments.runtime_manifest import ManifestError

    model_pack = getattr(manifest, "paddle_model_pack", None)
    if model_pack is None:
        return
    archive = Path(model_pack.archive_path)
    if not archive.is_file():
        raise ModelCacheError("bundled Paddle model pack is missing")
    catalog = _paddle_onnx_catalog()
    unknown = sorted(set(models).difference(catalog))
    if unknown:
        raise ModelCacheError(
            f"bundled Paddle model is not in the frozen catalog: {unknown}"
        )
    expected_members: dict[str, dict] = {}
    for name in models:
        for file_entry in catalog[name]["sources"]["modelscope"]["files"]:
            expected_members[f"{name}/{file_entry['path']}"] = file_entry
    shared_root = shared_root.resolve()
    with RuntimeStoreLock(shared_root / "prepare.lock", timeout=1800):
        try:
            with zipfile.ZipFile(archive) as bundle:
                names = set(bundle.namelist())
                unexpected = sorted(names.difference(expected_members))
                if unexpected or len(names) != len(expected_members):
                    raise ModelCacheError(
                        "bundled Paddle model pack contains unexpected members"
                    )
                for member, entry in expected_members.items():
                    check_cancelled()
                    payload_bytes = bundle.read(member)
                    if (
                        len(payload_bytes) != entry["size"]
                        or hashlib.sha256(payload_bytes).hexdigest()
                        != entry["checksum"]
                    ):
                        raise ModelCacheError(
                            f"bundled Paddle model does not match the frozen listing: {member}"
                        )
                    name = member.split("/", 1)[0]
                    for source in ("modelscope", "huggingface"):
                        source_entry = next(
                            (
                                item
                                for item in catalog[name]["sources"][source]["files"]
                                if item["path"] == entry["path"]
                            ),
                            None,
                        )
                        if source_entry is None:
                            continue
                        asset = ModelAsset(
                            _PADDLE_ONNX_NAMESPACE,
                            source,
                            catalog[name]["repo_id"],
                            name,
                        )
                        payload = (
                            _asset_root(shared_root, asset)
                            / "payload"
                            / source_entry["path"]
                        )
                        if _valid_file(payload, source_entry):
                            continue
                        payload.parent.mkdir(parents=True, exist_ok=True)
                        temporary = payload.with_name(
                            f".{payload.name}.{uuid4().hex}.tmp"
                        )
                        try:
                            temporary.write_bytes(payload_bytes)
                            os.replace(temporary, payload)
                        finally:
                            temporary.unlink(missing_ok=True)
        except zipfile.BadZipFile as exc:
            raise ModelCacheError("bundled Paddle model pack is invalid") from exc
        except ManifestError as exc:
            raise ModelCacheError(str(exc)) from exc


class ModelCacheError(RuntimeError):
    """A model could not be prepared without weakening its download contract."""


class ModelMetadataUnavailable(ModelCacheError):
    """No authority listing was obtained; legacy private reads remain possible."""


class LocalModelsNotPrepared(ModelCacheError):
    """Explicit local-only requests cannot prepare, repair or download models."""


@dataclass(frozen=True)
class ModelFile:
    path: str
    size: int
    algorithm: str
    checksum: str
    revision: str = ""


@dataclass(frozen=True)
class ModelAsset:
    namespace: str
    source: str
    repo_id: str
    local_name: str = ""
    required_paths: tuple[str, ...] = ("",)
    revision: str = ""
    files: tuple[ModelFile, ...] = ()


@dataclass(frozen=True)
class PreparedModels:
    root: Path
    reused_bytes: int
    downloaded_bytes: int | None


def _asset_request(asset: ModelAsset) -> dict:
    request = {
        "namespace": asset.namespace,
        "source": asset.source,
        "repo_id": asset.repo_id,
        "local_name": asset.local_name,
        "required_paths": list(asset.required_paths),
    }
    if asset.files:
        request.update(
            revision=asset.revision, files=[asdict(entry) for entry in asset.files]
        )
    return request


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


def _remote_manifest(asset: ModelAsset) -> dict:
    if asset.source == "huggingface":
        from huggingface_hub import HfApi

        api = HfApi()
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
    if asset.files:
        value = _validate_manifest(
            {
                "namespace": asset.namespace,
                "source": asset.source,
                "repo_id": asset.repo_id,
                "revision": asset.revision,
                "files": [asdict(entry) for entry in asset.files],
            },
            asset,
        )
        if not receipt.exists():
            _atomic_json(receipt, value)
        return value
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
            )
        )
    from modelscope_hub import HubApi

    return HubApi().download_file(
        asset.repo_id,
        "model",
        entry["path"],
        revision=entry.get("revision") or manifest["revision"],
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
    local_only: bool = False,
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
        "assets": [_asset_request(asset) for asset in assets],
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
                        if local_only:
                            raise LocalModelsNotPrepared(
                                f"Local model is missing or damaged: {asset.repo_id}/{entry['path']}"
                            )
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


def paddle_text_models_prepared(cache_home: Path) -> bool:
    """Inspect committed local views without downloading, loading sessions or rehashing.

    Preparation already validates the frozen provider checksums before publishing
    current.json. Observation checks that exact asset metadata and file sizes remain.
    """
    catalog = _paddle_onnx_catalog()
    for preset in ("tiny", "small", "medium"):
        for source in ("huggingface", "modelscope"):
            private = cache_home / "onnx" / preset / source
            try:
                current = json.loads(
                    (private / "current.json").read_text(encoding="utf-8")
                )
                generation = current["generation"]
                if not isinstance(generation, str) or not re.fullmatch(
                    r"[0-9a-f]{32}", generation
                ):
                    continue
                assets = current["request"]["assets"]
                names = {asset["local_name"] for asset in assets}
                if not any(
                    name.startswith("PP-OCRv6_") and name.endswith("_det")
                    for name in names
                ) or not any(
                    name.startswith("PP-OCRv6_") and name.endswith("_rec")
                    for name in names
                ):
                    continue
                candidate = private / "revisions" / generation
                valid = True
                for asset in assets:
                    name = asset["local_name"]
                    listing = catalog[name]["sources"][source]
                    expected = [
                        asdict(ModelFile(**entry)) for entry in listing["files"]
                    ]
                    if (
                        asset["namespace"] != "paddlex-onnx-3.7.2"
                        or asset["source"] != source
                        or asset["repo_id"] != catalog[name]["repo_id"]
                        or asset["revision"] != listing["revision"]
                        or asset["files"] != expected
                    ):
                        valid = False
                        break
                    for entry in expected:
                        path = candidate / _relative(name) / _relative(entry["path"])
                        if not path.is_file() or path.stat().st_size != entry["size"]:
                            valid = False
                            break
                if valid:
                    return True
            except (OSError, ValueError, KeyError, TypeError, ModelCacheError):
                continue
    return False


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
