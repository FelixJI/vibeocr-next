"""锁定 mineru 4.1.3 配置面合同：真实包行为与本仓直接入口一致。"""

from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

import yaml
from vibeocr.runtime.recognition.mineru_service import MinerUService

_CHILD = """
import json, os
from pathlib import Path
from vibeocr.runtime.environments.model_cache import prepare_mineru
root = prepare_mineru('basic')
assert root == Path(os.environ['MINERU_HOME']) / 'models', root
print(json.dumps({'root': str(root)}))
"""


def test_prepare_mineru_local_source_runs_in_locked_sdk(tmp_path: Path) -> None:
    """真实行为回归：source=local 时 prepare_mineru 直接返回声明本地模型根。

    旧实现 ``from mineru.config import config`` 在锁定包中 ImportError，本
    子进程会以非零退出；不做任何上游 API mock，无网络与模型下载。
    """
    home = tmp_path / "mineru-home"
    home.mkdir(parents=True, exist_ok=True)
    (home / "config.yaml").write_text("model:\n  source: local\n", encoding="utf-8")
    env = os.environ.copy()
    env.pop("PYTHONPATH", None)
    env["MINERU_HOME"] = str(home)
    env["MINERU_CONFIG"] = str(home / "config.yaml")
    env["MINERU_MODEL_SOURCE"] = "local"
    result = subprocess.run(
        [sys.executable, "-I", "-B", "-c", _CHILD],
        capture_output=True,
        text=True,
        env=env,
        cwd=str(tmp_path),
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
        timeout=120,
    )
    assert result.returncode == 0, result.stderr
    assert json.loads(result.stdout)["root"] == str(home / "models")


def test_local_environment_config_validates_under_locked_schema(
    tmp_path: Path, monkeypatch
) -> None:
    """_local_environment 生成的 yaml 必须通过真实 Config 校验并保留意图。

    旧顶层 ``vlm`` 写法会被 ``Config`` 静默忽略，``model.vlm.engine``
    退回 ``auto``。
    """
    from mineru.config import Config

    monkeypatch.setenv("MINERU_HOME", str(tmp_path / "mineru-home"))
    monkeypatch.delenv("MINERU_CONFIG", raising=False)
    env = MinerUService._local_environment()
    data = yaml.safe_load(Path(env["MINERU_CONFIG"]).read_text(encoding="utf-8"))
    assert "vlm" not in data
    config = Config(**data)
    assert config.model.small_backend == "onnx"
    assert config.model.vlm.engine == "llama-cpp"
