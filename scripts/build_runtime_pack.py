"""Build a manifest-bound offline runtime pack from an exact hash lock.

Two-phase flow (plan §4.2):

1. ``pip download --require-hashes`` pulls the closure — wheels where the
   index has them, hash-verified sdists where it does not (e.g.
   ``antlr4-python3-runtime==4.9.3`` via omegaconf ships an sdist only).
   Every downloaded artifact is re-verified byte-for-byte against the lock.
2. ``pip wheel --no-index --find-links <downloads>`` turns the verified
   closure into a pure wheel directory offline — direct wheels are reused,
   sdists are built with the running environment's setuptools
   (``--no-build-isolation``; callers must provide setuptools, e.g. the
   release workflow installs it alongside ``build``).

The final pack is a flat, deterministic zip of every wheel. Release
automation binds it into ``runtime-manifest.json`` via
``build_runtime_manifest.py --base-runtime-pack``; the installer then
installs with ``--no-index --find-links`` so a blocked network can never
degrade an offline install.

``--profile win-x64-shared-pool`` builds the shared offline wheel pool
instead: every member lock still resolves its own hash-verified closure,
byte-identical wheels are distributed exactly once, same-name conflicts
with different pins keep both wheels, and each member lock gets a tiny
companion zip carrying only its exact ``pack-requirements.txt``. Release
automation binds ``[requirements zip, *pool parts]`` through the existing
repeatable ``runtime_pack`` tuple interface, so every install scope picks
its own closure from the shared wheel directory without any manifest or
installer protocol change.
"""

from __future__ import annotations

import argparse
import filecmp
import hashlib
import os
import re
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO_ROOT / "src" / "runtime"))
sys.path.insert(0, str(REPO_ROOT / "contracts" / "runtime" / "python"))

from vibeocr.runtime.environments.model_cache import (  # noqa: E402
    BUNDLED_PADDLE_ONNX_MODELS,
)
from vibeocr.runtime.environments.network_detector import get_pip_mirror  # noqa: E402
from vibeocr.runtime.environments.onnx_assets import (  # noqa: E402
    pinned_download,
)
from vibeocr.runtime.environments.runtime_manifest import (  # noqa: E402
    ManifestError,
    validate_requirements_lock,
)

# Deterministic zip metadata: identical inputs produce identical bytes.
_FIXED_ZIP_DATE_TIME = (1980, 1, 1, 0, 0, 0)
_HASH_ENTRY_RE = re.compile(r"--hash=sha256:([0-9a-f]{64})")
_DECLARATION_RE = re.compile(r"(?m)^([A-Za-z0-9][A-Za-z0-9._-]*)==(\S+)")
_WHEEL_FILENAME_RE = re.compile(
    r"^(?P<name>[A-Za-z0-9][A-Za-z0-9._-]*)-(?P<version>\d[^-]*)-(?P<rest>.+)\.whl$"
)
DEFAULT_PACKAGE_INDEX = get_pip_mirror("international")
PACKAGE_INDEX_ENV = "VIBEOCR_RUNTIME_PACK_INDEX_URL"
# 共享离线 wheel pool 的 profile 名；成员 lock 各自携带真实 profile。
SHARED_POOL_PROFILE = "win-x64-shared-pool"


def _normalize(name: str) -> str:
    return re.sub(r"[-_.]+", "-", name).lower()


def _resolve_package_index(index_url: str | None) -> str:
    resolved = (
        index_url
        if index_url is not None
        else os.environ.get(
            PACKAGE_INDEX_ENV,
            DEFAULT_PACKAGE_INDEX,
        )
    )
    if not resolved.strip():
        raise ValueError(f"{PACKAGE_INDEX_ENV} must not be empty")
    return resolved


def _lock_hashes(path: Path) -> set[str]:
    return set(_HASH_ENTRY_RE.findall(path.read_text(encoding="utf-8")))


def _lock_requirements(path: Path) -> set[tuple[str, str]]:
    return {
        (_normalize(match.group(1)), match.group(2))
        for match in _DECLARATION_RE.finditer(path.read_text(encoding="utf-8"))
    }


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _run(command: list[str]) -> None:
    child_environment = {
        name: value
        for name, value in os.environ.items()
        if not name.upper().startswith(("PIP_", "UV_"))
    }
    child_environment.update(
        {
            "PIP_CONFIG_FILE": os.devnull,
            "PIP_DISABLE_PIP_VERSION_CHECK": "1",
            "PIP_NO_INPUT": "1",
            "PYTHONNOUSERSITE": "1",
            "PYTHONUTF8": "1",
        }
    )
    result = subprocess.run(
        command,
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=child_environment,
    )
    if result.returncode != 0:
        raise RuntimeError(
            f"command failed ({' '.join(command[1:4])}...):\n"
            f"{result.stdout}\n{result.stderr}"
        )


