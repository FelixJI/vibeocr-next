from pathlib import Path
from zipfile import ZipFile

from vibeocr.runtime.environments.bundled_uv import uv_executable

from scripts import build_runtime_installer as builder


def test_frozen_installer_binds_uv_and_licenses_inside_single_exe(
    tmp_path, monkeypatch
):
    uv_binary = uv_executable()
    commands = []
    monkeypatch.setattr(builder, "uv_executable", lambda: uv_binary)

    def build(command, **kwargs):
        commands.append(command)
        dist = Path(command[command.index("--distpath") + 1])
        (dist / "vibeocr-runtime-installer.exe").write_bytes(b"frozen fixture")

    monkeypatch.setattr(builder.subprocess, "run", build)
    executable, archive = builder.build_runtime_installer(
        output_dir=tmp_path / "output", work_dir=tmp_path / "work", version="0.7.0"
    )
    command = commands[0]
    assert command[command.index("--add-binary") + 1] == f"{uv_binary};uv"
    license_arg = command[command.index("--add-data") + 1]
    license_path, destination = license_arg.split(";")
    assert destination == "uv/licenses"
    assert {p.name for p in Path(license_path).iterdir()} == {
        "LICENSE-MIT",
        "LICENSE-APACHE",
    }
    with ZipFile(archive) as zipped:
        assert zipped.namelist() == ["runtime-installer/vibeocr-runtime-installer.exe"]
        assert zipped.read(zipped.namelist()[0]) == executable.read_bytes()
