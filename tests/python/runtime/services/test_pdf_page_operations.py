"""真实 PDF 结构映射、旋转和文字层组合契约。"""

import fitz
import pytest
from vibeocr.runtime.documents.models.pdf_document import PdfDocument
from vibeocr.runtime.documents.pdf_service import PdfService
from vibeocr.runtime.recognition.models.ocr_result import OCRResult, TextBlock


def document():
    doc = fitz.open()
    for index in range(3):
        page = doc.new_page(width=300 + index * 10, height=500)
        page.insert_text((30, 30), f"original-{index}")
    model = PdfDocument()
    PdfService.build_page_infos(doc, model)
    return doc, model


def texts(doc):
    return [page.get_text().strip() for page in doc]


@pytest.mark.parametrize(
    "source,target,order", [(0, 2, [1, 2, 0]), (2, 0, [2, 0, 1]), (0, 1, [1, 0, 2])]
)
def test_move_uses_final_index_and_preserves_metadata(source, target, order):
    doc, model = document()
    try:
        old = list(model.pages)
        old[0].ocr_text_blocks = ["synthetic OCR metadata"]
        old[0].ocr_preproc_angle = 90
        PdfService.move_page(doc, model, source, target)
        assert texts(doc) == [f"original-{index}" for index in order]
        assert model.pages == [old[index] for index in order]
        assert [info.page_index for info in model.pages] == [0, 1, 2]
        assert old[0].ocr_text_blocks == ["synthetic OCR metadata"]
        assert old[0].ocr_preproc_angle == 90
    finally:
        doc.close()


@pytest.mark.parametrize("order", [[0, 0, 2], [2, 1], [0, 1, 3], [-1, 0, 1]])
def test_invalid_reorder_is_atomic(order):
    doc, model = document()
    try:
        old = list(model.pages)
        with pytest.raises(ValueError):
            PdfService.reorder_pages(doc, model, order)
        assert texts(doc) == ["original-0", "original-1", "original-2"]
        assert model.pages == old
        assert not model.is_modified
    finally:
        doc.close()


def test_layer_rotate_insert_reorder_continue_ocr_save(tmp_path):
    doc, model = document()
    try:
        result = OCRResult(
            raw_text="SEARCHABLE ZERO",
            text_blocks=[
                TextBlock(text="SEARCHABLE ZERO", bbox=(100, 200, 800, 280), score=0.9)
            ],
        )
        PdfService.add_text_layer(doc, model, 0, result, overwrite=True)
        original = model.pages[0]
        PdfService.rotate_pages(doc, model, [0, 0], 90)
        assert doc[0].rotation == 90
        assert original.ocr_preproc_angle == 90
        PdfService.insert_blank_page(doc, model, 0, 640, 480)
        assert model.pages[0] is original
        assert model.pages[1].rect == (0, 0, 640, 480)
        source = tmp_path / "source.pdf"
        with fitz.open() as external:
            page = external.new_page(width=220, height=330)
            page.insert_text((20, 30), "EXTERNAL TEXT")
            external.save(source)
        PdfService.insert_pages_from(doc, model, str(source), 1)
        assert model.pages[0] is original
        assert model.pages[2].has_text_layer
        PdfService.reorder_pages(doc, model, [2, 3, 0, 1, 4])
        assert model.pages[2] is original
        bbox_before = doc[2].get_text("words")[0][:4]
        PdfService.rewrite_text_layer(
            doc, model, 2, original.ocr_text_blocks, original.ocr_preproc_angle
        )
        bbox_after = doc[2].get_text("words")[0][:4]
        assert bbox_after == pytest.approx(bbox_before, abs=0.2)
        PdfService.add_text_layer(
            doc,
            model,
            3,
            OCRResult(
                raw_text="NEW PAGE",
                text_blocks=[
                    TextBlock(text="NEW PAGE", bbox=(100, 100, 500, 180), score=0.9)
                ],
            ),
        )
        out = tmp_path / "combined.pdf"
        PdfService.save(doc, model, str(out))
        with fitz.open(out) as saved:
            assert saved.page_count == 5
            assert "EXTERNAL TEXT" in saved[0].get_text()
            assert "original-1" in saved[1].get_text()
            assert "SEARCHABLE ZERO" in saved[2].get_text()
            assert saved[2].rotation == 90
            assert "NEW PAGE" in saved[3].get_text()
            assert "original-2" in saved[4].get_text()
    finally:
        doc.close()


def test_partial_external_insert_rolls_back_only_inserted_pages(tmp_path, monkeypatch):
    doc, model = document()
    source = tmp_path / "external.pdf"
    with fitz.open() as external:
        external.new_page()
        external.new_page()
        external.save(source)
    original_insert = fitz.Document.insert_pdf

    def interrupted_insert(target, external, *, start_at):
        original_insert(target, external, from_page=0, to_page=0, start_at=start_at)
        raise RuntimeError("synthetic interrupted insertion")

    monkeypatch.setattr(fitz.Document, "insert_pdf", interrupted_insert)
    try:
        old = list(model.pages)
        with pytest.raises(RuntimeError, match="interrupted"):
            PdfService.insert_pages_from(doc, model, str(source), 0)
        assert texts(doc) == ["original-0", "original-1", "original-2"]
        assert model.pages == old
        assert [page.page_index for page in model.pages] == [0, 1, 2]
        assert not model.is_modified
    finally:
        doc.close()


def test_blank_insert_detection_failure_restores_document(monkeypatch):
    doc, model = document()

    def fail_detection(*_args):
        raise RuntimeError("synthetic page detection failed")

    monkeypatch.setattr(PdfService, "update_page_info", fail_detection)
    try:
        old = list(model.pages)
        with pytest.raises(RuntimeError, match="detection"):
            PdfService.insert_blank_page(doc, model, 0, 640, 480)
        assert texts(doc) == ["original-0", "original-1", "original-2"]
        assert model.pages == old
        assert [page.page_index for page in model.pages] == [0, 1, 2]
        assert not model.is_modified
    finally:
        doc.close()


def test_invalid_insert_rotation_and_last_page_delete_leave_document_unchanged():
    doc, model = document()
    try:
        for operation in [
            lambda: PdfService.insert_blank_page(doc, model, 3),
            lambda: PdfService.insert_blank_page(doc, model, 0, float("nan"), 200),
            lambda: PdfService.rotate_pages(doc, model, [0, 3], 90),
            lambda: PdfService.delete_pages(doc, model, [0, 1, 2]),
        ]:
            with pytest.raises(ValueError):
                operation()
            assert texts(doc) == ["original-0", "original-1", "original-2"]
            assert not model.is_modified
    finally:
        doc.close()
