from __future__ import annotations

import json
import os
import shutil
import subprocess
from pathlib import Path

import pytest

from scripts import verify_velopack_portable_delta_e2e as portable


def _caller_environment(tmp_path: Path) -> dict[str, str]:
    """The real call shape: a full filtered caller environment, not six keys."""
    product = {
        name: str(tmp_path / "product" / name)
        for name in ("LOCALAPPDATA", "APPDATA", "USERPROFILE", "HOME", "TEMP", "TMP")
    }
    return {
        "PATH": str(tmp_path / "bin"),
        "SYSTEMROOT": r"C:\\Windows",
        "SECRET_SENTINEL": str(tmp_path / "secret" / "token"),
        "VIBEOCR_SELF_TEST_NONCE": "must-not-reach-tooling",
        "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS": "--removed",
        **product,
    }


def test_rollback_preserves_forward_packages_and_uses_the_original_payload(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    root = tmp_path / "installed-old"
    packages = root / "packages"
    packages.mkdir(parents=True)
    (packages / "new-full.nupkg").write_bytes(b"new")
    old = tmp_path / "original-full.nupkg"
    old.write_bytes(b"original")
    calls = []

    def run(arguments: list[str], **kwargs: object) -> subprocess.CompletedProcess:
        calls.append((arguments, kwargs))
        assert not packages.exists()
        return subprocess.CompletedProcess(arguments, 0, b"applied", b"")

    monkeypatch.setattr(portable.subprocess, "run", run)
    environment = {"LOCALAPPDATA": str(tmp_path / "external")}
    portable._rollback_portable(root, tmp_path, old, environment)
    assert (tmp_path / "forward-packages/new-full.nupkg").read_bytes() == b"new"
    assert old.read_bytes() == b"original"
    assert calls == [
        (
            [
                str(root / "Update.exe"),
                "apply",
                "--norestart",
                "--package",
                str(tmp_path / "rollback-full.nupkg"),
            ],
            {"cwd": root, "env": environment, "capture_output": True, "timeout": 600},
        )
    ]


def test_rollback_rejects_an_unowned_installation_before_moving_files(
    tmp_path: Path,
) -> None:
    other = tmp_path / "user-installation"
    packages = other / "packages"
    packages.mkdir(parents=True)
    marker = packages / "keep"
    marker.write_bytes(b"keep")
    with pytest.raises(RuntimeError, match="harness-owned"):
        portable._rollback_portable(other, tmp_path, tmp_path / "old.nupkg", {})
    assert marker.read_bytes() == b"keep"


def test_rollback_propagates_updater_failure(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    root = tmp_path / "installed-old"
    root.mkdir()
    old = tmp_path / "original-full.nupkg"
    old.write_bytes(b"historical baseline")

    def delete_input_and_fail(
        arguments: list[str], **kwargs: object
    ) -> subprocess.CompletedProcess:
        Path(arguments[-1]).unlink()
        return subprocess.CompletedProcess(arguments, 1, b"", b"failed")

    monkeypatch.setattr(portable.subprocess, "run", delete_input_and_fail)
    with pytest.raises(subprocess.CalledProcessError):
        portable._rollback_portable(root, tmp_path, old, {})
    assert old.read_bytes() == b"historical baseline"
    assert (tmp_path / "rollback-updater.log").read_bytes() == b"failed"


def _snapshot(scanned: dict[str, Path]) -> dict[str, tuple[str, ...]]:
    return {name: portable._tree_inventory(path) for name, path in scanned.items()}


def test_external_layout_normalizes_appdata_inside_the_single_profile(
    tmp_path: Path,
) -> None:
    environment, scanned = portable._external_layout(tmp_path)
    assert set(scanned) == {"profile", "temp"}
    assert environment["USERPROFILE"] == environment["HOME"] == scanned["profile"]
    assert environment["LOCALAPPDATA"] == scanned["profile"] / "AppData" / "Local"
    assert environment["APPDATA"] == scanned["profile"] / "AppData" / "Roaming"
    assert environment["TEMP"] == environment["TMP"] == scanned["temp"]
    assert (scanned["profile"] / "AppData" / "Local").is_dir()
    assert (scanned["profile"] / "AppData" / "Roaming").is_dir()


def test_only_the_exact_velopack_log_is_allowed_in_the_profile(tmp_path: Path) -> None:
    _, scanned = portable._external_layout(tmp_path)
    before = _snapshot(scanned)
    velopack = scanned["profile"] / "AppData" / "Local" / "velopack"
    velopack.mkdir(parents=True)
    (velopack / "velopack_VibeOCRNext.log").write_text("log", encoding="utf-8")
    portable._check_external_writes(scanned, before, tmp_path)

    (velopack / "velopack_VibeOCRNext-other.log").write_text("log", encoding="utf-8")
    local_low = scanned["profile"] / "AppData" / "LocalLow"
    local_low.mkdir()
    (local_low / "sentinel").write_text("identity", encoding="utf-8")
    with pytest.raises(RuntimeError, match="outside the Portable root"):
        portable._check_external_writes(scanned, before, tmp_path)


def test_webview_tooling_environment_keeps_caller_env_and_normalizes_layout(
    tmp_path: Path,
) -> None:
    caller = _caller_environment(tmp_path)
    product_values = {name: caller[name] for name in portable._PRODUCT_ENVIRONMENT_KEYS}
    tooling_profile = tmp_path / "work" / "webview-transition" / "tooling-profile"
    tooling = portable._webview_tooling_environment(caller, tooling_profile)
    home = tooling_profile / "home"
    # The caller's non-path, already-filtered entries survive (incl. PATH);
    # VIBEOCR_/WEBVIEW2_ never reach the tooling environment.
    assert tooling["PATH"] == caller["PATH"]
    assert tooling["SYSTEMROOT"] == caller["SYSTEMROOT"]
    assert tooling["SECRET_SENTINEL"] == caller["SECRET_SENTINEL"]
    assert "VIBEOCR_SELF_TEST_NONCE" not in tooling
    assert "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS" not in tooling
    # All six tooling paths exist and mirror the product's normalized layout.
    assert tooling["USERPROFILE"] == tooling["HOME"] == str(home)
    assert tooling["LOCALAPPDATA"] == str(home / "AppData" / "Local")
    assert tooling["APPDATA"] == str(home / "AppData" / "Roaming")
    assert tooling["TEMP"] == tooling["TMP"] == str(tooling_profile / "temp")
    for path in (
        home,
        home / "AppData" / "Local",
        home / "AppData" / "Roaming",
        tooling_profile / "temp",
    ):
        assert path.is_dir()
    assert not (set(tooling.values()) & set(product_values.values()))


def test_webview_probe_forwards_only_the_six_product_keys(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    calls = []

    def record(arguments, **kwargs):
        calls.append((arguments, kwargs))
        return subprocess.CompletedProcess(arguments, 0, b"", b"")

    monkeypatch.setattr(portable.subprocess, "run", record)
    caller = _caller_environment(tmp_path)
    portable._webview_probe(
        tmp_path / "node.exe",
        tmp_path / "root",
        tmp_path / "evidence",
        "before",
        "0123456789abcdef0123456789abcdef",
        caller,
    )
    arguments, kwargs = calls[0]
    forwarded = json.loads(arguments[arguments.index("--product-environment") + 1])
    assert set(forwarded) == set(portable._PRODUCT_ENVIRONMENT_KEYS)
    for name, value in forwarded.items():
        assert value == caller[name]
    # The command line carries nothing unrelated: no key/value from the rest
    # of the caller environment ever appears in any argument.
    for argument in arguments:
        assert "SECRET_SENTINEL" not in argument
        assert caller["SECRET_SENTINEL"] not in argument
        assert caller["PATH"] not in argument
    environment = kwargs["env"]
    assert environment["PATH"] == caller["PATH"]
    for name, value in forwarded.items():
        assert environment.get(name) != value
    assert environment["USERPROFILE"] == str(
        tmp_path / "evidence" / "tooling-profile" / "home"
    )


def test_product_environment_extracts_exactly_the_six_keys(tmp_path: Path) -> None:
    caller = _caller_environment(tmp_path)
    product = portable._product_environment(caller)
    assert set(product) == set(portable._PRODUCT_ENVIRONMENT_KEYS)
    assert product["LOCALAPPDATA"] == caller["LOCALAPPDATA"]
    assert "PATH" not in product and "SECRET_SENTINEL" not in product
    del caller["HOME"]
    with pytest.raises(RuntimeError, match="HOME"):
        portable._product_environment(caller)


def test_tooling_sentinel_does_not_pollute_the_monitored_roots(
    tmp_path: Path,
) -> None:
    _, scanned = portable._external_layout(tmp_path)
    before = _snapshot(scanned)
    tooling = portable._webview_tooling_environment(
        _caller_environment(tmp_path),
        tmp_path / "work" / "webview-transition" / "tooling-profile",
    )
    tooling_sentinel = Path(tooling["USERPROFILE"]) / "sentinel"
    tooling_sentinel.write_text("tooling", encoding="utf-8")
    portable._check_external_writes(scanned, before, tmp_path)

    product_sentinel = scanned["profile"] / "AppData" / "Local" / "sentinel"
    product_sentinel.write_text("tooling", encoding="utf-8")
    with pytest.raises(RuntimeError, match="outside the Portable root"):
        portable._check_external_writes(scanned, before, tmp_path)


def test_unknown_directories_are_rejected_without_being_entered(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    _, scanned = portable._external_layout(tmp_path)
    before = _snapshot(scanned)
    unknown = scanned["profile"] / "AppData" / "Local" / "SomeIdentityCache"
    unknown.mkdir(parents=True)
    (unknown / "cache.bin").write_text("identity", encoding="utf-8")
    original_iterdir = Path.iterdir

    def guarded_iterdir(self, *args, **kwargs):
        if self == unknown:
            raise AssertionError("unknown directories must not be enumerated")
        return original_iterdir(self, *args, **kwargs)

    monkeypatch.setattr(Path, "iterdir", guarded_iterdir)
    with pytest.raises(RuntimeError, match="outside the Portable root"):
        portable._check_external_writes(scanned, before, tmp_path)


def test_reparse_point_under_an_allowed_name_is_rejected(tmp_path: Path) -> None:
    import _winapi

    create_junction = getattr(_winapi, "CreateJunction", None)
    if create_junction is None or not hasattr(os.path, "isjunction"):
        pytest.skip("Windows junction creation unavailable on this Python")
    _, scanned = portable._external_layout(tmp_path)
    before = _snapshot(scanned)
    target = tmp_path / "junction-target"
    target.mkdir()
    (target / "leak.txt").write_text("x", encoding="utf-8")
    allowed_name = scanned["profile"] / "AppData" / "Local" / "Microsoft"
    create_junction(str(target), str(allowed_name))
    assert os.path.isjunction(allowed_name)
    with pytest.raises(RuntimeError, match="outside the Portable root"):
        portable._check_external_writes(scanned, before, tmp_path)


def test_swapped_junction_roots_are_rejected_before_any_enumeration(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    import _winapi

    create_junction = getattr(_winapi, "CreateJunction", None)
    if create_junction is None or not hasattr(os.path, "isjunction"):
        pytest.skip("Windows junction creation unavailable on this Python")

    def guarded_iterdir(self, *args, **kwargs):
        raise AssertionError("a swapped root must be rejected before enumeration")

    monkeypatch.setattr(Path, "iterdir", guarded_iterdir)
    for swap in ("profile-root", "external-parent"):
        case = tmp_path / swap
        _, scanned = portable._external_layout(case)
        before = _snapshot(scanned)
        empty = case / "elsewhere"
        empty.mkdir()
        if swap == "profile-root":
            shutil.rmtree(scanned["profile"])
            create_junction(str(empty), str(scanned["profile"]))
            assert os.path.isjunction(scanned["profile"])
        else:
            shutil.rmtree(case / "external")
            create_junction(str(empty), str(case / "external"))
            assert os.path.isjunction(case / "external")
        with pytest.raises(RuntimeError, match="outside the Portable root"):
            portable._check_external_writes(scanned, before, case)


def test_missing_or_wrong_type_scan_roots_are_rejected(tmp_path: Path) -> None:
    _, scanned = portable._external_layout(tmp_path)
    before = _snapshot(scanned)
    shutil.rmtree(scanned["temp"])
    with pytest.raises(RuntimeError, match="outside the Portable root"):
        portable._check_external_writes(scanned, before, tmp_path)
    scanned["temp"].write_text("now a plain file", encoding="utf-8")
    with pytest.raises(RuntimeError, match="outside the Portable root"):
        portable._check_external_writes(scanned, before, tmp_path)
