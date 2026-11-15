"""#321 WebView2 Fixed Version 锁 consumer（expand_webview2_runtime.ps1）回归。

覆盖廉价失败路径：CAB 哈希失配、外部 override 缺失、锁字段缺失时
fail closed，且绝不留下半成品 WebView2Runtime 目标目录。完整 CAB
展开由 release build 在真实锁与官方 CAB 上执行。
"""

from __future__ import annotations

import hashlib
import json
import os
import subprocess
from pathlib import Path

from scripts.check_quality import resolve_executable
from scripts.generate_webview2_lock_version import generate as generate_lock_version
from scripts.generate_webview2_lock_version import locked_runtime_version

REPO_ROOT = Path(__file__).resolve().parents[2]
SCRIPT = REPO_ROOT / "scripts/expand_webview2_runtime.ps1"


def _run_script(
    publish_root: Path,
    *,
    cab: Path | None,
    lock_file: Path | None,
) -> subprocess.CompletedProcess[str]:
    environment = dict(os.environ)
    environment.pop("VIBEOCR_WEBVIEW2_CAB", None)
    if cab is not None:
        environment["VIBEOCR_WEBVIEW2_CAB"] = str(cab)
    command = [resolve_executable("pwsh"), "-NoProfile", "-File", str(SCRIPT)]
    if lock_file is not None:
        command += ["-LockFile", str(lock_file)]
    return subprocess.run(
        command + ["-AppPublishRoot", str(publish_root)],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=environment,
        timeout=120,
        cwd=REPO_ROOT,
    )


def _tiny_lock(tmp_path: Path, **overrides: object) -> Path:
    lock = json.loads(
        (REPO_ROOT / "config/runtime/webview2-runtime.lock.json").read_text(
            encoding="utf-8"
        )
    )
    lock.update(overrides)
    path = tmp_path / "webview2-runtime.lock.json"
    path.write_text(json.dumps(lock), encoding="utf-8")
    return path


def test_generated_lock_version_is_derived_from_the_single_lock(
    tmp_path: Path,
) -> None:
    """#321 F1：生成常量与锁同源，无第二处手写 pin。"""
    version = locked_runtime_version()
    output = generate_lock_version(tmp_path / "obj" / "WebView2LockVersion.g.cs")
    content = output.read_text(encoding="utf-8")
    assert f'LockedRuntimeVersion = "{version}"' in content
    lock = json.loads(
        (REPO_ROOT / "config/runtime/webview2-runtime.lock.json").read_text(
            encoding="utf-8"
        )
    )
    assert version == lock["version"]
    # 幂等：重复生成不变化。
    assert generate_lock_version(output).read_text(encoding="utf-8") == content


def test_fails_closed_when_override_cab_is_missing(tmp_path: Path) -> None:
    publish_root = tmp_path / "app-publish"
    publish_root.mkdir()
    result = _run_script(
        publish_root,
        cab=tmp_path / "missing.cab",
        lock_file=None,
    )
    assert result.returncode != 0
    assert "missing" in (result.stderr + result.stdout)
    assert not (publish_root / "WebView2Runtime").exists()


def test_fails_closed_on_cab_hash_mismatch(tmp_path: Path) -> None:
    publish_root = tmp_path / "app-publish"
    publish_root.mkdir()
    fake = tmp_path / "Microsoft.WebView2.FixedVersionRuntime.156.0.4314.58.x64.cab"
    fake.write_bytes(b"not-the-locked-cab")
    result = _run_script(publish_root, cab=fake, lock_file=None)
    assert result.returncode != 0
    assert "webview2-runtime.lock.json" in (result.stderr + result.stdout)
    assert not (publish_root / "WebView2Runtime").exists()


def test_fails_closed_on_cab_size_mismatch(tmp_path: Path) -> None:
    publish_root = tmp_path / "app-publish"
    publish_root.mkdir()
    fake = tmp_path / "Microsoft.WebView2.FixedVersionRuntime.156.0.4314.58.x64.cab"
    payload = b"x" * 128
    fake.write_bytes(payload)
    # 哈希与实际文件一致但锁定的 size 不一致：下载/缓存链路外仍 fail closed。
    lock_file = _tiny_lock(
        tmp_path,
        size_bytes=129,
        sha256=hashlib.sha256(payload).hexdigest(),
    )
    result = _run_script(publish_root, cab=fake, lock_file=lock_file)
    assert result.returncode != 0
    assert not (publish_root / "WebView2Runtime").exists()


def test_fails_closed_when_lock_fields_are_missing(tmp_path: Path) -> None:
    publish_root = tmp_path / "app-publish"
    publish_root.mkdir()
    lock_file = tmp_path / "broken-lock.json"
    lock_file.write_text(json.dumps({"version": "156.0.4314.58"}), encoding="utf-8")
    fake = tmp_path / "Microsoft.WebView2.FixedVersionRuntime.156.0.4314.58.x64.cab"
    fake.write_bytes(b"irrelevant")
    result = _run_script(publish_root, cab=fake, lock_file=lock_file)
    assert result.returncode != 0
    assert not (publish_root / "WebView2Runtime").exists()
