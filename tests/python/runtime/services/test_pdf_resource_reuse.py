"""合成 PDF 的跨批资源复用与文件体积合同。"""

from __future__ import annotations

import io
import random
import time
from pathlib import Path

import pymupdf as fitz
import pytest
from PIL import Image
from vibeocr.runtime.documents.models.pdf_document import PdfDocument, PdfPageInfo
from vibeocr.runtime.documents.pdf_service import PdfService

PAGE_W, PAGE_H = 612, 792
JPEG_SIZE = (900, 700)
BATCH_CHARS = ("甲乙丙丁", "子丑寅卯")


def _jpeg_bytes(seed: int, size: tuple[int, int] = JPEG_SIZE) -> bytes:
    """确定性伪随机噪声 JPEG（不同 seed → 不同字节；同 seed → 完全相同）。"""
    rng = random.Random(seed)
    w, h = size
    data = bytes(rng.randrange(256) for _ in range(w * h))
    image = Image.frombytes("L", (w, h), data)
    buffer = io.BytesIO()
    image.save(buffer, format="JPEG", quality=82)
    return buffer.getvalue()


def _ocr_result(text: str) -> dict:
    """单块 OCR 结果（归一化 0-1000 bbox，含 CJK + Latin）。"""
    return {
        "preproc_angle": 0,
        "text_blocks": [
            {
                "text": text,
                "score": 0.9,
                "bbox": [100, 150, 620, 230],
                "polygon": [100, 150, 620, 150, 620, 230, 100, 230],
            }
        ],
    }


