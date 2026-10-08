"""Deterministic unit tests for PaddleExecutor (the supervisor↔adapter bridge).

These exercise the bridge logic (queued→running→terminal, per-item mapping,
cancel handling, whole-batch failure) with a fake adapter, so the executor is
covered without the heavy real-model integration test.
"""

from __future__ import annotations

from typing import TYPE_CHECKING, Any

from vibeocr.runtime.jobs.registry import JobRegistry
from vibeocr.runtime.jobs.staging import StagedInput
from vibeocr.runtime.recognition.paddle_adapter import PaddlePipelineAdapter
from vibeocr.runtime.recognition.paddle_executor import PaddleExecutor
from vibeocr.runtime_contracts import ItemState, JobKind, JobPriority, JobState

if TYPE_CHECKING:
    from pathlib import Path


class _FakeService:
    """Returns one OCR-result-shaped dict per input, in order."""

    def __init__(self, texts: list[str], *, fail: bool = False) -> None:
        self._texts = texts
        self._fail = fail

    def recognize_batch(
        self, images: list[Any], options: Any | None = None, *, asset_sinks=None
    ) -> list[dict[str, Any]]:
        if self._fail:
            raise RuntimeError("OOM during predict")
        return [{"text": t} for t in self._texts]

    def preload_pipelines_sequential(self, pipelines: list[Any]) -> dict[str, bool]:
        return {str(pipeline): True for pipeline in pipelines}


def _make_adapter(service: _FakeService) -> PaddlePipelineAdapter:
    return PaddlePipelineAdapter(service=service)


def _make_job(registry: JobRegistry, items: int) -> Any:
    record = registry.create(
        kind=JobKind.RECOGNITION,
        priority=JobPriority.INTERACTIVE,
        items=[
            __import__("vibeocr.runtime_contracts", fromlist=["JobItem"]).JobItem(
                item_id=f"it-{i}",
                display_name=f"f{i}.png",
                state=__import__(
                    "vibeocr.runtime_contracts", fromlist=["ItemState"]
                ).ItemState.QUEUED,
            )
            for i in range(items)
        ],
        progress_total=items,
    )
    record.transition(JobState.QUEUED)
    return record


def _valid_png_bytes() -> bytes:
    """A minimal 1x1 valid PNG so the real adapter's PIL decode succeeds."""
    import io

    from PIL import Image

    buf = io.BytesIO()
    Image.new("RGB", (1, 1)).save(buf, format="PNG")
    return buf.getvalue()


def _staged(items: int, base: Path) -> list[StagedInput]:
    out: list[StagedInput] = []
    png = _valid_png_bytes()
    for i in range(items):
        p = base / f"f{i}.png"
        p.write_bytes(png)
        out.append(
            StagedInput(
                item_id=f"it-{i}", display_name=f"f{i}.png", path=p, size_bytes=len(png)
            )
        )
    return out


def test_execute_runs_job_to_completed_and_maps_results(tmp_path, monkeypatch) -> None:
    png = _valid_png_bytes()
    for i in range(2):
        (tmp_path / f"f{i}.png").write_bytes(png)
    reg = JobRegistry(instance_id="t")
    record = _make_job(reg, 2)
    staged = [
        StagedInput(
            item_id=f"it-{i}",
            display_name=f"f{i}.png",
            path=tmp_path / f"f{i}.png",
            size_bytes=len(png),
        )
        for i in range(2)
    ]
    executor = PaddleExecutor(
        adapter_factory=lambda: _make_adapter(_FakeService(["alpha", "beta"]))
    )
    executor.execute(record, staged)
    snap = record.snapshot()
    assert snap.state is JobState.COMPLETED
    assert [it.state for it in snap.items] == [ItemState.SUCCEEDED, ItemState.SUCCEEDED]
    results = [record.results[f"it-{i}"] for i in range(2)]
    assert [r["text"] for r in results] == ["alpha", "beta"]


def test_execute_isolates_whole_batch_failure(tmp_path: Path) -> None:
    reg = JobRegistry(instance_id="t")
    record = _make_job(reg, 2)
    executor = PaddleExecutor(
        adapter_factory=lambda: _make_adapter(_FakeService(["x"], fail=True))
    )
    executor.execute(record, _staged(2, tmp_path))
    snap = record.snapshot()
    assert snap.state is JobState.FAILED


def test_local_models_failure_is_not_retried_and_exposes_explicit_reason(tmp_path):
    from vibeocr.runtime.environments.model_cache import LocalModelsNotPrepared

    calls = []

    class MissingModels(_FakeService):
        def recognize_batch(self, images, options=None, *, asset_sinks=None):
            calls.append(True)
            raise LocalModelsNotPrepared("private diagnostic path must not reach UI")

    record = _make_job(JobRegistry(instance_id="local-only"), 2)
    executor = PaddleExecutor(
        adapter_factory=lambda: _make_adapter(MissingModels([])),
        sleeper=lambda _delay: (_ for _ in ()).throw(
            AssertionError("must not back off")
        ),
    )
    executor.execute(record, _staged(2, tmp_path))
    assert len(calls) == 1
    assert record.snapshot().state is JobState.FAILED
    outcomes = record.observe(0).outcomes
    assert len(outcomes) == 2
    assert all(
        outcome.error_code == "BACKEND_UNAVAILABLE"
        and outcome.error_detail
        == {
            "message": "local_models_not_prepared",
            "reason": "local_models_not_prepared",
        }
        for outcome in outcomes
    )


