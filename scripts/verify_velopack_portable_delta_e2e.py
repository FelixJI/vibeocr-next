"""Run a real full or delta Velopack Portable apply/restart E2E on Windows."""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import stat
import subprocess
import threading
import time
import uuid
import xml.etree.ElementTree as ET
import zipfile
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


class _RecordingHandler(SimpleHTTPRequestHandler):
    requests: list[str] = []

    def do_GET(self) -> None:  # noqa: N802 - stdlib handler interface
        self.requests.append(self.path)
        super().do_GET()

    def log_message(self, _format: str, *args: object) -> None:
        pass


def _portable_root(extracted: Path) -> Path:
    markers = list(extracted.rglob(".portable"))
    if len(markers) != 1:
        raise RuntimeError(
            f"Portable archive must contain one .portable marker: {markers}"
        )
    root = markers[0].parent
    for relative in (
        "Update.exe",
        "VibeOCR.exe",
        "current/VibeOCR.exe",
        "current/sq.version",
    ):
        if not (root / relative).is_file():
            raise RuntimeError(f"Portable layout is missing {relative}")
    return root


def _extract_safe(archive: Path, destination: Path) -> Path:
    destination.mkdir(parents=True)
    with zipfile.ZipFile(archive) as package:
        for member in package.infolist():
            relative = Path(member.filename)
            if relative.is_absolute() or ".." in relative.parts:
                raise RuntimeError(f"unsafe Portable archive member: {member.filename}")
        package.extractall(destination)
    return _portable_root(destination)


def _wait_for_result(
    path: Path, timeout: float, bootstrap_log_root: Path
) -> dict[str, object]:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if path.is_file():
            return json.loads(path.read_text(encoding="utf-8"))
        logs = sorted(bootstrap_log_root.glob("bootstrapper-*.log"))
        if logs:
            try:
                content = logs[-1].read_text(encoding="utf-8", errors="replace")
            except FileNotFoundError:
                # Updating may move the log after enumeration; result remains required.
                content = ""
            if "[ERROR]" in content:
                raise RuntimeError(f"Portable bootstrapper failed:\n{content[-4000:]}")
        time.sleep(0.25)
    raise RuntimeError(f"Portable delta update timed out after {timeout:.0f}s")


def _wait_for_evidence_writer_exit(
    evidence: dict[str, object], *, timeout: float
) -> None:
    process_id = evidence.get("process_id")
    if (
        isinstance(process_id, bool)
        or not isinstance(process_id, int)
        or process_id <= 0
    ):
        raise RuntimeError(
            f"Portable E2E evidence has invalid process_id: {process_id!r}"
        )
    _wait_for_pid_exit(process_id, timeout=timeout)


def _wait_for_pid_exit(process_id: int, *, timeout: float) -> None:
    import ctypes
    from ctypes import wintypes

    synchronize = 0x00100000
    wait_object_0 = 0x00000000
    wait_timeout = 0x00000102
    wait_failed = 0xFFFFFFFF
    error_invalid_parameter = 87
    maximum_finite_wait = 0xFFFFFFFE

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    open_process = kernel32.OpenProcess
    open_process.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
    open_process.restype = wintypes.HANDLE
    wait_for_single_object = kernel32.WaitForSingleObject
    wait_for_single_object.argtypes = (wintypes.HANDLE, wintypes.DWORD)
    wait_for_single_object.restype = wintypes.DWORD
    close_handle = kernel32.CloseHandle
    close_handle.argtypes = (wintypes.HANDLE,)
    close_handle.restype = wintypes.BOOL

    handle = open_process(synchronize, False, process_id)
    if not handle:
        error = ctypes.get_last_error()
        if error == error_invalid_parameter:
            return
        raise OSError(error, f"OpenProcess failed for evidence writer {process_id}")

    milliseconds = min(maximum_finite_wait, max(1, int(timeout * 1000)))
    try:
        wait_result = wait_for_single_object(handle, milliseconds)
        if wait_result == wait_object_0:
            return
        if wait_result == wait_timeout:
            raise RuntimeError(
                "Portable E2E evidence writer did not exit naturally within "
                f"{timeout:.0f}s: process_id={process_id}"
            )
        if wait_result == wait_failed:
            error = ctypes.get_last_error()
            raise OSError(
                error,
                f"WaitForSingleObject failed for evidence writer {process_id}",
            )
        raise RuntimeError(
            "Portable E2E evidence writer returned unexpected wait status: "
            f"process_id={process_id}; status={wait_result}"
        )
    finally:
        close_handle(handle)


def _tree_inventory(root: Path) -> tuple[str, ...]:
    return tuple(
        sorted(
            f"{path.relative_to(root).as_posix()}{'/' if path.is_dir() else ''}"
            for path in root.rglob("*")
        )
    )


