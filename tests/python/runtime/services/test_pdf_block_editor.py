"""Real PDF correction resource, geometry and rollback contracts."""

from dataclasses import replace

import numpy as np
import pymupdf as fitz
import pytest
from vibeocr.runtime.documents.models.pdf_document import PdfDocument, PdfPageInfo
from vibeocr.runtime.documents.models.pdf_ocr_options import PdfGlobalSettings
from vibeocr.runtime.documents.pdf_block_editor import PdfBlockEditor
from vibeocr.runtime.documents.pdf_service import PdfService
from vibeocr.runtime.recognition.models.ocr_result import OCRResult, TextBlock


def fixture(rotation=0, angle=0):
    source = fitz.open()
    page = source.new_page(width=612, height=792)
    pixels = np.random.default_rng(201).integers(0, 256, (300, 200, 3), dtype=np.uint8)
    page.insert_image(
        page.rect,
        pixmap=fitz.Pixmap(fitz.Colorspace(fitz.CS_RGB), 200, 300, pixels.tobytes(), 0),
    )
    page.insert_text((80, 150), "共享Form中文原文", fontname="china-s", fontsize=18)
    doc = fitz.open()
    for _ in range(2):
        page = doc.new_page(width=612, height=792)
        page.show_pdf_page(page.rect, source, 0)
    source.close()
    doc[0].set_cropbox(fitz.Rect(20, 30, 590, 760))
    doc[0].set_rotation(rotation)
    doc[0].add_text_annot((40, 50), "annotation survives")
    doc[0].insert_link(
        {
            "kind": fitz.LINK_GOTO,
            "from": fitz.Rect(5, 5, 30, 30),
            "page": 1,
            "to": fitz.Point(0, 0),
        }
    )
    model = PdfDocument(pages=[PdfPageInfo(page_index=i) for i in range(2)])
    blocks = [
        TextBlock(text="original 201", score=0.9, bbox=(100, 100, 800, 200)),
        TextBlock(text="保留KEEP", score=None, bbox=(100, 350, 600, 450)),
    ]
    assert PdfService.add_text_layer(
        doc,
        model,
        0,
        OCRResult(text_blocks=blocks, preproc_angle=angle),
        overwrite=True,
    ) == (2, 0)
    return doc, model


def stats(doc):
    active = []
    for xref in range(1, doc.xref_length()):
        try:
            if doc.xref_object(xref).strip() != "null":
                active.append(xref)
        except RuntimeError:
            pass  # retired empty xref slots
    images = [
        xref for xref in active if doc.xref_get_key(xref, "Subtype")[1] == "/Image"
    ]
    fonts = [xref for xref in active if doc.xref_get_key(xref, "Type")[1] == "/Font"]
    return (
        len(images),
        len(fonts),
        sum(
            len(doc.xref_stream_raw(xref))
            for xref in active
            if doc.xref_is_stream(xref)
        ),
    )


@pytest.mark.parametrize("rotation", [0, 90, 180, 270])
@pytest.mark.parametrize("angle", [0, 90, 180, 270])
def test_geometry_and_shared_form_other_page_survive(rotation, angle, tmp_path):
    doc, model = fixture(rotation, angle)
    try:
        editor = PdfBlockEditor(doc)
        target = doc.page_xref(0)
        geometry = {
            key: doc.xref_get_key(target, key)[1]
            for key in ("CropBox", "Rotate", "Annots")
        }
        pixels, text = doc[1].get_pixmap().samples, doc[1].get_text()
        expected = "中文校正 English long sentence 完整末尾TAIL201"
        blocks = [
            replace(
                model.pages[0].ocr_text_blocks[0],
                text=expected,
                is_manually_edited=True,
            ),
            model.pages[0].ocr_text_blocks[1],
        ]
        assert editor.rewrite(model, 0, blocks, angle, PdfGlobalSettings()) == (2, 0)
        assert doc.page_xref(0) == target
        assert geometry == {key: doc.xref_get_key(target, key)[1] for key in geometry}
        assert doc[1].get_text() == text and doc[1].get_pixmap().samples == pixels
        extracted = doc[0].get_text()
        assert (
            "TAIL201" in extracted
            and "KEEP" in extracted
            and "original" not in extracted
        )
        projected = PdfService.page_inspect(doc, model, 0).ocr_blocks[0].bbox
        assert projected is not None
        marker = next(word for word in doc[0].get_text("words") if "TAIL201" in word[4])
        center = (
            fitz.Point((marker[0] + marker[2]) / 2, (marker[1] + marker[3]) / 2)
            * doc[0].rotation_matrix
        )
        x, y = center.x / doc[0].rect.width * 1000, center.y / doc[0].rect.height * 1000
        assert projected[0] - 30 <= x <= projected[2] + 30
        assert projected[1] - 30 <= y <= projected[3] + 30
        assert len(list(doc[0].annots())) == 1 and len(doc[0].get_links()) == 1
        path = tmp_path / "edited.pdf"
        doc.save(path, garbage=3)
        with fitz.open(path) as reopened:
            assert (
                "TAIL201" in reopened[0].get_text() and "KEEP" in reopened[0].get_text()
            )
    finally:
        doc.close()


