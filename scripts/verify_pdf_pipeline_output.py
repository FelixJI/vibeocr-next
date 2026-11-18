"""pdf_pipeline 冒烟阶段的最终导出 PDF 校验（仓库锁定 pymupdf-lite）。

scripts/t3_pdf_pipeline.ps1 的 UI phase（health JSON）只证明应用内通过公开
操作完成 OCR→写层→另存；可搜索文字、页序归属、原生文字层保留、扫描栅格
仍在、源 fixture 未被写入，必须发生在保存的 PDF 文件本身上。本脚本即该
final output verification：任一断言失败以非 0 退出（冒烟脚本据此置败），
缺字段/缺文件 fail-closed，不以 0 或空充当证据。
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import pymupdf as fitz


def _load_health(path: Path) -> dict:
    health = json.loads(path.read_text(encoding="utf-8-sig"))
    if health.get("state") != "passed":
        raise SystemExit(
            f"verify_pdf_pipeline_output: health state={health.get('state')} != passed"
        )
    evidence = health.get("evidence")
    if not isinstance(evidence, dict) or evidence.get("input_kind") != "pdf_pipeline":
        raise SystemExit(
            "verify_pdf_pipeline_output: health evidence is not pdf_pipeline"
        )
    return health


def _fixture_manifest_entry(manifest_path: Path, fixture: Path) -> dict:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    key = f"t3-pipeline/{fixture.name}"
    entry = manifest.get(key)
    if not isinstance(entry, dict):
        raise SystemExit(f"verify_pdf_pipeline_output: manifest entry missing: {key}")
    return entry


def verify(
    health_path: Path, manifest_path: Path, fixture: Path, fixture_copy: Path | None
) -> list[str]:
    errors: list[str] = []
    health = _load_health(health_path)
    evidence = health["evidence"]
    entry = _fixture_manifest_entry(manifest_path, fixture)
    expected_pages = int(entry["pages"])
    raster_pages = set(int(page) for page in entry.get("raster_pages", []))
    native_pages = set(int(page) for page in entry.get("native_text_pages", []))

    passes = evidence.get("passes")
    if not isinstance(passes, list) or len(passes) != 2:
        raise SystemExit(
            "verify_pdf_pipeline_output: evidence.passes must contain cold+steady passes"
        )
    for index, run_pass in enumerate(passes):
        label = run_pass.get("label", f"pass{index}")
        saved_path = (
            run_pass.get("document", {}).get("saved", {}).get("saved", {}).get("file")
        )
        if not saved_path:
            errors.append(
                f"{label}: saved file path missing in evidence (NOT_MEASURED 不允许充当通过)"
            )
            continue
        saved = Path(saved_path)
        if not saved.is_file() or saved.stat().st_size <= 0:
            errors.append(f"{label}: saved file missing or empty: {saved}")
            continue
        selected = [int(page) for page in run_pass["detection"]["selected_pages"]]
        if run_pass["detection"]["page_count"] != expected_pages:
            errors.append(
                f"{label}: page_count {run_pass['detection']['page_count']} != fixture {expected_pages}"
            )
        with fitz.open(saved) as document:
            if len(document) != expected_pages:
                errors.append(
                    f"{label}: saved page count {len(document)} != {expected_pages}"
                )
                continue
            for page_index in selected:
                text = document[page_index].get_text()
                normalized = "".join(text.split()).upper()
                if page_index in raster_pages:
                    marker = f"PAGE{page_index + 1:03d}"
                    if "PIPELINE" not in normalized:
                        errors.append(
                            f"{label}: page {page_index} has no searchable PIPELINE OCR text"
                        )
                    if marker not in normalized:
                        errors.append(
                            f"{label}: page {page_index} text misses page-order marker {marker}; got {normalized[:80]!r}"
                        )
                    if not document[page_index].get_images():
                        errors.append(
                            f"{label}: raster page {page_index} lost its scan image"
                        )
            for page_index in sorted(native_pages):
                text = document[page_index].get_text()
                if "PIPELINE NATIVE PAGE" not in text:
                    errors.append(
                        f"{label}: native page {page_index} lost its original text layer"
                    )

    if fixture_copy is not None:
        if not fixture_copy.is_file():
            errors.append(f"fixture pre-run copy missing: {fixture_copy}")
        elif fixture.read_bytes() != fixture_copy.read_bytes():
            errors.append(
                "source fixture bytes changed during the run (dirty write-back is forbidden)"
            )
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--health", required=True, type=Path, help="pdf_pipeline 冒烟 health JSON"
    )
    parser.add_argument(
        "--manifest", required=True, type=Path, help="fixture manifest.json"
    )
    parser.add_argument(
        "--fixture", required=True, type=Path, help="本次输入 fixture PDF"
    )
    parser.add_argument(
        "--fixture-copy",
        type=Path,
        default=None,
        help="运行前字节级 fixture 副本（证明源未被写入）",
    )
    args = parser.parse_args()
    errors = verify(args.health, args.manifest, args.fixture, args.fixture_copy)
    if errors:
        for error in errors:
            print(f"verify_pdf_pipeline_output: FAIL {error}", file=sys.stderr)
        return 1
    print(
        "verify_pdf_pipeline_output: PASS (searchable text, page order, native layers, raster, source untouched)"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
