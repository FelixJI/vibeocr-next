"""Goal #325/#328 合成件和保存产物校验契约。"""

import json
import shutil

import pymupdf as fitz
import pytest

from scripts.paddle_smoke_fixtures import draw_pdf_pipeline_fixtures
from scripts.verify_pdf_pipeline_output import verify


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
        first_page_images = {image[0] for image in doc[0].get_images(full=True)}
        fifth_page_images = {image[0] for image in doc[4].get_images(full=True)}
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
        assert set(mixed_entry["raster_pages"]) | set(
            mixed_entry["native_text_pages"]
        ) | set(mixed_entry["blank_pages"]) == set(range(30))


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
        assert entry["badge_format"] == "PAGE {number:03d}"
        assert set(entry["raster_pages"]) & set(entry["requested_pages"])
    mixed = manifest["t3-pipeline/mixed.pdf"]
    assert set(mixed["native_text_pages"]) <= set(mixed["requested_pages"])
    assert len(set(mixed["raster_pages"]) - set(mixed["requested_pages"])) == 1
    long = manifest["t3-pipeline/scan_long.pdf"]
    assert long["requested_pages"] == list(range(long["pages"]))


@pytest.mark.parametrize(
    ("fault", "expected_error"),
    [
        (None, None),
        ("empty_targets", "selected pages"),
        ("samefile", "reuses an input or prior pass"),
        ("wrong_marker", "wrong searchable marker"),
        ("image_changed", "changed visual content"),
        ("outside_export", "outside export root"),
    ],
)
def test_verify_saved_pdfs(tmp_path, fault, expected_error):
    source = tmp_path / "mixed.pdf"
    fixture_copy = tmp_path / "before.pdf"
    export = tmp_path / "export"
    export.mkdir()
    with fitz.open() as document:
        with fitz.open() as raster:
            page = raster.new_page(width=90, height=110)
            page.draw_rect(fitz.Rect(10, 10, 60, 70), fill=(1, 0, 0))
            png = page.get_pixmap().tobytes("png")
        for _ in range(2):
            page = document.new_page(width=90, height=110)
            page.insert_image(page.rect, stream=png)
        document.new_page().insert_text((30, 60), "Original native text")
        document.new_page()
        document.save(source)
    shutil.copyfile(source, fixture_copy)

    health = tmp_path / "health.json"
    manifest = tmp_path / "manifest.json"
    requested = [0, 2]
    manifest.write_text(
        json.dumps(
            {
                "t3-pipeline/mixed.pdf": {
                    "pages": 4,
                    "requested_pages": requested,
                    "raster_pages": [0, 1],
                    "native_text_pages": [2],
                    "blank_pages": [3],
                    "badge_format": "PAGE {number:03d}",
                }
            }
        ),
        encoding="utf-8",
    )
    passes = []
    for label in ("cold", "steady"):
        path = export / f"{label}.pdf"
        with fitz.open(source) as document:
            document[0].insert_text(
                (10, 30),
                "PAGE 002" if fault == "wrong_marker" else "PAGE 001",
                render_mode=3,
            )
            if fault == "image_changed":
                document[1].draw_rect(fitz.Rect(1, 1, 89, 109), fill=(0, 1, 0))
            document.save(path)
        if fault == "samefile" and label == "steady":
            path = export / "cold.pdf"
        elif fault == "outside_export" and label == "steady":
            path = path.rename(tmp_path / "outside.pdf")
        passes.append(
            {
                "label": label,
                "detection": {
                    "selected_pages": [] if fault == "empty_targets" else requested,
                    "page_count": 4,
                    "ocr_pages": [0],
                    "skipped_native_pages": [2],
                },
                "document": {"saved": {"saved": {"file": str(path)}}},
            }
        )
    health.write_text(
        json.dumps(
            {
                "state": "passed",
                "evidence": {"input_kind": "pdf_pipeline", "passes": passes},
            }
        ),
        encoding="utf-8",
    )
    errors = verify(health, manifest, source, fixture_copy, export)
    if expected_error is None:
        assert errors == []
    else:
        assert any(expected_error in error for error in errors)