def _paddle_model_sources() -> dict[str, dict[str, tuple[tuple[str, str, str], ...]]]:
    """冻结清单 → 每模型每个下载源的候选 URL（modelscope 优先，HF 兑底）。"""
    import json
    from importlib.resources import files

    catalog = json.loads(
        files("vibeocr.runtime.environments")
        .joinpath("paddle_onnx_models.json")
        .read_text(encoding="utf-8")
    )["models"]
    sources: dict[str, dict[str, tuple[tuple[str, str, str], ...]]] = {}
    for name in BUNDLED_PADDLE_ONNX_MODELS:
        entry = catalog[name]
        per_file: dict[str, tuple[tuple[str, str, str], ...]] = {}
        scope = entry["sources"]["modelscope"]
        for file_entry in scope["files"]:
            urls = (
                (
                    f"https://www.modelscope.cn/models/{entry['repo_id']}/resolve/"
                    f"{scope['revision']}/{file_entry['path']}",
                    file_entry["checksum"],
                    str(file_entry["size"]),
                ),
                (
                    f"https://huggingface.co/{entry['repo_id']}/resolve/"
                    f"{entry['sources']['huggingface']['revision']}/{file_entry['path']}",
                    file_entry["checksum"],
                    str(file_entry["size"]),
                ),
            )
            per_file[file_entry["path"]] = urls
        sources[name] = per_file
    return sources


def build_paddle_model_pack(*, work_dir: Path, output: Path) -> Path:
    """下载冻结清单里的默认模型并生成确定性的随包 model pack。

    包成员为 ``<ModelName>/<file>``；字节完整性由冻结清单的 SHA-256 逐件
    校验，pack 整体由 manifest 的 paddle_model_pack.sha256 绑定。安装侧
    （seed_bundled_paddle_onnx_models）把同一冻结清单作为唯一真值布置进
    共享模型缓存。
    """
    if output.suffix != ".zip":
        raise ValueError("paddle model pack output must be a .zip archive")
    if work_dir.is_symlink() or work_dir.is_junction():
        raise ValueError("runtime pack work directory must not be a link")
    work_dir.mkdir(parents=True, exist_ok=True)
    members: list[tuple[str, bytes]] = []
    for name, per_file in sorted(_paddle_model_sources().items()):
        for path, candidates in sorted(per_file.items()):
            destination = work_dir / name / path
            errors: list[Exception] = []
            for url, digest, _size in candidates:
                try:
                    # pinned_download 对已存在缓存同样执行冻结清单 SHA-256
                    # 校验：损坏/陈旧缓存会被重新下载替换，不进入 pack。
                    pinned_download(url, destination, digest, lambda: None)
                    break
                except Exception as error:  # noqa: BLE001 - try next mirror
                    errors.append(error)
            else:
                raise RuntimeError(
                    f"paddle model download failed: {name}/{path}: {errors}"
                )
            members.append((f"{name}/{path}", destination.read_bytes()))
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists():
        output.unlink()
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for member, payload in members:
            info = zipfile.ZipInfo(member, date_time=_FIXED_ZIP_DATE_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, payload)
    return output


