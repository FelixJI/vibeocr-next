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


def _workspace_root(tmp_path: Path) -> Path:
    from scripts.verify_pdf_editing_export import verify_workspace_export

    root = _make_root(tmp_path, "workspace", GOOD_LINES)
    original_health = json.loads(
        (root / "paddle-input-pdf_editing.json").read_text(encoding="utf-8")
    )
    fixtures = root / "fixtures"
    sources = []
    for folder, count in (("workspace-a", 75), ("workspace-b", 3)):
        path = fixtures / folder / "same.pdf"
        path.parent.mkdir()
        with fitz.open(fixtures / "document_scan.pdf") as scan, fitz.open() as source:
            source.insert_pdf(scan)
            page = source.new_page(width=595, height=842)
            page.insert_text((60, 80), "WORKSPACE NATIVE TAIL202", fontsize=18)
            page.draw_rect(fitz.Rect(40, 120, 220, 200), fill=(0.2, 0.7, 0.3))
            for _ in range(count - 2):
                source.new_page(width=595, height=842)
            source.save(path)
        sources.append(path)
    with fitz.open() as inserted:
        inserted.new_page().insert_text((60, 80), "INSERTED FULL TAIL202")
        inserted.save(fixtures / "document_mixed.pdf")
    a_target, b_target = root / "a-target.pdf", root / "b-target.pdf"
    with (
        fitz.open(sources[0]) as a,
        fitz.open(fixtures / "document_mixed.pdf") as inserted,
    ):
        for y, text in [
            (100, NEW_ZH + " AFTER_SAVE_AS202"),
            (140, NEW_EN),
            (180, KEEP),
        ]:
            a[0].insert_text(
                (48, y),
                text,
                fontname="helv" if text.isascii() else "china-s",
                fontsize=6,
                render_mode=3,
            )
        a[1].add_redact_annot(fitz.Rect(50, 50, 550, 100), fill=False)
        a[1].apply_redactions(images=0, graphics=0)
        a.insert_pdf(inserted)
        a.new_page(width=612, height=792)
        a.save(a_target)
    with fitz.open(sources[1]) as b:
        b.save(b_target)
    retry_items, completed_items = [], []
    for directory, items in (("partial", retry_items), ("cancelled", completed_items)):
        parent = root / directory
        parent.mkdir()
        for identifier, target in (("A", a_target), ("B", b_target)):
            output = parent / target.name
            output.write_bytes(target.read_bytes())
            items.append(
                {"DocumentId": identifier, "Output": output.name, "Status": "saved"}
            )
    competitor = root / "partial" / "competitor.pdf"
    competitor.write_text("T4_SYNTHETIC_COMPETITOR")
    editing = original_health["evidence"]["editing"]
    editing["DocumentId"] = "A"
    editing["edits"].append(
        {"block_index": 0, "old_text": NEW_ZH, "new_text": NEW_ZH + " AFTER_SAVE_AS202"}
    )
    health = {
        "state": "passed",
        "evidence": {
            "input_kind": "pdf_workspace",
            "job": original_health["evidence"]["job"],
            "workspace": {
                "editing": editing,
                "original_paths": list(map(str, sources)),
                "a_saved": {"file": str(a_target)},
                "b_saved": {"file": str(b_target)},
                "partial_directory": str(root / "partial"),
                "retry_items": retry_items,
                "cancelled_directory": str(root / "cancelled"),
                "completed_items": completed_items,
                "cancelled_items": [{"Status": "saved"}, {"Status": "cancelled"}],
                "competitor": str(competitor),
                "before_copy_documents": [{"IsModified": True}, {"IsModified": True}],
                "after_copy_documents": [{"IsModified": True}, {"IsModified": True}],
                "resources": [{"thumbnails": 64, "published_files": 66}],
            },
        },
    }
    (root / "paddle-input-pdf_workspace.json").write_text(
        json.dumps(health, ensure_ascii=False), encoding="utf-8"
    )
    assert verify_workspace_export(root)["copy_dirty_preserved"]
    return root


def test_workspace_complete_outputs_and_save_as_continuation_pass(
    tmp_path: Path,
) -> None:
    from scripts.verify_pdf_editing_export import verify_workspace_export

    report = verify_workspace_export(_workspace_root(tmp_path))
    assert len(report["outputs"]) == 6
    assert report["original_sources_preserved"] and report["save_as_continuation"]


def test_workspace_truncated_successful_copy_is_rejected(tmp_path: Path) -> None:
    from scripts.verify_pdf_editing_export import verify_workspace_export

    root = _workspace_root(tmp_path)
    health_path = root / "paddle-input-pdf_workspace.json"
    health = json.loads(health_path.read_text(encoding="utf-8"))
    health["evidence"]["workspace"]["editing"]["edits"][-1]["new_text"] += (
        " MUST_NOT_BE_MISSING"
    )
    health_path.write_text(json.dumps(health, ensure_ascii=False), encoding="utf-8")
    with pytest.raises(AssertionError, match="完整校正"):
        verify_workspace_export(root)


@pytest.mark.parametrize("workspace", [False, True])
def test_attempt_health_keeps_prior_failure(tmp_path: Path, workspace: bool) -> None:
    from scripts.verify_pdf_editing_export import verify_workspace_export

    root = (
        _workspace_root(tmp_path)
        if workspace
        else _make_root(tmp_path, "attempt", GOOD_LINES)
    )
    verify = verify_workspace_export if workspace else verify_export
    name = (
        "paddle-input-pdf_workspace.json"
        if workspace
        else "paddle-input-pdf_editing.json"
    )
    original = root / name
    attempt = root / "attempt-new.json"
    attempt.write_bytes(original.read_bytes())
    original.write_text('{"state": "failed"}', encoding="utf-8")
    with pytest.raises(AssertionError):
        verify(root)
    verify(root, attempt)
    assert json.loads(original.read_text(encoding="utf-8"))["state"] == "failed"
    with pytest.raises(AssertionError, match="合成隔离根"):
        verify(root, tmp_path / "outside.json")
