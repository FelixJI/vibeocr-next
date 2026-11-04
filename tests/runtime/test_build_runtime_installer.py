from pathlib import Path

from scripts import build_runtime_installer as builder


def test_frozen_installer_is_single_exe_without_bundled_uv(tmp_path, monkeypatch):
    commands = []

    def build(command, **kwargs):
        commands.append(command)
        dist = Path(command[command.index("--distpath") + 1])
        (dist / "vibeocr-runtime-installer.exe").write_bytes(b"frozen fixture")

    monkeypatch.setattr(builder.subprocess, "run", build)
    staged = builder.build_runtime_installer(
        output_dir=tmp_path / "output", work_dir=tmp_path / "work"
    )
    command = commands[0]
    # 预构建运行时树（PR2）后安装器不再内嵌 uv：运行时零安装，exe 只
    # 保留环境注册/切换/清理侍者。
    assert "--add-binary" not in command
    assert "--add-data" not in command
    # 冻结安装器必须携带 environments 包数据：模型布置读取冻结清单
    # paddle_onnx_models.json（importlib.resources），缺失时 exe 内
    # FileNotFoundError。
    collected_data = [
        command[index + 1]
        for index, argument in enumerate(command)
        if argument == "--collect-data"
    ]
    assert collected_data == [
        "vibeocr.runtime_contracts",
        "vibeocr.runtime.environments",
    ]
    from importlib.resources import files

    assert (files("vibeocr.runtime.environments") / "paddle_onnx_models.json").is_file()
    # 构建产物只有一份可执行安装器：不再生成单成员 zip 中间层，产品不重复携带。
    assert staged == tmp_path / "output" / "vibeocr-runtime-installer.exe"
    assert staged.read_bytes() == b"frozen fixture"
    assert sorted(path.name for path in (tmp_path / "output").iterdir()) == [
        "vibeocr-runtime-installer.exe"
    ]
