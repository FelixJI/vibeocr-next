"""Build and verify the Next single-product runtime manifest.

The manifest binds the one Next Runtime wheel and Windows runtime
profiles by raw-byte SHA-256.  Release automation should publish only the
files copied into ``--output-dir`` plus the generated runtime manifest.
"""

from __future__ import annotations

import argparse
import json
import re
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path, PurePosixPath

REPO_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO_ROOT / "src" / "runtime"))
sys.path.insert(0, str(REPO_ROOT / "contracts" / "runtime" / "python"))

from vibeocr.runtime.environments.runtime_manifest import (  # noqa: E402
    INSTALLER_EXECUTABLE_NAME,
    PROFILE_NAMES,
    default_profile_components,
    load_runtime_manifest,
    locked_distribution_version,
    runtime_component_binding,
    sha256_file,
    validate_requirements_lock,
)

_STABLE_SEMVER = re.compile(r"^\d+\.\d+\.\d+$")
_FULL_SHA = re.compile(r"^[0-9a-f]{40}$")


def _extract_product_runtime_code(wheel: Path, output_dir: Path, version: str) -> None:
    """Stage the already bound wheel's Python code once in the product image."""
    target_root = output_dir / "runtime-code"
    target_root.mkdir()
    dist_info = f"vibeocr_next_runtime-{version}.dist-info/"
    with zipfile.ZipFile(wheel) as archive:
        selected: set[str] = set()
        for entry in archive.infolist():
            name = entry.filename
            relative = PurePosixPath(name)
            if (
                relative.is_absolute()
                or ".." in relative.parts
                or "\\" in name
                or ":" in name
            ):
                raise ValueError("Runtime wheel contains an unsafe path")
            if not name.startswith(
                ("vibeocr/runtime/", "vibeocr/runtime_contracts/", dist_info)
            ):
                continue
            if entry.is_dir():
                continue
            selected.add(name)
            destination = target_root.joinpath(*relative.parts)
            destination.parent.mkdir(parents=True, exist_ok=True)
            with archive.open(entry) as source, destination.open("wb") as output:
                shutil.copyfileobj(source, output)
    if (
        not {
            "vibeocr/runtime/__init__.py",
            "vibeocr/runtime/host/main.py",
            "vibeocr/runtime/recognition/paddle_worker.py",
            "vibeocr/runtime/documents/pdf_backend_process.py",
            "vibeocr/runtime/environments/dependency_profiles.json",
            "vibeocr/runtime_contracts/__init__.py",
            f"{dist_info}METADATA",
        }
        <= selected
    ):
        raise ValueError("Runtime wheel lacks product entry modules or metadata")


DEFAULT_CAPABILITIES = (
    "export.document.v1",
    "ocr.recognition.v2",
    "pdf.edit.v2",
    "qrcode.v2",
    "runtime.settings.v2",
    "runtime.capability-metadata.v1",
    "runtime.events.sse.v1",
    "runtime.events.ndjson.v1",
    "task.progress.v1",
    "ocr.engine-selection.v1",
    "ocr.recognition-modes.v1",
    "ocr.mineru-config.v1",
    "ocr.mineru-remote-api.v1",
    "runtime.download-sources.v1",
    "runtime.component-selection.v1",
)


def _component_version_package(profile: str, component_id: str) -> str | None:
    return runtime_component_binding(profile, component_id).distribution


def _git_sha() -> str:
    result = subprocess.run(
        ["git", "rev-parse", "HEAD"],
        cwd=REPO_ROOT,
        check=True,
        capture_output=True,
        text=True,
    )
    return result.stdout.strip()


def _copy_exact(
    source: Path,
    output_dir: Path,
    *,
    destination_name: str | None = None,
) -> Path:
    source = source.resolve(strict=True)
    target = output_dir / (destination_name or source.name)
    if source != target:
        shutil.copyfile(source, target)
    return target


def _canonical_json(value: object) -> bytes:
    return (
        json.dumps(
            value,
            ensure_ascii=False,
            indent=2,
            sort_keys=True,
        )
        + "\n"
    ).encode("utf-8")


