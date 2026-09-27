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
    installer_executable_sha256,
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
    "runtime.maintenance.v1",
    "runtime.maintenance.v2",
    "runtime.component-repair.v1",
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
    "runtime.install-plan.v1",
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


def _profile_components(
    path: Path, profile: str, paddle_lock: Path | None = None
) -> list[dict[str, str]]:
    result: list[dict[str, str]] = []
    for descriptor in default_profile_components(profile):
        version = locked_distribution_version(
            paddle_lock
            if paddle_lock and descriptor.component_id.startswith("paddleocr-")
            else path,
            _component_version_package(profile, descriptor.component_id),
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
    *,
    isolated: bool,
) -> list[dict[str, object]]:
    """Compose existing verified host/Paddle domains without new lock resolution."""
    if profile == "win-x64-base":
        return []
    base_ids = [
        item.component_id for item in default_profile_components("win-x64-base")
    ]
    scopes: list[dict[str, object]] = []

    def add(scope_id: str, ids: list[str], lock: Path) -> None:
        scopes.append(
            {
                "scope_id": scope_id,
                "component_ids": ids,
                "lock": lock.name,
                "sha256": sha256_file(lock),
                "runtime_pack": [pack.name for pack in base_packs]
                if lock == locks["win-x64-base"] and base_packs
                else None,
                **(
                    {"runtime_pack_sha256": [sha256_file(pack) for pack in base_packs]}
                    if lock == locks["win-x64-base"] and base_packs
                    else {}
                ),
            }
        )

    cuda = profile == "win-x64-cu126"
    if cuda:
        add("gpu-runtime", [*base_ids, "gpu_runtime"], gpu_lock)
    if isolated:
        suffix = "cuda" if cuda else "cpu"
        paddle = f"paddleocr-{suffix}"
        add("paddle", [*base_ids, paddle], locks["win-x64-base"])
        add(
            "mineru",
            [*base_ids, f"mineru-{suffix}", *(["gpu_runtime"] if cuda else [])],
            locks[profile],
        )
        if cuda:
            add("paddle-gpu-runtime", [*base_ids, paddle, "gpu_runtime"], gpu_lock)
    return scopes


