"""#321 F8：产品模式下 Paddle 探针失败报产品损坏，不引导在线准备。"""

from __future__ import annotations

import importlib

import pytest

process_adapter = importlib.import_module(
    "vibeocr.runtime.recognition.paddle_process_adapter"
)


@pytest.fixture(name="no_paddle_python")
def _no_paddle_python(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(process_adapter, "paddle_python", lambda: None, raising=True)


def test_product_paddle_mode_projection_is_unavailable_without_component(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """#321 F8：产品模式 Paddle text/table/structure 全部投影产品损坏，
    不携带在线准备组件。"""
    import vibeocr.runtime.recognition.paddle_onnx as paddle_onnx
    from vibeocr.runtime.host import composition
    from vibeocr.runtime.recognition.ocr_engines import (
        EngineAvailability,
        EngineDescriptor,
        OcrEngineRegistry,
        OcrEngineResolver,
    )
    from vibeocr.runtime_contracts.dtos import OcrEngine

    monkeypatch.setattr(paddle_onnx, "is_ort_environment", lambda: True)

    class _Engine:
        def __init__(self, engine_id, availability, reason, component):
            self.engine_id = engine_id
            self._engine_id = engine_id
            self._availability = availability
            self._reason = reason
            self._component = component

        def descriptor(self):
            return EngineDescriptor(
                engine_id=self._engine_id,
                availability=self._availability,
                included_in_base=True,
                reason_code=self._reason,
                required_component=self._component,
            )

    windows = _Engine(OcrEngine.WINDOWS, EngineAvailability.READY, None, None)
    paddle = _Engine(
        OcrEngine.PADDLEOCR,
        EngineAvailability.UNAVAILABLE,
        "product_runtime_damaged",
        None,
    )
    resolver = OcrEngineResolver(OcrEngineRegistry([windows, paddle]))
    modes = composition._build_recognition_mode_registry(
        engine_resolver=resolver,
        use_paddle=False,
        use_mineru=False,
    )

    entries = {item["id"]: item for item in modes.catalog_payload()["modes"]}
    for mode_id in ("paddle_text", "paddle_table", "paddle_structure"):
        assert entries[mode_id]["availability"] == "unavailable", mode_id
        assert entries[mode_id]["reason_code"] == "product_runtime_damaged", mode_id
        assert entries[mode_id]["required_component"] is None, mode_id
    # Windows 投影不受 Paddle 产品语义影响。
    assert entries["windows_text"]["required_component"] is None
    assert entries["windows_text"]["availability"] == "ready"


def test_dev_unavailable_probe_keeps_recipe_component(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """开发语义：非产品损坏的待准备/不可用仍携带统一配方提示
    （与 test_app_engine_selection 的消费者契约一致）。"""
    import vibeocr.runtime.recognition.paddle_onnx as paddle_onnx
    from vibeocr.runtime.host import composition
    from vibeocr.runtime.recognition.ocr_engines import (
        EngineAvailability,
        EngineDescriptor,
        OcrEngineRegistry,
        OcrEngineResolver,
    )
    from vibeocr.runtime_contracts.dtos import OcrEngine

    monkeypatch.setattr(paddle_onnx, "is_ort_environment", lambda: True)

    class _Engine:
        def __init__(self, engine_id, availability, reason, component):
            self.engine_id = engine_id
            self._engine_id = engine_id
            self._availability = availability
            self._reason = reason
            self._component = component

        def descriptor(self):
            return EngineDescriptor(
                engine_id=self._engine_id,
                availability=self._availability,
                included_in_base=True,
                reason_code=self._reason,
                required_component=self._component,
            )

    windows = _Engine(OcrEngine.WINDOWS, EngineAvailability.READY, None, None)
    paddle = _Engine(
        OcrEngine.PADDLEOCR,
        EngineAvailability.PREPARATION_REQUIRED,
        "engine_not_installed",
        None,
    )
    resolver = OcrEngineResolver(OcrEngineRegistry([windows, paddle]))
    modes = composition._build_recognition_mode_registry(
        engine_resolver=resolver,
        use_paddle=False,
        use_mineru=False,
    )

    entries = {item["id"]: item for item in modes.catalog_payload()["modes"]}
    for mode_id in ("paddle_text", "paddle_table", "paddle_structure"):
        assert entries[mode_id]["required_component"] == "paddleocr-ort-directml", (
            mode_id
        )


def test_product_probe_failure_is_unavailable_without_component(
    monkeypatch: pytest.MonkeyPatch, no_paddle_python: None
) -> None:
    monkeypatch.setenv("VIBEOCR_PRODUCT_RUNTIME", "1")

    descriptor = process_adapter.PaddleProcessAdapter.probe_descriptor()

    assert descriptor.availability.value == "unavailable"
    assert descriptor.reason_code == "product_runtime_damaged"
    assert descriptor.required_component is None


def test_dev_probe_failure_keeps_preparation_semantics(
    monkeypatch: pytest.MonkeyPatch, no_paddle_python: None
) -> None:
    monkeypatch.delenv("VIBEOCR_PRODUCT_RUNTIME", raising=False)

    descriptor = process_adapter.PaddleProcessAdapter.probe_descriptor()

    assert descriptor.availability.value == "preparation_required"
    assert descriptor.reason_code == process_adapter.REASON_ENGINE_NOT_INSTALLED
    assert descriptor.required_component == "paddleocr-ort-directml"
