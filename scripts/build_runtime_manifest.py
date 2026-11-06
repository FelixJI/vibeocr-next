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
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO_ROOT / "src" / "runtime"))
sys.path.insert(0, str(REPO_ROOT / "contracts" / "runtime" / "python"))

from vibeocr.runtime.environments.runtime_manifest import (  # noqa: E402
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


def build_runtime_manifest(
    *,
    base_lock: Path,
    python_version: str,
    python_source_url: str,
    version: str,
    source_commit: str,
    build_workflow: str,
    output_dir: Path,
    capabilities: tuple[str, ...] = DEFAULT_CAPABILITIES,
    paddle_onnx_locks: dict[str, Path],
    paddle_model_pack: Path,
    runtime_env_tree: Path,
    runtime_env_manifest: Path,
) -> Path:
    if not _STABLE_SEMVER.fullmatch(version):
        raise ValueError("version must be stable SemVer")
    if not _FULL_SHA.fullmatch(source_commit):
        raise ValueError("source_commit must be a full lowercase Git SHA")
    if not build_workflow.strip():
        raise ValueError("build_workflow is required")
    if not capabilities or any(not item.strip() for item in capabilities):
        raise ValueError("at least one non-empty capability is required")

    # 统一推理闭包后仅构建 base profile；推理栈（Paddle ONNX + MinerU）全部
    # 由 paddle_onnx_environments 的 directml 绑定承载。
    profile_sources = {
        "win-x64-base": base_lock.resolve(strict=True),
    }
    for profile in PROFILE_NAMES:
        validate_requirements_lock(profile_sources[profile], profile=profile)
    if set(paddle_onnx_locks) != {"directml"}:
        raise ValueError(
            "Paddle ONNX locks must bind only the unified directml closure"
        )
    for device, lock in paddle_onnx_locks.items():
        validate_requirements_lock(lock, profile=f"win-x64-paddle-ort-{device}")

    output_dir.mkdir(parents=True, exist_ok=True)
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
    copied_profiles = {
        profile: _copy_exact(profile_sources[profile], output_dir)
        for profile in PROFILE_NAMES
    }
    copied_paddle_onnx = {
        device: _copy_exact(lock, output_dir)
        for device, lock in paddle_onnx_locks.items()
    }
    copied_paddle_model_pack = _copy_exact(paddle_model_pack, output_dir)
    manifest = {
        "schema_version": 2,
        "paddle_onnx_environments": {
            device: {"lock": lock.name, "sha256": sha256_file(lock)}
            for device, lock in copied_paddle_onnx.items()
        },
        "product": {
            "component": "next",
            "repository": "FelixJI/vibeocr-next",
            "version": version,
            "source_sha": source_commit,
        },
        "runtime_env": runtime_env,
        "python": {
            "version": python_version,
            "abi": "cp313",
            "platform": "win_amd64",
            "source_url": python_source_url,
        },
        "profiles": {
            profile: {
                "lock": copied_profiles[profile].name,
                "sha256": sha256_file(copied_profiles[profile]),
                "components": _profile_components(copied_profiles[profile], profile),
            }
            for profile in PROFILE_NAMES
        },
        "capabilities": sorted(set(capabilities)),
        "paddle_model_pack": {
            "archive": copied_paddle_model_pack.name,
            "sha256": sha256_file(copied_paddle_model_pack),
        },
        "build_workflow": build_workflow,
    }
    manifest_path = output_dir / "runtime-manifest.json"
    manifest_path.write_bytes(_canonical_json(manifest))
    load_runtime_manifest(manifest_path)

    return manifest_path


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-lock", type=Path, required=True)
    parser.add_argument("--paddle-ort-directml-lock", type=Path, required=True)
    parser.add_argument(
        "--paddle-model-pack",
        type=Path,
        required=True,
        help="Offline bundle of the default Paddle ONNX models.",
    )
    parser.add_argument(
        "--runtime-env-tree",
        type=Path,
        required=True,
        help=(
            "Prebuilt runtime env tree (expanded directory with python.exe); "
            "replaces the wheel/python archive payloads."
        ),
    )
    parser.add_argument(
        "--runtime-env-manifest",
        type=Path,
        required=True,
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
        base_lock=args.base_lock,
        paddle_onnx_locks={"directml": args.paddle_ort_directml_lock},
        paddle_model_pack=args.paddle_model_pack,
        python_version=args.python_version,
        python_source_url=args.python_source_url,
        version=args.version,
        source_commit=source_commit,
        build_workflow=args.build_workflow,
        output_dir=args.output_dir,
        capabilities=tuple(args.capabilities or DEFAULT_CAPABILITIES),
        runtime_env_tree=args.runtime_env_tree,
        runtime_env_manifest=args.runtime_env_manifest,
    )
    print(path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
