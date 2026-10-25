"""ocr_service.py staticmethod 纯逻辑测试。

ocr_service.py 仅 33% 覆盖。聚焦可独立测试的 staticmethod：
_to_ndarray / _normalize_result_bbox。
"""

from __future__ import annotations

import numpy as np
from vibeocr.runtime.recognition.models.ocr_result import OCRResult, TextBlock
from vibeocr.runtime.recognition.ocr_service import OCRService


class TestToNdarray:
    """_to_ndarray：统一输入为 ndarray 或路径字符串。"""

    def test_ndarray_passthrough(self):
        arr = np.zeros((10, 10, 3), dtype=np.uint8)
        # ndarray 无 convert 属性 → 原样返回
        result = OCRService._to_ndarray(arr)
        assert result is arr

    def test_str_path_passthrough(self):
        result = OCRService._to_ndarray("/some/path/image.png")
        assert result == "/some/path/image.png"

    def test_bytes_converted_to_ndarray(self):
        import io

        from PIL import Image

        img = Image.new("RGB", (5, 3), color=(255, 0, 0))
        buf = io.BytesIO()
        img.save(buf, format="PNG")
        result = OCRService._to_ndarray(buf.getvalue())
        assert isinstance(result, np.ndarray)
        assert result.shape == (3, 5, 3)

    def test_pil_image_converted(self):
        from PIL import Image

        img = Image.new("RGBA", (4, 2), color=(0, 255, 0, 128))
        result = OCRService._to_ndarray(img)
        assert isinstance(result, np.ndarray)
        assert result.shape == (2, 4, 3)  # converted to RGB


class TestNormalizeResultBbox:
    """_normalize_result_bbox：像素坐标归一化到 [0,1000]。"""

    def test_uses_preproc_dimensions(self):
        """优先用 result.preproc_img_w/h"""
        result = OCRResult(
            raw_text="x",
            preproc_img_w=100,
            preproc_img_h=50,
            text_blocks=[TextBlock(text="a", score=1.0, bbox=(10, 10, 50, 25))],
        )
        OCRService._normalize_result_bbox(result, image=None)
        assert result.text_blocks[0].bbox == (100.0, 200.0, 500.0, 500.0)

    def test_falls_back_to_image_shape(self):
        """无 preproc 尺寸时回退到 ndarray.shape"""
        img = np.zeros((50, 100, 3), dtype=np.uint8)  # h=50, w=100
        result = OCRResult(
            raw_text="x",
            preproc_img_w=0,
            preproc_img_h=0,
            text_blocks=[TextBlock(text="a", score=1.0, bbox=(50, 25, 100, 50))],
        )
        OCRService._normalize_result_bbox(result, image=img)
        assert result.text_blocks[0].bbox == (500.0, 500.0, 1000.0, 1000.0)

    def test_falls_back_to_pil_size(self):
        """无 shape 时回退到 PIL .size"""
        from PIL import Image

        img = Image.new("RGB", (200, 100))  # w=200, h=100
        result = OCRResult(
            raw_text="x",
            preproc_img_w=0,
            preproc_img_h=0,
            text_blocks=[TextBlock(text="a", score=1.0, bbox=(100, 50, 200, 100))],
        )
        OCRService._normalize_result_bbox(result, image=img)
        assert result.text_blocks[0].bbox == (500.0, 500.0, 1000.0, 1000.0)

    def test_rotation_swaps_dimensions(self):
        """90°/270° 旋转时宽高互换（仅无 preproc 尺寸时）"""
        img = np.zeros((50, 100, 3), dtype=np.uint8)  # h=50, w=100
        result = OCRResult(
            raw_text="x",
            preproc_img_w=0,
            preproc_img_h=0,
            preproc_angle=90,
            text_blocks=[TextBlock(text="a", score=1.0, bbox=(25, 50, 50, 100))],
        )
        OCRService._normalize_result_bbox(result, image=img)
        # 旋转后 img_w/h 互换：原 shape h=50,w=100 → 旋转后 w=50,h=100
        # bbox (25,50,50,100) / (50,100) * 1000
        assert result.text_blocks[0].bbox == (500.0, 500.0, 1000.0, 1000.0)

    def test_zero_dimensions_no_change(self):
        """尺寸为 0 时不修改（早返回）"""
        result = OCRResult(
            raw_text="x",
            preproc_img_w=0,
            preproc_img_h=0,
            text_blocks=[TextBlock(text="a", score=1.0, bbox=(10, 10, 20, 20))],
        )
        OCRService._normalize_result_bbox(result, image=None)
        assert result.text_blocks[0].bbox == (10, 10, 20, 20)

    def test_polygon_normalized_when_large(self):
        """polygon 像素值 >1001 时归一化"""
        result = OCRResult(
            raw_text="x",
            preproc_img_w=100,
            preproc_img_h=100,
            text_blocks=[
                TextBlock(
                    text="a",
                    score=1.0,
                    bbox=None,
                    polygon=(0, 0, 100, 0, 100, 100, 0, 100),
                )
            ],
        )
        OCRService._normalize_result_bbox(result, image=None)
        poly = result.text_blocks[0].polygon
        assert poly is not None
        assert max(poly) <= 1001

    def test_content_list_bbox_normalized(self):
        """content_list 的 bbox 也被归一化"""
        result = OCRResult(
            raw_text="x",
            preproc_img_w=100,
            preproc_img_h=100,
            text_blocks=[],
            content_list=[{"bbox": [10, 10, 50, 50]}],
        )
        OCRService._normalize_result_bbox(result, image=None)
        assert result.content_list[0]["bbox"] == [100.0, 100.0, 500.0, 500.0]