def _page_text(page_index: int, round_no: int = 0) -> str:
    chars = BATCH_CHARS[(page_index // 4) % len(BATCH_CHARS)]
    ch = chars[page_index % 4]
    return f"第{page_index}页共享背景{ch}R{round_no}L{page_index:02d}"


def _build_synthetic(path: Path, pages: int = 8, shared: bool = True) -> bytes:
    """构建合成 PDF：shared=True 时全部页引用同一 JPEG xref，否则逐页独立。

    落盘到 path 并返回共享 JPEG 字节（供流精确比较）。
    """
    doc = fitz.open()
    shared_stream = _jpeg_bytes(42)
    first_xref: int | None = None
    for i in range(pages):
        page = doc.new_page(width=PAGE_W, height=PAGE_H)
        if shared:
            if first_xref is None:
                page.insert_image(page.rect, stream=shared_stream)
                first_xref = doc[i].get_images(full=True)[0][0]
            else:
                page.insert_image(page.rect, xref=first_xref)
        else:
            page.insert_image(page.rect, stream=_jpeg_bytes(1000 + i))
    doc.save(str(path))
    doc.close()
    return shared_stream


def _open_with_model(path: Path) -> tuple[fitz.Document, PdfDocument]:
    doc = fitz.open(str(path))
    model = PdfDocument(
        file_path=str(path),
        pages=[PdfPageInfo(page_index=i) for i in range(doc.page_count)],
    )
    return doc, model


def _write_batches(
    doc: fitz.Document,
    model: PdfDocument,
    pages: int,
    round_no: int = 0,
    overwrite: bool = False,
) -> list[float]:
    """按 4 页一批写文字层（跨批共享背景），可选逐批增量落盘（OCR 流程）。

    首轮（页无文字层）overwrite=False 即可；重复编辑轮必须显式 overwrite=True，
    否则整页被"已有文字层"守卫跳过。
    """
    elapsed: list[float] = []
    for start in range(0, pages, 4):
        data = [
            {"page": i, "ocr_result": _ocr_result(_page_text(i, round_no))}
            for i in range(start, min(start + 4, pages))
        ]
        began = time.perf_counter()
        results = PdfService.add_text_layer_batch(doc, model, data, overwrite=overwrite)
        elapsed.append(time.perf_counter() - began)
        assert data or results
        for item in data:
            count = results.get(item["page"], (0, 0))
            # (0,1) = 页已有文字层且未开 overwrite 的合法跳过；其余必须写入
            assert count[0] >= 1 or count == (0, 1), (
                f"page {item['page']} 未写入: {results}"
            )
    return elapsed


# ---- 资源取证工具（xref/引用 + raw stream 精确比较） -------------------------


def _all_image_xrefs(doc: fitz.Document) -> list[int]:
    out = []
    for xref in range(1, doc.xref_length()):
        try:
            if doc.xref_get_key(xref, "Subtype")[1] == "/Image":
                out.append(xref)
        except Exception:
            continue
    return out


def _page_image_xrefs(doc: fitz.Document, page_index: int) -> set[int]:
    return {entry[0] for entry in doc[page_index].get_images(full=True)}


def _in_use_image_xrefs(doc: fitz.Document) -> set[int]:
    return set().union(*(_page_image_xrefs(doc, i) for i in range(doc.page_count)))


def _in_use_copies(doc: fitz.Document, stream: bytes) -> list[int]:
    """在用（被页引用）且 raw stream 与给定字节完全相同的图像 xref。"""
    copies = []
    for xref in sorted(_in_use_image_xrefs(doc)):
        if doc.xref_stream_raw(xref) == stream:
            copies.append(xref)
    return copies


def _unreferenced_image_xrefs(doc: fitz.Document) -> set[int]:
    return set(_all_image_xrefs(doc)) - _in_use_image_xrefs(doc)


def _in_use_duplicate_stream_groups(doc: fitz.Document) -> int:
    """在用图像流按字节分组后，出现多于一份的分组数（通用重复度量）。"""
    groups: dict[bytes, int] = {}
    for xref in _in_use_image_xrefs(doc):
        raw = bytes(doc.xref_stream_raw(xref))
        groups[raw] = groups.get(raw, 0) + 1
    return sum(1 for count in groups.values() if count > 1)


def _font_xrefs(doc: fitz.Document) -> set[int]:
    return {f[0] for i in range(doc.page_count) for f in doc[i].get_fonts(full=True)}


def _font_payload_bytes(doc: fitz.Document) -> int:
    """FontDescriptor 引用的 FontFile/2/3 流总字节（子集字体净开销）。"""
    total = 0
    for xref in range(1, doc.xref_length()):
        try:
            if doc.xref_get_key(xref, "Type")[1] == "/FontDescriptor":
                for key in ("FontFile", "FontFile2", "FontFile3"):
                    kind, value = doc.xref_get_key(xref, key)
                    if kind == "xref":
                        total += len(doc.xref_stream(int(value.split()[0])))
        except Exception:
            continue
    return total


def _render_samples(doc: fitz.Document, page_index: int, dpi: int = 60) -> bytes:
    return doc[page_index].get_pixmap(dpi=dpi).samples


def _assert_text_and_visual(
    doc: fitz.Document, pages: int, baseline: dict[int, bytes], round_no: int = 0
):
    for i in range(pages):
        assert _page_text(i, round_no) in doc[i].get_text()
        assert _render_samples(doc, i) == baseline[i]


def test_fonts_share_xref_within_batch_and_extract_across_batches(tmp_path):
    """字体量化：同批各页共享同一字体 xref；跨批不同字符为合法新增子集。

    8 页两批（甲乙丙丁 / 子丑寅卯）后：在用字体 xref = 2，全部文本可提取。
    """
    pdf = tmp_path / "shared.pdf"
    _build_synthetic(pdf, pages=8)
    doc, model = _open_with_model(pdf)
    _write_batches(doc, model, pages=8)

    batch1 = {f[0] for f in doc[0].get_fonts(full=True)}
    for i in range(4):
        assert {f[0] for f in doc[i].get_fonts(full=True)} == batch1
    batch2 = {f[0] for f in doc[4].get_fonts(full=True)}
    assert len(batch1) == 1 and len(batch2) == 1
    assert batch1 != batch2  # 跨批字符不同 → 合法独立子集
    assert len(_font_xrefs(doc)) == 2
    assert _font_payload_bytes(doc) > 0
    for i in range(8):
        assert _page_text(i) in doc[i].get_text()
    doc.close()


def test_independent_backgrounds_stay_unique_after_save(tmp_path):
    """对照组：各页独立 JPEG 时，保存后每个唯一流仍恰有 1 份在用副本。"""
    pdf = tmp_path / "independent.pdf"
    _build_synthetic(pdf, pages=4, shared=False)
    doc, model = _open_with_model(pdf)
    baseline = {i: _render_samples(doc, i) for i in range(4)}
    originals = {
        bytes(doc.xref_stream_raw(x)) for x in sorted(_in_use_image_xrefs(doc))
    }
    assert len(originals) == 4  # 每页一份唯一图像
    _write_batches(doc, model, pages=4)

    result = PdfService.save_with_rewrite(doc, model, rewrite_text_layers=False)
    doc = result.new_doc
    assert doc is not None
    streams = [bytes(doc.xref_stream_raw(x)) for x in sorted(_in_use_image_xrefs(doc))]
    assert len(streams) == 4
    assert len(set(streams)) == 4  # 无跨页重复
    assert set(streams) == originals  # 每个原图恰有一份在用副本
    _assert_text_and_visual(doc, 4, baseline)
    doc.close()


@pytest.mark.parametrize(
    "pages,mode", [(8, "overwrite"), (8, "rebind"), (8, "copy"), (100, "overwrite")]
)
def test_full_save_reuses_shared_background_and_meets_byte_budget(
    tmp_path, pages, mode
):
    pdf = tmp_path / "shared.pdf"
    shared_stream = _build_synthetic(pdf, pages=pages)
    original = pdf.read_bytes()
    doc, model = _open_with_model(pdf)
    baseline = {i: _render_samples(doc, i) for i in range(pages)}
    _write_batches(doc, model, pages=pages)
    assert pdf.read_bytes() == original
    target = pdf if mode == "overwrite" else tmp_path / "output.pdf"
    result = PdfService.save_with_rewrite(
        doc,
        model,
        None if mode == "overwrite" else str(target),
        rewrite_text_layers=False,
        copy_export=mode == "copy",
        rebind_target=mode == "rebind",
    )
    if result.new_doc is not None:
        doc = result.new_doc
    with fitz.open(target) as disk:
        assert len(_in_use_copies(disk, shared_stream)) == 1
        assert not _unreferenced_image_xrefs(disk)
        assert (
            target.stat().st_size
            <= len(original) + _font_payload_bytes(disk) + pages * 2048 + 65536
        )
        _assert_text_and_visual(disk, pages, baseline)
    if mode == "copy":
        assert model.is_modified and model.file_path == str(pdf)
        assert pdf.read_bytes() == original
    elif mode == "rebind":
        assert not model.is_modified and model.file_path == str(target)
        assert pdf.read_bytes() == original
    doc.close()


def test_repeated_edit_save_does_not_accumulate_background_copies(tmp_path):
    pdf = tmp_path / "shared.pdf"
    shared_stream = _build_synthetic(pdf, pages=4)
    doc, model = _open_with_model(pdf)
    _write_batches(doc, model, pages=4)
    result = PdfService.save_with_rewrite(doc, model, rewrite_text_layers=False)
    doc = result.new_doc

    for round_no in range(1, 4):  # 重复编辑保存 ×3
        _write_batches(doc, model, pages=4, round_no=round_no, overwrite=True)
        result = PdfService.save_with_rewrite(doc, model, rewrite_text_layers=False)
        doc = result.new_doc
        copies = _in_use_copies(doc, shared_stream)
        assert len(copies) == 1, f"第 {round_no} 轮编辑保存在用副本 {copies}"
        assert _page_text(0, round_no) in doc[0].get_text()
    doc.close()


def test_nochange_repeat_save_keeps_single_shared_background(tmp_path):
    pdf = tmp_path / "shared.pdf"
    shared_stream = _build_synthetic(pdf, pages=4)
    doc, model = _open_with_model(pdf)
    _write_batches(doc, model, pages=4)
    result = PdfService.save_with_rewrite(doc, model, rewrite_text_layers=False)
    doc = result.new_doc
    first_size = pdf.stat().st_size

    for _ in range(3):  # 无变更重复保存 ×3
        result = PdfService.save_with_rewrite(doc, model, rewrite_text_layers=False)
        doc = result.new_doc
        assert len(_in_use_copies(doc, shared_stream)) == 1
    assert abs(pdf.stat().st_size - first_size) <= 4096  # 无逐轮膨胀
    doc.close()


def test_shared_form_and_soft_mask_keep_pixels_and_replace_old_text(tmp_path):
    image = Image.new("RGBA", (32, 32), (50, 100, 200, 127))
    png = io.BytesIO()
    image.save(png, format="PNG")
    with fitz.open() as source:
        page = source.new_page()
        page.insert_image(page.rect, stream=png.getvalue())
        page.insert_text((72, 100), "OLD", render_mode=3)
        doc = fitz.open()
        for _ in range(2):
            page = doc.new_page()
            page.show_pdf_page(page.rect, source, 0)
    # Existing MuPDF redact can quantize translucent Form pixels by one level.
    # Compare the transfer against that established replacement operation exactly.
    with fitz.open() as redacted:
        redacted.insert_pdf(doc)
        redacted_model = PdfDocument(
            pages=[PdfPageInfo(page_index=i) for i in range(2)]
        )
        for index in range(2):
            PdfService.delete_text_layers(redacted, redacted_model, index)
        pixels = [page.get_pixmap().samples for page in redacted]
    images = _all_image_xrefs(doc)
    assert len(images) == 2
    original_streams = [doc.xref_stream(xref) for xref in images]
    doc.set_toc([[1, "first", 1], [1, "second", 2]])
    page_refs = [page.xref for page in doc]
    model = PdfDocument(
        pages=[PdfPageInfo(page_index=i, has_text_layer=True) for i in range(2)]
    )
    for index in range(2):
        results = PdfService.add_text_layer_batch(
            doc,
            model,
            [{"page": index, "ocr_result": _ocr_result(f"NEW{index}")}],
            overwrite=True,
        )
        assert results[index] == (1, 0)
        assert doc[index].xref == page_refs[index]
        if index == 0:
            assert "OLD" in doc[1].get_text()
    target = tmp_path / "forms.pdf"
    PdfService.save_with_rewrite(doc, model, str(target), rewrite_text_layers=False)
    with fitz.open(target) as disk:
        assert len(_all_image_xrefs(disk)) == 2
        assert [
            disk.xref_stream(xref) for xref in _all_image_xrefs(disk)
        ] == original_streams
        assert disk.get_toc() == [[1, "first", 1], [1, "second", 2]]
        for index, page in enumerate(disk):
            assert page.get_pixmap().samples == pixels[index]
            assert page.get_text().strip() == f"NEW{index}"
    doc.close()
