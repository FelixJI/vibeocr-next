"""核验 #328 两遍真实保存 PDF 的文字归属、视觉保全和独立输出。"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

import pymupdf as fitz


def _within(path: Path, root: Path) -> bool:
    return path.resolve().is_relative_to(root.resolve())


def _page_markers(text: str) -> set[int]:
    normalized = "".join(text.upper().split())
    return {int(number) for number in re.findall(r"PAGE(\d{3})(?!\d)", normalized)}


def verify(
    health_path: Path,
    manifest_path: Path,
    fixture: Path,
    fixture_copy: Path,
    export_root: Path,
) -> list[str]:
    errors: list[str] = []
    health = json.loads(health_path.read_text(encoding="utf-8-sig"))
    evidence = health.get("evidence", {})
    if health.get("state") != "passed" or evidence.get("input_kind") != "pdf_pipeline":
        return ["expected passed pdf_pipeline health evidence"]
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    entry = manifest[f"t3-pipeline/{fixture.name}"]
    requested = entry["requested_pages"]
    raster_pages = set(entry["raster_pages"])
    targets = set(requested) & raster_pages
    native_pages = set(entry["native_text_pages"])
    native_skips = set(requested) & native_pages
    expected_pages = entry["pages"]
    if not requested or not targets or len(set(requested)) != len(requested):
        return [
            "fixture request must contain unique pages and at least one scan target"
        ]
    if not set(requested) <= raster_pages | native_pages:
        return ["fixture request contains a non-OCR page"]
    if fixture.read_bytes() != fixture_copy.read_bytes():
        errors.append("source fixture changed during the run")

    passes = evidence.get("passes", [])
    if [run_pass.get("label") for run_pass in passes] != ["cold", "steady"]:
        return [*errors, "expected cold and steady passes in order"]

    outputs: list[Path] = []
    with fitz.open(fixture_copy) as source:
        if len(source) != expected_pages:
            return [*errors, "fixture page count differs from manifest"]
        for run_pass in passes:
            label = run_pass["label"]
            detection = run_pass.get("detection", {})
            if (
                detection.get("selected_pages") != requested
                or detection.get("page_count") != expected_pages
                or detection.get("ocr_pages") != sorted(targets)
                or detection.get("skipped_native_pages") != sorted(native_skips)
            ):
                errors.append(f"{label}: selected pages differ from fixture request")
            saved_path = (
                run_pass.get("document", {})
                .get("saved", {})
                .get("saved", {})
                .get("file")
            )
            if not saved_path:
                errors.append(f"{label}: missing saved path")
                continue
            saved = Path(saved_path)
            if not saved.is_file() or not _within(saved, export_root):
                errors.append(f"{label}: saved file missing or outside export root")
                continue
            if any(saved.samefile(path) for path in [fixture, fixture_copy, *outputs]):
                errors.append(f"{label}: saved output reuses an input or prior pass")
                continue
            outputs.append(saved)
            with fitz.open(saved) as document:
                if len(document) != expected_pages:
                    errors.append(f"{label}: saved page count differs from fixture")
                    continue
                for index in range(expected_pages):
                    original, page = source[index], document[index]
                    text = page.get_text()
                    if index in targets:
                        marker = entry["badge_format"].format(number=index + 1)
                        if _page_markers(text) != _page_markers(marker):
                            errors.append(
                                f"{label}: page {index + 1} has wrong searchable marker"
                            )
                    elif text != original.get_text():
                        errors.append(
                            f"{label}: page {index + 1} changed original text"
                        )
                    if (
                        page.rect != original.rect
                        or len(page.get_images(full=True))
                        != len(original.get_images(full=True))
                        or page.get_pixmap().samples != original.get_pixmap().samples
                    ):
                        errors.append(
                            f"{label}: page {index + 1} changed visual content"
                        )
                resources = {
                    image[0] for page in document for image in page.get_images()
                }
                fonts = {font[0] for page in document for font in page.get_fonts()}
                print(
                    f"{label}: {saved.stat().st_size} bytes, {len(document)} pages, "
                    f"{len(resources)} distinct image resources, {len(fonts)} font resources"
                )
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--health", required=True, type=Path)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--fixture", required=True, type=Path)
    parser.add_argument("--fixture-copy", required=True, type=Path)
    parser.add_argument("--export-root", required=True, type=Path)
    args = parser.parse_args()
    try:
        errors = verify(
            args.health,
            args.manifest,
            args.fixture,
            args.fixture_copy,
            args.export_root,
        )
    except (KeyError, TypeError, ValueError, OSError, RuntimeError) as error:
        errors = [str(error)]
    for error in errors:
        print(f"verify_pdf_pipeline_output: FAIL {error}", file=sys.stderr)
    if errors:
        return 1
    print("verify_pdf_pipeline_output: PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
