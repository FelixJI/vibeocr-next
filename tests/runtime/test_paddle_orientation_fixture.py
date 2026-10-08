"""合成方向 fixture 的独立渲染检查，不需要 Paddle 或模型。"""

import fitz
from PIL import Image, ImageDraw

from scripts.paddle_smoke_fixtures import draw_orientation_pdf


def test_orientation_fixture_covers_quadrants_and_existing_rotate(tmp_path):
    upright = Image.new("RGB", (300, 210), "white")
    draw = ImageDraw.Draw(upright)
    draw.rectangle((10, 10, 70, 50), fill="red")
    draw.rectangle((180, 120, 240, 160), fill="blue")
    upright.save(tmp_path / "text_zh_en.png")
    manifest = draw_orientation_pdf(tmp_path)
    with fitz.open(tmp_path / "document_orientation.pdf") as doc:
        assert doc.page_count == 8
        assert [page.rotation for page in doc] == [0, 90] * 4
        cases = manifest["rotations"]
        assert isinstance(cases, list)
        for page, case in zip(doc, cases, strict=True):
            page.set_rotation(case["expected_rotate"])
            rendered = page.get_pixmap(matrix=fitz.Matrix(3, 3), alpha=False)
            assert (rendered.width, rendered.height) == upright.size
            assert rendered.samples == upright.tobytes()
