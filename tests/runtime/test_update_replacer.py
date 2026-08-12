from __future__ import annotations

import hashlib
import json
import zipfile
from pathlib import Path

import pytest

from scripts import update_replacer
from scripts.product_layout import ROOT_ALLOWLIST, load_product_layout


def _write_product(root: Path, marker: str) -> Path:
    files = {
        "VibeOCR.exe": marker,
        "Velopack.dll": "velopack",
        "LICENSE": "license",
        "CHANGELOG.md": marker,
        "app/VibeOCR.WinUI.exe": marker,
        "app/VibeOCR.WinUI.dll": marker,
        "app/VibeOCR.WinUI.pri": "pri",
        "app/App.xbf": "xbf",
        "app/MainWindow.xbf": "xbf",
        "app/WebAssets/index.html": "<html></html>",
        "app/tools/updater.exe": "updater",
        "runtime/installer/vibeocr-runtime-installer.exe": "installer",
        "runtime/backend/protocol-release-manifest.json": "protocol",
        "runtime/backend/release-manifest.json": "backend",
    }
    for relative, content in files.items():
        target = root / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content, encoding="utf-8")
    descriptor = {
        "schema_version": 1,
        "product_id": "vibeocr",
        "public_entry": "VibeOCR.exe",
        "roots": {"app": "app", "runtime": "runtime", "metadata": "app/metadata"},
        "app": {
            "entry": "app/VibeOCR.WinUI.exe",
            "web_assets": "app/WebAssets",
            "updater": "app/tools/updater.exe",
        },
        "runtime": {
            "manifest": "runtime/backend/runtime-manifest.json",
            "installer": "runtime/installer/vibeocr-runtime-installer.exe",
        },
        "metadata": {
            "component_lock": "app/metadata/component-lock.json",
            "component_identities": "app/metadata/component-identities.json",
            "release_manifest": "app/metadata/product-release-manifest.json",
        },
        "user_data": {"known_folder": "LocalApplicationData", "relative": "VibeOCR"},
    }
    layout_path = root / "app/metadata/product-layout.json"
    layout_path.parent.mkdir(parents=True, exist_ok=True)
    layout_path.write_text(json.dumps(descriptor), encoding="utf-8")
    runtime_manifest = {
        "installer": {
            "executable_sha256": hashlib.sha256(b"installer").hexdigest(),
        }
    }
    runtime_path = root / "runtime/backend/runtime-manifest.json"
    runtime_path.write_text(json.dumps(runtime_manifest), encoding="utf-8")
    protocol_path = root / "runtime/backend/protocol-release-manifest.json"
    backend_path = root / "runtime/backend/release-manifest.json"
    component_lock = {
        "backend": {
            "runtime_manifest_sha256": hashlib.sha256(
                runtime_path.read_bytes()
            ).hexdigest()
        },
        "protocol": {
            "manifest_sha256": hashlib.sha256(protocol_path.read_bytes()).hexdigest()
        },
    }
    lock_path = root / "app/metadata/component-lock.json"
    lock_path.write_text(json.dumps(component_lock), encoding="utf-8")
    identities = {
        "component_lock_sha256": hashlib.sha256(lock_path.read_bytes()).hexdigest(),
        "backend": {
            "runtime_manifest_sha256": hashlib.sha256(
                runtime_path.read_bytes()
            ).hexdigest(),
            "release_manifest_sha256": hashlib.sha256(
                backend_path.read_bytes()
            ).hexdigest(),
        },
        "protocol": {
            "release_manifest_sha256": hashlib.sha256(
                protocol_path.read_bytes()
            ).hexdigest()
        },
    }
    identities_path = root / "app/metadata/component-identities.json"
    identities_path.write_text(json.dumps(identities), encoding="utf-8")
    release_path = root / "app/metadata/product-release-manifest.json"
    release_files = {
        path.relative_to(root).as_posix(): {
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "size": path.stat().st_size,
        }
        for path in sorted(root.rglob("*"))
        if path.is_file() and path != release_path
    }
    release_path.write_text(
        json.dumps(
            {
                "schema_version": 1,
                "frontend": "next",
                "component_lock_sha256": hashlib.sha256(
                    lock_path.read_bytes()
                ).hexdigest(),
                "files": release_files,
            }
        ),
        encoding="utf-8",
    )
    load_product_layout(root)
    return root


def _archive_product(product: Path, package: Path) -> None:
    with zipfile.ZipFile(package, "w") as archive:
        for path in sorted(product.rglob("*")):
            if path.is_file():
                archive.write(
                    path, (Path("VibeOCR") / path.relative_to(product)).as_posix()
                )
    digest = hashlib.sha256(package.read_bytes()).hexdigest()
    Path(f"{package}.sha256").write_text(
        f"{digest}  {package.name}\n", encoding="utf-8"
    )


def test_replace_deployment_switches_only_product_entries(tmp_path: Path) -> None:
    install = _write_product(tmp_path / "install", "old")
    candidate = _write_product(tmp_path / "candidate", "new")
    user_data = tmp_path / "user-data"
    user_data.mkdir()
    (user_data / "settings.json").write_text("keep", encoding="utf-8")

    update_replacer.replace_deployment(candidate, install, tmp_path / "rollback")

    assert {path.name for path in install.iterdir()} == ROOT_ALLOWLIST
    assert (install / "VibeOCR.exe").read_text(encoding="utf-8") == "new"
    assert (user_data / "settings.json").read_text(encoding="utf-8") == "keep"


