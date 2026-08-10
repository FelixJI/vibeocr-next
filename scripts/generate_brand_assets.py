"""Generate Windows raster icons from the single VibeOCR SVG source."""

from __future__ import annotations

import argparse
import struct
import subprocess
import tempfile
from pathlib import Path

SIZES = (16, 24, 32, 48, 64, 128, 256)


def _edge() -> Path:
    candidates = (
        Path(r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"),
        Path(r"C:\Program Files\Microsoft\Edge\Application\msedge.exe"),
    )
    for candidate in candidates:
        if candidate.is_file():
            return candidate
    raise FileNotFoundError("Microsoft Edge is required to generate brand assets")


def _write_ico(pngs: list[tuple[int, bytes]], destination: Path) -> None:
    header = struct.pack("<HHH", 0, 1, len(pngs))
    offset = 6 + 16 * len(pngs)
    directory = bytearray()
    payload = bytearray()
    for size, content in pngs:
        encoded_size = 0 if size == 256 else size
        directory.extend(
            struct.pack(
                "<BBBBHHII",
                encoded_size,
                encoded_size,
                0,
                0,
                1,
                32,
                len(content),
                offset,
            )
        )
        payload.extend(content)
        offset += len(content)
    destination.write_bytes(header + directory + payload)


def _png_dimensions(content: bytes) -> tuple[int, int]:
    if content[:8] != b"\x89PNG\r\n\x1a\n" or content[12:16] != b"IHDR":
        raise ValueError("Edge did not produce a PNG brand asset")
    return struct.unpack(">II", content[16:24])


def _generate(source: Path, destination: Path) -> None:
    destination.mkdir(parents=True, exist_ok=True)
    pngs: list[tuple[int, bytes]] = []
    with tempfile.TemporaryDirectory(prefix="vibeocr-brand-") as temporary:
        workspace = Path(temporary)
        html = workspace / "render.html"
        html.write_text(
            "<!doctype html><style>html,body,img{width:100%;height:100%;margin:0}"
            "img{display:block}</style>"
            f'<img src="{source.as_uri()}">',
            encoding="utf-8",
        )
        for size in SIZES:
            output = destination / f"vibeocr-{size}.png"
            subprocess.run(
                [
                    str(_edge()),
                    "--headless=new",
                    "--disable-gpu",
                    "--hide-scrollbars",
                    "--no-first-run",
                    "--force-device-scale-factor=1",
                    "--default-background-color=00000000",
                    f"--user-data-dir={workspace / 'profile'}",
                    f"--window-size={size},{size}",
                    f"--screenshot={output}",
                    html.as_uri(),
                ],
                check=True,
                capture_output=True,
            )
            content = output.read_bytes()
            if _png_dimensions(content) != (size, size):
                raise ValueError(f"Brand asset has the wrong dimensions: {output}")
            pngs.append((size, content))
    _write_ico(pngs, destination / "vibeocr.ico")


def _check(expected: Path, actual: Path) -> None:
    filenames = [*(f"vibeocr-{size}.png" for size in SIZES), "vibeocr.ico"]
    mismatches = [
        filename
        for filename in filenames
        if not (expected / filename).is_file()
        or (expected / filename).read_bytes() != (actual / filename).read_bytes()
    ]
    if mismatches:
        joined = ", ".join(mismatches)
        raise ValueError(f"Generated brand assets are stale: {joined}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    source = root / "assets" / "brand" / "vibeocr.svg"
    generated = source.parent / "generated"
    if args.check:
        with tempfile.TemporaryDirectory(prefix="vibeocr-brand-check-") as temporary:
            actual = Path(temporary)
            _generate(source, actual)
            _check(generated, actual)
        return
    _generate(source, generated)


if __name__ == "__main__":
    main()