def _reject_unexpected_external(
    unexpected: dict[str, tuple[str, ...]], external_roots: dict[str, Path]
) -> None:
    """Reject unexpected writes without exposing OS cache identifiers in paths."""
    if not unexpected:
        return
    aliases_by_directory: dict[Path, set[str]] = {}
    unknown_by_directory: dict[Path, set[str]] = {}
    for name in unexpected:
        directory = external_roots[name]
        aliases_by_directory.setdefault(directory, set()).add(name)
        unknown_by_directory.setdefault(directory, set()).update(unexpected[name])
    summary = "; ".join(
        f"{' + '.join(sorted(aliases))}: "
        f"{len(unknown_by_directory[directory])} unexpected entries"
        for directory, aliases in sorted(
            aliases_by_directory.items(), key=lambda item: sorted(item[1])
        )
    )
    raise RuntimeError(
        "Velopack Portable E2E wrote unexpected data outside the Portable root: "
        f"{summary}"
    )


def _installed_version(root: Path) -> str:
    version = ET.parse(root / "current" / "sq.version").findtext(
        "{*}metadata/{*}version"
    )
    if not version or re.fullmatch(r"\d+\.\d+\.\d+", version) is None:
        raise RuntimeError("Portable sq.version does not contain a stable version")
    return version


def _external_layout(work_dir: Path) -> tuple[dict[str, Path], dict[str, Path]]:
    """Monitor exactly one profile root plus one temp root, with the product's
    AppData/Local and AppData/Roaming normalized inside the profile so the
    USERPROFILE and LOCALAPPDATA classifications cannot diverge."""
    profile = work_dir / "external" / "user-profile"
    temp = work_dir / "external" / "temp"
    environment = {
        "LOCALAPPDATA": profile / "AppData" / "Local",
        "APPDATA": profile / "AppData" / "Roaming",
        "USERPROFILE": profile,
        "HOME": profile,
        "TEMP": temp,
        "TMP": temp,
    }
    scanned_roots = {"profile": profile, "temp": temp}
    for path in environment.values():
        path.mkdir(parents=True, exist_ok=True)
    return environment, scanned_roots


def _allowed_external() -> dict[str, set[str]]:
    # Velopack 1.2 与 .NET Framework 会创建固定日志和空的 known-folder
    # 骨架。它们不是产品状态；逐项 allowlist（相对 profile/temp 两个扫描根，
    # 原 LOCALAPPDATA 精确项已移到 AppData/Local 前缀），不放大也不跳过子树。
    return {
        "profile": {
            "AppData/",
            "AppData/Roaming/",
            "AppData/Local/Microsoft/",
            "AppData/Local/Microsoft/Windows/",
            "AppData/Local/Microsoft/Windows/Caches/",
            "AppData/Local/velopack/",
            "AppData/Local/velopack/velopack_VibeOCRNext.log",
        },
        "temp": {"velopack_VibeOCRNext.log"},
    }


_REPARSE_ATTRIBUTE = 0x400  # Windows FILE_ATTRIBUTE_REPARSE_POINT


def _carries_reparse_metadata(path: Path) -> bool:
    """lstat-only check: POSIX symlinks and Windows reparse points both
    reject, and any lstat failure rejects too. File contents are never read."""
    try:
        metadata = path.lstat()
    except OSError:
        return True
    if stat.S_ISLNK(metadata.st_mode):
        return True
    return bool(getattr(metadata, "st_file_attributes", 0) & _REPARSE_ATTRIBUTE)


def _replaces_owned_directory(path: Path) -> bool:
    """True when a harness-owned directory is missing, is not a plain
    directory, or has been replaced by a reparse point."""
    if _carries_reparse_metadata(path):
        return True
    try:
        return not stat.S_ISDIR(path.lstat().st_mode)
    except OSError:
        return True


def _scan_external_root(
    root: Path, known_directories: frozenset[str]
) -> tuple[tuple[str, ...], tuple[str, ...]]:
    """Bounded fail-closed inventory of one scanned root: only directories that
    existed in the baseline or are named verbatim in the exact allowlist are
    enumerated; every other entry is recorded without descending into it, and
    reparse points are rejected outright even under an allowed name. File
    contents are never read."""
    inventory: list[str] = []
    violations: list[str] = []
    pending = [root]
    while pending:
        directory = pending.pop()
        try:
            children = sorted(directory.iterdir())
        except OSError:
            violations.append("unreadable-directory")
            continue
        for child in children:
            relative = child.relative_to(root).as_posix()
            if _carries_reparse_metadata(child):
                violations.append("reparse-point")
                continue
            try:
                is_directory = stat.S_ISDIR(child.lstat().st_mode)
            except OSError:
                violations.append("unreadable-entry")
                continue
            if is_directory:
                relative += "/"
                inventory.append(relative)
                if relative in known_directories:
                    pending.append(child)
            else:
                inventory.append(relative)
    return tuple(inventory), tuple(violations)


