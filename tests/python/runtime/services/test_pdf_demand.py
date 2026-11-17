"""PDF demand admission, detection and raster contracts."""

from __future__ import annotations

import asyncio
import json
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from typing import TYPE_CHECKING

import pymupdf as fitz
import pytest
from fastapi import HTTPException
from vibeocr.runtime.documents import pdf_backend_process as backend
from vibeocr.runtime.documents.pdf_backend_client import (
    PdfBackendClient,
    PdfBackendError,
)
from vibeocr.runtime.documents.pdf_native_gate import PdfNativeGate
from vibeocr.runtime.documents.wire_schemas import (
    BatchAddTextLayerRequest,
    DetectTextLayersRequest,
    PdfDemandLoadRequest,
    PdfReadCancelRequest,
    PdfReadContext,
    RenderPreviewRequest,
)
from vibeocr.runtime.recognition.models.ocr_result import TextBlock

if TYPE_CHECKING:
    from collections.abc import Iterator
    from pathlib import Path


@pytest.fixture
def demand_session(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> Iterator[backend.BackendSession]:
    file = tmp_path / "demand.pdf"
    with fitz.open() as doc:
        page = doc.new_page(width=300, height=600)
        page.insert_text((30, 30), "selectable")
        doc.new_page(width=20, height=2000)
        scanned = doc.new_page(width=300, height=600)
        pixels = fitz.Pixmap(fitz.csRGB, 2, 2, b"\xff" * 12, False)
        scanned.insert_image(scanned.rect, pixmap=pixels, keep_proportion=False)
        doc.save(file)
    registry = backend.SessionRegistry()
    monkeypatch.setattr(backend, "_REGISTRY", registry)
    monkeypatch.setattr(backend, "_NATIVE_GATE", PdfNativeGate())
    session = registry.add(str(file))
    try:
        yield session
    finally:
        registry.remove(session.session_id)


def _read(scope: str, epoch: int) -> PdfReadContext:
    return PdfReadContext.model_validate({"scope": scope, "epoch": epoch})


def _cancel(session: backend.BackendSession, scope: str, before_epoch: int) -> None:
    req = PdfReadCancelRequest.model_validate(
        {"scope": scope, "before_epoch": before_epoch}
    )
    result = backend.cancel(session.session_id, req)
    assert result.model_dump() == {"cancelled": True}


async def _lines(session: backend.BackendSession, req: PdfDemandLoadRequest | None):
    response = backend.session_load(session.session_id, req)
    return [json.loads(line) async for line in response.body_iterator]


async def test_read_cutoffs_are_monotonic_and_independent_of_write_cancel(
    demand_session,
):
    s = demand_session
    epoch = 2**32 + 1
    _cancel(s, "viewport", epoch)
    _cancel(s, "viewport", 1)
    assert not s.cancel_event.is_set()
    backend.cancel(s.session_id)
    stale = await _lines(s, PdfDemandLoadRequest(read=_read("viewport", epoch - 1)))
    assert [line["message"] for line in stale] == ["cancelled"]
    allowed = await _lines(
        s, PdfDemandLoadRequest(read=_read("viewport", epoch), pages=[0])
    )
    assert allowed[-1]["message"] == "done"
    allowed = await _lines(
        s, PdfDemandLoadRequest(read=_read("background", 1), pages=[0])
    )
    assert allowed[-1]["message"] == "done"
    legacy = await _lines(s, None)
    assert [line["message"] for line in legacy] == ["cancelled"]

    _cancel(s, "all_reads", epoch + 1)
    backend.reset_cancel(s.session_id)
    for scope in ("viewport", "background"):
        fenced = await _lines(
            s, PdfDemandLoadRequest(read=_read(scope, epoch), pages=[0])
        )
        assert [line["message"] for line in fenced] == ["cancelled"]


async def test_targeted_presence_preserves_metadata_and_reports_partial_failure(
    demand_session, monkeypatch
):
    s = demand_session
    info = s.pdf_document.pages[0]
    info.ocr_text_blocks = [TextBlock(text="recognized", score=0.8, bbox=None)]
    info.ocr_preproc_angle = 270
    info.deskewed = True

    # Demand must not pay image coverage costs even for a blank page.
    def unexpected_coverage(*_):
        pytest.fail("geometry/presence must skip image coverage")

    monkeypatch.setattr(backend.PdfService, "is_page_scanned", unexpected_coverage)
    req = PdfDemandLoadRequest(read=_read("background", 1), pages=[1, 0, 20, 0])
    events = await _lines(s, req)
    assert [event["page_index"] for event in events[:-1]] == [1, 0]
    blank, text = (event["page_payload"] for event in events[:-1])
    assert blank["has_text_layer"] is False and blank["scan_checked"] is False
    assert text["has_text_layer"] is True and text["scan_checked"] is False
    assert text["ocr_text_blocks"][0]["text"] == "recognized"
    assert text["ocr_preproc_angle"] == 270 and text["deskewed"] is True
    assert events[-1]["message"] == "incomplete"
    assert events[-1]["current"] == 2 and events[-1]["total"] == 3


def test_revokes_waiter_and_drops_inflight_copy_before_encoding(
    demand_session, monkeypatch
):
    s = demand_session
    entered = threading.Event()
    release = threading.Event()
    copied = threading.Event()
    calls: list[int] = []

    def rasterize(doc, page, dpi):
        assert s.fitz_lock.locked()
        calls.append(page)
        entered.set()
        assert release.wait(5)
        copied.set()
        return b"\0\0\0", 1, 1

    def encoding(*_):
        pytest.fail("cancelled read must skip PNG encoding")

    monkeypatch.setattr(backend, "_render_page_pixels", rasterize)
    monkeypatch.setattr(backend, "_encode_png", encoding)
    read = _read("viewport", 1)
    with ThreadPoolExecutor(max_workers=2) as pool:
        in_native = pool.submit(
            backend.render_preview,
            s.session_id,
            RenderPreviewRequest(page=0, read=read),
        )
        try:
            assert entered.wait(5)
            queued = pool.submit(
                backend.render_preview,
                s.session_id,
                RenderPreviewRequest(page=1, read=read),
            )
            deadline = time.monotonic() + 5
            while s.native_gate.foreground_waiters != 1 and time.monotonic() < deadline:
                time.sleep(0.002)
            assert s.native_gate.foreground_waiters == 1
            _cancel(s, "viewport", 2)
            with pytest.raises(HTTPException, match="409"):
                queued.result(timeout=5)
            assert not in_native.done()
            assert s.native_gate.foreground_waiters == 0
            assert s.active_ops == 1
        finally:
            release.set()
        with pytest.raises(HTTPException, match="409"):
            in_native.result(timeout=5)
    assert copied.is_set() and calls == [0]


async def test_smoke_delay_can_be_cancelled_without_holding_native_gate(
    demand_session, monkeypatch
):
    s = demand_session
    delay_entered = asyncio.Event()
    loop = asyncio.get_running_loop()

    class ObservedCondition(threading.Condition):
        def wait(self, timeout=None):
            loop.call_soon_threadsafe(delay_entered.set)
            return super().wait(timeout)

    monkeypatch.setenv("VIBEOCR_PDF_DEMAND_TEST_DELAY_MS", "999")
    assert backend._demand_self_test_delay_seconds() == 0
    monkeypatch.setenv("VIBEOCR_SELF_TEST_SMOKE", "paddle-modes-e2e")
    assert backend._demand_self_test_delay_seconds() == 0
    monkeypatch.setenv("VIBEOCR_PADDLE_SMOKE_INPUT_KIND", "pdf_baseline")
    assert backend._demand_self_test_delay_seconds() == 0.5
    monkeypatch.setattr(s, "read_changed", ObservedCondition())
    # Use an intentionally long wait to prove the condition wakes on scoped cancel.
    monkeypatch.setattr(backend, "_demand_self_test_delay_seconds", lambda: 30)
    auto = asyncio.create_task(
        _lines(s, PdfDemandLoadRequest(read=_read("background", 1)))
    )
    try:
        await asyncio.wait_for(delay_entered.wait(), 5)
        assert s.active_ops == 0 and not s.native_gate.holder
        foreground = await asyncio.wait_for(
            _lines(s, PdfDemandLoadRequest(read=_read("viewport", 1), pages=[0])), 5
        )
        assert foreground[-1]["message"] == "done"
    finally:
        _cancel(s, "background", 2)
    events = await asyncio.wait_for(auto, 5)
    assert [line["message"] for line in events] == ["cancelled"]


def test_direct_raster_obeys_requested_edge_and_pixel_budget(demand_session):
    with backend._fitz_op(demand_session), demand_session.fitz_lock:
        doc = demand_session.doc
        _, width, height = backend._render_page_pixels(doc, 0, 72, target_size=120)
        assert (width, height) == (60, 120)
        _, width, height = backend._render_page_pixels(doc, 0, 72, max_edge=960)
        assert (width, height) == (480, 960)
        _, width, height = backend._render_page_pixels(
            doc, 1, 1200, max_edge=502, max_pixels=1000
        )
        assert max(width, height) <= 502 and width * height <= 1000
        _, width, height = backend._render_page_pixels(doc, 0, 72)
        assert (width, height) == (300, 600)


@pytest.mark.parametrize("response", [{"cancelled": False}, {"cancelled": "true"}, {}])
def test_worker_read_cancel_requires_explicit_ack(monkeypatch, response):
    class Reply:
        def json(self):
            return response

    client = PdfBackendClient()
    monkeypatch.setattr(client, "_post", lambda *_: Reply())
    with pytest.raises(PdfBackendError, match="ACK"):
        client.cancel_reads("session", "all_reads", 2)


async def test_committed_text_writes_invalidate_only_changed_page_detection(
    demand_session,
):
    s = demand_session
    scanned = (await _lines(s, None))[2]["page_payload"]
    assert scanned["is_scanned"] is True and scanned["scan_checked"] is True
    assert (
        await _lines(s, PdfDemandLoadRequest(read=_read("viewport", 1), pages=[2]))
    )[0]["page_payload"]["scan_checked"] is True
    backend.detect_text_layers(s.session_id, DetectTextLayersRequest(page=0))
    assert s.pdf_document.pages[0].text_layers[0].text_preview == "selectable"

    ocr = {
        "preproc_angle": 0,
        "text_blocks": [
            {"text": "recognized", "score": 0.9, "bbox": [100, 100, 800, 180]}
        ],
    }
    noop = backend.add_text_layer_batch(
        s.session_id,
        BatchAddTextLayerRequest.model_validate(
            {
                "pages": [
                    {"page": 2, "ocr_result": {"preproc_angle": 0, "text_blocks": []}}
                ],
            }
        ),
    )
    assert noop.extra["results"][2][0] == 0
    assert s.pdf_document.pages[2].scan_checked

    result = backend.add_text_layer_batch(
        s.session_id,
        BatchAddTextLayerRequest.model_validate(
            {
                "pages": [{"page": index, "ocr_result": ocr} for index in (0, 2)],
                "overwrite": True,
            }
        ),
    )
    assert {index: counts[0] for index, counts in result.extra["results"].items()} == {
        0: 1,
        2: 1,
    }
    current = await _lines(
        s, PdfDemandLoadRequest(read=_read("viewport", 1), pages=[0, 2])
    )
    for item in current[:-1]:
        page = item["page_payload"]
        assert page["has_text_layer"] is True
        assert page["is_scanned"] is False and page["scan_checked"] is False
        assert page["text_layers"] == []
        assert page["ocr_text_blocks"][0]["text"] == "recognized"


async def test_presence_change_and_insert_do_not_recompute_scan_details(
    demand_session, monkeypatch
):
    s = demand_session
    await _lines(s, None)
    page = s.pdf_document.pages[2]
    assert page.is_scanned and page.scan_checked
    with backend._fitz_op(s), s.fitz_lock:
        s.doc[2].insert_text((30, 30), "changed outside page model")

    def unexpected_detail(*_):
        pytest.fail("ordinary page refresh must skip detail and scan coverage")

    monkeypatch.setattr(backend.PdfService, "is_page_scanned", unexpected_detail)
    monkeypatch.setattr(backend.PdfService, "detect_text_layers", unexpected_detail)
    row = (await _lines(s, PdfDemandLoadRequest(read=_read("viewport", 1), pages=[2])))[
        0
    ]["page_payload"]
    assert row["has_text_layer"] and not row["scan_checked"]
    with backend._fitz_op(s), s.fitz_lock:
        backend.PdfService.insert_pages_from(
            s.doc, s.pdf_document, s.file_path, after_index=2
        )
    inserted = s.pdf_document.pages[3:6]
    assert [info.has_text_layer for info in inserted] == [True, False, False]
    assert not any(info.scan_checked or info.text_layers for info in inserted)
