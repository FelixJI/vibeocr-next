"""确定性品牌资源生成入口。

无母版/SVG 源：以健康的 64px 层为母版等比放大（LANCZOS）重建 128/256
两层，16/24/32/48/64 层保持不变；按既有容器约定（PNG 载荷、尺寸升序、
256 记 0）重新装配 7 层 vibeocr.ico。放大层比真实大尺寸母版更柔，属已
知限制。语义校验：每层非空、四边留白不截断、居中，ICO 恰好包含 7 个
声明尺寸且与载荷一致；不以 hash 作为正确性依据。
"""

from __future__ import annotations

import argparse
import io
import struct
import sys
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
GENERATED_DIR = ROOT / "assets" / "brand" / "generated"
LAYER_SIZES = (16, 24, 32, 48, 64, 128, 256)
MASTER_SIZE = 64
UPSCALED_SIZES = (128, 256)


def layer_path(root: Path, size: int) -> Path:
    return root / f"vibeocr-{size}.png"


def ico_path(root: Path) -> Path:
    return root / "vibeocr.ico"


def layer_issues(label: str, image: Image.Image, expected_size: int) -> list[str]:
    """语义校验单层：尺寸精确、非空、四边留白 ≥1px（不截断）、居中。"""
    width, height = image.size
    if (width, height) != (expected_size, expected_size):
        return [
            f"{label}: expected {expected_size}x{expected_size}, got {width}x{height}"
        ]

    bbox = image.convert("RGBA").getchannel("A").getbbox()
    if bbox is None:
        return [f"{label}: fully transparent"]

    left, top, right, bottom = bbox
    issues: list[str] = []
    if min(left, top, width - right, height - bottom) < 1:
        issues.append(f"{label}: clipped to canvas edge (bbox={bbox})")
    tolerance = max(1, expected_size // 32)
    if abs(left - (width - right)) > tolerance:
        issues.append(f"{label}: not centered horizontally (bbox={bbox})")
    if abs(top - (height - bottom)) > tolerance:
        issues.append(f"{label}: not centered vertically (bbox={bbox})")
    return issues


def parse_ico_entries(data: bytes) -> list[tuple[int, int, bytes]]:
    """解析 ICO 目录为 (width, height, payload)；只按本仓 PNG 载荷约定。"""
    _reserved, icon_type, count = struct.unpack_from("<HHH", data, 0)
    if icon_type != 1:
        raise ValueError("not an icon container")
    entries: list[tuple[int, int, bytes]] = []
    for index in range(count):
        width, height, _cc, _res, _planes, _bpp, size, offset = struct.unpack_from(
            "<BBBBHHII", data, 6 + index * 16
        )
        if offset + size > len(data):
            raise ValueError(f"entry {index} payload out of bounds")
        entries.append((width or 256, height or 256, data[offset : offset + size]))
    return entries


def rebuild_upscaled_layers(root: Path = GENERATED_DIR) -> None:
    """从 64px 母版等比重建 128/256 两层；其余层不触碰。"""
    master = Image.open(layer_path(root, MASTER_SIZE)).convert("RGBA")
    if master.size != (MASTER_SIZE, MASTER_SIZE):
        raise ValueError(f"master layer must be {MASTER_SIZE}x{MASTER_SIZE}")
    for size in UPSCALED_SIZES:
        master.resize((size, size), Image.Resampling.LANCZOS).save(
            layer_path(root, size), format="PNG"
        )


def rebuild_ico(root: Path = GENERATED_DIR) -> None:
    """装配 7 层 ICO，载荷为各层 PNG 原始字节（小层保持字节一致）。"""
    directory = struct.pack("<HHH", 0, 1, len(LAYER_SIZES))
    offset = 6 + 16 * len(LAYER_SIZES)
    payload = bytearray()
    for size in LAYER_SIZES:
        data = layer_path(root, size).read_bytes()
        dimension = 0 if size == 256 else size
        directory += struct.pack(
            "<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(data), offset
        )
        payload += data
        offset += len(data)
    ico_path(root).write_bytes(directory + bytes(payload))


def check(root: Path = GENERATED_DIR) -> list[str]:
    """语义校验全部生成资产，返回问题清单（空列表即通过）。"""
    issues: list[str] = []
    for size in LAYER_SIZES:
        label = f"vibeocr-{size}.png"
        try:
            with Image.open(layer_path(root, size)) as image:
                issues.extend(layer_issues(label, image, size))
        except OSError as error:
            issues.append(f"{label}: unreadable ({error})")

    try:
        entries = parse_ico_entries(ico_path(root).read_bytes())
    except (OSError, ValueError, struct.error) as error:
        issues.append(f"vibeocr.ico: {error}")
        return issues

    if [(width, height) for width, height, _ in entries] != [
        (size, size) for size in LAYER_SIZES
    ]:
        issues.append(f"vibeocr.ico: expected layers {LAYER_SIZES}")
    for width, height, payload in entries:
        label = f"vibeocr.ico[{width}x{height}]"
        try:
            with Image.open(io.BytesIO(payload)) as image:
                issues.extend(layer_issues(label, image, width))
        except OSError as error:
            issues.append(f"{label}: payload undecodable ({error})")
    return issues


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="rebuild and check brand assets")
    parser.add_argument(
        "--check", action="store_true", help="validate only, do not regenerate"
    )
    args = parser.parse_args(argv)

    if not args.check:
        rebuild_upscaled_layers()
        rebuild_ico()

    issues = check()
    if issues:
        print("semantic check FAILED:")
        for issue in issues:
            print(f"  - {issue}")
        return 1
    print("semantic check passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
