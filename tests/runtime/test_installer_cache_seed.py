import importlib
import json
from pathlib import Path

import pytest
from vibeocr.runtime.environments.runtime_installer import RuntimeInstallError


@pytest.mark.parametrize("kind", ["legacy", "uv", "outside"])
def test_seed_rebases_only_owned_copied_file_urls(tmp_path, monkeypatch, kind):
    monkeypatch.syspath_prepend(str(Path(__file__).resolve().parents[2] / "scripts"))
    smoke = importlib.import_module("smoke_environment_cleanup")
    source = tmp_path / "source"
    artifacts = source / "downloads" / "artifacts"
    artifacts.mkdir(parents=True)
    wheel = artifacts / "target-1-py3-none-any.whl"
    wheel.write_bytes(b"synthetic artifact")
    reports = source / "resolve"
    reports.mkdir()
    report = reports / "fixture-report.json"
    url = (tmp_path / "outside.whl").as_uri() if kind == "outside" else wheel.as_uri()
    document = {
        "install": [
            {
                "metadata": {"name": "target", "version": "1"},
                "download_info": {
                    "url": url,
                    "archive_info": {"hashes": {"sha256": "a" * 64}},
                },
            },
            {
                "metadata": {"name": "remote", "version": "1"},
                "download_info": {
                    "url": "https://trusted.invalid/remote-1-py3-none-any.whl",
                    "archive_info": {"hashes": {"sha256": "b" * 64}},
                },
            },
        ]
    }
    if kind == "uv":
        document["executor"] = "uv-0.12.22"
    report.write_text(json.dumps(document), encoding="utf-8")
    inputs = {"lock": "fixture lock", "endpoint": "https://trusted.invalid/simple/"}
    if kind == "uv":
        inputs.update(
            ignore_installed=True, executor="uv-0.12.22", target={"version": "3.13.16"}
        )
    input_path = report.with_suffix(".inputs.json")
    input_path.write_text(json.dumps(inputs), encoding="utf-8")
    original_report, original_inputs = report.read_bytes(), input_path.read_bytes()
    product = tmp_path / "product"
    if kind == "outside":
        with pytest.raises(RuntimeInstallError, match="outside download cache"):
            smoke.seed_installer_cache(product, source)
    else:
        evidence = smoke.seed_installer_cache(product, source)
        assert evidence["relocated_file_artifacts"] == 1
        assert evidence["patched_ignore_installed"] == int(kind == "legacy")
        copied_root = product / "state" / "installer-cache"
        copied = json.loads((copied_root / "resolve" / report.name).read_text())
        assert (
            copied["install"][0]["download_info"]["url"]
            == (copied_root / "downloads" / "artifacts" / wheel.name).as_uri()
        )
        assert (
            copied["install"][0]["download_info"]["archive_info"]
            == document["install"][0]["download_info"]["archive_info"]
        )
        assert copied["install"][1] == document["install"][1]
        assert (
            copied_root / "downloads" / "artifacts" / wheel.name
        ).read_bytes() == wheel.read_bytes()
    assert report.read_bytes() == original_report
    assert input_path.read_bytes() == original_inputs
