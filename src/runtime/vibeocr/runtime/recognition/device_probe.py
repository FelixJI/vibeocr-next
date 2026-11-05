"""Exercise the requested local provider before committing a device change."""

from __future__ import annotations

import subprocess
import sys


def prepare_device(device: str) -> None:
    try:
        probe = subprocess.run(
            [sys.executable, "-I", "-B", "-X", "utf8", "-m", __name__, device],
            stdin=subprocess.DEVNULL,
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=30,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise ValueError(f"无法准备计算设备 {device}：{exc}") from exc
    if probe.returncode:
        raise ValueError(f"无法准备计算设备 {device}：{probe.stderr[-1200:]}")


def _run(device: str) -> None:
    import numpy as np
    import onnx
    import onnxruntime as ort
    from onnx import helper
    from vibeocr.runtime.recognition.directml_devices import hardware_adapters
    from vibeocr.runtime.recognition.paddle_onnx import PROVIDERS

    adapters = hardware_adapters() if device in {"auto", "directml"} else []
    if device == "auto":
        device = "directml" if adapters else "cpu"
    if device == "directml" and not adapters:
        raise ValueError("没有可用的 DirectML 硬件适配器。")
    provider = PROVIDERS[device]

    def tensor(name: str):
        return helper.make_tensor_value_info(name, onnx.TensorProto.FLOAT, [1, 2])

    graph = helper.make_graph(
        [helper.make_node("Add", ["a", "b"], ["sum"])],
        "device_probe",
        [tensor("a"), tensor("b")],
        [tensor("sum")],
    )
    model = helper.make_model(
        graph, opset_imports=[helper.make_opsetid("", 17)], ir_version=9
    )
    options = ort.SessionOptions()
    options.enable_mem_pattern = False
    options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    if device == "directml":
        options.add_session_config_entry("session.disable_cpu_ep_fallback", "1")
    session = ort.InferenceSession(
        model.SerializeToString(),
        sess_options=options,
        providers=[(provider, {"device_id": str(adapters[0]["device_id"])})]
        if device == "directml"
        else [provider],
    )
    if session.get_providers()[0] != provider:
        raise ValueError(f"{provider} 未生效。")
    session.disable_fallback()
    values = np.array([[1.0, 2.0]], dtype=np.float32)
    actual = session.run(["sum"], {"a": values, "b": values})[0]
    if not np.array_equal(actual, values * 2):
        raise ValueError(f"{provider} 计算结果异常。")


if __name__ == "__main__":
    _run(sys.argv[1])
