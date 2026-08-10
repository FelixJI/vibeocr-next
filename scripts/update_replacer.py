"""Transactional updater for the versioned VibeOCR product layout."""

from __future__ import annotations

import hashlib
import logging
import os
import shutil
import subprocess
import time
import uuid
import zipfile
from collections.abc import Callable, Sequence
from logging.handlers import RotatingFileHandler
from pathlib import Path, PurePosixPath

if __package__:
    from .product_layout import (
        ROOT_ALLOWLIST,
        ProductLayoutError,
        load_product_layout,
        verify_product_release,
    )
else:
    from product_layout import (
        ROOT_ALLOWLIST,
        ProductLayoutError,
        load_product_layout,
        verify_product_release,
    )

logger = logging.getLogger("updater")


class UpdateError(RuntimeError):
    """A stable update transaction failure."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(f"{code}: {message}")
        self.code = code


def setup_logging(user_data_root: Path, log_filename: str) -> None:
    """Write updater diagnostics only to the product user-data root."""

    log_dir = user_data_root.resolve(strict=False) / "logs"
    log_dir.mkdir(parents=True, exist_ok=True)
    handler = RotatingFileHandler(
        log_dir / log_filename,
        maxBytes=2 * 1024 * 1024,
        backupCount=2,
        encoding="utf-8",
        delay=True,
    )
    handler.setFormatter(logging.Formatter("%(asctime)s [%(levelname)s] %(message)s"))
    root = logging.getLogger()
    root.setLevel(logging.INFO)
    root.handlers.clear()
    root.addHandler(handler)


def verify_sha256(package: Path) -> bool:
    checksum = Path(f"{package}.sha256")
    if not checksum.is_file():
        return False
    expected = checksum.read_text(encoding="utf-8").split()[0].lower()
    digest = hashlib.sha256()
    with package.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().lower() == expected


def _safe_archive_path(root: Path, member: zipfile.ZipInfo) -> Path:
    value = PurePosixPath(member.filename)
    if value.is_absolute() or any(part in {"", ".", ".."} for part in value.parts):
        raise UpdateError("PackageInvalid", f"unsafe archive entry: {member.filename}")
    target = root.joinpath(*value.parts).resolve(strict=False)
    try:
        target.relative_to(root)
    except ValueError as error:
        raise UpdateError(
            "PackageInvalid", f"archive entry escapes root: {member.filename}"
        ) from error
    return target


def extract_and_verify(package: Path, staging_root: Path) -> Path:
    """Extract a candidate and validate its complete ProductLayout before handoff."""

    if not verify_sha256(package):
        raise UpdateError("PackageInvalid", "SHA-256 verification failed")
    staging_root.mkdir(parents=True, exist_ok=False)
    try:
        with zipfile.ZipFile(package) as archive:
            for member in archive.infolist():
                target = _safe_archive_path(staging_root, member)
                if member.is_dir():
                    target.mkdir(parents=True, exist_ok=True)
                    continue
                target.parent.mkdir(parents=True, exist_ok=True)
                with archive.open(member) as source, target.open("wb") as destination:
                    shutil.copyfileobj(source, destination)
        entries = list(staging_root.iterdir())
        product_root = (
            entries[0] if len(entries) == 1 and entries[0].is_dir() else staging_root
        )
        verify_product_release(product_root)
        return product_root
    except (OSError, zipfile.BadZipFile, ProductLayoutError) as error:
        raise UpdateError("PackageInvalid", str(error)) from error


def signal_ready(user_data_root: Path, ready_filename: str) -> Path:
    ready = user_data_root / "cache" / "update" / ready_filename
    ready.parent.mkdir(parents=True, exist_ok=True)
    ready.write_text("ready\n", encoding="utf-8")
    return ready


def _move_path(source: Path, destination: Path) -> None:
    source.replace(destination)


def _remove_path(path: Path) -> None:
    if path.is_dir():
        shutil.rmtree(path)
    else:
        path.unlink(missing_ok=True)


def _restore_install(
    install_root: Path,
    backup_root: Path,
    deployed: Sequence[str],
    backed_up: Sequence[str],
) -> None:
    for name in reversed(deployed):
        target = install_root / name
        if target.exists():
            _remove_path(target)
    failures: list[str] = []
    for name in reversed(backed_up):
        source = backup_root / name
        try:
            _move_path(source, install_root / name)
        except OSError:
            failures.append(name)
    if failures:
        raise UpdateError("RollbackFailed", f"could not restore: {sorted(failures)}")


def replace_deployment(
    candidate_root: Path, install_root: Path, backup_root: Path
) -> None:
    """Atomically replace the five root deployment entries or restore all old entries."""

    candidate = load_product_layout(candidate_root)
    current = load_product_layout(install_root)
    del candidate, current
    backup_root.mkdir(parents=True, exist_ok=False)
    names = sorted(ROOT_ALLOWLIST)
    backed_up: list[str] = []
    deployed: list[str] = []
    try:
        for name in names:
            _move_path(install_root / name, backup_root / name)
            backed_up.append(name)
        for name in names:
            _move_path(candidate_root / name, install_root / name)
            deployed.append(name)
        load_product_layout(install_root)
    except Exception as error:
        try:
            _restore_install(install_root, backup_root, deployed, backed_up)
        except UpdateError:
            raise
        raise UpdateError(
            "ApplyFailed", "deployment restored after replacement failure"
        ) from error


def prepare_same_volume_deployment(
    candidate_root: Path, install_root: Path, transaction_id: str
) -> Path:
    """Copy a verified candidate beside the install before atomic root moves."""

    deployment = install_root.parent / f".{install_root.name}.stage-{transaction_id}"
    if deployment.exists():
        raise UpdateError(
            "StageInvalid", f"deployment stage already exists: {deployment}"
        )
    try:
        shutil.copytree(candidate_root, deployment)
        verify_product_release(deployment)
        return deployment
    except (OSError, ProductLayoutError) as error:
        shutil.rmtree(deployment, ignore_errors=True)
        raise UpdateError("StageInvalid", str(error)) from error


def launch_and_wait(
    install_root: Path,
    entry_args: Sequence[str],
    health_file: Path,
    timeout_seconds: float = 30.0,
) -> None:
    health_file.parent.mkdir(parents=True, exist_ok=True)
    health_file.unlink(missing_ok=True)
    command = [str(install_root / "VibeOCR.exe"), *entry_args]
    subprocess.Popen(
        command, cwd=str(install_root), creationflags=0x8 if os.name == "nt" else 0
    )
    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        if health_file.is_file():
            return
        time.sleep(0.1)
    raise UpdateError("HealthTimeout", "new application did not publish health")


def run_replacement(
    package: Path,
    install_root: Path,
    user_data_root: Path,
    *,
    ready_filename: str = "updater.ready",
    launch_args: tuple[str, ...] = (),
    launch_health_file: Path | None = None,
    launch: Callable[[Path, Sequence[str], Path], None] = launch_and_wait,
    on_failure: Callable[[str], None] | None = None,
) -> int:
    """Verify, replace, health-check and roll back a new-layout update."""

    install = install_root.resolve(strict=True)
    user_data = user_data_root.resolve(strict=False)
    update_root = user_data / "cache" / "update"
    transaction_id = uuid.uuid4().hex
    transaction = update_root / f"transaction-{transaction_id}"
    staging = transaction / "staging"
    deployment = install.parent / f".{install.name}.stage-{transaction_id}"
    backup = install.parent / f".{install.name}.rollback-{transaction_id}"
    health = launch_health_file or (update_root / "application.healthy")
    try:
        if (
            install == user_data
            or install.is_relative_to(user_data)
            or user_data.is_relative_to(install)
        ):
            raise UpdateError(
                "LayoutInvalid",
                "install and user-data roots must not contain each other",
            )
        product = extract_and_verify(package.resolve(strict=True), staging)
        deployment = prepare_same_volume_deployment(product, install, transaction_id)
        signal_ready(user_data, ready_filename)
        time.sleep(2.0)
        replace_deployment(deployment, install, backup)
        try:
            launch(install, launch_args, health)
        except Exception as error:
            names = sorted(ROOT_ALLOWLIST)
            _restore_install(install, backup, names, names)
            subprocess.Popen(
                [str(install / "VibeOCR.exe"), *launch_args],
                cwd=str(install),
                creationflags=0x8 if os.name == "nt" else 0,
            )
            raise UpdateError("HealthTimeout", "new deployment rolled back") from error
        shutil.rmtree(backup)
        shutil.rmtree(deployment, ignore_errors=True)
        shutil.rmtree(transaction, ignore_errors=True)
        package.unlink(missing_ok=True)
        Path(f"{package}.sha256").unlink(missing_ok=True)
        return 0
    except Exception as error:
        logger.exception("update failed")
        preserve_transaction = (
            isinstance(error, UpdateError) and error.code == "RollbackFailed"
        )
        if not preserve_transaction:
            shutil.rmtree(deployment, ignore_errors=True)
            shutil.rmtree(backup, ignore_errors=True)
            shutil.rmtree(transaction, ignore_errors=True)
        if on_failure is not None:
            on_failure(str(error))
        return 1