def test_replace_deployment_rolls_back_partial_failure(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    install = _write_product(tmp_path / "install", "old")
    candidate = _write_product(tmp_path / "candidate", "new")
    calls = 0

    def fail_once(source: Path, destination: Path) -> None:
        nonlocal calls
        calls += 1
        if calls == 7:
            raise OSError("injected move failure")
        source.replace(destination)

    monkeypatch.setattr(update_replacer, "_move_path", fail_once)

    with pytest.raises(update_replacer.UpdateError, match="ApplyFailed"):
        update_replacer.replace_deployment(candidate, install, tmp_path / "rollback")

    load_product_layout(install)
    assert (install / "VibeOCR.exe").read_text(encoding="utf-8") == "old"


def test_health_failure_restores_old_product_and_relaunches_it(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    install = _write_product(tmp_path / "install", "old")
    candidate = _write_product(tmp_path / "candidate", "new")
    package = tmp_path / "VibeOCR-v1.0.0-win64.zip"
    _archive_product(candidate, package)
    launches: list[list[str]] = []

    class Process:
        pass

    def record_launch(command: list[str], **_: object) -> Process:
        launches.append(command)
        return Process()

    def unhealthy(_: Path, __: object, ___: Path) -> None:
        raise update_replacer.UpdateError("HealthTimeout", "injected")

    monkeypatch.setattr(update_replacer.time, "sleep", lambda _: None)
    monkeypatch.setattr(update_replacer.subprocess, "Popen", record_launch)

    result = update_replacer.run_replacement(
        package,
        install,
        tmp_path / "user-data",
        launch_args=("--profile", "production"),
        launch=unhealthy,
    )

    assert result == 1
    load_product_layout(install)
    assert (install / "VibeOCR.exe").read_text(encoding="utf-8") == "old"
    assert launches == [[str(install / "VibeOCR.exe"), "--profile", "production"]]


def test_run_replacement_keeps_atomic_moves_outside_user_data(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    install = _write_product(tmp_path / "install", "old")
    candidate = _write_product(tmp_path / "candidate", "new")
    package = tmp_path / "VibeOCR-v1.0.0-win64.zip"
    _archive_product(candidate, package)
    user_data = tmp_path / "user-data"
    user_data.mkdir()
    (user_data / "settings.json").write_text("keep", encoding="utf-8")
    moves: list[tuple[Path, Path]] = []

    def record_same_volume_move(source: Path, destination: Path) -> None:
        assert user_data not in source.parents
        assert user_data not in destination.parents
        moves.append((source, destination))
        source.replace(destination)

    monkeypatch.setattr(update_replacer, "_move_path", record_same_volume_move)
    monkeypatch.setattr(update_replacer.time, "sleep", lambda _: None)

    result = update_replacer.run_replacement(
        package,
        install,
        user_data,
        launch=lambda *_: None,
    )

    assert result == 0
    assert len(moves) == len(ROOT_ALLOWLIST) * 2
    load_product_layout(install)
    assert (install / "VibeOCR.exe").read_text(encoding="utf-8") == "new"
    assert (user_data / "settings.json").read_text(encoding="utf-8") == "keep"
    assert not list(tmp_path.glob(".install.stage-*"))
    assert not list(tmp_path.glob(".install.rollback-*"))


def test_release_closure_tamper_fails_before_any_install_move(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    install = _write_product(tmp_path / "install", "old")
    candidate = _write_product(tmp_path / "candidate", "new")
    (candidate / "app/VibeOCR.WinUI.exe").write_text("tampered", encoding="utf-8")
    package = tmp_path / "VibeOCR-v1.0.0-win64.zip"
    _archive_product(candidate, package)
    failures: list[str] = []

    def forbid_move(_: Path, __: Path) -> None:
        raise AssertionError("install deployment was touched")

    monkeypatch.setattr(update_replacer, "_move_path", forbid_move)

    result = update_replacer.run_replacement(
        package,
        install,
        tmp_path / "user-data",
        on_failure=failures.append,
    )

    assert result == 1
    assert failures and "PackageInvalid" in failures[0]
    load_product_layout(install)
    assert (install / "VibeOCR.exe").read_text(encoding="utf-8") == "old"


def test_run_replacement_rejects_user_data_nested_in_install(tmp_path: Path) -> None:
    install = _write_product(tmp_path / "install", "old")
    candidate = _write_product(tmp_path / "candidate", "new")
    package = tmp_path / "VibeOCR-v1.0.0-win64.zip"
    _archive_product(candidate, package)
    failures: list[str] = []

    result = update_replacer.run_replacement(
        package,
        install,
        install / "data",
        on_failure=failures.append,
    )

    assert result == 1
    assert failures and failures[0].startswith("LayoutInvalid:")
    assert (install / "VibeOCR.exe").read_text(encoding="utf-8") == "old"
    load_product_layout(install)
