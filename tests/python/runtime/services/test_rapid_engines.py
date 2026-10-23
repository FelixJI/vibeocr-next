"""Rapid 系引擎契约测试：device 语义、有界并行、RAPID_TABLE 路由与 payload。"""

from __future__ import annotations

import sys
import threading
import time
import types
from pathlib import Path
from typing import Any

import pytest
from vibeocr.runtime.jobs.budgets import (
    AdapterCapability,
    BudgetPlanner,
    InputItem,
)
from vibeocr.runtime.recognition.ocr_engine_router import OcrEngineRoutingAdapter
from vibeocr.runtime.recognition.pipeline_contracts import (
    OCRPipeline,
    RecognitionMode,
    get_recognition_mode_definition,
)


@pytest.mark.parametrize("retry_device", ["cpu", "cuda"])
def test_rapidtable_cuda_activation_failure_does_not_poison_retry(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path, retry_device: str
) -> None:
    from vibeocr.runtime.recognition import rapidtable_engine as module

    candidates: list[object] = []

    def construct(config: object) -> object:
        candidate = object()
        candidates.append(candidate)
        return candidate

    fake = types.ModuleType("rapid_table")
    setattr(fake, "RapidTable", construct)
    setattr(fake, "RapidTableInput", lambda **kwargs: kwargs)
    monkeypatch.setitem(sys.modules, "rapid_table", fake)
    monkeypatch.setattr(module, "resolve_model_path", lambda: tmp_path / "model.onnx")
    monkeypatch.setattr(module, "cuda_provider_ready", lambda: True)
    activated: list[object] = []

    def activate(candidate: object) -> None:
        activated.append(candidate)
        if len(activated) == 1:
            raise RuntimeError("temporary CUDA allocation failure")

    monkeypatch.setattr(module, "activate_cuda_sessions", activate)
    engine = module.RapidTableEngine()
    with pytest.raises(RuntimeError, match="temporary CUDA allocation failure"):
        engine._ensure_engine("cuda")
    assert engine._ensure_engine(retry_device) is candidates[1]
    assert len(candidates) == 2


# ---------------------------------------------------------------------------
# 预算：parallel_compute 分组语义
# ---------------------------------------------------------------------------


def _items(count: int) -> list[InputItem]:
    return [
        InputItem(item_id=f"i{index}", encoded_bytes=10, decoded_pixels=10)
        for index in range(count)
    ]


def test_plan_compute_singletons_without_parallel_promise() -> None:
    planner = BudgetPlanner()
    capability = AdapterCapability(name="OCR", real_batch=False)
    batches = planner.plan_compute(_items(3), capability)
    assert [len(batch.items) for batch in batches] == [1, 1, 1]


def test_plan_compute_groups_bounded_parallel_batches() -> None:
    planner = BudgetPlanner()
    capability = AdapterCapability(
        name="OCR", real_batch=False, parallel_compute=True, max_compute_batch=2
    )
    batches = planner.plan_compute(_items(5), capability)
    assert [len(batch.items) for batch in batches] == [2, 2, 1]


# ---------------------------------------------------------------------------
# RapidOcrEngine：device fail-closed + 有界独立实例并行保序
# ---------------------------------------------------------------------------


class _FakeRapidOcrModule(types.ModuleType):
    """线程安全记录调用的 rapidocr 替身。"""

    instances: list["_FakeRapidOcr"] = []
    lock = threading.Lock()

    def __init__(self, name: str) -> None:
        super().__init__(name)
        self.RapidOCR = _FakeRapidOcr  # type: ignore[assignment]


class _FakeRapidOcr:
    def __init__(self, **params: Any) -> None:
        self.params = params
        self.calls_lock = threading.Lock()
        with _FakeRapidOcrModule.lock:
            _FakeRapidOcrModule.instances.append(self)

    def __call__(self, image: Any) -> Any:
        with self.calls_lock:
            time.sleep(0.05)  # 触发真实并行窗口
            marker = getattr(image, "marker", "?")
        return _FakeOutput(
            texts=(f"text-{marker}",),
            scores=(0.9,),
            boxes=(((0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0)),),
        )


class _FakeOutput:
    def __init__(
        self,
        *,
        texts: tuple[str, ...],
        scores: tuple[float, ...],
        boxes: tuple[Any, ...],
    ) -> None:
        self.txts = texts
        self.scores = scores
        self.boxes = boxes


