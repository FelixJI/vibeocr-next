"""合成 PDF 的文字层事务、当前修订与预算回归。"""

import io
from pathlib import Path

import pymupdf as fitz
import pytest
from fastapi.testclient import TestClient
from PIL import Image
from vibeocr.runtime.documents import pdf_backend_process as backend
from vibeocr.runtime.documents.models.pdf_document import PdfDocument, PdfPageInfo
from vibeocr.runtime.documents.models.pdf_ocr_options import PdfGlobalSettings
from vibeocr.runtime.documents.pdf_service import PdfService


def result(text, angle=0):
    return {
        "preproc_angle": angle,
        "text_blocks": [
            {
                "text": text,
                "score": 0.9,
                "bbox": [100, 100, 600, 180],
                "polygon": [100, 100, 600, 100, 600, 180, 100, 180],
            }
        ],
    }


def document():
    doc = fitz.open()
    for i in range(3):
        page = doc.new_page(width=400, height=300)
        page.draw_rect(fitz.Rect(10, 10, 390, 290), color=(1, 0, 0))
        page.insert_text((30, 70), f"OLD{i}")
    model = PdfDocument(
        pages=[PdfPageInfo(page_index=i, has_text_layer=True) for i in range(3)]
    )
    return doc, model


def test_empty_failure_and_cancel_preserve_uncommitted_layers(monkeypatch):
    doc, model = document()
    before = [doc[i].get_text() for i in range(3)]
    PdfService.add_text_layer_batch(
        doc, model, [{"page": 0, "ocr_result": result("")}], overwrite=True
    )
    assert doc[0].get_text() == before[0]
    original = PdfService._write_blocks_to_page

    def fail(*args, **kwargs):
        raise RuntimeError("injected write failure")

    monkeypatch.setattr(PdfService, "_write_blocks_to_page", fail)
    with pytest.raises(RuntimeError, match="injected"):
        PdfService.add_text_layer_batch(
            doc, model, [{"page": 0, "ocr_result": result("NEW")}], overwrite=True
        )
    assert [doc[i].get_text() for i in range(3)] == before
    monkeypatch.setattr(PdfService, "_write_blocks_to_page", original)
    calls = 0

    def cancel():
        nonlocal calls
        calls += 1
        return calls > 1

    out = PdfService.add_text_layer_batch(
        doc,
        model,
        [{"page": i, "ocr_result": result(f"NEW{i}")} for i in range(3)],
        overwrite=True,
        cancel_check=cancel,
    )
    assert list(out) == [0]
    assert "NEW0" in doc[0].get_text() and "OLD0" not in doc[0].get_text()
    assert [doc[i].get_text() for i in (1, 2)] == before[1:]
    assert model.pages[1].has_text_layer and model.pages[2].has_text_layer
    doc.close()


def test_commit_failure_rolls_back_page_object(monkeypatch):
    doc, model = document()
    page_xref = doc.page_xref(0)
    original_text = doc[0].get_text()
    original = fitz.Document.xref_set_key
    injected = False

    def fail(self, xref, key, value):
        nonlocal injected
        if self is doc and xref == page_xref and key == "Resources" and not injected:
            injected = True
            raise RuntimeError("commit failure")
        return original(self, xref, key, value)

    monkeypatch.setattr(fitz.Document, "xref_set_key", fail)
    with pytest.raises(RuntimeError, match="commit failure"):
        PdfService.add_text_layer_batch(
            doc, model, [{"page": 0, "ocr_result": result("NEW")}], overwrite=True
        )
    assert doc.page_count == 3 and doc.page_xref(0) == page_xref
    assert doc[0].get_text() == original_text
    assert model.pages[0].has_text_layer
    doc.close()


@pytest.mark.parametrize("rotation", [0, 90, 180, 270])
@pytest.mark.parametrize("angle", [0, 90, 180, 270])
def test_batch_roundtrip_crop_rotation_and_preprocessing(tmp_path, rotation, angle):
    doc = fitz.open()
    page = doc.new_page(width=500, height=400)
    page.set_cropbox(fitz.Rect(40, 50, 440, 350))
    page.set_rotation(rotation)
    page.draw_rect(fitz.Rect(10, 10, 390, 290), color=(1, 0, 0))
    page.add_text_annot((20, 20), "preserved annotation")
    doc.new_page()
    doc[1].insert_link(
        {"kind": fitz.LINK_GOTO, "from": fitz.Rect(10, 10, 100, 30), "page": 0}
    )
    doc.set_metadata({"title": "synthetic metadata"})
    page_xref = doc.page_xref(0)
    pixel_before = doc[0].get_pixmap().samples
    model = PdfDocument(pages=[PdfPageInfo(page_index=i) for i in range(2)])
    out = PdfService.add_text_layer_batch(
        doc, model, [{"page": 0, "ocr_result": result("中文 Search", angle)}]
    )
    assert out[0][0] == 1
    assert doc[0].get_pixmap().samples == pixel_before
    assert doc.page_xref(0) == page_xref
    assert doc[0].rotation == rotation and doc[0].cropbox == fitz.Rect(40, 50, 440, 350)
    assert doc[1].get_links()[0]["page"] == 0
    assert len(list(doc[0].annots())) == 1
    target = tmp_path / "search.pdf"
    PdfService.save_with_rewrite(doc, model, str(target), rewrite_text_layers=False)
    with fitz.open(target) as reopened:
        assert "中文" in reopened[0].get_text() and "Search" in reopened[0].get_text()
        assert reopened.metadata["title"] == "synthetic metadata"
        assert reopened[1].get_text() == ""
        expected = PdfService._denormalize_and_unrotate_bbox(
            (100, 100, 600, 180), angle, reopened[0].rect
        )
        found = (
            fitz.Rect(reopened[0].get_text("words")[0][:4])
            * reopened[0].rotation_matrix
        )
        assert expected.intersects(found)
    assert not model.is_modified
    doc.close()


