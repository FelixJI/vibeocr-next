"""page_inspect 显示空间投影的几何回归（AC2 实质用例）。

覆盖非零 OCR 预处理角、页面 /Rotate 90/180/270 与非零 CropBox 原点：
- 原生文字层：以真实渲染像素中的墨迹包围盒为独立锚点，inspect 归一化
  bbox 必须框住墨迹且中心对齐（不与 fitz 坐标数学互证）。
- OCR 块投影：以写入后 get_text 实际字形位置（未旋转空间经
  rotation_matrix 转 display）为独立锚点，验证 preproc 逆旋转与归一化。
"""

from __future__ import annotations

import fitz
import numpy as np
import pytest
from vibeocr.runtime.documents.models.pdf_document import PdfDocument, PdfPageInfo
from vibeocr.runtime.documents.pdf_service import PdfService
from vibeocr.runtime.recognition.models.ocr_result import OCRResult, TextBlock


def _doc_with_text(rotation: int = 0, cropbox: bool = False) -> fitz.Document:
    doc = fitz.open()
    page = doc.new_page(width=612, height=792)
    # 未旋转空间内一段可见黑字（框住足够多墨迹）；fontsize 36 的 textbox
    # 在 60pt 高内 rc<0 不写入，必须用 insert_text 单点写入。
    page.insert_text((100, 150), "ANCHOR TEXT", fontsize=36)
    if cropbox:
        # 非零 CropBox 原点：裁掉左上角，文字仍留在裁剪区内
        page.set_cropbox(fitz.Rect(50, 50, 562, 742))
    if rotation:
        page.set_rotation(rotation)
    return doc


def _model(doc: fitz.Document) -> PdfDocument:
    return PdfDocument(
        file_path=None,
        pages=[PdfPageInfo(page_index=i) for i in range(doc.page_count)],
    )


def _ink_bbox_pixels(doc: fitz.Document, dpi: int = 96) -> tuple[float, ...]:
    """渲染并找黑色墨迹的像素包围盒（独立于任何坐标变换实现）。"""
    pix = doc[0].get_pixmap(matrix=fitz.Matrix(dpi / 72, dpi / 72), alpha=False)
    arr = np.frombuffer(pix.samples, dtype=np.uint8).reshape(pix.height, pix.width, 3)
    mask = arr.max(axis=2) < 128
    assert mask.any(), "fixture 未产生可见墨迹"
    rows = np.where(mask.any(axis=1))[0]
    cols = np.where(mask.any(axis=0))[0]
    return (
        float(cols.min()),
        float(rows.min()),
        float(cols.max()),
        float(rows.max()),
    )


def _normalized_to_pixels(
    bbox: tuple[float, float, float, float], pix_w: int, pix_h: int
) -> tuple[float, ...]:
    return (
        bbox[0] / 1000 * pix_w,
        bbox[1] / 1000 * pix_h,
        bbox[2] / 1000 * pix_w,
        bbox[3] / 1000 * pix_h,
    )


class TestNativeLineProjection:
    @pytest.mark.parametrize("rotation", [0, 90, 180, 270])
    def test_rotation_anchors_to_ink(self, rotation: int):
        doc = _doc_with_text(rotation=rotation)
        payload = PdfService.page_inspect(doc, _model(doc), 0)
        assert payload.native_lines, "原生行投影不应为空"
        dpi = 96
        pix = doc[0].get_pixmap(matrix=fitz.Matrix(dpi / 72, dpi / 72), alpha=False)
        projected = _normalized_to_pixels(
            payload.native_lines[0].bbox, pix.width, pix.height
        )
        ink = _ink_bbox_pixels(doc, dpi)
        tol = 0.04 * max(pix.width, pix.height)
        assert projected[0] <= ink[0] + tol and projected[1] <= ink[1] + tol
        assert projected[2] >= ink[2] - tol and projected[3] >= ink[3] - tol
        center_dx = abs((projected[0] + projected[2]) / 2 - (ink[0] + ink[2]) / 2)
        center_dy = abs((projected[1] + projected[3]) / 2 - (ink[1] + ink[3]) / 2)
        assert center_dx <= 0.1 * pix.width and center_dy <= 0.1 * pix.height

    def test_nonzero_cropbox_anchors_to_ink(self):
        doc = _doc_with_text(rotation=90, cropbox=True)
        payload = PdfService.page_inspect(doc, _model(doc), 0)
        assert payload.native_lines
        dpi = 96
        pix = doc[0].get_pixmap(matrix=fitz.Matrix(dpi / 72, dpi / 72), alpha=False)
        projected = _normalized_to_pixels(
            payload.native_lines[0].bbox, pix.width, pix.height
        )
        ink = _ink_bbox_pixels(doc, dpi)
        tol = 0.04 * max(pix.width, pix.height)
        assert projected[0] <= ink[0] + tol and projected[1] <= ink[1] + tol
        assert projected[2] >= ink[2] - tol and projected[3] >= ink[3] - tol
        center_dx = abs((projected[0] + projected[2]) / 2 - (ink[0] + ink[2]) / 2)
        center_dy = abs((projected[1] + projected[3]) / 2 - (ink[1] + ink[3]) / 2)
        assert center_dx <= 0.1 * pix.width and center_dy <= 0.1 * pix.height

    def test_invalid_page_rejected(self):
        doc = _doc_with_text()
        with pytest.raises(ValueError):
            PdfService.page_inspect(doc, _model(doc), -1)
        with pytest.raises(ValueError):
            PdfService.page_inspect(doc, _model(doc), 9)