def _check_external_writes(
    scanned_roots: dict[str, Path],
    external_before: dict[str, tuple[str, ...]],
    work_dir: Path,
) -> None:
    """The real fail-closed boundary of the E2E: reject any unexpected write
    in the two scanned physical roots, enumerating only baseline/allowlist
    directories; the harness-owned chain is verified before any enumeration."""
    allowed_external = _allowed_external()
    unexpected: dict[str, tuple[str, ...]] = {}
    for name, root in scanned_roots.items():
        # Parent→child: the owned work_dir, its external root, then the scan
        # root itself. A junction-swapped root must fail before any iterdir.
        if any(
            _replaces_owned_directory(owned)
            for owned in (work_dir, work_dir / "external", root)
        ):
            unexpected[name] = ("replaced-owned-root",)
    if unexpected:
        _reject_unexpected_external(unexpected, scanned_roots)
        return
    for name, root in scanned_roots.items():
        known_directories = frozenset(
            entry
            for entry in (*external_before[name], *allowed_external[name])
            if entry.endswith("/")
        )
        inventory, violations = _scan_external_root(root, known_directories)
        entries = set(violations) | (
            set(inventory) - set(external_before[name]) - allowed_external[name]
        )
        if entries:
            unexpected[name] = tuple(sorted(entries))
    _reject_unexpected_external(unexpected, scanned_roots)


_PRODUCT_ENVIRONMENT_KEYS = (
    "LOCALAPPDATA",
    "APPDATA",
    "USERPROFILE",
    "HOME",
    "TEMP",
    "TMP",
)


def _product_environment(environment: dict[str, str]) -> dict[str, str]:
    """Extract exactly the six monitored product path keys; nothing else may
    ever reach the transition command line."""
    product = {name: environment.get(name, "") for name in _PRODUCT_ENVIRONMENT_KEYS}
    missing = sorted(name for name, value in product.items() if not value)
    if missing:
        raise RuntimeError(
            f"WebView transition requires the monitored product keys: {missing}"
        )
    return product


def _rollback_portable(
    root: Path, work_dir: Path, old_package: Path, environment: dict[str, str]
) -> None:
    # Only the harness's fresh extraction may be changed; moving cached forward
    # packages prevents VelopackApp from immediately upgrading the restored app.
    owned = work_dir.resolve(strict=True)
    installation = root.resolve(strict=True)
    if installation != owned / "installed-old":
        raise RuntimeError("Rollback requires the harness-owned Portable extraction")
    packages = installation / "packages"
    if packages.exists():
        if packages.is_symlink() or packages.is_junction():
            raise RuntimeError("Rollback package cache must not be a reparse point")
        if packages.resolve(strict=True).parent != installation:
            raise RuntimeError("Rollback package cache escaped the owned installation")
        packages.rename(owned / "forward-packages")
    # Velopack deletes its package input after some failures; never give it
    # the preserved historical baseline or a shared cache entry.
    rollback_package = owned / "rollback-full.nupkg"
    if rollback_package.exists():
        raise RuntimeError("Disposable rollback package already exists")
    shutil.copyfile(old_package, rollback_package)
    result = subprocess.run(
        [
            str(installation / "Update.exe"),
            "apply",
            "--norestart",
            "--package",
            str(rollback_package),
        ],
        cwd=installation,
        env=environment,
        capture_output=True,
        timeout=600,
    )
    (owned / "rollback-updater.log").write_bytes(result.stdout + result.stderr)
    result.check_returncode()


