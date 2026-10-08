"""人工编辑块的严格写入语义（长文本容纳与超限拒绝，AC5）。

以真实 PDF 断言：
- 可容纳长中英文本：收缩字号后完整写入，doc 与保存重开文件中末尾
  token 均可提取，旧文字不重复。
- 超限文本：编辑提交与保存重写均如实失败，原内容/模型/文件保留。
"""

from __future__ import annotations

import fitz
import numpy as np
import pytest
from vibeocr.runtime.documents.models.pdf_document import PdfDocument, PdfPageInfo
from vibeocr.runtime.documents.pdf_service import PdfService
from vibeocr.runtime.recognition.models.ocr_result import OCRResult, TextBlock


def _scanned_doc(path) -> fitz.Document:
    doc = fitz.open()
    page = doc.new_page(width=612, height=792)
    img = np.ones((792, 612, 3), dtype=np.uint8) * 240
    cs = fitz.Colorspace(fitz.CS_RGB)
    pixmap = fitz.Pixmap(cs, 612, 792, img.tobytes(), 0)
    page.insert_image(fitz.Rect(0, 0, 612, 792), pixmap=pixmap)
    doc.save(str(path))
    return doc


def _layered_model(doc: fitz.Document) -> PdfDocument:
    model = PdfDocument(
        file_path=None,
        pages=[PdfPageInfo(page_index=i) for i in range(doc.page_count)],
    )
    PdfService.add_text_layer(
        doc,
        model,
        0,
        OCRResult(
            text_blocks=[
                TextBlock(text="原始块零", score=0.9, bbox=(60.0, 60.0, 520.0, 130.0)),
                TextBlock(text="keep", score=0.8, bbox=(60.0, 200.0, 300.0, 260.0)),
            ]
        ),
        overwrite=True,
    )
    return model


class TestEditedBlockFit:
    def test_long_text_fits_and_is_fully_extractable(self, tmp_path):
        """长中英文本收缩写入：doc 与保存重开都能提取末尾 token。"""
        path = tmp_path / "edit.pdf"
        doc = _scanned_doc(path)
        model = _layered_model(doc)
        end_token = "ENDTOKEN8877"
        long_text = (
            "长文本编辑校验开始段落，中English混排123，"
            + "语义填充句子semantic padding sentence. " * 2
            + end_token
        )
        edited = TextBlock(
            text=long_text,
            score=0.9,
            bbox=(60.0, 60.0, 520.0, 130.0),
            is_manually_edited=True,
        )
        written, skipped = PdfService.rewrite_text_layer(
            doc,
            model,
            0,
            [edited, model.pages[0].ocr_text_blocks[1]],
            0,
            require_all=True,
        )
        assert written == 2 and skipped == 0
        page_text = doc[0].get_text()
        assert end_token in page_text
        assert "".join(long_text.split()) in "".join(page_text.split())
        assert "keep" in page_text

        model.file_path = str(path)
        PdfService.save_with_rewrite(doc, model, path=str(tmp_path / "saved.pdf"))
        with fitz.open(str(tmp_path / "saved.pdf")) as reopened:
            saved_text = reopened[0].get_text()
        assert end_token in saved_text
        assert "".join(long_text.split()) in "".join(saved_text.split())
        assert "原始块零" not in saved_text
        assert "keep" in saved_text

    def test_over_limit_edit_rejected_and_original_preserved(self, tmp_path):
        """超限文本：编辑提交返回 0 写入，doc/模型原样保留。"""
        path = tmp_path / "edit2.pdf"
        doc = _scanned_doc(path)
        model = _layered_model(doc)
        text_before = doc[0].get_text()
        huge = "超" * 1200
        edited = TextBlock(
            text=huge,
            score=0.9,
            bbox=(60.0, 60.0, 160.0, 130.0),
            is_manually_edited=True,
        )
        written, skipped = PdfService.rewrite_text_layer(
            doc,
            model,
            0,
            [edited, model.pages[0].ocr_text_blocks[1]],
            0,
            require_all=True,
        )
        assert written == 0 and skipped > 0
        assert doc[0].get_text() == text_before
        assert model.pages[0].ocr_text_blocks[0].text == "原始块零"

    def test_save_with_unfittable_edit_fails_honestly(self, tmp_path):
        """模型中存在放不下的编辑块时保存如实失败，目标文件不落盘。"""
        path = tmp_path / "edit3.pdf"
        doc = _scanned_doc(path)
        model = _layered_model(doc)
        model.pages[0].ocr_text_blocks[0] = TextBlock(
            text="放不下的超长编辑" * 120,
            score=0.9,
            bbox=(60.0, 60.0, 160.0, 130.0),
            is_manually_edited=True,
        )
        model.is_modified = True
        target = tmp_path / "target.pdf"
        with pytest.raises(RuntimeError):
            PdfService.save_with_rewrite(doc, model, path=str(target))
        assert not target.exists()
        assert model.is_modified is True
        # doc 中仍是编辑提交前的内容（保存重写在 candidate 阶段被拒绝）
        assert "原始块零" in doc[0].get_text()


@pytest.mark.parametrize(
    "bbox",
    [(100, 950, 900, 980), (100, 950, 110, 980)],
    ids=["horizontal-primary", "narrow-textbox-fallback"],
)
def test_multiline_strict_rewrite_rejects_without_changing_page(tmp_path, bbox):
    doc = _scanned_doc(tmp_path / "multiline.pdf")
    try:
        model = _layered_model(doc)
        model.is_modified = False
        blocks_before = model.pages[0].ocr_text_blocks
        text_before = doc[0].get_text()
        edited = TextBlock(
            text="LINE1\nLINE2\nLINE3\nLINE4\nTAIL201",
            score=0.9,
            bbox=bbox,
            is_manually_edited=True,
        )
        written, skipped = PdfService.rewrite_text_layer(
            doc,
            model,
            0,
            [edited, blocks_before[1]],
            0,
            require_all=True,
        )
        assert written == 0 and skipped > 0
        assert model.is_modified is False
        assert model.pages[0].ocr_text_blocks is blocks_before
        assert doc[0].get_text() == text_before
        saved = tmp_path / "unchanged.pdf"
        doc.save(saved, garbage=3)
        with fitz.open(saved) as reopened:
            assert reopened[0].get_text() == text_before
    finally:
        doc.close()
