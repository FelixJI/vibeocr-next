"""Generate fixed independent QR/barcode decode fixtures for #213.

All QR samples come from the Python ``qrcode`` library and all 1D barcodes
from ``python-barcode`` (rendered to PNG with Pillow) — never from ZXing, so
the C# decoder tests cannot be satisfied by self-encoding/self-decoding with
the library under test. Composition (multi-code canvas, rotation) uses Pillow
only. The script is deterministic: fixed payloads and geometry; re-running
produces equivalent pixels.

Usage (locked uv environment only):

    uv run --frozen --no-sync python scripts/generate_code_decode_fixtures.py

Output: tests/dotnet/VibeOCR.App.Tests/fixtures/codes/*.png + manifest.json
"""

from __future__ import annotations

import json
import pathlib
import sys
from importlib.metadata import version as _dist_version

import barcode as python_barcode
import qrcode
from barcode.writer import ImageWriter
from PIL import Image

REPO_ROOT = pathlib.Path(__file__).resolve().parents[1]
OUTPUT_ROOT = (
    REPO_ROOT / "tests" / "dotnet" / "VibeOCR.App.Tests" / "fixtures" / "codes"
)
TMP_PREFIX = ".fixture-tmp-"

# (file name, payload, note) for qrcode[pil] samples.
QR_SAMPLES = [
    (
        "qr_v1_unicode.png",
        "中文🙂",
        "V1 QR (10 UTF-8 bytes, ECC M) — the pinned ZXing.Net 0.16.11 detector gap payload",
    ),
    (
        "qr_url_https.png",
        "https://example.com/independent",
        "strict http(s) URL classification",
    ),
    (
        "qr_not_url.png",
        "javascript:alert(1)",
        "non-http scheme must stay non-openable",
    ),
]

# (file name, provider, payload, note) for python-barcode 1D samples.
# python-barcode 0.16 provides no code93/UPC-E/DataBar provider; those stay
# uncovered here (documented gap — no sample is invented with ZXing).
ONE_D_SAMPLES = [
    ("code128_independent.png", "code128", "INDEP-128", "independent Code 128"),
    (
        "ean13_independent.png",
        "ean13",
        "5901234123457",
        "independent EAN-13 (checksum digit included)",
    ),
    (
        "code39_independent.png",
        "code39",
        "INDEP-39",
        "independent Code 39; the writer appends its checksum char C, so the physical payload is INDEP-39C (confirmed identically by pyzbar)",
    ),
    (
        "ean8_independent.png",
        "ean8",
        "96385074",
        "independent EAN-8 (checksum digit included)",
    ),
    (
        "upca_independent.png",
        "upca",
        "036000291452",
        "independent UPC-A; the pyzbar baseline surfaces it as EAN-13 0036000291452 (leading zero)",
    ),
    ("itf_independent.png", "itf", "1234567890", "independent Interleaved 2 of 5"),
    (
        "codabar_independent.png",
        "codabar",
        "A1234567B",
        "independent Codabar (NW-7); the pinned ZXing reader strips the A/B start/stop glyphs and returns 1234567 (pyzbar keeps A1234567B)",
    ),
]


def make_qr(payload: str) -> Image.Image:
    qr = qrcode.QRCode(
        error_correction=qrcode.constants.ERROR_CORRECT_M,
        box_size=10,
        border=4,
    )
    qr.add_data(payload)
    qr.make(fit=True)
    return qr.make_image(fill_color="black", back_color="white").convert("RGB")


def make_one_d(provider: str, payload: str) -> Image.Image:
    cls = python_barcode.get_barcode_class(provider)
    tmp = OUTPUT_ROOT / (TMP_PREFIX + provider)
    path = cls(payload, writer=ImageWriter()).save(str(tmp))
    with Image.open(path) as image:
        rgb = image.convert("RGB")
        rgb.load()
    pathlib.Path(path).unlink()
    return rgb


