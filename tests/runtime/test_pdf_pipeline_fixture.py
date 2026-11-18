"""Goal #325/#328 T3 串行阶段测量合成件的独立契约检查。

只验证生成器输出结构（页数、逐页真实栅格、混合件组成、共享背景与逐页
唯一页码章），不需要 Paddle/模型/真实 PDF。运行使用仓库锁定环境
（uv run --frozen）。
"""

import pymupdf as fitz

from scripts.paddle_smoke_fixtures import draw_pdf_pipeline_fixtures


def _assert_scan_pages(doc: fitz.Document, page_numbers: range) -> None:
    for index in page_numbers:
        page = doc[index]
        images = page.get_images(full=True)
        assert images, f"scan page {index} must carry a real raster"
        assert not page.get_text().strip(), (
            f"scan page {index} must not have native text"
        )


def _assert_unique_page_images(doc: fitz.Document) -> None:
    page_xrefs = [
        frozenset(image[0] for image in page.get_images(full=True)) for page in doc
    ]
    for index in range(len(page_xrefs)):
        for other in range(index + 1, len(page_xrefs)):
            assert page_xrefs[index] != page_xrefs[other], (
                f"pages {index} and {other} share the identical image xref set"
            )


def test_pdf_pipeline_fixtures_match_manifest(tmp_path):
    manifest = draw_pdf_pipeline_fixtures(tmp_path)

    long_entry = manifest["t3-pipeline/scan_long.pdf"]
    assert long_entry["pages"] >= 120
    with fitz.open(tmp_path / "t3-pipeline" / "scan_long.pdf") as doc:
        assert doc.page_count == long_entry["pages"]
        _assert_scan_pages(doc, range(doc.page_count))
        _assert_unique_page_images(doc)
        # 共享背景：页 1 与页 5 使用同一背景 xref，仅页码章不同（跨批共享资源）。
        first_page_images = {
            image[0] for image in doc[0].get_images(full=True)
        }
        fifth_page_images = {
            image[0] for image in doc[4].get_images(full=True)
        }
        assert first_page_images & fifth_page_images, (
            "pages 0 and 4 must share one background image xref"
        )

    short_entry = manifest["t3-pipeline/scan_short.pdf"]
    assert 1 <= short_entry["pages"] <= 10
    with fitz.open(tmp_path / "t3-pipeline" / "scan_short.pdf") as doc:
        assert doc.page_count == short_entry["pages"]
        _assert_scan_pages(doc, range(doc.page_count))
        _assert_unique_page_images(doc)

    mixed_entry = manifest["t3-pipeline/mixed.pdf"]
    with fitz.open(tmp_path / "t3-pipeline" / "mixed.pdf") as doc:
        assert doc.page_count == mixed_entry["pages"] == 30
        _assert_scan_pages(
            doc,
            range(mixed_entry["raster_pages"][0], mixed_entry["raster_pages"][-1] + 1),
        )
        for index in mixed_entry["native_text_pages"]:
            assert "PIPELINE NATIVE PAGE" in doc[index].get_text(), (
                f"native page {index} must keep its text layer"
            )
            assert not doc[index].get_images(full=True), (
                f"native page {index} must stay raster-free"
            )
        for index in mixed_entry["blank_pages"]:
            assert not doc[index].get_text().strip()
            assert not doc[index].get_images(full=True)
        assert (
            set(mixed_entry["raster_pages"])
            | set(mixed_entry["native_text_pages"])
            | set(mixed_entry["blank_pages"])
            == set(range(30))
        )


def test_pdf_pipeline_manifest_declares_synthetic_only(tmp_path):
    manifest = draw_pdf_pipeline_fixtures(tmp_path)
    assert set(manifest) == {
        "t3-pipeline/scan_short.pdf",
        "t3-pipeline/scan_long.pdf",
        "t3-pipeline/mixed.pdf",
    }
    for entry in manifest.values():
        assert entry["synthetic_only"] is True
        assert entry["pages"] > 0
        assert entry["badge_format"] == "PAGE {number:03d}/PIPE"
        assert entry["ocr_text_markers"], "OCR 文本核验标记必须显式声明"
