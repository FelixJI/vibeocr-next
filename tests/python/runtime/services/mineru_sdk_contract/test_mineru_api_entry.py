"""自有 MinerU API 启动入口的真实 SDK 合同。

覆盖两点：metacommands 策略确实经锁定 SDK ``create_ort_session`` 传入
DML provider 选项（fake session seam，不冒称 GPU 实测）；入口转发的
官方 api_server 参数/端点不变。另含显式 DML fail closed 门禁的确定性
分支。GPU 上的真实推理复验由主代理独立闭包执行。
"""

from __future__ import annotations

import os
import socket
import subprocess
import sys
import threading
import time
from pathlib import Path

import httpx
import pytest
from vibeocr.runtime.recognition.mineru_api_entry import (
    apply_locked_sdk_metacommand_policy,
)


class _FakeDmlSession:
    def __init__(self, *_args, **_kwargs) -> None:
        self.fallback_disabled = False

    def get_providers(self) -> list[str]:
        return ["DmlExecutionProvider", "CPUExecutionProvider"]

    def disable_fallback(self) -> None:
        self.fallback_disabled = True


def _nvidia_adapter():
    from mineru.model.runtime.windows_gpu import GpuAdapter

    return GpuAdapter(
        index=0,
        luid="diagnostic",
        name="diagnostic-nvidia",
        vendor_id=0x10DE,
        device_id=0x2684,
        preference=0,
    )


def test_entry_policy_reaches_dml_provider_options(monkeypatch, tmp_path) -> None:
    """锁定默认 nvidia.layout=True 会传 disable_metacommands="false"（GPU
    上即崩溃路径）；应用入口策略后同一构造传 "true"——证明适配真正到达
    DML factory 选项，而不是只改了孤立变量。"""
    from mineru.model.runtime import onnx as mineru_onnx
    from mineru.model.runtime.onnx_device import METACOMMANDS

    captured: list[list[tuple[str, dict]]] = []

    def _fake_inference_session(_model_path, sess_options=None, providers=None):
        captured.append(list(providers or []))
        return _FakeDmlSession()

    monkeypatch.setattr(mineru_onnx.ort, "InferenceSession", _fake_inference_session)
    adapter = _nvidia_adapter()

    def dml_options() -> dict:
        captured.clear()
        mineru_onnx.create_ort_session(
            str(tmp_path / "layout.onnx"), adapter=adapter, model_kind="layout"
        )
        (name, options), *_ = captured[0]
        assert name == "DmlExecutionProvider"
        return options

    monkeypatch.setitem(METACOMMANDS["nvidia"], "layout", True)
    assert dml_options()["disable_metacommands"] == "false"

    applied = apply_locked_sdk_metacommand_policy()
    assert applied == {"nvidia.layout": False}
    assert dml_options()["disable_metacommands"] == "true"


def test_entry_policy_is_bounded_to_nvidia_layout() -> None:
    """只改 nvidia.layout：其余厂商/模型表项保持锁定默认。"""
    from copy import deepcopy

    from mineru.model.runtime.onnx_device import METACOMMANDS

    before = deepcopy(METACOMMANDS)
    apply_locked_sdk_metacommand_policy()
    after = deepcopy(METACOMMANDS)
    before["nvidia"]["layout"] = False
    assert after == before


class _ProviderFakeSession:
    def __init__(self, providers: list[str]) -> None:
        self._providers = list(providers)
        self.closed: list[bool] = []

    def get_providers(self) -> list[str]:
        return list(self._providers)

    def close(self) -> None:
        self.closed.append(True)


