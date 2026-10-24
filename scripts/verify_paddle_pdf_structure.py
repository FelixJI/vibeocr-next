"""Verify public two-page Paddle PDF evidence against the frozen native quality contract."""

from __future__ import annotations

import argparse
import copy
import json
from pathlib import Path
from types import SimpleNamespace

import pymupdf
from PIL import Image

try:
    from scripts.verify_paddle_structure_native import _check_result
except ModuleNotFoundError:
    from verify_paddle_structure_native import _check_result


SOURCES = {
    "paddle_table": ["table_merged_zh_en.png", "wireless_dispatch.png"],
    "paddle_structure": ["document_mixed.png", "document_columns_90.png"],
}


def verify(root: Path, health_path: Path) -> dict:
    root = root.resolve()
    assert health_path.resolve().parent == root, (
        "health must belong to the synthetic root"
    )
    health = json.loads(health_path.read_text(encoding="utf-8-sig"))
    assert health["state"] == "passed"
    proof = health["evidence"]
    mode = proof["mode"]
    assert proof["input_kind"] == "pdf" and mode in SOURCES
    source = Path(proof["fixture_path"]).resolve()
    assert source.is_relative_to(root) and source.is_file()
    pages = proof["pages"]
    assert [page["page_index"] for page in pages] == [0, 1]
    job = proof["job"]
    items = {item["ClientItemKey"]: item for item in job["Items"]}
    outcomes = {item["ItemId"]: item for item in job["outcomes"]}
    assert len(items) == len(outcomes) == 2
    payloads = []
    with pymupdf.open(source) as pdf:
        assert pdf.page_count == 2
        for index, (page, name) in enumerate(zip(pages, SOURCES[mode], strict=True)):
            # The submitted scan is the frozen native sample, not a replacement PDF text layer.
            rendered = pdf[index].get_pixmap(matrix=pymupdf.Matrix(3, 3), alpha=False)
            with Image.open(source.parent / name) as image:
                assert (rendered.width, rendered.height) == image.size
                assert rendered.samples == image.convert("RGB").tobytes()
            item = items[f"page-{index}"]
            assert (
                page["ClientItemKey"] == f"page-{index}"
                and page["ItemId"] == item["ItemId"]
            )
            payload = outcomes[item["ItemId"]]["Payload"]
            payloads.append(payload)
            blocks = payload["content_list"]
            published = copy.deepcopy(page["content_blocks"])
            assert (
                len(blocks) == len(published) and page["structured_inspected"] is True
            )
            for block in published:
                if "image" in block:
                    asset = block["image"]
                    assert (
                        asset["job_id"] == job["task_id"]
                        and asset["item_id"] == item["ItemId"]
                    )
                    assert asset["asset_id"]
                    asset.pop("resource", None)
            assert published == blocks, "public resource differs from its page outcome"
            result = SimpleNamespace(
                content_list=blocks, raw_text=payload.get("raw_text", "")
            )
            _check_result(name, result, 1000, 1000)
    assert payloads[0]["content_list"] != payloads[1]["content_list"], (
        "pages share the same result"
    )
    return {
        "passed": True,
        "mode": mode,
        "pages": 2,
        "job_id": job["task_id"],
        "quality_contract": "verify_paddle_structure_native._check_result",
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--health", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(verify(args.root, args.health), ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