def test_twenty_four_edits_have_stable_live_streams_and_xref_increment():
    doc, model = fixture()
    try:
        editor = PdfBlockEditor(doc)
        pixels, text = doc[1].get_pixmap().samples, doc[1].get_text()
        counts, sizes, increments = [], [], []
        for step in range(25):
            before = doc.xref_length()
            blocks = [
                replace(
                    model.pages[0].ocr_text_blocks[0],
                    text=f"中文校正 {step:02d} END201",
                    is_manually_edited=True,
                ),
                model.pages[0].ocr_text_blocks[1],
            ]
            assert editor.rewrite(model, 0, blocks, 0, PdfGlobalSettings()) == (2, 0)
            images, fonts, size = stats(doc)
            counts.append((images, fonts))
            sizes.append(size)
            if step:
                increments.append(doc.xref_length() - before)
            assert doc[1].get_text() == text and doc[1].get_pixmap().samples == pixels
            assert "END201" in doc[0].get_text()
        assert len(set(counts)) == 1
        assert max(sizes) - min(sizes) < 4096
        assert len(set(increments)) == 1
    finally:
        doc.close()


def test_second_commit_key_failure_rolls_back_content_metadata_and_candidate(
    monkeypatch,
):
    doc, model = fixture()
    try:
        editor = PdfBlockEditor(doc)
        target = doc.page_xref(0)
        keys = {
            key: doc.xref_get_key(target, key)[1] for key in ("Contents", "Resources")
        }
        pixels, text, original = (
            doc[0].get_pixmap().samples,
            doc[0].get_text(),
            model.pages[0].ocr_text_blocks,
        )
        before = stats(doc)
        original_set = doc.xref_set_key
        failed = False

        def set_key(xref, key, value):
            nonlocal failed
            if xref == target and key == "Resources" and not failed:
                failed = True
                raise RuntimeError("second commit key failed")
            return original_set(xref, key, value)

        monkeypatch.setattr(doc, "xref_set_key", set_key)
        blocks = [
            replace(original[0], text="new TAIL201", is_manually_edited=True),
            original[1],
        ]
        with pytest.raises(RuntimeError, match="second commit"):
            editor.rewrite(model, 0, blocks, 0, PdfGlobalSettings())
        assert {key: doc.xref_get_key(target, key)[1] for key in keys} == keys
        assert model.pages[0].ocr_text_blocks is original
        assert doc[0].get_text() == text and doc[0].get_pixmap().samples == pixels
        assert stats(doc) == before and doc.page_count == 2
    finally:
        doc.close()


def test_page_xref_cache_preserves_aliases_remaps_and_releases_deleted_page():
    doc, model = fixture()
    try:
        editor = PdfBlockEditor(doc)

        def edit(index, text):
            blocks = [
                replace(
                    model.pages[index].ocr_text_blocks[0],
                    text=text,
                    is_manually_edited=True,
                ),
                model.pages[index].ocr_text_blocks[1],
            ]
            assert editor.rewrite(model, index, blocks, 0, PdfGlobalSettings()) == (
                2,
                0,
            )

        edit(0, "FIRST201")
        doc.fullcopy_page(0)
        alias_text = doc[2].get_text()
        edit(0, "SECOND201")
        assert doc[2].get_text() == alias_text and "FIRST201" in alias_text
        doc.delete_page(2)
        editor.prune()
        target = doc.page_xref(0)
        PdfService.insert_blank_page(doc, model, -1)
        PdfService.reorder_pages(doc, model, [2, 1, 0])
        index = next(i for i in range(doc.page_count) if doc.page_xref(i) == target)
        edit(index, "REMAPPED201")
        assert "REMAPPED201" in doc[index].get_text()
        PdfService.delete_pages(doc, model, [index])
        editor.prune()
        assert target not in editor._pages
    finally:
        doc.close()


def test_collection_failure_keeps_successful_commit_and_retries(monkeypatch):
    doc, model = fixture()
    try:
        editor = PdfBlockEditor(doc)

        def edit(text):
            blocks = [
                replace(
                    model.pages[0].ocr_text_blocks[0],
                    text=text,
                    is_manually_edited=True,
                ),
                model.pages[0].ocr_text_blocks[1],
            ]
            assert editor.rewrite(model, 0, blocks, 0, PdfGlobalSettings()) == (2, 0)

        edit("FIRST201")
        original_update = doc.update_object

        def fail_retire(xref, value):
            if value == "null":
                raise RuntimeError("collection unavailable")
            return original_update(xref, value)

        monkeypatch.setattr(doc, "update_object", fail_retire)
        edit("SECOND201")
        assert "SECOND201" in doc[0].get_text()
        assert model.pages[0].ocr_text_blocks[0].text == "SECOND201"
        assert editor._retained
        monkeypatch.setattr(doc, "update_object", original_update)
        edit("THIRD201")
        assert "THIRD201" in doc[0].get_text() and not editor._retained
    finally:
        doc.close()


def test_explicit_invalidation_keeps_an_in_place_content_update():
    doc, model = fixture()
    try:
        editor = PdfBlockEditor(doc)
        blocks = model.pages[0].ocr_text_blocks
        assert editor.rewrite(model, 0, blocks, 0, PdfGlobalSettings()) == (2, 0)
        target = doc.page_xref(0)
        keys = {
            key: doc.xref_get_key(target, key)[1] for key in ("Contents", "Resources")
        }
        content = doc[0].get_contents()[0]
        doc.update_stream(
            content, doc.xref_stream(content) + b"\nq 1 0 0 rg 20 20 80 80 re f Q\n"
        )
        assert keys == {key: doc.xref_get_key(target, key)[1] for key in keys}
        editor.invalidate([0])
        assert target not in editor._pages
        pixels = doc[0].get_pixmap().samples
        assert editor.rewrite(
            model,
            0,
            [replace(blocks[0], text="CHANGE201", is_manually_edited=True), blocks[1]],
            0,
            PdfGlobalSettings(),
        ) == (2, 0)
        assert doc[0].get_pixmap().samples == pixels
    finally:
        doc.close()