def _closure_wheels(
    *, lock: Path, work_dir: Path, index_url: str
) -> tuple[list[Path], set[tuple[str, str]]]:
    """按一个 exact hash lock 下载并离线 wheel 化完整闭包。

    Phase 1: hash-verified closure download (wheels preferred, sdists kept).
    Phase 2: offline wheelification. Build isolation is disabled on purpose:
    ``--no-index`` forbids the isolated build env from fetching setuptools,
    so the running env provides it.
    """
    if work_dir.is_symlink() or work_dir.is_junction():
        raise ValueError("runtime pack work directory must not be a link")
    work_dir.mkdir(parents=True, exist_ok=True)
    downloads = work_dir / "downloads"
    wheels = work_dir / "wheels"
    for directory in (downloads, wheels):
        if directory.is_symlink() or directory.is_junction():
            raise ValueError("runtime pack work child must not be a link")
        if directory.exists():
            shutil.rmtree(directory)
        directory.mkdir(parents=True)

    _run(
        [
            sys.executable,
            "-m",
            "pip",
            "download",
            "--index-url",
            index_url,
            "--require-hashes",
            "-d",
            str(downloads),
            "-r",
            str(lock),
        ]
    )
    known_hashes = _lock_hashes(lock)
    artifacts = sorted(downloads.iterdir())
    if not artifacts:
        raise RuntimeError("runtime pack closure is empty")
    for artifact in artifacts:
        if artifact.suffix not in (".whl", ".gz", ".zip"):
            raise RuntimeError(
                f"runtime pack download is neither wheel nor sdist: {artifact.name}"
            )
        if _sha256_file(artifact) not in known_hashes:
            raise RuntimeError(f"runtime pack artifact not bound by lock: {artifact}")

    requirement_set = _lock_requirements(lock)
    if not requirement_set:
        raise RuntimeError("runtime lock declares no requirements")
    _run(
        [
            sys.executable,
            "-m",
            "pip",
            "wheel",
            "--no-index",
            "--no-deps",
            "--no-build-isolation",
            "--find-links",
            str(downloads),
            "-w",
            str(wheels),
            *(f"{name}=={version}" for name, version in sorted(requirement_set)),
        ]
    )
    wheel_files = sorted(wheels.glob("*.whl"))
    if not wheel_files:
        raise RuntimeError("runtime pack produced no wheels")
    packed: set[tuple[str, str]] = set()
    for wheel in wheel_files:
        match = _WHEEL_FILENAME_RE.match(wheel.name)
        if match is None:
            raise RuntimeError(f"unparsable wheel filename: {wheel.name}")
        packed.add((_normalize(match.group("name")), match.group("version")))
    missing = sorted(requirement_set.difference(packed))
    if missing:
        raise RuntimeError(f"runtime pack is missing wheels for: {missing}")
    return wheel_files, requirement_set


def _write_pack_parts(
    *,
    output: Path,
    max_part_bytes: int | None,
    wheel_files: list[Path],
    text_members: dict[str, str],
) -> list[Path]:
    """把 wheel 目录写成确定性分片 zip；文本清单固定放在第一片。"""
    # 分片:单 wheel 不可再分,超过 max_part_bytes 的 wheel 独占一片。
    # wheel 本身已是 deflate 容器,直接以文件大小做贪心装箱即可。
    parts: list[list[Path]] = []
    if max_part_bytes is None:
        parts.append(list(wheel_files))
    else:
        current: list[Path] = []
        current_bytes = 0
        for wheel in wheel_files:
            size = wheel.stat().st_size
            if current and current_bytes + size > max_part_bytes:
                parts.append(current)
                current = []
                current_bytes = 0
            current.append(wheel)
            current_bytes += size
        if current:
            parts.append(current)

    output.parent.mkdir(parents=True, exist_ok=True)
    written: list[Path] = []
    width = max(2, len(str(len(parts))))
    for index, part_files in enumerate(parts, start=1):
        part_path = (
            output
            if max_part_bytes is None
            else output.with_name(f"{output.stem}.part{index:0{width}d}.zip")
        )
        if part_path.exists():
            part_path.unlink()
        with zipfile.ZipFile(part_path, "w", compression=zipfile.ZIP_DEFLATED) as (
            archive
        ):
            if index == 1:
                for member, payload in text_members.items():
                    requirements_info = zipfile.ZipInfo(
                        member, date_time=_FIXED_ZIP_DATE_TIME
                    )
                    requirements_info.compress_type = zipfile.ZIP_DEFLATED
                    archive.writestr(requirements_info, payload.encode("utf-8"))
            for wheel in part_files:
                info = zipfile.ZipInfo(wheel.name, date_time=_FIXED_ZIP_DATE_TIME)
                info.compress_type = zipfile.ZIP_DEFLATED
                archive.writestr(info, wheel.read_bytes())
        written.append(part_path)
    return written


