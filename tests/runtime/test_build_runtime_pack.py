"""Runtime pack / shared wheel pool build contract (lock-bound dedupe)."""

from __future__ import annotations

import hashlib
import json
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
        _fake_pip_command(command, salt_by_lock=salt_by_lock)

    monkeypatch.setattr(builder, "_run", fake_run)
    return commands


def _fake_pip_command(
    command: list[str], *, salt_by_lock: dict[str, str] | None = None
) -> None:
    if command[1:3] != ["-m", "pip"]:
        return
    if True:
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
    ("onnxruntime-directml", "1.24.4"),
    ("mineru", "4.1.3"),
    ("onnx", "1.23.2"),
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


# ---------------------------------------------------------------------------
# 预构建运行时树（PR2：构建期装配，运行时零 venv/零 uv/零安装）
# ---------------------------------------------------------------------------


def test_extract_python_archive_strips_prefix_and_rejects_unsafe_members(
    tmp_path: Path,
) -> None:
    import io
    import tarfile

    archive = tmp_path / "cpython.tar.gz"
    payload = b"python-bin"
    with tarfile.open(archive, mode="w:gz") as tar:
        for name, content in (
            ("python/python.exe", payload),
            ("python/Lib/os.py", b"stdlib"),
        ):
            info = tarfile.TarInfo(name)
            info.size = len(content)
            tar.addfile(info, io.BytesIO(content))
    tree = tmp_path / "tree"
    builder.extract_python_archive(
        archive, tree, expected_sha256=builder._sha256_file(archive)
    )
    assert (tree / "python.exe").read_bytes() == payload
    assert (tree / "Lib" / "os.py").read_bytes() == b"stdlib"

    with tarfile.open(archive, mode="w:gz") as tar:
        info = tarfile.TarInfo("python/evil.exe")
        info.size = 3
        info.type = tarfile.SYMTYPE
        info.linkname = "../evil"
        tar.addfile(info, io.BytesIO(b"bad"))
    with pytest.raises(RuntimeError, match="unsupported python archive member"):
        builder.extract_python_archive(
            archive, tmp_path / "tree2", expected_sha256=builder._sha256_file(archive)
        )

    with pytest.raises(RuntimeError, match="sha256 does not match"):
        builder.extract_python_archive(
            archive, tmp_path / "tree3", expected_sha256="0" * 64
        )


def test_build_runtime_env_preinstalls_offline_and_smokes(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    import io
    import tarfile

    base_lock = _write_lock(tmp_path / "requirements-win-x64-base.lock", BASE_PINS)
    dml_lock = _write_lock(
        tmp_path / "requirements-win-x64-paddle-ort-directml.lock",
        [*BASE_PINS, ("paddleocr", "3.7.0")],
    )
    archive = tmp_path / "cpython.tar.gz"
    payload = b"python-bin"
    with tarfile.open(archive, mode="w:gz") as tar:
        for name, content in (
            ("python/python.exe", payload),
            ("python/Lib/os.py", b"stdlib"),
        ):
            info = tarfile.TarInfo(name)
            info.size = len(content)
            tar.addfile(info, io.BytesIO(content))
    runtime_wheel = tmp_path / "vibeocr_next_runtime-0.9.0-py3-none-any.whl"
    runtime_wheel.write_bytes(b"runtime-wheel")

    uv_commands: list[list[str]] = []

    def fake_run(command: list[str]) -> None:
        uv_commands.append(list(command))
        if command[1:3] == ["-m", "pip"]:
            _fake_pip_command(command)

    monkeypatch.setattr(builder, "_run", fake_run)

    tree = tmp_path / "runtime-env"
    builder.build_runtime_env(
        python_archive=archive,
        python_sha256=builder._sha256_file(archive),
        python_version="3.13.16",
        locks=[("win-x64-base", base_lock), ("win-x64-paddle-ort-directml", dml_lock)],
        runtime_wheel=runtime_wheel,
        runtime_version="0.9.0",
        work_dir=tmp_path / "work",
        output=tree,
        uv_executable="uv-test",
    )

    assert (tree / "python.exe").is_file()
    # wheel 池去重：base 与 directml 共享 pin 只进池一次。
    pool_dir = tmp_path / "work" / "env-pool"
    assert sorted(path.name for path in pool_dir.glob("*.whl")) == [
        "fastapi-1.0.0-py3-none-any.whl",
        "numpy-2.5.3-py3-none-any.whl",
        "paddleocr-3.7.0-py3-none-any.whl",
        "winrt-runtime-3.2.1-py3-none-any.whl",
        "winrt-windows-foundation-3.2.1-py3-none-any.whl",
        "winrt-windows-foundation-collections-3.2.1-py3-none-any.whl",
    ]
    uv_lines = [line for line in uv_commands if line[0] == "uv-test"]
    assert len(uv_lines) == 3
    install_closure = uv_lines[0]
    assert install_closure[install_closure.index("pip") + 1] == "install"
    assert "--offline" in install_closure and "--no-index" in install_closure
    assert install_closure[install_closure.index("--find-links") + 1] == str(pool_dir)
    install_wheel = uv_lines[1]
    assert "--no-deps" in install_wheel
    assert str(runtime_wheel.resolve()) in install_wheel
    assert uv_lines[2][uv_lines[2].index("pip") + 1] == "check"
    smoke = [line for line in uv_commands if line[0] == str(tree / "python.exe")]
    assert len(smoke) == 1 and smoke[0][1] == "-I"
    assert "vibeocr.runtime.host.main" in smoke[0][-1]


def test_build_runtime_env_rejects_same_name_two_versions(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    _fake_pip(monkeypatch)
    base_lock = _write_lock(tmp_path / "b.lock", [("numpy", "2.5.3")])
    other_lock = _write_lock(tmp_path / "d.lock", [("numpy", "2.3.5")])
    with pytest.raises(RuntimeError, match="one version per package"):
        builder.build_runtime_env(
            python_archive=tmp_path / "a.tar.gz",
            python_sha256="0" * 64,
            python_version="3.13.16",
            locks=[("win-x64-base", base_lock), ("other", other_lock)],
            runtime_wheel=tmp_path / "w.whl",
            runtime_version="0.9.0",
            work_dir=tmp_path / "work",
            output=tmp_path / "runtime-env",
        )


def test_runtime_env_manifest_is_deterministic(tmp_path: Path) -> None:
    tree = tmp_path / "tree"
    (tree / "Lib").mkdir(parents=True)
    (tree / "python.exe").write_bytes(b"bin")
    (tree / "Lib" / "os.py").write_bytes(b"os")

    first = tmp_path / "first.json"
    second = tmp_path / "second.json"
    builder.write_runtime_env_manifest(tree, first)
    builder.write_runtime_env_manifest(tree, second)
    assert first.read_bytes() == second.read_bytes()

    manifest = json.loads(first.read_text(encoding="utf-8"))
    assert manifest["schema_version"] == 1
    assert manifest["file_count"] == 2
    # 逐文件摘要不在树清单层重复：树内容完整性由产品发布清单唯一对账，
    # 清单文件自身字节由 manifest.runtime_env.manifest_sha256 绑定。
    assert "files" not in manifest
