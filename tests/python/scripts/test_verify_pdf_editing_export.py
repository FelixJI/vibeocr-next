"""verify_pdf_editing_export 最终导出校验回归。

正例：合成扫描源 + 导出（隐形层完整新长句/未改块 + 空白页）必须通过。
负例：真实构造“尾部截断”与“旧文残留”的导出 PDF，helper 必须失败——
防止仓库内校验退化成 UI phase 的 prefix-only 弱断言。
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import TYPE_CHECKING

import fitz
import pytest

from scripts.verify_pdf_editing_export import verify_export

if TYPE_CHECKING:
    from typing import Any

# 与 MainWindow.PaddleSmoke.cs 的 pdf_editing 阶段固定句式一致。
NEW_ZH = (
    "中文校正长句：高清检查中的可搜索文字应完整保存，保留扫描图形与其他"
    "文字块，并在重新打开后找到末尾标记CNTAIL201。"
)
NEW_EN = (
    "English correction keeps the entire searchable sentence after saving "
    "and reopening, including this final marker ENTAIL201."
)
OLD_ZH = "本 P DF 含中英文正文与 1 1 20 等验收标记。"
OLD_EN = "Second paragraph with English 2244 tokens."
KEEP = "GOAL103 untouched paragraph 2244"


def _make_root(tmp_path: Path, name: str, lines: list[tuple[float, str]]) -> Path:
    root = tmp_path / name
    source = root / "fixtures" / "document_scan.pdf"
    source.parent.mkdir(parents=True)
    doc = fitz.open()
    page = doc.new_page(width=612, height=792)
    cs = fitz.Colorspace(fitz.CS_RGB)
    pixmap = fitz.Pixmap(cs, 612, 792, bytes([240]) * (612 * 792 * 3), 0)
    page.insert_image(fitz.Rect(0, 0, 612, 792), pixmap=pixmap)
    doc.save(str(source))
    doc.close()

    out = root / "ui-exports" / "input-pdf_editing" / "saved.pdf"
    out.parent.mkdir(parents=True)
    with fitz.open(source) as src:
        saved = fitz.open()
        saved.insert_pdf(src)
        for y, text in lines:
            # 长英文句用内置 helv 保证整行可提取（china-s 会截断 ASCII 长行）；
            # render_mode=3 隐形，不影响与源扫描页的像素一致性比对。
            saved[0].insert_text(
                fitz.Point(72, y),
                text,
                fontsize=8,
                fontname="helv" if text.isascii() else "china-s",
                render_mode=3,
            )
        saved.new_page(width=612, height=792)
        saved.save(str(out))
        saved.close()

    blocks = [
        {"text": OLD_ZH, "bbox": [50, 50, 700, 120]},
        {"text": OLD_EN, "bbox": [50, 150, 700, 220]},
        {"text": KEEP, "bbox": [50, 260, 700, 320]},
    ]
    health: dict[str, Any] = {
        "state": "passed",
        "evidence": {
            "input_kind": "pdf_editing",
            "fixture_path": str(source),
            "job": {"outcomes": [{"Payload": {"text_blocks": blocks}}]},
            "editing": {
                "edits": [
                    {"block_index": 0, "old_text": OLD_ZH, "new_text": NEW_ZH},
                    {"block_index": 1, "old_text": OLD_EN, "new_text": NEW_EN},
                ],
                "saved": {"file": str(out)},
            },
        },
    }
    (root / "paddle-input-pdf_editing.json").write_text(
        json.dumps(health, ensure_ascii=False), encoding="utf-8"
    )
    return root


GOOD_LINES = [(100, NEW_ZH), (140, NEW_EN), (180, KEEP)]


def test_complete_export_passes(tmp_path: Path) -> None:
    report = verify_export(_make_root(tmp_path, "pm-good", GOOD_LINES))
    assert report["full_new_text_and_tail_tokens_exact"] is True
    assert report["old_layer_duplicates"] is False
    assert report["cancelled_drafts_present"] is False
    assert report["source_still_scan_only"] is True
    assert report["scan_visible_pixels_exact_at_dpi"] == 144
    assert report["blank_page_unchanged"] is True
    assert [b["text"] for b in report["unchanged_blocks"]] == [KEEP]


def test_truncated_tail_marker_fails(tmp_path: Path) -> None:
    truncated = NEW_ZH[: NEW_ZH.index("CNTAIL201")]
    root = _make_root(
        tmp_path, "pm-trunc", [(100, truncated), (140, NEW_EN), (180, KEEP)]
    )
    with pytest.raises(AssertionError, match="新长句|末尾标记"):
        verify_export(root)


def test_old_text_residue_fails(tmp_path: Path) -> None:
    root = _make_root(tmp_path, "pm-residue", [*GOOD_LINES, (220, OLD_ZH)])
    with pytest.raises(AssertionError, match="旧文字层残留或重复"):
        verify_export(root)
