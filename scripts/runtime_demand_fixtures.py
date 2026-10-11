"""Generate and validate the fixed synthetic PNG/PDF inputs for Task #214."""

from __future__ import annotations

import argparse
from pathlib import Path

import pymupdf
from PIL import Image, ImageDraw, ImageFont

TEXT = "VibeOCR 123"
SIZE = (960, 240)
FONT = Path(r"C:\Windows\Fonts\arial.ttf")


def generate(out: Path) -> None:
    out.mkdir(parents=True, exist_ok=False)
    font = ImageFont.truetype(str(FONT), 96)
    image = Image.new("RGB", SIZE, "white")
    draw = ImageDraw.Draw(image)
    left, top, right, bottom = draw.textbbox((0, 0), TEXT, font=font)
    draw.text(
        ((SIZE[0] - right + left) / 2 - left, (SIZE[1] - bottom + top) / 2 - top),
        TEXT,
        font=font,
        fill="black",
    )
    png = out / "vibeocr-123.png"
    image.save(png)
    with Image.open(png) as reopened:
        assert reopened.size == SIZE and reopened.format == "PNG"
        assert reopened.convert("L").getextrema() == (0, 255)

    pdf = out / "vibeocr-123.pdf"
    with pymupdf.open() as document:
        page = document.new_page(width=SIZE[0], height=SIZE[1])
        page.insert_font(fontname="arial", fontfile=str(FONT))
        page.insert_text(
            ((SIZE[0] - font.getlength(TEXT)) / 2, 152),
            TEXT,
            fontsize=96,
            fontname="arial",
        )
        document.save(pdf)
    with pymupdf.open(pdf) as document:
        assert document.page_count == 1
        assert " ".join(document[0].get_text().split()) == TEXT
        pixmap = document[0].get_pixmap()
        assert (pixmap.width, pixmap.height) == SIZE
        assert min(pixmap.samples) == 0 and max(pixmap.samples) == 255
    print(f"Validated {TEXT}: {SIZE[0]}x{SIZE[1]} Arial 96 PNG and one-page PDF")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", required=True, type=Path)
    generate(parser.parse_args().out)