def build_runtime_manifest(
    *,
    runtime_wheel: Path,
    base_lock: Path,
    cpu_lock: Path,
    cu126_lock: Path,
    cu126_gpu_lock: Path,
    python_archive: Path,
    python_version: str,
    python_source_url: str,
    installer_archive: Path,
    version: str,
    source_commit: str,
    build_workflow: str,
    output_dir: Path,
    capabilities: tuple[str, ...] = DEFAULT_CAPABILITIES,
    runtime_packs: dict[str, list[Path]] | None = None,
    paddle_locks: dict[str, Path] | None = None,
) -> Path:
    if not _STABLE_SEMVER.fullmatch(version):
        raise ValueError("version must be stable SemVer")
    if not _FULL_SHA.fullmatch(source_commit):
        raise ValueError("source_commit must be a full lowercase Git SHA")
    if not build_workflow.strip():
        raise ValueError("build_workflow is required")
    if not capabilities or any(not item.strip() for item in capabilities):
        raise ValueError("at least one non-empty capability is required")

    runtime_wheel = runtime_wheel.resolve(strict=True)
    python_archive = python_archive.resolve(strict=True)
    installer_archive = installer_archive.resolve(strict=True)
    if not runtime_wheel.name.startswith(f"vibeocr_next_runtime-{version}-"):
        raise ValueError("Runtime wheel filename does not match product version")

    profile_sources = {
        "win-x64-base": base_lock.resolve(strict=True),
        "win-x64-cpu": cpu_lock.resolve(strict=True),
        "win-x64-cu126": cu126_lock.resolve(strict=True),
    }
    paddle_locks = paddle_locks or {}
    if paddle_locks and set(paddle_locks) != {"win-x64-cpu", "win-x64-cu126"}:
        raise ValueError("Paddle locks must cover CPU and cu126")
    for profile in PROFILE_NAMES:
        validate_requirements_lock(
            profile_sources[profile],
            profile=profile,
            paddle_isolated=profile in paddle_locks,
        )
    for profile, lock in paddle_locks.items():
        validate_requirements_lock(
            lock, profile=profile.replace("win-x64-", "win-x64-paddle-")
        )
    cu126_gpu_lock = cu126_gpu_lock.resolve(strict=True)
    validate_requirements_lock(
        cu126_gpu_lock, profile="win-x64-cu126", paddle_isolated=bool(paddle_locks)
    )

    output_dir.mkdir(parents=True, exist_ok=True)
    copied_runtime = _copy_exact(runtime_wheel, output_dir)
    copied_python = _copy_exact(python_archive, output_dir)
    copied_installer = _copy_exact(installer_archive, output_dir)
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
    copied_cu126_gpu_lock = _copy_exact(cu126_gpu_lock, output_dir)
    copied_paddle = {
        profile: _copy_exact(lock, output_dir) for profile, lock in paddle_locks.items()
    }

    manifest = {
        "schema_version": 2,
        "product": {
            "component": "next",
            "repository": "FelixJI/vibeocr-next",
            "version": version,
            "source_sha": source_commit,
        },
        "runtime_wheel": copied_runtime.name,
        "runtime_sha256": sha256_file(copied_runtime),
        "python": {
            "version": python_version,
            "abi": "cp313",
            "platform": "win_amd64",
            "source_url": python_source_url,
            "archive": copied_python.name,
            "sha256": sha256_file(copied_python),
        },
        "installer": {
            "archive": copied_installer.name,
            "sha256": sha256_file(copied_installer),
            "executable_path": "runtime-installer/vibeocr-runtime-installer.exe",
            "executable_sha256": installer_executable_sha256(
                copied_installer,
                "runtime-installer/vibeocr-runtime-installer.exe",
            ),
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
                "components": _profile_components(
                    copied_profiles[profile], profile, copied_paddle.get(profile)
                ),
                **(
                    {
                        "paddle_environment": {
                            "lock": copied_paddle[profile].name,
                            "sha256": sha256_file(copied_paddle[profile]),
                        }
                    }
                    if profile in copied_paddle
                    else {}
                ),
                "install_scopes": _independent_scopes(
                    profile,
                    copied_profiles,
                    copied_cu126_gpu_lock,
                    copied_packs.get("win-x64-base", []),
                    isolated=profile in copied_paddle,
                ),
            }
            for profile in PROFILE_NAMES
        },
        "capabilities": sorted(set(capabilities)),
        "build_workflow": build_workflow,
    }
    manifest_path = output_dir / "runtime-manifest.json"
    manifest_path.write_bytes(_canonical_json(manifest))
    load_runtime_manifest(manifest_path)

    return manifest_path


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime-wheel", type=Path, required=True)
    parser.add_argument("--base-lock", type=Path, required=True)
    parser.add_argument("--cpu-lock", type=Path, required=True)
    parser.add_argument("--cu126-lock", type=Path, required=True)
    parser.add_argument("--cu126-gpu-lock", type=Path, required=True)
    parser.add_argument("--paddle-cpu-lock", type=Path, required=True)
    parser.add_argument("--paddle-cu126-lock", type=Path, required=True)
    parser.add_argument("--python-archive", type=Path, required=True)
    parser.add_argument("--python-version", default="3.13.15")
    parser.add_argument(
        "--python-source-url",
        default=(
            "https://github.com/astral-sh/python-build-standalone/releases/"
            "download/20260807/"
            "cpython-3.13.15+20260807-x86_64-pc-windows-msvc"
            "-install_only.tar.gz"
        ),
    )
    parser.add_argument("--installer-archive", type=Path, required=True)
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
        cpu_lock=args.cpu_lock,
        cu126_lock=args.cu126_lock,
        cu126_gpu_lock=args.cu126_gpu_lock,
        paddle_locks={
            "win-x64-cpu": args.paddle_cpu_lock,
            "win-x64-cu126": args.paddle_cu126_lock,
        },
        python_archive=args.python_archive,
        python_version=args.python_version,
        python_source_url=args.python_source_url,
        installer_archive=args.installer_archive,
        version=args.version,
        source_commit=source_commit,
        build_workflow=args.build_workflow,
        output_dir=args.output_dir,
        capabilities=tuple(args.capabilities or DEFAULT_CAPABILITIES),
        runtime_packs={"win-x64-base": args.base_runtime_pack or []},
    )
    print(path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