class TestOcrBlockProjection:
    @pytest.mark.parametrize("preproc_angle", [0, 90, 180, 270])
    @pytest.mark.parametrize("page_rotation", [0, 90])
    def test_preproc_projection_matches_written_text(
        self, preproc_angle: int, page_rotation: int
    ):
        """写入后的实际字形位置（独立锚点）必须落在投影框内。"""
        doc = _doc_with_text(rotation=page_rotation)
        model = _model(doc)
        block = TextBlock(
            text="检测文本LINE07",
            score=0.9,
            bbox=(120.0, 120.0, 620.0, 220.0),
        )
        result = OCRResult(text_blocks=[block], preproc_angle=preproc_angle)
        PdfService.add_text_layer(doc, model, 0, result, overwrite=True)
        payload = PdfService.page_inspect(doc, model, 0)
        assert payload.preproc_angle == preproc_angle
        assert len(payload.ocr_blocks) == 1
        assert payload.native_lines == []
        projected = payload.ocr_blocks[0].bbox

        page = doc[0]
        rect = page.rect
        rm = page.rotation_matrix
        words = [
            word
            for word in page.get_text("words")
            if "LINE07" in word[4] or any("\u4e00" <= ch <= "\u9fff" for ch in word[4])
        ]
        assert words, "写入层后应能提取到目标块词"
        xs, ys = [], []
        for word in words:
            for point in (
                fitz.Point(word[0], word[1]) * rm,
                fitz.Point(word[2], word[1]) * rm,
                fitz.Point(word[2], word[3]) * rm,
                fitz.Point(word[0], word[3]) * rm,
            ):
                xs.append(point.x)
                ys.append(point.y)
        written = (
            min(xs) / rect.width * 1000,
            min(ys) / rect.height * 1000,
            max(xs) / rect.width * 1000,
            max(ys) / rect.height * 1000,
        )
        tol = 60.0  # 归一化单位容差（字形度量与框归一差异）
        assert projected[0] <= written[0] + tol
        assert projected[1] <= written[1] + tol
        assert projected[2] >= written[2] - tol
        assert projected[3] >= written[3] - tol
        center_dx = abs(
            (projected[0] + projected[2]) / 2 - (written[0] + written[2]) / 2
        )
        center_dy = abs(
            (projected[1] + projected[3]) / 2 - (written[1] + written[3]) / 2
        )
        assert center_dx <= 100.0 and center_dy <= 100.0

    def test_polygon_projected_when_present(self):
        doc = _doc_with_text()
        model = _model(doc)
        block = TextBlock(
            text="poly",
            score=None,
            bbox=(100.0, 100.0, 300.0, 180.0),
            polygon=(100.0, 100.0, 300.0, 100.0, 300.0, 180.0, 100.0, 180.0),
        )
        PdfService.add_text_layer(
            doc, model, 0, OCRResult(text_blocks=[block]), overwrite=True
        )
        payload = PdfService.page_inspect(doc, model, 0)
        first = payload.ocr_blocks[0]
        assert first.score_unknown is True
        assert first.polygon is not None and len(first.polygon) == 8
        assert all(0 <= v <= 1000 for v in first.polygon)

    def test_missing_bbox_block_reports_null(self):
        """缺几何块如实投影 bbox=None，不伪造。"""
        doc = _doc_with_text()
        model = _model(doc)
        model.pages[0].ocr_text_blocks = [
            TextBlock(text="no-geom", score=0.5, bbox=None)
        ]
        model.pages[0].has_ocr_text_layer = True
        payload = PdfService.page_inspect(doc, model, 0)
        assert payload.ocr_blocks[0].bbox is None
        # 存在可信 OCR 块（含缺几何块）的页不再投影原生行（来源区分）
        assert payload.native_lines == []


def test_inspection_bounds_boxes_text_and_preserves_ocr_indices():
    with fitz.open() as doc:
        doc.new_page()
        model = _model(doc)
        blocks = [
            TextBlock(text="X" * 2500, score=None, bbox=(10, 10, 900, 50))
            for _ in range(20000)
        ]
        model.pages[0].ocr_text_blocks = blocks
        payload = PdfService.page_inspect(doc, model, 0)
        assert payload.truncated
        assert len(payload.ocr_blocks) == 1000
        assert [block.index for block in payload.ocr_blocks] == list(range(1000))
        assert sum(len(block.text) for block in payload.ocr_blocks) == 128000
        assert all(
            len(block.text) <= 2000 and block.text_truncated
            for block in payload.ocr_blocks
        )
        assert model.pages[0].ocr_text_blocks is blocks
        assert model.pages[0].ocr_text_blocks[-1].text == "X" * 2500


def test_native_inspection_is_bounded_without_replacing_complete_model_cache(
    monkeypatch,
):
    from vibeocr.runtime.documents.models.pdf_document import TextLayerInfo

    with fitz.open() as doc:
        doc.new_page()
        model = _model(doc)
        layers = [
            TextLayerInfo(
                index=i,
                text_preview="native",
                char_count=6,
                bbox=(1, 1, 50, 20),
                color_id=0,
            )
            for i in range(12000)
        ]
        model.pages[0].text_layers = layers

        def detect(_doc, _page, *, limit=None):
            assert limit == 1001
            return layers[:limit]

        monkeypatch.setattr(PdfService, "detect_text_layers", detect)
        payload = PdfService.page_inspect(doc, model, 0)
        assert payload.truncated and len(payload.native_lines) == 1000
        assert model.pages[0].text_layers is layers
