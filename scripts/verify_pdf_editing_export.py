"""pdf_editing 冒烟阶段的最终导出 PDF 校验（仓库锁定 pymupdf）。

smoke_paddle_modes.ps1 的 pdf_editing UI phase（health JSON）只证明应用内
流程：高清检查、取消/切页草稿、中英文长句校正、公共 picker 保存与重开
预览；完整文字与像素校验必须发生在导出 PDF 文件本身上。本脚本即该
final export verification：完整长句与尾标记恰好一次、旧文字层不重复、
未改块保留、取消/切页草稿不存在、原扫描页像素与空白插入页不变。
任一断言失败直接以非 0 退出（冒烟脚本据此置败）；报告仅在校验通过后
写出（默认隔离根内新文件，不覆盖任何原始 health 证据）。
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import fitz

HEALTH_NAME = "paddle-input-pdf_editing.json"
REPORT_NAME = "pdf-editing-export-verification.json"
SCAN_COMPARE_DPI = 144


def _norm(value: str) -> str:
    return "".join(value.split())


def verify_export(root: Path) -> dict:
    """读取隔离根内固定 health 证据并校验导出 PDF；通过返回报告 dict，
    任一校验失败抛 AssertionError/异常（调用方以非 0 退出）。"""
    root = root.resolve()
    health = json.loads((root / HEALTH_NAME).read_text(encoding="utf-8-sig"))
    assert health["state"] == "passed", f"health state={health.get('state')}"
    proof = health["evidence"]
    assert proof["input_kind"] == "pdf_editing", proof.get("input_kind")
    edits = proof["editing"]["edits"]
    output = Path(proof["editing"]["saved"]["file"]).resolve()
    source = Path(proof["fixture_path"]).resolve()
    assert output.is_relative_to(root) and output.is_file(), f"导出缺失: {output}"
    assert source.is_relative_to(root) and source.is_file(), f"源缺失: {source}"
    blocks = proof["job"]["outcomes"][0]["Payload"]["text_blocks"]
    changed = [int(edit["block_index"]) for edit in edits]
    assert len(changed) == 2 and len(set(changed)) == 2, f"应有恰好两次编辑: {edits}"
    assert all(0 <= index < len(blocks) for index in changed), "block_index 越界"
    for edit in edits:
        index = int(edit["block_index"])
        assert _norm(blocks[index]["text"]) == _norm(edit["old_text"]), (
            f"edit block {index} 的 old_text 与原始块不一致"
        )

    with fitz.open(output) as saved:
        assert len(saved) == 2, f"导出页数应为 2，实际 {len(saved)}"
        page, blank_page = saved[0], saved[1]
        actual = _norm(page.get_text())
        assert not blank_page.get_text().strip(), "插入空白页出现了可提取文字"

        # 完整新长句与尾标记：恰好一次（截断=0 次，重复=2 次都失败）。
        for edit in edits:
            new_text = _norm(edit["new_text"])
            old_text = _norm(edit["old_text"])
            assert actual.count(new_text) == 1, f"新长句未完整且仅一次: {new_text[:24]}"
            expected_old = sum(
                _norm(block["text"]).count(old_text)
                for i, block in enumerate(blocks)
                if i not in changed
            ) + sum(_norm(item["new_text"]).count(old_text) for item in edits)
            assert actual.count(old_text) == expected_old, (
                f"旧文字层残留或重复: {old_text[:24]}"
            )
        for marker in ("CNTAIL201", "ENTAIL201"):
            assert actual.count(marker) == 1, f"末尾标记应恰好出现一次: {marker}"
        for draft in ("CANCELLED_DRAFT201", "PAGE_SWITCH_DRAFT201"):
            assert draft not in actual, f"取消/切页草稿泄漏进导出: {draft}"
        unchanged = []
        for i, block in enumerate(blocks):
            if i in changed:
                continue
            assert _norm(block["text"]) in actual, f"未改块丢失: block {i}"
            unchanged.append({"index": i, "text": block["text"]})

        with fitz.open(source) as origin:
            assert len(origin) == 1 and not origin[0].get_text().strip(), (
                "源 fixture 应为单页纯扫描件"
            )
            assert tuple(origin[0].rect) == tuple(page.rect), "首页几何与源不一致"
            assert origin[0].rotation == page.rotation, "首页旋转与源不一致"
            before = origin[0].get_pixmap(dpi=SCAN_COMPARE_DPI, alpha=False)
            after = page.get_pixmap(dpi=SCAN_COMPARE_DPI, alpha=False)
            assert (before.width, before.height, before.samples) == (
                after.width,
                after.height,
                after.samples,
            ), "原扫描页可见像素发生变化（UI 辅助框或重绘）"
            assert len(list(origin[0].annots() or [])) == len(
                list(page.annots() or [])
            ), "首页注解数量与源不一致"
            blank = blank_page.get_pixmap(dpi=72, alpha=False)
            assert set(blank.samples) == {255}, "插入空白页出现可见内容"

    return {
        "candidate_root": str(root),
        "output": str(output),
        "page_count": 2,
        "edits": edits,
        "unchanged_blocks": unchanged,
        "full_new_text_and_tail_tokens_exact": True,
        "old_layer_duplicates": False,
        "cancelled_drafts_present": False,
        "source_still_scan_only": True,
        "scan_visible_pixels_exact_at_dpi": SCAN_COMPARE_DPI,
        "blank_page_unchanged": True,
        "verifier": "scripts/verify_pdf_editing_export.py (pymupdf)",
    }


def verify_workspace_export(root: Path) -> dict:
    """同一真实候选的工作区流程：检查全部成功副本及当前保存目标。"""
    root = root.resolve()
    health = json.loads(
        (root / "paddle-input-pdf_workspace.json").read_text(encoding="utf-8-sig")
    )
    assert health["state"] == "passed", health.get("state")
    evidence = health["evidence"]
    assert evidence["input_kind"] == "pdf_workspace"
    workspace = evidence["workspace"]
    editing = workspace["editing"]
    edits = editing["edits"]
    final_edits = {int(edit["block_index"]): edit for edit in edits}
    assert len(edits) == 3 and len(final_edits) == 2
    assert "AFTER_SAVE_AS202" in edits[-1]["new_text"]
    blocks = evidence["job"]["outcomes"][0]["Payload"]["text_blocks"]
    original = [Path(path).resolve() for path in workspace["original_paths"]]
    assert len(original) == 2 and original[0].name == original[1].name
    assert original[0].parent != original[1].parent
    a_id = editing["DocumentId"]
    outputs = [
        (Path(workspace["a_saved"]["file"]), True),
        (Path(workspace["b_saved"]["file"]), False),
    ]
    for directory, key in (
        ("partial_directory", "retry_items"),
        ("cancelled_directory", "completed_items"),
    ):
        for item in workspace[key]:
            assert item["Status"] == "saved", item
            outputs.append(
                (
                    Path(workspace[directory]) / item["Output"],
                    item["DocumentId"] == a_id,
                )
            )
    assert any(item["Status"] == "saved" for item in workspace["cancelled_items"])
    assert any(
        item["Status"] in ("cancelled", "not_started")
        for item in workspace["cancelled_items"]
    )
    assert all(item["IsModified"] for item in workspace["before_copy_documents"])
    assert all(item["IsModified"] for item in workspace["after_copy_documents"])
    competitor = Path(workspace["competitor"]).resolve()
    assert (
        competitor.is_relative_to(root)
        and competitor.read_text() == "T4_SYNTHETIC_COMPETITOR"
    )
    checked = []
    for path, is_a in outputs:
        path = path.resolve()
        assert path.is_relative_to(root) and path.is_file(), path
        with fitz.open(path) as saved, fitz.open(original[0 if is_a else 1]) as source:
            assert len(saved) == (77 if is_a else 3), (path, len(saved))
            assert len(source) == (75 if is_a else 3)
            assert not source[0].get_text().strip(), "源扫描页被写入"
            assert "WORKSPACE NATIVE TAIL202" in source[1].get_text(), "源原生页被覆盖"
            before = source[0].get_pixmap(dpi=SCAN_COMPARE_DPI, alpha=False)
            after = saved[0].get_pixmap(dpi=SCAN_COMPARE_DPI, alpha=False)
            assert (before.width, before.height, before.samples) == (
                after.width,
                after.height,
                after.samples,
            ), "扫描像素改变"
            actual = _norm(saved[0].get_text())
            if is_a:
                for edit in final_edits.values():
                    assert actual.count(_norm(edit["new_text"])) == 1, (
                        "完整校正文本丢失或重复"
                    )
                for token in ("CNTAIL201", "ENTAIL201", "AFTER_SAVE_AS202"):
                    assert actual.count(token) == 1, token
                for index, block in enumerate(blocks):
                    expected = _norm(
                        final_edits[index]["new_text"]
                        if index in final_edits
                        else block["text"]
                    )
                    assert expected in actual, f"未改块/完整新文本丢失 {index}"
                    if index in final_edits:
                        old = _norm(block["text"])
                        expected_count = sum(
                            _norm(
                                final_edits[i]["new_text"]
                                if i in final_edits
                                else value["text"]
                            ).count(old)
                            for i, value in enumerate(blocks)
                        )
                        assert actual.count(old) == expected_count, "旧文字层残留或重复"
                assert not saved[1].get_text().strip(), "明确删除的原生文字仍在"
                clip = fitz.Rect(40, 120, 220, 200)
                assert (
                    source[1].get_pixmap(clip=clip).samples
                    == saved[1].get_pixmap(clip=clip).samples
                ), "删除文字破坏非文字图形"
                with fitz.open(root / "fixtures" / "document_mixed.pdf") as inserted:
                    assert _norm(inserted[0].get_text()) == _norm(
                        saved[75].get_text()
                    ), "插入页顺序/全文不正确"
                    assert (
                        inserted[0].get_pixmap().samples
                        == saved[75].get_pixmap().samples
                    ), "插入页像素改变"
                blank_indices = [*range(2, 75), 76]
            else:
                assert not actual, "副本导出给未识别扫描页伪造文字"
                assert source[1].get_text() == saved[1].get_text()
                assert (
                    source[1].get_pixmap().samples == saved[1].get_pixmap().samples
                ), "非目标原生页像素改变"
                blank_indices = [2]
            for index in blank_indices:
                assert not saved[index].get_text().strip() and set(
                    saved[index].get_pixmap().samples
                ) == {255}, f"空白页 {index} 改变"
            assert all(
                draft not in actual
                for draft in ("CANCELLED_DRAFT201", "PAGE_SWITCH_DRAFT201")
            )
            checked.append(
                {
                    "file": str(path),
                    "page_count": len(saved),
                    "document": "A" if is_a else "B",
                }
            )
    assert all(
        sample["thumbnails"] <= 64 and sample["published_files"] <= 72
        for sample in workspace["resources"]
    )
    return {
        "candidate_root": str(root),
        "outputs": checked,
        "edits": edits,
        "resources": workspace["resources"],
        "full_text_exact": True,
        "save_as_continuation": True,
        "original_sources_preserved": True,
        "scan_pixels_exact_at_dpi": SCAN_COMPARE_DPI,
        "copy_dirty_preserved": True,
        "commit_collision_preserved": True,
        "verifier": "scripts/verify_pdf_editing_export.py (pymupdf)",
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True, help="冒烟隔离根")
    parser.add_argument(
        "--output",
        type=Path,
        default=None,
        help="报告输出路径（默认隔离根内；旧证据只读验收时指定仓库内路径）",
    )
    parser.add_argument(
        "--workspace", action="store_true", help="校验 pdf_workspace 组合输出"
    )
    args = parser.parse_args(argv)
    report = (
        verify_workspace_export(args.root)
        if args.workspace
        else verify_export(args.root)
    )
    name = "pdf-workspace-export-verification.json" if args.workspace else REPORT_NAME
    report_path = (args.output or args.root / name).resolve()
    report_path.write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(json.dumps(report, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
