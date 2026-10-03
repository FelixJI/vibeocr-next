"""品牌生成资产的语义回归（goal #133 / U01）。

历史缺陷：vibeocr-128.png 全透明、vibeocr-256.png 右移截断，高 DPI 任务栏
选层后图标偏移。这里锁住：语义规则能捕获两类历史坏层；确定性重建只修
128/256/ICO，小层字节不变；ICO 从原始字节独立验证 7 层齐全。
"""

from __future__ import annotations

import shutil
import struct
from pathlib import Path

from PIL import Image

from scripts.generate_brand_assets import (
    LAYER_SIZES,
    UPSCALED_SIZES,
    check,
    layer_issues,
    rebuild_ico,
    rebuild_upscaled_layers,
)

GENERATED = Path(__file__).resolve().parents[3] / "assets" / "brand" / "generated"


class TestBrandAssets:
    def test_committed_assets_pass_semantic_check(self):
        assert check(GENERATED) == []

    def test_semantic_rules_reject_historical_faults(self):
        empty = Image.new("RGBA", (128, 128), (0, 0, 0, 0))
        assert any(
            "fully transparent" in issue for issue in layer_issues("empty", empty, 128)
        )

        clipped = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
        mark = (
            Image.open(GENERATED / "vibeocr-64.png").convert("RGBA").resize((114, 208))
        )
        clipped.paste(mark, (142, 24), mark)
        issues = layer_issues("clipped", clipped, 256)
        assert any("clipped" in issue for issue in issues)
        assert any("not centered horizontally" in issue for issue in issues)

    def test_rebuild_repairs_broken_layers_and_keeps_small_layers(self, tmp_path):
        for size in LAYER_SIZES:
            shutil.copy2(GENERATED / f"vibeocr-{size}.png", tmp_path)
        Image.new("RGBA", (128, 128), (0, 0, 0, 0)).save(
            tmp_path / "vibeocr-128.png", format="PNG"
        )
        clipped = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
        mark = (
            Image.open(GENERATED / "vibeocr-64.png").convert("RGBA").resize((114, 208))
        )
        clipped.paste(mark, (142, 24), mark)
        clipped.save(tmp_path / "vibeocr-256.png", format="PNG")
        assert check(tmp_path) != []

        small = {
            size: (tmp_path / f"vibeocr-{size}.png").read_bytes()
            for size in LAYER_SIZES
            if size not in UPSCALED_SIZES
        }

        rebuild_upscaled_layers(tmp_path)
        rebuild_ico(tmp_path)

        assert check(tmp_path) == []
        for size, payload in small.items():
            assert (tmp_path / f"vibeocr-{size}.png").read_bytes() == payload

    def test_ico_carries_seven_expected_layers(self, tmp_path):
        for size in LAYER_SIZES:
            shutil.copy2(GENERATED / f"vibeocr-{size}.png", tmp_path)
        rebuild_ico(tmp_path)

        data = (tmp_path / "vibeocr.ico").read_bytes()
        count = struct.unpack_from("<H", data, 4)[0]
        declared = []
        for index in range(count):
            offset = struct.unpack_from("<I", data, 6 + index * 16 + 12)[0]
            assert data[offset : offset + 4] == b"\x89PNG"
            declared.append((data[6 + index * 16] or 256, data[7 + index * 16] or 256))
        assert declared == [(size, size) for size in LAYER_SIZES]
