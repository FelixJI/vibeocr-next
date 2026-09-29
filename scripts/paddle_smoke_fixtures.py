"""Goal #110 五模式冒烟合成样本生成器。

只依赖仓库既有 Pillow（可选 pymupdf 生成 PDF fixture），不新增依赖。
所有内容为确定性绘制（无随机数）：文字、中英文合并单元格有线/无线
表格、分式/根式/上下标多公式、含合成图像(图表/印章)的图文混排页与
可选 PDF。输出目录只写入调用方指定的隔离目录，不触碰用户状态。

用法：
    python scripts/paddle_smoke_fixtures.py --out <dir> [--skip-pdf]
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

FONT_PRIORITY = (
    r"C:\Windows\Fonts\msyh.ttc",
    r"C:\Windows\Fonts\msyhbd.ttc",
    r"C:\Windows\Fonts\simhei.ttf",
    r"C:\Windows\Fonts\simsun.ttc",
)

SYMBOL_FONT_PRIORITY = (
    r"C:\Windows\Fonts\seguisym.ttf",
    r"C:\Windows\Fonts\msyh.ttc",
    r"C:\Windows\Fonts\simhei.ttf",
)

# 数学字形：优先专业数学字体，回退 Cambria/Times；斜体变量单独一链。
MATH_FONT_PRIORITY = (
    r"C:\Windows\Fonts\DejaVuMathTeXGyre.ttf",
    r"C:\Windows\Fonts\cambria.ttc",
    r"C:\Windows\Fonts\times.ttf",
    r"C:\Windows\Fonts\msyh.ttc",
)
MATH_ITALIC_PRIORITY = (
    r"C:\Windows\Fonts\DejaVuSerif-Italic.ttf",
    r"C:\Windows\Fonts\timesi.ttf",
    r"C:\Windows\Fonts\cambriai.ttf",
    r"C:\Windows\Fonts\msyh.ttc",
)


def load_font(size: int, symbol: bool = False) -> ImageFont.FreeTypeFont:
    for candidate in SYMBOL_FONT_PRIORITY if symbol else FONT_PRIORITY:
        path = Path(candidate)
        if path.is_file():
            return ImageFont.truetype(str(path), size)
    raise SystemExit(f"未找到可用字体（CJK/符号）：{candidate}")


def new_page(width: int, height: int) -> tuple[Image.Image, ImageDraw.ImageDraw]:
    image = Image.new("RGB", (width, height), "white")
    return image, ImageDraw.Draw(image)


def write_manifest(out: Path, entries: dict[str, object]) -> None:
    (out / "manifest.json").write_text(
        json.dumps(entries, ensure_ascii=False, indent=2), encoding="utf-8"
    )


def draw_text_page(out: Path) -> dict[str, object]:
    """paddle_text：中英文混排大字号文字页。"""
    image, draw = new_page(1600, 900)
    font = load_font(64)
    lines = [
        "VibeOCR GOAL103 Smoke",
        "Paddle 通用文字识别 1120",
        "中英文混合 Five Modes 验收",
        "END LINE 2244",
    ]
    for index, line in enumerate(lines):
        draw.text((120, 120 + index * 140), line, fill="black", font=font)
    path = out / "text_zh_en.png"
    image.save(path)
    return {
        "purpose": "paddle_text 中英文通用文字",
        "size": image.size,
        "tokens": ["GOAL103", "1120", "2244"],
    }


def _table_grid() -> list[list[str]]:
    """4 列网格；空串表示被上方/左侧合并吞掉的单元格。"""
    return [
        ["Paddle 表格验收 GOAL103", "", "", ""],
        ["项目 Item", "数量 Qty", "单价 Price", "备注 Note"],
        ["合并单元 Merged", "3", "12.50", "English mix"],
        ["", "7", "0.99", "中文备注\n第二行"],
        ["合计 Total", "10", "45.47", "END 1120"],
    ]


def draw_table(out: Path, wireless: bool) -> dict[str, object]:
    """paddle_table：表头跨 4 列、首列跨 2 行的中英文合并单元格表格。"""
    width, height = 1800, 1000
    image, draw = new_page(width, height)
    font = load_font(48)
    header_font = load_font(54)
    grid = _table_grid()
    margin_x, top, row_height = 100, 80, 160
    columns = [680, 1020, 1380, 1780]
    for row, cells in enumerate(grid):
        y = top + row * row_height
        for col, text in enumerate(cells):
            if not text:
                continue
            x = margin_x if col == 0 else columns[col - 1]
            # 表头整行合并：按整行宽度居中绘制。
            if row == 0:
                draw.text(
                    (margin_x + 440, y + 40), text, fill="black", font=header_font
                )
                break
            draw.text((x + 16, y + 28), text, fill="black", font=font)
    if not wireless:
        line = {"fill": "black", "width": 4}
        for row in range(len(grid) + 1):
            y = top + row * row_height
            draw.line([(margin_x, y), (columns[-1], y)], **line)
        for x in [margin_x, *columns]:
            draw.line([(x, top), (x, top + len(grid) * row_height)], **line)
        # 合并单元格：只擦除被合并跨越的内部线段（细矩形对准线条本身，
        # 不碰单元格内容）：表头跨 4 列 → 擦除行内 3 条竖线；首列第 3-4
        # 行跨 2 行 → 仅擦除中间横线段，保留列边界。
        for x in columns[:-1]:
            draw.rectangle((x - 3, top + 3, x + 3, top + row_height - 3), fill="white")
        draw.rectangle(
            (
                margin_x + 3,
                top + 3 * row_height - 3,
                columns[0] - 3,
                top + 3 * row_height + 3,
            ),
            fill="white",
        )
    name = "table_wireless.png" if wireless else "table_merged_zh_en.png"
    path = out / name
    image.save(path)
    return {
        "purpose": "paddle_table " + ("无线表格" if wireless else "有线合并单元格表格"),
        "size": image.size,
        "rows": 5,
        "columns": 4,
        "merges": ["表头跨 4 列", "第 1 列第 3-4 行跨 2 行"],
        "tokens": ["GOAL103", "1120", "Merged", "合计"],
    }


def load_math_font(size: int, italic: bool = False) -> ImageFont.FreeTypeFont:
    """数学字形：优先专业数学字体，回退 Cambria/Times；斜体变量单独一链。"""
    priority = MATH_ITALIC_PRIORITY if italic else MATH_FONT_PRIORITY
    for candidate in priority:
        if Path(candidate).is_file():
            return ImageFont.truetype(candidate, size)
    raise SystemExit(f"未找到可用数学字体：{candidate}")


def draw_formulas(out: Path) -> dict[str, object]:
    """paddle_formula：正常排版数学页（分式、根式+上标、上下标求和 3 式）。

    A4 竖版固定坐标 + Pillow 原生基线锚点（anchor="ls"）直绘：标题、
    正文与居中显示公式（编号 (1)-(3)）交替。
    """
    page_w, page_h = 1240, 1754  # A4 @150dpi
    image = Image.new("RGB", (page_w, page_h), "white")
    draw = ImageDraw.Draw(image)
    margin, axis_x, size = 100, page_w // 2, 60
    sup_size = size * 66 // 100  # 脚本字号（TeX 风格 66%）
    title_font = load_font(58)
    body_font = load_font(46)
    num_font = load_math_font(44)
    math = load_math_font(size)
    var = load_math_font(size, italic=True)
    sup = load_math_font(sup_size)
    sup_var = load_math_font(sup_size, italic=True)
    big = load_math_font(size * 2)

    def cjk_line(text: str, font: ImageFont.FreeTypeFont, y: int, center: bool) -> int:
        width = int(font.getlength(text))
        x = axis_x - width // 2 if center else margin
        draw.text((x, y), text, font=font, fill="black")
        return y + font.size + 14

    def formula_number(y_center: int, number: str) -> None:
        width = int(num_font.getlength(number))
        draw.text(
            (page_w - margin - width, y_center - 22),
            number,
            font=num_font,
            fill="black",
        )

    def run_width(pieces: list[tuple[str, ImageFont.FreeTypeFont, int]]) -> float:
        return sum(font.getlength(text) for text, font, _ in pieces)

    def draw_run(pieces, x: float, baseline: int) -> float:
        """基线锚点顺序绘制 (text, font, dy) 片段，返回游标终点。"""
        for text, font, dy in pieces:
            draw.text((x, baseline + dy), text, font=font, fill="black", anchor="ls")
            x += font.getlength(text)
        return x

    def draw_fraction(numer, denom, axis_y: int, center_x: float) -> tuple[int, int]:
        """分子/分数线/分母围绕 axis_y 水平居中；返回块顶/底 y。"""
        rule, gap = 5, 12
        width = max(run_width(numer), run_width(denom)) + 8
        ascent, descent = math.getmetrics()
        draw_run(numer, center_x - run_width(numer) / 2, axis_y - rule // 2 - gap)
        draw.line(
            [(center_x - width / 2, axis_y), (center_x + width / 2, axis_y)],
            fill="black",
            width=rule,
        )
        draw_run(
            denom, center_x - run_width(denom) / 2, axis_y + rule // 2 + gap + ascent
        )
        return (
            axis_y - rule // 2 - gap - ascent,
            axis_y + rule // 2 + gap + ascent + descent,
        )

    y = 96
    y = cjk_line("数学公式识别样例", title_font, y, center=True) + 42
    y = (
        cjk_line("下列公式包含分式、根式与上下标求和结构。", body_font, y, center=False)
        + 58
    )

    # (1) 分式：(a + b) / (c − d)。
    top, bottom = draw_fraction(
        [("a", var, 0), (" + ", math, 0), ("b", var, 0)],
        [("c", var, 0), (" \u2212 ", math, 0), ("d", var, 0)],
        axis_y=380,
        center_x=axis_x,
    )
    formula_number((top + bottom) // 2, "(1)")

    y = cjk_line("第二式含根式与平方上标：", body_font, bottom + 46, center=False) + 40

    # (2) x = √(b² − 4ac)：大根号 + 原生锚点上标（基线上移 0.40em）。
    y2 = 700
    pieces = [
        ("b", var, 0),
        ("2", sup, -size * 40 // 100),
        ("\u2212", math, 0),
        ("4", math, 0),
        ("a", var, 0),
        ("c", var, 0),
    ]
    prefix = [("x", var, 0), (" = ", math, 0)]
    rad = "\u221a"
    total = run_width(prefix) + big.getlength(rad) + 4 + run_width(pieces)
    x = axis_x - total / 2
    x = draw_run(prefix, x, y2)
    rad_box = draw.textbbox((x, y2), rad, font=big, anchor="ls")
    draw.text((x, y2), rad, font=big, fill="black", anchor="ls")
    x += big.getlength(rad)
    end = draw_run(pieces, x + 4, y2)
    draw.line(
        [(x - 4, rad_box[1] + 2), (end + 2, rad_box[1] + 2)], fill="black", width=5
    )
    formula_number((rad_box[1] + y2 + math.getmetrics()[1]) // 2, "(2)")

    y = cjk_line("第三式为带上下限的求和：", body_font, y2 + 96, center=False) + 46

    # (3) Σ(i=1..n) i² = n(n+1)(2n+1)/6：Σ 上下限 + 分式右端。
    y3 = 1060
    sigma = "\u2211"
    upper = [("n", sup_var, 0)]
    lower = [("i", sup_var, 0), (" = 1", sup, 0)]
    body = [("i", var, 0), ("2", sup, -size * 40 // 100), (" = ", math, 0)]
    numer = [
        ("n", var, 0),
        ("(", math, 0),
        ("n", var, 0),
        (" + 1)", math, 0),
        ("(", math, 0),
        ("2", math, 0),
        ("n", var, 0),
        (" + 1)", math, 0),
    ]
    denom = [("6", math, 0)]
    sig_box = draw.textbbox((0, y3), sigma, font=big, anchor="ls")
    sig_w = big.getlength(sigma)
    sig_block = max(sig_w, run_width(upper), run_width(lower))
    frac_w = max(run_width(numer), run_width(denom)) + 8
    total = sig_block + 18 + run_width(body) + 12 + frac_w
    x = axis_x - total / 2
    draw.text(
        (x + (sig_block - sig_w) / 2, y3), sigma, font=big, fill="black", anchor="ls"
    )
    draw_run(upper, x + (sig_block - run_width(upper)) / 2, sig_box[1] - 8)
    draw_run(
        lower,
        x + (sig_block - run_width(lower)) / 2,
        sig_box[3] + 8 + sup.getmetrics()[0],
    )
    x += sig_block + 18
    x = draw_run(body, x, y3)
    top, bottom = draw_fraction(
        numer, denom, axis_y=y3 - size // 3, center_x=x + 12 + frac_w / 2
    )
    formula_number((sig_box[1] - 8 - sup_var.getmetrics()[0] + bottom) // 2, "(3)")

    cjk_line(
        "样例公式页完 END OF SHEET",
        body_font,
        min(bottom + 60, page_h - 120),
        center=False,
    )

    path = out / "formulas_multi.png"
    image.save(path)
    return {
        "purpose": "paddle_formula 分式/根式上标/上下标求和正常排版数学页",
        "size": image.size,
        "formula_count": 3,
        "tokens": [],
    }


def _draw_chart(draw: ImageDraw.ImageDraw, x: int, y: int) -> None:
    """合成柱状图图像区块（非真实数据图表，仅用于版面/图表模块验证）。"""
    draw.rectangle((x, y, x + 360, y + 260), outline="black", width=3)
    for index, (bar, color) in enumerate(
        zip([90, 150, 210], ["#2f6fb3", "#b3452f", "#3f8f4f"])
    ):
        bar_x = x + 50 + index * 100
        draw.rectangle((bar_x, y + 250 - bar, bar_x + 60, y + 250), fill=color)
    draw.line((x + 20, y + 250, x + 350, y + 250), fill="black", width=3)
    draw.line((x + 20, y + 20, x + 20, y + 250), fill="black", width=3)


def _draw_seal(draw: ImageDraw.ImageDraw, x: int, y: int) -> None:
    """合成红色圆形印章区块（配合 use_seal_recognition 非默认选项）。"""
    draw.ellipse((x, y, x + 180, y + 180), outline="#c02020", width=8)
    font = load_font(64)
    draw.text((x + 58, y + 52), "验", fill="#c02020", font=font)


def draw_document_mixed(out: Path) -> dict[str, object]:
    """paddle_structure / paddle_document_vl：标题+段落+表格+公式+图表+印章。"""
    image, draw = new_page(1700, 2200)
    title = load_font(72)
    body = load_font(48)
    draw.text((120, 100), "混合文档验收 GOAL103", fill="black", font=title)
    for index, line in enumerate(
        [
            "本页包含正文段落、表格、公式与合成图像。",
            "Paragraph text with English words 1120 mixed in Chinese.",
            "版面顺序依次为：正文、表格、公式、图表、印章。",
        ]
    ):
        draw.text((120, 240 + index * 90), line, fill="black", font=body)

    grid = _table_grid()
    top = 560
    for row, cells in enumerate(grid[:4]):
        y = top + row * 110
        for col, text in enumerate(cells):
            if not text:
                continue
            draw.text((140 + col * 360, y + 20), text, fill="black", font=body)
    for row in range(5):
        y = top + row * 110
        draw.line([(120, y), (1560, y)], fill="black", width=3)
    for gx in (120, 480, 840, 1200, 1560):
        draw.line([(gx, top), (gx, top + 4 * 110)], fill="black", width=3)

    y = 1180
    draw.text((140, y - 20), "a + b", fill="black", font=body)
    draw.line([(140, y + 60), (380, y + 60)], fill="black", width=4)
    draw.text((160, y + 80), "c − d", fill="black", font=body)

    _draw_chart(draw, 120, 1420)
    _draw_seal(draw, 1200, 1500)
    draw.text((120, 2000), "MIXED PAGE END 2244", fill="black", font=body)

    path = out / "document_mixed.png"
    image.save(path)
    return {
        "purpose": "paddle_structure / paddle_document_vl 图文混排页",
        "size": image.size,
        "blocks": ["title", "paragraphs", "table", "formula", "chart-image", "seal"],
        "tokens": ["GOAL103", "1120", "2244"],
    }


def draw_document_pdf(out: Path) -> dict[str, object] | None:
    """可选 PDF fixture：pymupdf 缺失时跳过并如实说明。

    中文用内置 CID 字体 china-s（不嵌入字体文件，仅作合成输入裁体）。
    """
    try:
        import pymupdf  # noqa: PLC0415 — 可选依赖按需导入
    except ImportError:
        return None
    doc = pymupdf.open()
    page = doc.new_page(width=595, height=842)
    page.insert_text((60, 80), "混合文档验收 GOAL103", fontsize=22, fontname="china-s")
    for index, line in enumerate(
        [
            "本 PDF 含中英文正文与 1120 等验收标记。",
            "Second paragraph with English 2244 tokens.",
        ]
    ):
        page.insert_text((60, 130 + index * 30), line, fontsize=12, fontname="china-s")
    table = _table_grid()[:4]
    for row, cells in enumerate(table):
        for col, text in enumerate(cells):
            if text:
                page.insert_text(
                    (60 + col * 120, 220 + row * 24),
                    text,
                    fontsize=10,
                    fontname="china-s",
                )
    for row in range(5):
        page.draw_line(
            pymupdf.Point(55, 200 + row * 24), pymupdf.Point(540, 200 + row * 24)
        )
    page.insert_text((60, 380), "MIXED PDF END 2244", fontsize=12, fontname="china-s")
    path = out / "document_mixed.pdf"
    doc.save(str(path))
    doc.close()
    return {
        "purpose": "可选 PDF 输入（Goal 组合验证用；本冒烟不自动消费）",
        "font": "builtin-china-s",
        "pages": 1,
        "tokens": ["GOAL103", "1120", "2244"],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", required=True, type=Path, help="隔离输出目录")
    parser.add_argument("--skip-pdf", action="store_true", help="跳过可选 PDF fixture")
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    entries: dict[str, object] = {
        "schema_version": 1,
        "generator": "scripts/paddle_smoke_fixtures.py",
        "synthetic_only": True,
    }
    entries["text_zh_en.png"] = draw_text_page(args.out)
    entries["table_merged_zh_en.png"] = draw_table(args.out, wireless=False)
    entries["table_wireless.png"] = draw_table(args.out, wireless=True)
    entries["formulas_multi.png"] = draw_formulas(args.out)
    entries["document_mixed.png"] = draw_document_mixed(args.out)
    if not args.skip_pdf:
        pdf = draw_document_pdf(args.out)
        if pdf is None:
            entries["document_mixed.pdf"] = {
                "state": "skipped",
                "reason": "pymupdf 未安装（可选依赖）",
            }
        else:
            entries["document_mixed.pdf"] = pdf
    write_manifest(args.out, entries)
    print(json.dumps(entries, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