def build_runtime_pack(
    *,
    lock: Path,
    profile: str,
    work_dir: Path,
    output: Path,
    max_part_bytes: int | None = None,
    index_url: str | None = None,
) -> list[Path]:
    lock = lock.resolve(strict=True)
    validate_requirements_lock(lock, profile=profile)
    if output.suffix != ".zip":
        raise ValueError("runtime pack output must be a .zip archive")
    if max_part_bytes is not None and max_part_bytes <= 0:
        raise ValueError("max_part_bytes must be positive")
    resolved_index_url = _resolve_package_index(index_url)

    wheel_files, requirement_set = _closure_wheels(
        lock=lock, work_dir=work_dir, index_url=resolved_index_url
    )

    # RapidTable 随包模型已随引擎退役移除；Paddle 默认模型由独立的
    # paddle model pack（build_paddle_model_pack）随产品携带。

    # pack 内附无哈希需求清单:离线安装以它为解析输入(原 lock 含 sdist
    # 哈希行,pip 会对 sdist 构建的 wheel 报哈希不匹配);pack 整体字节
    # 完整性由 manifest 的 runtime_pack_sha256 绑定。清单固定放在第一片。
    newline = chr(10)
    pack_requirements = (
        newline.join(f"{name}=={version}" for name, version in sorted(requirement_set))
        + newline
    )
    return _write_pack_parts(
        output=output,
        max_part_bytes=max_part_bytes,
        wheel_files=wheel_files,
        text_members={"pack-requirements.txt": pack_requirements},
    )


def build_runtime_pool(
    *,
    locks: list[tuple[str, Path]],
    work_dir: Path,
    output: Path,
    max_part_bytes: int | None = None,
    index_url: str | None = None,
) -> list[Path]:
    """构建多个 profile 闭包共享的离线 wheel pool。

    每个成员 lock 仍按自身 exact hash lock 独立下载并 wheel 化，锁内版本
    不做任何升级或合并；文件名相同的 wheel 字节必须一致并只进入 pool 一
    次，同名不同版本（冲突 pin，如 numpy）以不同文件名共存。

    产物完全复用现有 ``runtime_pack`` 元组接口，不扩展 manifest/安装器：

    - pool 分片（``<output stem>.partNN.zip``）只含去重后的 wheel；
    - 每个成员 lock 另产一个小 zip（``<output stem>.<lock stem>.zip``），
      仅含该 lock 的无哈希精确 requirements 清单 ``pack-requirements.txt``。

    发布侧把 ``[成员 requirements zip, *pool 分片]`` 依序绑定到对应 profile
    的 ``runtime_pack``；安装侧既有多分片解包把全部成员解到同一缓存目
    录，requirements zip 决定当前 scope 的精确闭包，安装环境仍彼此隔离。
    绑定顺序中 requirements zip 在首位，使各 scope 缓存目录独立、完整标
    记稳定，不互相重建。pool 整体字节完整性由 manifest 的
    ``runtime_pack_sha256`` 逐件绑定，不新增哈希层。
    """
    if not locks:
        raise ValueError("shared wheel pool requires at least one lock")
    if output.suffix != ".zip":
        raise ValueError("runtime pack output must be a .zip archive")
    if max_part_bytes is not None and max_part_bytes <= 0:
        raise ValueError("max_part_bytes must be positive")
    resolved_index_url = _resolve_package_index(index_url)
    resolved = [(profile, lock.resolve(strict=True)) for profile, lock in locks]
    stems = [lock.stem for _profile, lock in resolved]
    if len(set(stems)) != len(stems):
        raise ValueError("shared wheel pool locks must have distinct file stems")
    for profile, lock in resolved:
        validate_requirements_lock(lock, profile=profile)

    if work_dir.is_symlink() or work_dir.is_junction():
        raise ValueError("runtime pack work directory must not be a link")
    work_dir.mkdir(parents=True, exist_ok=True)
    pool = work_dir / "pool"
    if pool.is_symlink() or pool.is_junction():
        raise ValueError("runtime pack work child must not be a link")
    if pool.exists():
        shutil.rmtree(pool)
    pool.mkdir(parents=True)

    requirement_archives: list[Path] = []
    owners: dict[str, str] = {}
    newline = chr(10)
    # 成员 requirements zip 先于 pool 分片落盘：显式准备输出父目录，独立
    # CLI 调用不依赖 _write_pack_parts 的隐式创建。
    output.parent.mkdir(parents=True, exist_ok=True)
    for profile, lock in resolved:
        wheel_files, requirement_set = _closure_wheels(
            lock=lock,
            work_dir=work_dir / "closures" / lock.stem,
            index_url=resolved_index_url,
        )
        pack_requirements = (
            newline.join(
                f"{name}=={version}" for name, version in sorted(requirement_set)
            )
            + newline
        )
        requirements_archive = output.with_name(f"{output.stem}.{lock.stem}.zip")
        if requirements_archive.exists():
            requirements_archive.unlink()
        with zipfile.ZipFile(
            requirements_archive, "w", compression=zipfile.ZIP_DEFLATED
        ) as archive:
            info = zipfile.ZipInfo(
                "pack-requirements.txt", date_time=_FIXED_ZIP_DATE_TIME
            )
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, pack_requirements.encode("utf-8"))
        requirement_archives.append(requirements_archive)
        for wheel in wheel_files:
            existing = pool / wheel.name
            if existing.is_file():
                # 去重校验只做逐字节比对：来源完整性已由下载阶段的 lock 哈希
                # 保证，不新增本地哈希层；同名不同字节即冲突，fail closed。
                if not filecmp.cmp(wheel, existing, shallow=False):
                    raise RuntimeError(
                        "shared wheel pool has conflicting bytes for "
                        f"{wheel.name}: {owners[wheel.name]} vs {profile}"
                    )
                continue
            shutil.copyfile(wheel, existing)
            owners[wheel.name] = profile
    pool_wheels = sorted(pool.glob("*.whl"))
    if not pool_wheels:
        raise RuntimeError("shared wheel pool produced no wheels")
    pool_parts = _write_pack_parts(
        output=output,
        max_part_bytes=max_part_bytes,
        wheel_files=pool_wheels,
        text_members={},
    )
    return [*sorted(requirement_archives), *pool_parts]


