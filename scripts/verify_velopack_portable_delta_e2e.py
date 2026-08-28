"""Run a real adjacent-delta Velopack Portable apply/restart E2E on Windows."""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import threading
import time
import uuid
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
            content = logs[-1].read_text(encoding="utf-8", errors="replace")
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

    external = work_dir / "external"
    external_roots = {
        "LOCALAPPDATA": external / "local-app-data",
        "APPDATA": external / "roaming-app-data",
        "USERPROFILE": external / "user-profile",
        "TEMP": external / "temp",
        "TMP": external / "temp",
        "HOME": external / "user-profile",
    }
    for path in set(external_roots.values()):
        path.mkdir(parents=True, exist_ok=True)
    external_before = {
        name: _tree_inventory(path) for name, path in external_roots.items()
    }

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
            **{name: str(path) for name, path in external_roots.items()},
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
    external_after = {
        name: _tree_inventory(path) for name, path in external_roots.items()
    }
    # Velopack 1.2 与 .NET Framework 会创建固定日志和空的 known-folder
    # 骨架。它们不是产品状态；逐项 allowlist，避免把任意外写入放行。
    allowed_external = {
        "LOCALAPPDATA": {
            "Microsoft/",
            "Microsoft/Windows/",
            "Microsoft/Windows/Caches/",
            "velopack/",
            "velopack/velopack_VibeOCRNext.log",
        },
        "APPDATA": set(),
        "USERPROFILE": {"AppData/", "AppData/Roaming/"},
        "TEMP": {"velopack_VibeOCRNext.log"},
        "TMP": {"velopack_VibeOCRNext.log"},
        "HOME": {"AppData/", "AppData/Roaming/"},
    }
    unexpected = {
        name: tuple(
            sorted(set(inventory) - set(external_before[name]) - allowed_external[name])
        )
        for name, inventory in external_after.items()
    }
    unexpected = {name: paths for name, paths in unexpected.items() if paths}
    if unexpected:
        raise RuntimeError(
            "Velopack Portable E2E wrote unexpected data outside the Portable root: "
            f"{unexpected}"
        )


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
    print("Velopack Portable adjacent-delta E2E passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