def compose_multi(sources: list[Image.Image]) -> Image.Image:
    canvas = Image.new("RGB", (1280, 520), "white")
    x = 60
    for part in sources:
        part.thumbnail((560, 460))
        canvas.paste(part, (x, (520 - part.height) // 2))
        x += part.width + 80
    return canvas


def compose_orientation_mixed(
    upright: Image.Image, rotated: Image.Image
) -> Image.Image:
    """Same-image mixed-orientation composite: one upright 1D code and one
    1D code rotated 90° CCW. zbar scans both orientations in a single decode
    (measured with pyzbar on these pixels), so this is the #213 multi-code
    orientation contract the bounded pass sequence must accumulate across."""
    canvas = Image.new("RGB", (1400, 700), "white")
    canvas.paste(upright, (100, 140))
    canvas.paste(rotated.rotate(90, expand=True, fillcolor="white"), (900, 60))
    return canvas


def main() -> int:
    OUTPUT_ROOT.mkdir(parents=True, exist_ok=True)
    manifest: dict[str, object] = {
        "purpose": (
            "Independent #213 decode fixtures: qrcode[pil] + python-barcode, "
            "composed with Pillow"
        ),
        "generators": {
            "qrcode": _dist_version("qrcode"),
            "python-barcode": _dist_version("python-barcode"),
        },
        "samples": [],
    }
    samples: list[dict[str, str]] = []

    for name, payload, note in QR_SAMPLES:
        image = make_qr(payload)
        image.save(OUTPUT_ROOT / name, format="PNG")
        version = "V1" if payload == "中文🙂" else "auto-fit"
        samples.append(
            {
                "file": name,
                "payload": payload,
                "generator": "qrcode[pil]",
                "variant": f"{version} ECC M box=10 border=4",
                "note": note,
            }
        )

    code128 = None
    for name, provider, payload, note in ONE_D_SAMPLES:
        image = make_one_d(provider, payload)
        image.save(OUTPUT_ROOT / name, format="PNG")
        if provider == "code128":
            code128 = image
        samples.append(
            {
                "file": name,
                "payload": payload,
                "generator": "python-barcode ImageWriter",
                "variant": provider,
                "note": note,
            }
        )

    rotated = make_qr("rotated-independent-42").rotate(
        90, expand=True, fillcolor="white"
    )
    rotated.save(OUTPUT_ROOT / "qr_rotated_90.png", format="PNG")
    samples.append(
        {
            "file": "qr_rotated_90.png",
            "payload": "rotated-independent-42",
            "generator": "qrcode[pil] + Pillow rotate(90)",
            "variant": "CCW 90 degrees",
            "note": "bounded rotation pass coverage",
        }
    )

    multi = compose_multi([make_qr("multi-qrcode-independent"), code128])
    multi.save(OUTPUT_ROOT / "multi_composite.png", format="PNG")
    samples.append(
        {
            "file": "multi_composite.png",
            "payload": "multi-qrcode-independent|INDEP-128",
            "generator": "qrcode[pil] + python-barcode, Pillow canvas",
            "variant": "two codes side by side",
            "note": "DecodeMultiple coverage",
        }
    )

    orientation = compose_orientation_mixed(
        make_one_d("code128", "MIX-ORIENT-128"),
        make_one_d("code39", "MIX-ORIENT-39"),
    )
    orientation.save(OUTPUT_ROOT / "multi_orientation_composite.png", format="PNG")
    samples.append(
        {
            "file": "multi_orientation_composite.png",
            "payload": "MIX-ORIENT-128|MIX-ORIENT-39",
            "generator": "python-barcode ImageWriter, Pillow canvas + rotate(90)",
            "variant": (
                "upright Code 128 (left) + 90-degree CCW Code 39 (right); the "
                "physical Code 39 payload gains the writer checksum character"
            ),
            "note": (
                "mixed-orientation same-image contract: pyzbar (zbar) scans both "
                "orientations and returns both codes, so the bounded pass "
                "sequence must keep accumulating after the first hit"
            ),
        }
    )

    blank = Image.new("RGB", (640, 320), "white")
    blank.save(OUTPUT_ROOT / "blank.png", format="PNG")
    samples.append(
        {
            "file": "blank.png",
            "payload": "",
            "generator": "Pillow",
            "variant": "plain white",
            "note": "no-code image yields zero results",
        }
    )

    manifest["samples"] = samples
    (OUTPUT_ROOT / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    leftover = sorted(p.name for p in OUTPUT_ROOT.glob(TMP_PREFIX + "*"))
    if leftover:
        raise SystemExit(f"temporary files were not cleaned up: {leftover}")
    print(f"wrote {len(samples)} fixtures to {OUTPUT_ROOT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