class TestStrictDirectmlFactoryGuard:
    """已提交 directml 意图时 GPU 线模型不得把 CPU 结果当成功。

    经公开 ``selection=`` 注入使选型与机器无关；native session 用 fake
    seam 控制真实 provider 输出，不 mock 不存在 API、不冒称 GPU 实测。
    """

    @staticmethod
    def _install(monkeypatch, truth: list[str]):
        from mineru.model.runtime import onnx as mineru_onnx
        from mineru.model.runtime.onnx_device import OnnxSelection
        from vibeocr.runtime.recognition.mineru_api_entry import (
            install_strict_directml_factory_guard,
        )

        monkeypatch.setenv("MINERU_MODEL_ONNX_DEVICE", "gpu")

        def _fake_inference_session(_model_path, sess_options=None, providers=None):
            # providers 是 factory 请求列表；native 真值由 truth 决定。
            return _ProviderFakeSession(truth)

        monkeypatch.setattr(
            mineru_onnx.ort, "InferenceSession", _fake_inference_session
        )
        uninstall = install_strict_directml_factory_guard()
        return mineru_onnx, OnnxSelection(), uninstall

    def test_gpu_kind_cpu_first_provider_is_refused_and_released(
        self, monkeypatch, tmp_path
    ):
        mineru_onnx, selection, uninstall = self._install(
            monkeypatch, ["CPUExecutionProvider"]
        )
        assert uninstall is not None
        with pytest.raises(RuntimeError, match="refusing implicit CPU result"):
            mineru_onnx.ort_session(
                str(tmp_path / "layout.onnx"),
                model_kind="layout",
                selection=selection,
            )
        uninstall()

    def test_gpu_kind_dml_first_provider_passes(self, monkeypatch, tmp_path):
        mineru_onnx, selection, uninstall = self._install(
            monkeypatch, ["DmlExecutionProvider", "CPUExecutionProvider"]
        )
        session = mineru_onnx.ort_session(
            str(tmp_path / "layout.onnx"),
            model_kind="layout",
            selection=selection,
        )
        assert session.get_providers()[0] == "DmlExecutionProvider"
        uninstall()

    def test_fixed_cpu_model_kind_still_allowed(self, monkeypatch, tmp_path):
        # Windows ocr_det 本来固定 CPU：不在 METACOMMANDS 集合，不门禁。
        mineru_onnx, selection, uninstall = self._install(
            monkeypatch, ["CPUExecutionProvider"]
        )
        session = mineru_onnx.ort_session(
            str(tmp_path / "ocr_det.onnx"),
            model_kind="ocr_det",
            selection=selection,
        )
        assert session.get_providers() == ["CPUExecutionProvider"]
        uninstall()

    def test_rejected_session_reference_is_not_retained_by_the_exception(
        self, monkeypatch, tmp_path
    ):
        """锁定 ORT 无公开 close：拒绝后回收只能靠引用删除。保留拒绝异常
        并 gc 后，weakref 必须失效（不靠 fake.close 声称 native 释放）。"""
        import gc
        import weakref

        mineru_onnx, selection, uninstall = self._install(
            monkeypatch, ["CPUExecutionProvider"]
        )
        references: list[weakref.ref] = []

        def _make_tracked_session(_model_path, sess_options=None, providers=None):
            session = _ProviderFakeSession(["CPUExecutionProvider"])
            references.append(weakref.ref(session))
            return session

        monkeypatch.setattr(mineru_onnx.ort, "InferenceSession", _make_tracked_session)
        raised: list[RuntimeError] = []
        with pytest.raises(
            RuntimeError, match="refusing implicit CPU result"
        ) as excinfo:
            mineru_onnx.ort_session(
                str(tmp_path / "layout.onnx"),
                model_kind="layout",
                selection=selection,
            )
        raised.append(excinfo.value)
        gc.collect()
        assert references[0]() is None
        assert "refusing implicit CPU result" in str(raised[0])
        uninstall()

    @pytest.mark.parametrize("intent", [None, "cpu", "auto"])
    def test_non_directml_intents_do_not_install_guard(self, monkeypatch, intent):
        from mineru.model.runtime import onnx as mineru_onnx
        from vibeocr.runtime.recognition.mineru_api_entry import (
            install_strict_directml_factory_guard,
        )

        if intent is None:
            monkeypatch.delenv("MINERU_MODEL_ONNX_DEVICE", raising=False)
        else:
            monkeypatch.setenv("MINERU_MODEL_ONNX_DEVICE", intent)
        original = mineru_onnx.ort_session
        assert install_strict_directml_factory_guard() is None
        assert mineru_onnx.ort_session is original


def test_gpu_eligible_kinds_come_from_public_policy_table() -> None:
    """GPU 模型集合单一真相源：锁定 4.1.3 Windows 为 layout/mfr/ocr_rec/unet。"""
    from mineru.model.runtime.onnx_device import METACOMMANDS
    from vibeocr.runtime.recognition.mineru_api_entry import (
        gpu_eligible_model_kinds,
    )

    expected = frozenset(key for table in METACOMMANDS.values() for key in table)
    assert gpu_eligible_model_kinds() == expected
    assert expected == frozenset({"layout", "mfr", "ocr_rec", "unet"})


