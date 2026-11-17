"""Goal #325/#327 T2 基线观测合成件的独立契约检查。

只验证生成器输出结构（页数、逐页真实栅格、混合件组成），不需要
Paddle/模型/真实 PDF。运行使用仓库锁定环境（uv run --frozen）。
"""

import pymupdf as fitz

from scripts.paddle_smoke_fixtures import draw_pdf_baseline_fixtures


def _assert_scan_pages(doc: fitz.Document, page_numbers: range) -> None:
    for index in page_numbers:
        page = doc[index]
        images = page.get_images(full=True)
        assert images, f"scan page {index} must carry a real raster"
        assert not page.get_text().strip(), (
            f"scan page {index} must not have native text"
        )


def test_pdf_baseline_fixtures_match_manifest(tmp_path):
    manifest = draw_pdf_baseline_fixtures(tmp_path)

    long_entry = manifest["t2-baseline/scan_long.pdf"]
    assert long_entry["pages"] >= 120
    with fitz.open(tmp_path / "t2-baseline" / "scan_long.pdf") as doc:
        assert doc.page_count == long_entry["pages"]
        _assert_scan_pages(doc, range(doc.page_count))
        # 逐页 xref 集合两两不同：共享背景之外每页有唯一页码章，页可区分。
        page_xrefs = [
            frozenset(image[0] for image in page.get_images(full=True)) for page in doc
        ]
        for index in range(len(page_xrefs)):
            for other in range(index + 1, len(page_xrefs)):
                assert page_xrefs[index] != page_xrefs[other], (
                    f"pages {index} and {other} share the identical image xref set"
                )

    landscape_entry = manifest["t2-baseline/scan_long_landscape.pdf"]
    assert landscape_entry["performance_eligible"] is False
    with fitz.open(tmp_path / "t2-baseline" / "scan_long_landscape.pdf") as doc:
        assert doc.page_count == 120
        _assert_scan_pages(doc, range(doc.page_count))
        assert doc[55].rotation == 90
        assert doc[55].rect.width > doc[55].rect.height
        assert all(
            doc[index].rect.width < doc[index].rect.height
            for index in range(120)
            if index != 55
        )
    with fitz.open(tmp_path / "t2-baseline" / "scan_long.pdf") as doc:
        assert all(
            page.rotation == 0 and page.rect.width < page.rect.height for page in doc
        )

    short_entry = manifest["t2-baseline/scan_short.pdf"]
    assert 1 <= short_entry["pages"] <= 10
    with fitz.open(tmp_path / "t2-baseline" / "scan_short.pdf") as doc:
        assert doc.page_count == short_entry["pages"]
        _assert_scan_pages(doc, range(doc.page_count))

    mixed_entry = manifest["t2-baseline/mixed.pdf"]
    with fitz.open(tmp_path / "t2-baseline" / "mixed.pdf") as doc:
        assert doc.page_count == mixed_entry["pages"] == 30
        _assert_scan_pages(
            doc,
            range(mixed_entry["raster_pages"][0], mixed_entry["raster_pages"][-1] + 1),
        )
        for index in mixed_entry["native_text_pages"]:
            assert doc[index].get_text().strip(), (
                f"native page {index} must have a text layer"
            )
            assert not doc[index].get_images(full=True), (
                f"native page {index} must stay raster-free"
            )
        for index in mixed_entry["blank_pages"]:
            assert not doc[index].get_text().strip()
            assert not doc[index].get_images(full=True)
        assert set(mixed_entry["raster_pages"]) | set(
            mixed_entry["native_text_pages"]
        ) | set(mixed_entry["blank_pages"]) == set(range(30))


def test_pdf_baseline_manifest_declares_synthetic_only(tmp_path):
    manifest = draw_pdf_baseline_fixtures(tmp_path)
    assert set(manifest) == {
        "t2-baseline/scan_short.pdf",
        "t2-baseline/scan_long.pdf",
        "t2-baseline/scan_long_landscape.pdf",
        "t2-baseline/mixed.pdf",
    }
    for entry in manifest.values():
        assert entry["synthetic_only"] is True
        assert entry["pages"] > 0
