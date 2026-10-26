"""Runtime pack / shared wheel pool build contract (lock-bound dedupe)."""

from __future__ import annotations

import hashlib
import re
import zipfile
from pathlib import Path

import pytest

from scripts import build_runtime_pack as builder

_DECLARATION_RE = re.compile(r"(?m)^([A-Za-z0-9][A-Za-z0-9._-]*)==(\S+)")


def _wheel_payload(name: str, version: str, salt: str = "") -> bytes:
    return f"wheel:{name}:{version}:{salt}".encode()


def _write_lock(path: Path, pins: list[tuple[str, str]], *, salt: str = "") -> Path:
    lines = []
    for name, version in pins:
        digest = hashlib.sha256(_wheel_payload(name, version, salt)).hexdigest()
        lines.append(f"{name}=={version} \\")
        lines.append(f"    --hash=sha256:{digest}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return path


def _fake_pip(
    monkeypatch: pytest.MonkeyPatch, *, salt_by_lock: dict[str, str] | None = None
) -> list[list[str]]:
    """Replace pip download/wheel with deterministic local wheel staging."""
    commands: list[list[str]] = []

    def fake_run(command: list[str]) -> None:
        commands.append(list(command))
        arguments = command[3:]
        if arguments[0] == "download":
            destination = Path(arguments[arguments.index("-d") + 1])
            lock = Path(arguments[arguments.index("-r") + 1])
            salt = (salt_by_lock or {}).get(lock.stem, "")
            destination.mkdir(parents=True, exist_ok=True)
            for match in _DECLARATION_RE.finditer(lock.read_text(encoding="utf-8")):
                name, version = match.group(1), match.group(2)
                wheel = destination / f"{name}-{version}-py3-none-any.whl"
                wheel.write_bytes(_wheel_payload(name, version, salt))
        elif arguments[0] == "wheel":
            source = Path(arguments[arguments.index("--find-links") + 1])
            destination = Path(arguments[arguments.index("-w") + 1])
            destination.mkdir(parents=True, exist_ok=True)
            for wheel in sorted(source.glob("*.whl")):
                (destination / wheel.name).write_bytes(wheel.read_bytes())
        else:  # pragma: no cover - contract guard
            raise AssertionError(f"unexpected pip command: {arguments[0]}")

    monkeypatch.setattr(builder, "_run", fake_run)
    return commands


BASE_PINS = [
    ("fastapi", "1.0.0"),
    ("numpy", "2.5.3"),
    ("winrt-runtime", "3.2.1"),
    ("winrt-windows-foundation", "3.2.1"),
    ("winrt-windows-foundation-collections", "3.2.1"),
]
CPU_PINS = [
    *[
        (name, "3.2.1")
        for name in (
            "winrt-runtime",
            "winrt-windows-foundation",
            "winrt-windows-foundation-collections",
            "winrt-windows-globalization",
            "winrt-windows-graphics-imaging",
            "winrt-windows-media-ocr",
            "winrt-windows-storage-streams",
        )
    ],
    ("fastapi", "1.0.0"),
    ("numpy", "2.3.5"),
    ("paddleocr", "3.7.0"),
    ("paddlex", "3.7.2"),
    ("opencv-contrib-python", "4.10.0.84"),
    ("onnxruntime", "1.30.0"),
]
DML_PINS = [
    *[(name, version) for name, version in CPU_PINS if name.startswith("winrt-")],
    ("fastapi", "1.0.0"),
    ("numpy", "2.3.5"),
    ("paddleocr", "3.7.0"),
    ("paddlex", "3.7.2"),
    ("opencv-contrib-python", "4.10.0.84"),
    ("onnxruntime-directml", "1.24.4"),
]


def _pool_locks(root: Path) -> list[tuple[str, Path]]:
    return [
        (
            "win-x64-base",
            _write_lock(
                root / "requirements-win-x64-base.lock",
                BASE_PINS,
            ),
        ),
        (
            "win-x64-paddle-ort-cpu",
            _write_lock(
                root / "requirements-win-x64-paddle-ort-cpu.lock",
                CPU_PINS,
            ),
        ),
        (
            "win-x64-paddle-ort-directml",
            _write_lock(
                root / "requirements-win-x64-paddle-ort-directml.lock",
                DML_PINS,
            ),
        ),
    ]


def _archive_members(path: Path) -> list[str]:
    with zipfile.ZipFile(path) as archive:
        return archive.namelist()


def test_single_profile_pack_still_embeds_pack_requirements(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    lock = _write_lock(tmp_path / "requirements-win-x64-base.lock", BASE_PINS)
    _fake_pip(monkeypatch)
    output = tmp_path / "out" / "vibeocr-runtime-pack-win-x64-base-0.7.0.zip"
    written = builder.build_runtime_pack(
        lock=lock,
        profile="win-x64-base",
        work_dir=tmp_path / "work",
        output=output,
    )
    assert written == [output]
    members = _archive_members(output)
    assert "pack-requirements.txt" in members
    assert "numpy-2.5.3-py3-none-any.whl" in members


def test_pool_deduplicates_shared_wheels_and_keeps_conflicting_versions(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    locks = _pool_locks(tmp_path)
    _fake_pip(monkeypatch)
    output = tmp_path / "out" / "vibeocr-runtime-pool-win-x64-0.7.0.zip"
    written = builder.build_runtime_pool(
        locks=locks,
        work_dir=tmp_path / "work",
        output=output,
    )
    # 三个成员 requirements zip + 一个 wheel-only pool 分片。
    requirements_archives = sorted(path for path in written if path != output)
    assert requirements_archives == [
        tmp_path
        / "out"
        / "vibeocr-runtime-pool-win-x64-0.7.0.requirements-win-x64-base.zip",
        tmp_path
        / "out"
        / "vibeocr-runtime-pool-win-x64-0.7.0.requirements-win-x64-paddle-ort-cpu.zip",
        tmp_path
        / "out"
        / "vibeocr-runtime-pool-win-x64-0.7.0.requirements-win-x64-paddle-ort-directml.zip",
    ]
    for (profile, lock), archive_path in zip(locks, requirements_archives, strict=True):
        assert _archive_members(archive_path) == ["pack-requirements.txt"]
        with zipfile.ZipFile(archive_path) as archive:
            text = archive.read("pack-requirements.txt").decode("utf-8")
        expected = sorted(
            (builder._normalize(name), version)
            for name, version in _DECLARATION_RE.findall(
                lock.read_text(encoding="utf-8")
            )
        )
        assert text.splitlines() == [
            f"{name}=={version}" for name, version in expected
        ], profile
    # pool 只含去重后的 wheel：15 个唯一 wheel（numpy 两个版本共存），无清单。
    pool_members = _archive_members(output)
    assert "pack-requirements.txt" not in pool_members
    assert len(pool_members) == len(set(pool_members)) == 15
    assert "numpy-2.5.3-py3-none-any.whl" in pool_members
    assert "numpy-2.3.5-py3-none-any.whl" in pool_members
    assert "onnxruntime-1.30.0-py3-none-any.whl" in pool_members
    assert "onnxruntime-directml-1.24.4-py3-none-any.whl" in pool_members
    assert "fastapi-1.0.0-py3-none-any.whl" in pool_members


def test_pool_rejects_conflicting_bytes_for_identical_wheel_name(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    base = _write_lock(
        tmp_path / "requirements-win-x64-base.lock",
        [("fastapi", "1.0.0"), *BASE_PINS[2:]],
        salt="base",
    )
    cpu = _write_lock(
        tmp_path / "requirements-win-x64-paddle-ort-cpu.lock",
        CPU_PINS,
        salt="paddle",
    )
    _fake_pip(
        monkeypatch,
        salt_by_lock={
            "requirements-win-x64-base": "base",
            "requirements-win-x64-paddle-ort-cpu": "paddle",
        },
    )
    with pytest.raises(RuntimeError, match="conflicting bytes for fastapi"):
        builder.build_runtime_pool(
            locks=[
                ("win-x64-base", base),
                ("win-x64-paddle-ort-cpu", cpu),
            ],
            work_dir=tmp_path / "work",
            output=tmp_path / "out" / "pool.zip",
        )


def test_pool_rejects_duplicate_lock_stems(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    _fake_pip(monkeypatch)
    lock = _write_lock(tmp_path / "same.lock", BASE_PINS)
    with pytest.raises(ValueError, match="distinct file stems"):
        builder.build_runtime_pool(
            locks=[("win-x64-base", lock), ("win-x64-base", lock)],
            work_dir=tmp_path / "work",
            output=tmp_path / "out" / "pool.zip",
        )


def test_pool_main_rejects_invalid_cli_combinations(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture
) -> None:
    lock = _write_lock(tmp_path / "requirements-win-x64-base.lock", BASE_PINS)
    common = [
        "--work-dir",
        str(tmp_path / "work"),
        "--output",
        str(tmp_path / "out" / "pool.zip"),
    ]
    assert (
        builder.main(
            [
                "--profile",
                builder.SHARED_POOL_PROFILE,
                *common,
            ]
        )
        == 1
    )
    assert "requires at least one lock" in capsys.readouterr().err
    assert (
        builder.main(
            [
                "--profile",
                builder.SHARED_POOL_PROFILE,
                "--pool-lock",
                "missing-separator",
                *common,
            ]
        )
        == 1
    )
    assert "invalid --pool-lock entry" in capsys.readouterr().err
    assert (
        builder.main(
            [
                "--profile",
                "win-x64-base",
                "--lock",
                str(lock),
                "--pool-lock",
                f"win-x64-base={lock}",
                *common,
            ]
        )
        == 1
    )
    assert "--pool-lock requires --profile" in capsys.readouterr().err


def test_pool_main_prints_every_artifact(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture
) -> None:
    locks = _pool_locks(tmp_path)
    _fake_pip(monkeypatch)
    output = tmp_path / "out" / "vibeocr-runtime-pool-win-x64-0.7.0.zip"
    assert (
        builder.main(
            [
                "--profile",
                builder.SHARED_POOL_PROFILE,
                *[
                    part
                    for profile, lock in locks
                    for part in ("--pool-lock", f"{profile}={lock}")
                ],
                "--work-dir",
                str(tmp_path / "work"),
                "--output",
                str(output),
            ]
        )
        == 0
    )
    printed = [Path(line) for line in capsys.readouterr().out.splitlines()]
    assert len(printed) == 4
    assert output in printed
    assert all(path.is_file() for path in printed)