def _profile_components(path: Path, profile: str) -> list[dict[str, str]]:
    result: list[dict[str, str]] = []
    for descriptor in default_profile_components(profile):
        version = locked_distribution_version(
            path, _component_version_package(profile, descriptor.component_id)
        )
        result.append(
            {
                **descriptor.to_payload(),
                **({"version": version} if version is not None else {}),
            }
        )
    return result


def _independent_scopes(
    profile: str,
    locks: dict[str, Path],
    gpu_lock: Path,
    base_packs: list[Path],
) -> list[dict[str, object]]:
    """历史 cpu/cu126 档位的独立安装域生成已随统一推理闭包退役。

    MinerU 现随 managed paddleocr-ort-directml 环境携带，base 是唯一
    profile 且没有独立安装域；函数保留空实现以维持既有调用形状。
    """
    del profile, locks, gpu_lock, base_packs
    return []


def build_runtime_manifest(
    *,
    runtime_wheel: Path | None,
    base_lock: Path,
    python_archive: Path | None,
    python_version: str,
    python_source_url: str,
    installer_executable: Path,
    version: str,
    source_commit: str,
    build_workflow: str,
    output_dir: Path,
    capabilities: tuple[str, ...] = DEFAULT_CAPABILITIES,
    runtime_packs: dict[str, list[Path]] | None = None,
    paddle_onnx_locks: dict[str, Path] | None = None,
    paddle_onnx_packs: dict[str, list[Path]] | None = None,
    paddle_model_pack: Path | None = None,
    runtime_env_tree: Path | None = None,
    runtime_env_manifest: Path | None = None,
) -> Path:
    if not _STABLE_SEMVER.fullmatch(version):
        raise ValueError("version must be stable SemVer")
    if not _FULL_SHA.fullmatch(source_commit):
        raise ValueError("source_commit must be a full lowercase Git SHA")
    if not build_workflow.strip():
        raise ValueError("build_workflow is required")
    if not capabilities or any(not item.strip() for item in capabilities):
        raise ValueError("at least one non-empty capability is required")

    prebuilt = runtime_env_tree is not None
    if prebuilt != (runtime_env_manifest is not None):
        raise ValueError(
            "runtime_env_tree and runtime_env_manifest must be provided together"
        )
    if prebuilt and (runtime_wheel is not None or python_archive is not None):
        raise ValueError(
            "the prebuilt runtime env replaces the wheel/python archive payloads"
        )
    if not prebuilt and (runtime_wheel is None or python_archive is None):
        raise ValueError("runtime wheel and python archive are required")
    if runtime_wheel is not None:
        runtime_wheel = runtime_wheel.resolve(strict=True)
        if not runtime_wheel.name.startswith(f"vibeocr_next_runtime-{version}-"):
            raise ValueError("Runtime wheel filename does not match product version")
    installer_executable = installer_executable.resolve(strict=True)
    runtime_env = None

    # 统一推理闭包后仅构建 base profile；推理栈（Paddle ONNX + MinerU）全部
    # 由 paddle_onnx_environments 的 directml 绑定承载。
    profile_sources = {
        "win-x64-base": base_lock.resolve(strict=True),
    }
    for profile in PROFILE_NAMES:
        validate_requirements_lock(profile_sources[profile], profile=profile)
    paddle_onnx_locks = paddle_onnx_locks or {}
    if paddle_onnx_locks and set(paddle_onnx_locks) != {"directml"}:
        raise ValueError(
            "Paddle ONNX locks must bind only the unified directml closure"
        )
    paddle_onnx_packs = paddle_onnx_packs or {}
    if paddle_onnx_packs and set(paddle_onnx_packs) - {"directml"}:
        raise ValueError("only DirectML ships offline runtime packs")
    for device, lock in paddle_onnx_locks.items():
        validate_requirements_lock(lock, profile=f"win-x64-paddle-ort-{device}")

    output_dir.mkdir(parents=True, exist_ok=True)
    if prebuilt:
        tree = runtime_env_tree.resolve(strict=True)
        if not (tree / "python.exe").is_file():
            raise ValueError("runtime env tree has no python.exe at its root")
        copied_env_manifest = _copy_exact(
            runtime_env_manifest.resolve(strict=True),
            output_dir,
            destination_name="runtime-env-manifest.json",
        )
        # 树整目录随 bundle 自包含分发：product_layout 从 backend 复制到
        # 产品的 runtime/env/<root>/，运行时零解包。
        shutil.copytree(tree, output_dir / "runtime-env")
        runtime_env = {
            "root": "runtime-env",
            "manifest": copied_env_manifest.name,
            "manifest_sha256": sha256_file(copied_env_manifest),
        }
    copied_runtime = (
        _copy_exact(runtime_wheel, output_dir) if runtime_wheel is not None else None
    )
    if copied_runtime is not None:
        _extract_product_runtime_code(copied_runtime, output_dir, version)
    copied_python = (
        _copy_exact(python_archive, output_dir) if python_archive is not None else None
    )
    # 冻结安装器只分发一次：产品内 exe 位于 runtime/installer/，manifest
    # 直接绑定其字节摘要（不再随 backend 携带重复的 zip 副本）。
    copied_installer = _copy_exact(
        installer_executable,
        output_dir,
        destination_name=INSTALLER_EXECUTABLE_NAME,
    )
    pack_inputs = runtime_packs or {}
    copied_packs: dict[str, list[Path]] = {
        profile: [_copy_exact(pack, output_dir) for pack in packs]
        for profile, packs in pack_inputs.items()
        if packs
    }
    for profile in copied_packs:
        if profile not in PROFILE_NAMES:
            raise ValueError(f"unknown runtime pack profile: {profile}")
    copied_profiles = {
        profile: _copy_exact(profile_sources[profile], output_dir)
        for profile in PROFILE_NAMES
    }
    copied_paddle_onnx = {
        device: _copy_exact(lock, output_dir)
        for device, lock in paddle_onnx_locks.items()
    }
    copied_paddle_onnx_packs: dict[str, list[Path]] = {
        device: [_copy_exact(pack, output_dir) for pack in packs]
        for device, packs in paddle_onnx_packs.items()
        if packs
    }
    copied_paddle_model_pack = (
        _copy_exact(paddle_model_pack, output_dir)
        if paddle_model_pack is not None
        else None
    )
    manifest = {
        "schema_version": 2,
        **(
            {
                "paddle_onnx_environments": {
                    device: {
                        "lock": lock.name,
                        "sha256": sha256_file(lock),
                        **(
                            {
                                "runtime_pack": [
                                    pack.name
                                    for pack in copied_paddle_onnx_packs[device]
                                ],
                                "runtime_pack_sha256": [
                                    sha256_file(pack)
                                    for pack in copied_paddle_onnx_packs[device]
                                ],
                            }
                            if copied_paddle_onnx_packs.get(device)
                            else {}
                        ),
                    }
                    for device, lock in copied_paddle_onnx.items()
                }
            }
            if copied_paddle_onnx
            else {}
        ),
        "product": {
            "component": "next",
            "repository": "FelixJI/vibeocr-next",
            "version": version,
            "source_sha": source_commit,
        },
        **(
            {
                "runtime_wheel": copied_runtime.name,
                "runtime_sha256": sha256_file(copied_runtime),
            }
            if copied_runtime is not None
            else {"runtime_wheel": None, "runtime_sha256": None}
        ),
        **({"runtime_env": runtime_env} if runtime_env is not None else {}),
        "python": {
            "version": python_version,
            "abi": "cp313",
            "platform": "win_amd64",
            "source_url": python_source_url,
            **(
                {
                    "archive": copied_python.name,
                    "sha256": sha256_file(copied_python),
                }
                if copied_python is not None
                else {}
            ),
        },
        "installer": {
            "executable": copied_installer.name,
            "executable_sha256": sha256_file(copied_installer),
        },
        "profiles": {
            profile: {
                "lock": copied_profiles[profile].name,
                **(
                    {
                        "runtime_pack": [pack.name for pack in copied_packs[profile]],
                        "runtime_pack_sha256": [
                            sha256_file(pack) for pack in copied_packs[profile]
                        ],
                    }
                    if copied_packs.get(profile)
                    else {"runtime_pack": None}
                ),
                "sha256": sha256_file(copied_profiles[profile]),
                "components": _profile_components(copied_profiles[profile], profile),
                "install_scopes": _independent_scopes(
                    profile,
                    copied_profiles,
                    Path(),  # 历史 gpu 锁槽位已退役，仅为调用形状保留
                    copied_packs.get("win-x64-base", []),
                ),
            }
            for profile in PROFILE_NAMES
        },
        "capabilities": sorted(set(capabilities)),
        **(
            {
                "paddle_model_pack": {
                    "archive": copied_paddle_model_pack.name,
                    "sha256": sha256_file(copied_paddle_model_pack),
                }
            }
            if copied_paddle_model_pack is not None
            else {}
        ),
        "build_workflow": build_workflow,
    }
    manifest_path = output_dir / "runtime-manifest.json"
    manifest_path.write_bytes(_canonical_json(manifest))
    load_runtime_manifest(manifest_path)

    return manifest_path


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime-wheel", type=Path, default=None)
    parser.add_argument("--base-lock", type=Path, required=True)
    parser.add_argument("--paddle-ort-directml-lock", type=Path, required=True)
    for device in ("directml",):
        parser.add_argument(
            f"--paddle-ort-{device}-runtime-pack",
            type=Path,
            action="append",
            default=None,
            help=(
                "Offline wheel-closure archive bound to the Paddle ONNX device "
                "(repeatable)."
            ),
        )
    parser.add_argument(
        "--paddle-model-pack",
        type=Path,
        required=True,
        help="Offline bundle of the default Paddle ONNX models.",
    )
    parser.add_argument("--python-archive", type=Path, default=None)
    parser.add_argument(
        "--runtime-env-tree",
        type=Path,
        default=None,
        help=(
            "Prebuilt runtime env tree (expanded directory with python.exe); "
            "replaces the wheel/python archive payloads."
        ),
    )
    parser.add_argument(
        "--runtime-env-manifest",
        type=Path,
        default=None,
        help="Deterministic per-file manifest of the prebuilt runtime env tree.",
    )
    parser.add_argument("--python-version", default="3.13.16")
    parser.add_argument(
        "--python-source-url",
        default=(
            "https://github.com/astral-sh/python-build-standalone/releases/"
            "download/20261001/"
            "cpython-3.13.16+20261001-x86_64-pc-windows-msvc"
            "-install_only.tar.gz"
        ),
    )
    parser.add_argument(
        "--installer-executable",
        type=Path,
        required=True,
        help="Frozen Runtime Installer executable bound by the manifest.",
    )
    parser.add_argument(
        "--base-runtime-pack",
        type=Path,
        action="append",
        default=None,
        help="Offline wheel-closure archive bound to the base profile (repeatable).",
    )

    parser.add_argument("--version", required=True)
    parser.add_argument("--source-commit", default=None)
    parser.add_argument("--build-workflow", required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument(
        "--capability",
        action="append",
        dest="capabilities",
    )
    args = parser.parse_args(argv)
    source_commit = args.source_commit or _git_sha()
    path = build_runtime_manifest(
        runtime_wheel=args.runtime_wheel,
        base_lock=args.base_lock,
        paddle_onnx_locks={"directml": args.paddle_ort_directml_lock},
        paddle_onnx_packs={"directml": args.paddle_ort_directml_runtime_pack or []},
        paddle_model_pack=args.paddle_model_pack,
        python_archive=args.python_archive,
        python_version=args.python_version,
        python_source_url=args.python_source_url,
        installer_executable=args.installer_executable,
        version=args.version,
        source_commit=source_commit,
        build_workflow=args.build_workflow,
        output_dir=args.output_dir,
        capabilities=tuple(args.capabilities or DEFAULT_CAPABILITIES),
        runtime_packs={"win-x64-base": args.base_runtime_pack or []},
        runtime_env_tree=args.runtime_env_tree,
        runtime_env_manifest=args.runtime_env_manifest,
    )
    print(path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
