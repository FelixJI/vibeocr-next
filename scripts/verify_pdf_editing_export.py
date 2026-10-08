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


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True, help="冒烟隔离根")
    parser.add_argument(
        "--output",
        type=Path,
        default=None,
        help="报告输出路径（默认隔离根内；旧证据只读验收时指定仓库内路径）",
    )
    args = parser.parse_args(argv)
    report = verify_export(args.root)
    report_path = (args.output or args.root / REPORT_NAME).resolve()
    report_path.write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(json.dumps(report, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
