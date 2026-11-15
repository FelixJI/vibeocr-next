from __future__ import annotations

from pathlib import Path

import pytest

from scripts import verify_velopack_portable_delta_e2e as portable


def test_result_wait_tolerates_log_removed_after_enumeration(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    result = tmp_path / "result.json"
    log = tmp_path / "bootstrapper-test.log"
    log.write_text("updating", encoding="utf-8")
    original_read = Path.read_text

    def read(path: Path, *args: object, **kwargs: object) -> str:
        if path == log:
            log.unlink()
        return original_read(path, *args, **kwargs)

    def finish(_seconds: float) -> None:
        result.write_text('{"status": "ok"}', encoding="utf-8")

    monkeypatch.setattr(Path, "read_text", read)
    monkeypatch.setattr(portable.time, "sleep", finish)
    assert portable._wait_for_result(result, 1, tmp_path) == {"status": "ok"}


def test_result_wait_preserves_bootstrapper_errors(tmp_path: Path) -> None:
    (tmp_path / "bootstrapper-test.log").write_text(
        "[ERROR] update failed", encoding="utf-8"
    )
    with pytest.raises(RuntimeError, match="update failed"):
        portable._wait_for_result(tmp_path / "result.json", 1, tmp_path)


def test_result_wait_does_not_hide_log_permission_errors(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    (tmp_path / "bootstrapper-test.log").write_text("updating", encoding="utf-8")

    def denied(_path: Path, *args: object, **kwargs: object) -> str:
        raise PermissionError("denied")

    monkeypatch.setattr(Path, "read_text", denied)
    with pytest.raises(PermissionError, match="denied"):
        portable._wait_for_result(tmp_path / "result.json", 1, tmp_path)


def test_result_wait_without_evidence_still_times_out(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    ticks = iter((0.0, 0.5, 1.5))
    monkeypatch.setattr(portable.time, "monotonic", lambda: next(ticks))
    monkeypatch.setattr(portable.time, "sleep", lambda _seconds: None)
    with pytest.raises(RuntimeError, match="timed out"):
        portable._wait_for_result(tmp_path / "result.json", 1, tmp_path)


def test_unexpected_external_rejection_hides_paths_and_merges_aliases(
    tmp_path: Path,
) -> None:
    # Mirrors the harness layout: USERPROFILE/HOME and TEMP/TMP alias one
    # physical directory each.
    external_roots = {
        "USERPROFILE": tmp_path / "user-profile",
        "HOME": tmp_path / "user-profile",
        "TEMP": tmp_path / "temp",
        "TMP": tmp_path / "temp",
    }
    nested_secret = "Microsoft/Windows/Caches/winstore-cache-identity"
    home_only = "velopack/home-only.log"
    temp_only = "velopack/temp-only.log"
    # Aliases of one physical directory overlap without being identical, so
    # the unknown count must be the union of their entries (3), not a single
    # alias snapshot (max would report 2 for TEMP/TMP).
    unexpected = {
        "USERPROFILE": (nested_secret,),
        "HOME": (nested_secret, "velopack/extra.log", home_only),
        "TEMP": (nested_secret, "velopack/extra.log"),
        "TMP": (nested_secret, temp_only),
    }
    with pytest.raises(RuntimeError) as excinfo:
        portable._reject_unexpected_external(unexpected, external_roots)
    message = str(excinfo.value)
    # Still rejected, but the message leaks no absolute or nested paths.
    assert "outside the Portable root" in message
    for leak in (
        nested_secret,
        "winstore-cache-identity",
        "velopack/extra.log",
        home_only,
        temp_only,
        str(tmp_path),
    ):
        assert leak not in message
    # Aliases over one physical directory collapse to one summary entry
    # reporting only the root aliases and the union unknown-entry count.
    assert message.count("HOME + USERPROFILE: 3 unexpected entries") == 1
    assert message.count("TEMP + TMP: 3 unexpected entries") == 1
    # Nothing unexpected must never raise.
    portable._reject_unexpected_external({}, external_roots)