def verify_portable_delta_e2e(
    old_portable: Path,
    old_package: Path | None,
    new_feed: Path,
    target_version: str,
    work_dir: Path,
    *,
    timeout: float,
    require_package_type: str,
    legacy_state_layout: bool = False,
) -> None:
    if os.name != "nt":
        raise RuntimeError("Velopack Portable delta E2E is Windows-only")
    if re.fullmatch(r"\d+\.\d+\.\d+", target_version) is None:
        raise RuntimeError(f"invalid target version: {target_version}")
    if work_dir.exists():
        raise RuntimeError("Portable delta E2E work directory must not already exist")

    root = _extract_safe(old_portable, work_dir / "installed-old")
    old_version = _installed_version(root) if old_package is not None else None
    if old_package is not None:
        packages = root / "packages"
        packages.mkdir()
        shutil.copy2(old_package, packages / old_package.name)

    nonce = uuid.uuid4().hex
    state_markers: dict[Path, bytes] = {}
    marker_source = (
        root / "current" / "state" if legacy_state_layout else root / "state"
    )
    for name in ("config", "logs", "cache", "models", "runtimes"):
        marker_name = f"delta-e2e-{nonce}.marker"
        marker = marker_source / name / marker_name
        marker.parent.mkdir(parents=True, exist_ok=True)
        payload = f"{name}:{nonce}".encode()
        marker.write_bytes(payload)
        state_markers[Path("state") / name / marker_name] = payload
    replaced = root / "current" / "app" / f"old-content-{nonce}.marker"
    replaced.write_text("must be replaced", encoding="utf-8")
    result = root / "state" / f"delta-e2e-result-{nonce}.json"

    external_environment, scanned_roots = _external_layout(work_dir)
    external_before = {
        name: _tree_inventory(path) for name, path in scanned_roots.items()
    }
    probe_environment = {
        key: value
        for key, value in os.environ.items()
        if not key.upper().startswith(("VIBEOCR_", "WEBVIEW2_"))
    }
    probe_environment.update(
        {name: str(path) for name, path in external_environment.items()}
    )
    handler = partial(_RecordingHandler, directory=str(new_feed))
    _RecordingHandler.requests = []
    server = ThreadingHTTPServer(("127.0.0.1", 0), handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()

    environment = os.environ.copy()
    environment.update(
        {
            "VIBEOCR_NEXT_TEST_MODE": "artifact-smoke",
            "VIBEOCR_SELF_TEST_NONCE": nonce,
            "VIBEOCR_SELF_TEST_VELOPACK_UPDATE": "1",
            "VIBEOCR_SELF_TEST_TARGET_VERSION": target_version,
            "VIBEOCR_SELF_TEST_RESULT": str(result),
            "VIBEOCR_SELF_TEST_INSTALL_ROOT": str(root),
            "VIBEOCR_SELF_TEST_UPDATE_FEED": (
                f"http://127.0.0.1:{server.server_port}/"
            ),
            "NO_PROXY": "127.0.0.1,localhost",
            "no_proxy": "127.0.0.1,localhost",
            **{name: str(path) for name, path in external_environment.items()},
        }
    )
    for name in (
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "http_proxy",
        "https_proxy",
        "all_proxy",
    ):
        environment.pop(name, None)

    process = subprocess.Popen([str(root / "VibeOCR.exe")], cwd=root, env=environment)
    try:
        evidence = _wait_for_result(result, timeout, root / "state" / "logs")
        _wait_for_evidence_writer_exit(evidence, timeout=15.0)
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=15)

    if evidence.get("installed_version") != target_version:
        raise RuntimeError(f"restarted app reported wrong version: {evidence}")
    if not any(
        path.endswith(f"-{require_package_type}.nupkg")
        for path in _RecordingHandler.requests
    ):
        raise RuntimeError(
            f"Velopack did not request {require_package_type} package: "
            f"{_RecordingHandler.requests}"
        )
    if require_package_type == "delta" and any(
        path.endswith(f"-{target_version}-full.nupkg")
        for path in _RecordingHandler.requests
    ):
        raise RuntimeError(
            "Velopack requested the target full package after delta: "
            f"{_RecordingHandler.requests}"
        )
    if replaced.exists():
        raise RuntimeError("Velopack apply did not replace the old current directory")
    for relative, payload in state_markers.items():
        if (root / relative).read_bytes() != payload:
            raise RuntimeError(f"Velopack apply lost state marker: {relative}")
    if old_package is not None:
        _rollback_portable(root, work_dir, old_package, probe_environment)
        if _installed_version(root) != old_version:
            raise RuntimeError("Rollback did not restore the original package version")
        for relative, payload in state_markers.items():
            if (root / relative).read_bytes() != payload:
                raise RuntimeError(f"Rollback lost state marker: {relative}")
    _check_external_writes(scanned_roots, external_before, work_dir)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--old-portable", type=Path, required=True)
    parser.add_argument("--old-package", type=Path)
    parser.add_argument("--new-feed", type=Path, required=True)
    parser.add_argument("--target-version", required=True)
    parser.add_argument("--work-dir", type=Path, required=True)
    parser.add_argument("--timeout", type=float, default=600)
    parser.add_argument(
        "--require-package-type", choices=("full", "delta"), required=True
    )
    parser.add_argument("--legacy-state-layout", action="store_true")
    args = parser.parse_args()
    verify_portable_delta_e2e(
        args.old_portable.resolve(strict=True),
        args.old_package.resolve(strict=True) if args.old_package is not None else None,
        args.new_feed.resolve(strict=True),
        args.target_version,
        args.work_dir.resolve(),
        timeout=args.timeout,
        require_package_type=args.require_package_type,
        legacy_state_layout=args.legacy_state_layout,
    )
    print(f"Velopack Portable {args.require_package_type} E2E passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