# 子进程内补丁：ORT provider 面钉为 CPU-only，硬件探测一调用即报错，
# 真实创建的 ONNX session 逐次记录。旧全局门禁实现在启动期即调用
# get_available_providers（CPU-only → exit 3），本测试在旧实现上红。
_ENTRY_BOOTSTRAP = """
import os, sys
import mineru.model.runtime.onnx as _monnx  # 先以真实类求值注解
import onnxruntime as _ort
_ort.get_available_providers = lambda: ["CPUExecutionProvider"]
_log = os.environ["ENTRY_TEST_SESSION_LOG"]
_real_session = _ort.InferenceSession
def _tracked_session(*args, **kwargs):
    with open(_log, "a", encoding="utf-8") as handle:
        handle.write("InferenceSession" + chr(10))
    return _real_session(*args, **kwargs)
_ort.InferenceSession = _tracked_session
_monnx.ort.InferenceSession = _tracked_session
import vibeocr.runtime.recognition.directml_devices as _devices
def _probe_must_not_run():
    raise AssertionError("hardware probe must not run during startup/flash")
_devices.hardware_adapters = _probe_must_not_run
sys.argv = ["mineru-api-entry"] + os.environ["ENTRY_TEST_ARGS"].split(" ")
from vibeocr.runtime.recognition.mineru_api_entry import main
main()
"""

_ENTRY_VERIFIABLE_TEXT = "VibeOCR entry flash readiness token 0137."


def test_entry_gpu_intent_flash_parse_needs_no_dml_or_hardware_probe(
    tmp_path: Path,
) -> None:
    """已提交 gpu 设备意图下走 entry.main 启动官方 api_server 并完成
    Flash 真实解析：CPU-only provider 面 + 硬件探测陷阱均不受影响，
    全过程不创建任何 ONNX session（旧全局门禁在旧实现上启动即失败）。"""
    from docx import Document
    from vibeocr.runtime.recognition.mineru_api import (
        MineruApiClient,
        MineruDocument,
    )
    from vibeocr.runtime.recognition.mineru_result import project_document
    from vibeocr.runtime_contracts import MineruConfig, MineruOcrMode, MineruTier

    home = tmp_path / "mineru-home"
    home.mkdir(parents=True, exist_ok=True)
    (home / "config.yaml").write_text(
        "model:\n  small_backend: onnx\n  vlm:\n    engine: llama-cpp\n",
        encoding="utf-8",
    )
    session_log = tmp_path / "onnx-sessions.log"
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    env = os.environ.copy()
    env.pop("PYTHONPATH", None)
    env["MINERU_MODEL_ONNX_DEVICE"] = "gpu"
    env["MINERU_HOME"] = str(home)
    env["MINERU_CONFIG"] = str(home / "config.yaml")
    env["ENTRY_TEST_SESSION_LOG"] = str(session_log)
    env["ENTRY_TEST_ARGS"] = f"--host 127.0.0.1 --port {port} --tier flash"
    process = subprocess.Popen(
        [sys.executable, "-c", _ENTRY_BOOTSTRAP],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        env=env,
        cwd=str(tmp_path),
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
    )
    stderr_lines: list[str] = []

    def drain() -> None:
        assert process.stderr is not None
        for raw in iter(process.stderr.readline, b""):
            stderr_lines.append(raw.decode("utf-8", errors="replace").rstrip())

    reader = threading.Thread(target=drain, daemon=True)
    reader.start()
    base_url = f"http://127.0.0.1:{port}"
    try:
        deadline = time.monotonic() + 60.0
        healthy = False
        while time.monotonic() < deadline:
            try:
                response = httpx.get(
                    f"{base_url}/v1/health", timeout=3, trust_env=False
                )
                if (
                    response.status_code == 200
                    and str(response.json().get("version", "")).split(".")[0] == "4"
                ):
                    healthy = True
                    break
            except Exception:
                pass
            time.sleep(0.5)
        assert healthy, "\n".join(stderr_lines[-30:])
        assert any("nvidia.layout=False" in line for line in stderr_lines)

        source = Document()
        source.add_paragraph(_ENTRY_VERIFIABLE_TEXT)
        docx_path = tmp_path / "entry-flash.docx"
        source.save(docx_path)
        result = MineruApiClient(base_url, timeout=180).parse(
            [("entry-flash.docx", docx_path.read_bytes())],
            MineruConfig(tier=MineruTier.FLASH, ocr_mode=MineruOcrMode.TXT),
        )
        document = result["entry-flash.docx"]
        assert not isinstance(document, Exception), f"{document}\n{stderr_lines[-10:]}"
        assert isinstance(document, MineruDocument)
        assert _ENTRY_VERIFIABLE_TEXT in project_document(document).raw_text, (
            stderr_lines[-10:]
        )
        # flash 纯抽取不得创建任何 ONNX session。
        assert not session_log.exists() or session_log.read_text("utf-8") == ""
    finally:
        process.terminate()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=15)