def test_cross_batch_different_subsets_preserve_all_characters(tmp_path):
    doc = fitz.open()
    for _ in range(4):
        doc.new_page()
    model = PdfDocument(pages=[PdfPageInfo(page_index=i) for i in range(4)])
    texts = ["甲乙 Alpha", "丙丁 Beta", "新字 Gamma", "甲乙 Delta"]
    for batch in ([0, 1], [2, 3]):
        PdfService.add_text_layer_batch(
            doc, model, [{"page": i, "ocr_result": result(texts[i])} for i in batch]
        )
    target = tmp_path / "cross-batch.pdf"
    PdfService.save_with_rewrite(doc, model, str(target), rewrite_text_layers=False)
    with fitz.open(target) as reopened:
        assert [reopened[i].get_text().strip() for i in range(4)] == texts
    doc.close()


def test_atomic_save_failure_preserves_target_and_dirty_edit(tmp_path, monkeypatch):
    doc, model = document()
    model.is_modified = True
    target = tmp_path / "existing.pdf"
    target.write_bytes(b"original target")

    def fail(self, target):
        raise OSError("replace failure")

    monkeypatch.setattr(Path, "replace", fail)
    with pytest.raises(OSError, match="replace failure"):
        PdfService.save_with_rewrite(doc, model, str(target), rewrite_text_layers=False)
    assert target.read_bytes() == b"original target"
    assert model.is_modified and "OLD0" in doc[0].get_text()
    assert not list(tmp_path.glob(".existing.pdf.*"))
    doc.close()


def test_current_unsaved_render_and_pixel_budget(tmp_path):
    doc, _ = document()
    source = tmp_path / "source.pdf"
    doc.save(source)
    doc.close()
    with TestClient(backend.app) as client:
        opened = client.post("/session/open", json={"path": str(source)}).json()
        sid = opened["session_id"]
        client.post(
            f"/session/{sid}/rotate", json={"pages": [1], "angle": 90}
        ).raise_for_status()
        client.post(
            f"/session/{sid}/delete_pages", json={"pages": [0]}
        ).raise_for_status()
        image = client.post(
            f"/session/{sid}/render_preview", json={"page": 0, "dpi": 300}
        )
        image.raise_for_status()
        assert Image.open(io.BytesIO(image.content)).size == (1250, 1667)
        assert (
            client.post(
                f"/session/{sid}/render_preview", json={"page": 0, "dpi": 1200}
            ).status_code
            == 400
        )
        current = client.post(f"/session/{sid}/model").json()
        assert (
            current["is_modified"]
            and len(current["pages"]) == 2
            and current["pages"][0]["rotation"] == 90
        )
        with fitz.open(source) as original:
            assert len(original) == 3 and original[0].rotation == 0
        client.post(f"/session/{sid}/close").raise_for_status()
    with pytest.raises(ValueError, match="最低"):
        PdfGlobalSettings().adjust_dpi(100000, 100000)


@pytest.mark.parametrize("angle", [None, 12, "0"])
def test_untrusted_angle_does_not_replace_original(angle):
    doc, model = document()
    payload = result("NEW")
    payload["preproc_angle"] = angle
    before = doc[0].get_text()
    assert not PdfService.add_text_layer_batch(
        doc, model, [{"page": 0, "ocr_result": payload}], overwrite=True
    )[0][0]
    assert doc[0].get_text() == before
    doc.close()


def test_summary_retains_source_without_serializing_all_ocr_blocks():
    doc, model = document()
    PdfService.add_text_layer_batch(
        doc, model, [{"page": 0, "ocr_result": result("中文 Search")}], overwrite=True
    )
    summary = backend._doc_to_mirror(model, summary=True)
    assert summary.pages[0].has_ocr_text_layer
    assert not summary.pages[0].ocr_text_blocks
    assert not summary.pages[1].has_ocr_text_layer
    doc.close()


def test_http_partial_commit_reports_success_and_preserves_failed_page(
    tmp_path, monkeypatch
):
    doc, _ = document()
    source = tmp_path / "partial-source.pdf"
    doc.save(source)
    doc.close()
    with TestClient(backend.app) as client:
        sid = client.post("/session/open", json={"path": str(source)}).json()[
            "session_id"
        ]
        session = backend._get_registry().get(sid)
        failed_xref = session.doc.page_xref(1)
        set_key = fitz.Document.xref_set_key
        injected = False

        def fail_once(current, xref, key, value):
            nonlocal injected
            if (
                current is session.doc
                and xref == failed_xref
                and key == "Resources"
                and not injected
            ):
                injected = True
                raise RuntimeError("partial commit failure")
            return set_key(current, xref, key, value)

        monkeypatch.setattr(fitz.Document, "xref_set_key", fail_once)
        response = client.post(
            f"/session/{sid}/add_text_layer_batch",
            json={
                "pages": [{"page": i, "ocr_result": result(f"NEW{i}")} for i in (0, 1)],
                "overwrite": True,
                "save": False,
            },
        )
        response.raise_for_status()
        assert response.json()["extra"]["results"] == {"0": [1, 0]}
        assert "NEW0" in session.doc[0].get_text()
        assert (
            "OLD1" in session.doc[1].get_text()
            and "NEW1" not in session.doc[1].get_text()
        )
        model = client.post(f"/session/{sid}/model").json()
        assert model["is_modified"] and model["pages"][0]["has_ocr_text_layer"]
        with fitz.open(source) as original:
            assert "OLD0" in original[0].get_text()
        client.post(f"/session/{sid}/close").raise_for_status()