def test_execute_honours_cancel_before_run(tmp_path: Path) -> None:
    reg = JobRegistry(instance_id="t")
    record = _make_job(reg, 1)
    # Simulate cancel requested before the executor runs.
    record.cancel_requested_at = "2026-07-25T00:00:00+00:00"
    record.transition(JobState.CANCEL_REQUESTED)
    executor = PaddleExecutor(
        adapter_factory=lambda: _make_adapter(_FakeService(["x"]))
    )
    executor.execute(record, _staged(1, tmp_path))
    assert record.snapshot().state is JobState.CANCELLED


def test_execute_empty_items_completes() -> None:
    reg = JobRegistry(instance_id="t")
    record = _make_job(reg, 0)
    executor = PaddleExecutor(adapter_factory=lambda: _make_adapter(_FakeService([])))
    executor.execute(record, [])
    assert record.snapshot().state is JobState.COMPLETED


def test_execute_rejects_document_input_kinds_with_typed_error(
    tmp_path: Path,
) -> None:
    """Batch documents must be rejected clearly, never decoded as images.

    回归契约：图片解码管线（OCR/PP-StructureV3 等全部 RECOGNITION 路径）
    收到 PDF/Office 输入时按文件给出类型化 VALIDATION_ERROR 失败并指向
    MinerU 文档解析，不进入 PIL 解码/恢复路径；同批图片项继续完成。
    """
    png = _valid_png_bytes()
    (tmp_path / "page.png").write_bytes(png)
    (tmp_path / "doc.pdf").write_bytes(b"%PDF-1.4 not an image")
    reg = JobRegistry(instance_id="t")
    from vibeocr.runtime_contracts import JobItem

    record = reg.create(
        kind=JobKind.RECOGNITION,
        priority=JobPriority.INTERACTIVE,
        items=[
            JobItem(
                item_id="it-img",
                display_name="page.png",
                state=ItemState.QUEUED,
            ),
            JobItem(
                item_id="it-pdf",
                display_name="doc.pdf",
                state=ItemState.QUEUED,
            ),
        ],
        progress_total=2,
    )
    record.transition(JobState.QUEUED)
    staged = [
        StagedInput(
            item_id="it-img",
            display_name="page.png",
            path=tmp_path / "page.png",
            size_bytes=len(png),
            content_type="image/png",
        ),
        StagedInput(
            item_id="it-pdf",
            display_name="doc.pdf",
            path=tmp_path / "doc.pdf",
            size_bytes=20,
            content_type="application/pdf",
        ),
    ]
    executor = PaddleExecutor(
        adapter_factory=lambda: _make_adapter(_FakeService(["alpha"]))
    )
    executor.execute(record, staged)
    snap = record.snapshot()
    assert snap.state is JobState.COMPLETED_WITH_ERRORS
    states = {it.item_id: it.state for it in snap.items}
    assert states["it-img"] is ItemState.SUCCEEDED
    assert states["it-pdf"] is ItemState.FAILED
    assert record.item_errors["it-pdf"] == "VALIDATION_ERROR"
    failed = next(it for it in snap.items if it.item_id == "it-pdf")
    assert failed.error is not None
    assert "MinerU" in failed.error
    assert record.results["it-img"]["text"] == "alpha"


def test_execute_rejects_office_content_type_without_extension(
    tmp_path: Path,
) -> None:
    """Wire content_type alone must route the document gate (drop-friendly)."""
    (tmp_path / "upload.bin").write_bytes(b"PK\x03\x04 zip container")
    reg = JobRegistry(instance_id="t")
    from vibeocr.runtime_contracts import JobItem

    record = reg.create(
        kind=JobKind.RECOGNITION,
        priority=JobPriority.INTERACTIVE,
        items=[
            JobItem(
                item_id="it-xlsx",
                display_name="upload.bin",
                state=ItemState.QUEUED,
            ),
        ],
        progress_total=1,
    )
    record.transition(JobState.QUEUED)
    staged = [
        StagedInput(
            item_id="it-xlsx",
            display_name="upload.bin",
            path=tmp_path / "upload.bin",
            size_bytes=18,
            content_type=(
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            ),
        ),
    ]
    executor = PaddleExecutor(
        adapter_factory=lambda: _make_adapter(_FakeService(["unused"]))
    )
    executor.execute(record, staged)
    snap = record.snapshot()
    assert snap.state is JobState.FAILED
    assert snap.items[0].state is ItemState.FAILED
    assert record.item_errors["it-xlsx"] == "VALIDATION_ERROR"