def _parse_pool_locks(entries: list[str]) -> list[tuple[str, Path]]:
    resolved: list[tuple[str, Path]] = []
    for entry in entries:
        profile, separator, path = entry.partition("=")
        profile = profile.strip()
        path = path.strip()
        if not separator or not profile or not path:
            raise ValueError(f"invalid --pool-lock entry: {entry}")
        resolved.append((profile, Path(path)))
    return resolved


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--lock", type=Path, required=False)
    parser.add_argument("--profile", required=True)
    parser.add_argument(
        "--pool-lock",
        action="append",
        default=None,
        metavar="PROFILE=LOCK",
        help=(
            "Member lock of the shared wheel pool as PROFILE=LOCK (repeatable); "
            f"requires --profile {SHARED_POOL_PROFILE}."
        ),
    )
    parser.add_argument("--work-dir", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument(
        "--index-url",
        default=None,
        help=(
            f"Package index for closure download (default: official PyPI; "
            f"local override: {PACKAGE_INDEX_ENV})"
        ),
    )
    parser.add_argument(
        "--max-part-bytes",
        type=int,
        default=None,
        help=(
            "Split the pack into partNN.zip archives whose uncompressed size "
            "stays under this limit (single wheels may exceed it)."
        ),
    )
    args = parser.parse_args(argv)
    try:
        if args.profile == "paddle-onnx-models":
            if args.lock is not None or args.pool_lock:
                raise ValueError("paddle-onnx-models pack does not use a wheel lock")
            paths = [
                build_paddle_model_pack(work_dir=args.work_dir, output=args.output)
            ]
        elif args.profile == SHARED_POOL_PROFILE:
            if args.lock is not None:
                raise ValueError("shared wheel pool does not use --lock")
            paths = build_runtime_pool(
                locks=_parse_pool_locks(args.pool_lock or []),
                work_dir=args.work_dir,
                output=args.output,
                max_part_bytes=args.max_part_bytes,
                index_url=args.index_url,
            )
        else:
            if args.pool_lock:
                raise ValueError(
                    f"--pool-lock requires --profile {SHARED_POOL_PROFILE}"
                )
            if args.lock is None:
                raise ValueError("--lock is required for wheel-closure packs")
            paths = build_runtime_pack(
                lock=args.lock,
                profile=args.profile,
                work_dir=args.work_dir,
                output=args.output,
                max_part_bytes=args.max_part_bytes,
                index_url=args.index_url,
            )
    except (ManifestError, ValueError, RuntimeError) as exc:
        print(f"runtime pack build failed: {exc}", file=sys.stderr)
        return 1
    for path in paths:
        print(path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