class _FakeImage:
    """带 marker 的 ndarray 替身（避免 numpy 依赖差异）。"""

    shape = (10, 10)

    def __init__(self, marker: str) -> None:
        self.marker = marker


class _FakeSelection:
    def __init__(self, pipeline_id: str, options: dict[str, Any]) -> None:
        self.pipeline_id = pipeline_id
        self.options = options


class _FakeItem:
    def __init__(self, marker: str, data: bytes) -> None:
        self.item_id = marker
        self.data = data


@pytest.fixture()
def fake_rapidocr(monkeypatch: pytest.MonkeyPatch) -> None:
    module = _FakeRapidOcrModule("rapidocr")
    _FakeRapidOcrModule.instances = []
    monkeypatch.setitem(sys.modules, "rapidocr", module)


def _png_bytes() -> bytes:
    import io

    from PIL import Image

    buffer = io.BytesIO()
    Image.new("RGB", (4, 4), "white").save(buffer, format="PNG")
    return buffer.getvalue()


def test_rapidocr_cuda_fails_closed_without_provider(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    from vibeocr.runtime.recognition import rapidocr_engine
    from vibeocr.runtime.recognition.ocr_engines import OcrEngineError

    monkeypatch.setattr(rapidocr_engine, "cuda_provider_ready", lambda: False)
    engine = rapidocr_engine.RapidOcrEngine()
    selection = _FakeSelection("OCR", {"device": "cuda"})
    # capabilities 调用点不抛错（executor 只容 AttributeError/TypeError）。
    assert engine.capabilities(selection).parallel_compute is False
    with pytest.raises(OcrEngineError) as info:
        engine.recognize_many([_FakeItem("a", _png_bytes())], options=selection)
    assert info.value.reason_code == "device_provider_unavailable"


def test_rapidocr_invalid_device_fails_closed(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    from vibeocr.runtime.recognition import rapidocr_engine
    from vibeocr.runtime.recognition.ocr_engines import OcrEngineError

    monkeypatch.setattr(rapidocr_engine, "cuda_provider_ready", lambda: False)
    engine = rapidocr_engine.RapidOcrEngine()
    with pytest.raises(OcrEngineError) as info:
        engine.recognize_many(
            [_FakeItem("a", _png_bytes())],
            options=_FakeSelection("OCR", {"device": "gpu"}),
        )
    assert info.value.reason_code == "device_value_invalid"


def test_rapidocr_cuda_ready_reports_parallel_disabled(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    from vibeocr.runtime.recognition import rapidocr_engine

    monkeypatch.setattr(rapidocr_engine, "cuda_provider_ready", lambda: True)
    engine = rapidocr_engine.RapidOcrEngine()
    cuda_cap = engine.capabilities(_FakeSelection("OCR", {"device": "cuda"}))
    assert cuda_cap.parallel_compute is False
    assert cuda_cap.max_compute_batch == 1


def test_rapidocr_parallel_preserves_order_and_isolates_instances(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    from vibeocr.runtime.recognition import rapidocr_engine

    monkeypatch.setattr(rapidocr_engine, "cuda_provider_ready", lambda: False)
    module = _FakeRapidOcrModule("rapidocr")
    _FakeRapidOcrModule.instances = []
    monkeypatch.setitem(sys.modules, "rapidocr", module)
    engine = rapidocr_engine.RapidOcrEngine()
    if engine.parallel_workers() < 2:
        pytest.skip("single-core host: parallel path not engaged")
    capability = engine.capabilities(_FakeSelection("OCR", {"device": "cpu"}))
    assert capability.parallel_compute is True
    assert capability.max_compute_batch == engine.parallel_workers()

    payload_bytes = _png_bytes()
    items = [_FakeItem(f"item-{index}", payload_bytes) for index in range(4)]
    # monkeypatch 图像解码：直接返回带 marker 的替身。
    monkeypatch.setattr(
        rapidocr_engine.RapidOcrEngine,
        "_to_ndarray",
        staticmethod(lambda raw: _FakeImage(raw.item_id)),
    )
    monkeypatch.setattr(
        rapidocr_engine.RapidOcrEngine, "_raw_bytes", staticmethod(lambda item: item)
    )
    payloads = engine.recognize_many(
        items, options=_FakeSelection("OCR", {"device": "cpu"})
    )
    assert len(payloads) == 4
    texts = [payload["raw_text"] for payload in payloads]
    assert texts == [f"text-item-{index}" for index in range(4)]
    # 每张图一个独立实例调用（线程私有实例），且总实例数受 worker 数约束。
    called = [inst for inst in _FakeRapidOcrModule.instances if inst.params]
    assert 1 <= len(called) <= engine.parallel_workers() + 1
    engine.close()


# ---------------------------------------------------------------------------
# RapidTableEngine：模型就绪门禁 + payload 形状 + device fail-closed
# ---------------------------------------------------------------------------


def test_rapid_table_probe_requires_model(monkeypatch: pytest.MonkeyPatch) -> None:
    from vibeocr.runtime.recognition import rapidtable_engine

    module = types.ModuleType("rapid_table")
    monkeypatch.setitem(sys.modules, "rapid_table", module)
    monkeypatch.setattr(rapidtable_engine, "resolve_model_path", lambda: None)
    engine = rapidtable_engine.RapidTableEngine()
    assert engine.probe_reason() == "model_not_prepared"


def test_rapid_table_cuda_fails_closed_without_provider(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    from vibeocr.runtime.recognition import rapidtable_engine
    from vibeocr.runtime.recognition.ocr_engines import OcrEngineError

    module = types.ModuleType("rapid_table")
    monkeypatch.setitem(sys.modules, "rapid_table", module)
    monkeypatch.setattr(rapidtable_engine, "resolve_model_path", lambda: object())
    monkeypatch.setattr(rapidtable_engine, "cuda_provider_ready", lambda: False)
    engine = rapidtable_engine.RapidTableEngine()
    selection = _FakeSelection("RAPID_TABLE", {"device": "cuda"})
    # capabilities 不抛错；确定性设备失败在 recognize_many 抛 OcrEngineError。
    assert engine.capabilities(selection).max_compute_batch == 1
    with pytest.raises(OcrEngineError) as info:
        engine.recognize_many([_FakeItem("t", _png_bytes())], options=selection)
    assert info.value.reason_code == "device_provider_unavailable"
    assert info.value.engine == "rapidtable"


def test_rapid_table_payload_matches_table_contract(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    from vibeocr.runtime.documents.tables.contracts import TableModelV1
    from vibeocr.runtime.recognition import rapidtable_engine

    html = (
        "<table><tr><th>Item</th><th>Qty</th></tr>"
        "<tr><td>Apple</td><td>10</td></tr></table>"
    )

    class _FakeResult:
        pred_htmls = [html]
        cell_bboxes = [
            [
                [0.0, 0.0, 5.0, 5.0],
                [5.0, 0.0, 10.0, 5.0],
                [0.0, 5.0, 5.0, 10.0],
                [5.0, 5.0, 10.0, 10.0],
            ]
        ]

    class _FakeEngine:
        def __call__(self, image: Any) -> Any:
            return _FakeResult()

    engine = rapidtable_engine.RapidTableEngine()
    monkeypatch.setattr(engine, "_ensure_engine", lambda device: _FakeEngine())
    monkeypatch.setattr(
        rapidtable_engine.RapidTableEngine,
        "_to_ndarray",
        staticmethod(lambda raw: _FakeImage("t")),
    )
    payload = engine.recognize_many(
        [_FakeItem("t", _png_bytes())],
        options=_FakeSelection("RAPID_TABLE", {"device": "cpu"}),
    )[0]
    blocks = [b for b in payload["content_list"] if b.get("type") == "table"]
    assert len(blocks) == 1
    table = TableModelV1.from_payload(blocks[0]["table"])  # 契约回读校验
    assert table.row_count == 2 and table.column_count == 2
    texts = {cell.text for cell in table.cells}
    assert {"Item", "Qty", "Apple", "10"} <= texts
    assert all(cell.bbox is not None for cell in table.cells)
    assert table.coordinate_space.value == "pixel"
    assert blocks[0]["table_body"].startswith("<table")
    assert payload["pipeline_type"] == "TABLE_RECOGNITION"


# ---------------------------------------------------------------------------
# 路由与目录
# ---------------------------------------------------------------------------


class _RecordingEngine:
    engine_id = None

    def __init__(self) -> None:
        self.calls: list[str] = []

    def capabilities(self, options: Any = None) -> AdapterCapability:
        self.calls.append("capabilities")
        return AdapterCapability(name="RAPID_TABLE", real_batch=False)

    def recognize_many(
        self,
        items: list[Any],
        *,
        options: Any | None = None,
        compute_batch: Any | None = None,
    ) -> list[dict[str, Any]]:
        self.calls.append(f"recognize:{len(items)}")
        return [{} for _ in items]

    def preload(self, pipelines: tuple[str, ...]) -> Any:
        self.calls.append("preload")
        return None

    def release_idle(self, pipeline: Any = None) -> Any:
        self.calls.append("release")
        return None

    def residency_status(self) -> Any:
        return None

    def configure_settings(self, snapshot: Any) -> None:
        self.calls.append("configure")

    def close(self) -> None:
        self.calls.append("close")


def test_router_routes_rapid_table_pipeline() -> None:
    from vibeocr.runtime.recognition.ocr_engines import (
        OcrEngineRegistry,
        OcrEngineResolver,
    )
    from vibeocr.runtime_contracts import PipelineSelection

    recording = _RecordingEngine()
    registry = OcrEngineRegistry([])
    resolver = OcrEngineResolver(registry=registry)
    fallback = _RecordingEngine()
    router = OcrEngineRoutingAdapter(
        fallback_factory=lambda: fallback,
        resolver=resolver,
        rapid_table_factory=lambda: recording,
    )
    selection = PipelineSelection(pipeline_id="RAPID_TABLE", options={"device": "cpu"})
    assert router.capabilities(selection).name == "RAPID_TABLE"
    assert router.recognize_many([object(), object()], options=selection) == [{}, {}]
    assert recording.calls == ["capabilities", "recognize:2"]
    assert fallback.calls == []
    router.close()
    assert "close" in recording.calls


def test_rapid_table_mode_definition_is_base_runtime_unmanaged() -> None:
    definition = get_recognition_mode_definition(RecognitionMode.RAPID_TABLE)
    assert definition.pipeline is OCRPipeline.RAPID_TABLE
    assert definition.engine is None
    assert definition.provisioning.value == "base_runtime"
    assert definition.lifecycle.kind.value == "unmanaged"
    assert not definition.lifecycle.supports_preload
    assert definition.supported_options == ("device",)


# ---------------------------------------------------------------------------
# CUDA 激活失败重试契约：不缓存降级实例
# ---------------------------------------------------------------------------


def test_rapidocr_cuda_activation_failure_does_not_cache_cpu_instance(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """激活失败后缓存字段必须为空：重试重新构造+激活，绝不复用 CPU 实例。"""
    from vibeocr.runtime.recognition import rapidocr_engine

    calls = {"construct": 0, "activate": 0}

    class _FakeCudaEngine:
        def __call__(self, image: Any) -> Any:
            return _FakeOutput(
                texts=("ok",),
                scores=(0.9,),
                boxes=(((0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0)),),
            )

    def _construct(internal_self: Any, params: dict[str, Any] | None) -> Any:
        del internal_self
        calls["construct"] += 1
        return _FakeCudaEngine()

    def _fail(engine: Any) -> None:
        calls["activate"] += 1
        if calls["activate"] == 1:
            raise RuntimeError("cuda activation failed")

    monkeypatch.setattr(rapidocr_engine, "cuda_provider_ready", lambda: True)
    monkeypatch.setattr(rapidocr_engine, "activate_cuda_sessions", _fail)
    monkeypatch.setattr(rapidocr_engine.RapidOcrEngine, "_construct_engine", _construct)
    engine = rapidocr_engine.RapidOcrEngine()
    selection = _FakeSelection("OCR", {"device": "cuda"})
    with pytest.raises(RuntimeError):
        engine.recognize_many([_FakeItem("a", _png_bytes())], options=selection)
    assert engine._cuda_engine is None  # noqa: SLF001 - 契约断言
    # 重试：第二次激活成功后缓存可用实例。
    payloads = engine.recognize_many([_FakeItem("a", _png_bytes())], options=selection)
    assert len(payloads) == 1
    assert calls["construct"] == 2 and calls["activate"] == 2
    engine.close()
