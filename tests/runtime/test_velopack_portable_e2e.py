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
