from importlib.resources import files
from pathlib import Path


def test_product_build_stages_the_prebuilt_tree_and_bundled_models():
    script = (
        Path(__file__).resolve().parents[2] / "scripts/build_internal_runtime.ps1"
    ).read_text(encoding="utf-8")
    assert "--runtime-env-tree" in script and "--runtime-env-manifest" in script
    assert "--paddle-model-pack" in script
    assert "runtime-installer" not in script
    assert (files("vibeocr.runtime.environments") / "paddle_onnx_models.json").is_file()
