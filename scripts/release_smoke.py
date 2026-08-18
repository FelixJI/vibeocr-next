"""验证 Next Velopack Portable 与 Backend/Protocol identity 资产。"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

try:
    from scripts.verify_release_assets import verify_release_assets
except ModuleNotFoundError:  # 直接执行 scripts/release_smoke.py
    from verify_release_assets import verify_release_assets


def _extract_product(archive: Path, destination: Path) -> Path:
    root = destination.resolve()
    with zipfile.ZipFile(archive) as package:
        for member in package.infolist():
            target = (root / member.filename).resolve()
            try:
                target.relative_to(root)
            except ValueError as error:
                raise ValueError(
                    f"release archive entry escapes the product root: {member.filename}"
                ) from error
            package.extract(member, root)
    roots = [path for path in root.iterdir() if path.is_dir()]
    return roots[0] if len(roots) == 1 else root


def verify(artifacts: Path) -> None:
    names = verify_release_assets(
        artifacts,
        required=(
            "VibeOCRNext-Portable.zip",
            "releases.win.json",
            "component-lock.json",
            "component-identities.json",
            "SBOM.spdx.json",
        ),
        require_one=("VibeOCRNext-*-full.nupkg",),
        require_index=False,
    )
    identity = json.loads(
        (artifacts / "component-identities.json").read_text(encoding="utf-8")
    )
    for component in ("backend", "protocol", "protocol_sdk"):
        record = identity.get(component, {})
        if not record.get("version") or len(str(record.get("source_sha", ""))) != 40:
            raise ValueError(f"missing actual {component} version/source identity")
    for component in ("protocol", "protocol_sdk"):
        record = identity[component]
        if not record.get("release_manifest_sha256"):
            raise ValueError(f"missing actual {component} release manifest identity")
    full = next(artifacts.glob("VibeOCRNext-*-full.nupkg"))
    expected_names = {
        full.name,
        "VibeOCRNext-Portable.zip",
        "releases.win.json",
        "component-lock.json",
        "component-identities.json",
        "SBOM.spdx.json",
    }
    if set(names) != expected_names:
        raise ValueError(
            "release asset set mismatch; "
            f"expected={sorted(expected_names)}, actual={sorted(names)}"
        )
    root = Path(os.environ.get("AUTOMATION_PROJECT_ROOT", Path(__file__).parents[1]))
    with tempfile.TemporaryDirectory(prefix="vibeocr-web-smoke-") as temporary:
        extracted = Path(temporary)
        product_root = _extract_product(
            artifacts / "VibeOCRNext-Portable.zip", extracted
        )
        subprocess.run(
            [
                "pwsh",
                "-File",
                str(root / "scripts/smoke_web_workbench.ps1"),
                "-ProductRoot",
                str(product_root),
            ],
            check=True,
            timeout=120,
        )
    if "component-lock.json" not in names:
        raise ValueError("component lock missing from release closure")


if __name__ == "__main__":
    try:
        verify(Path(os.environ["AUTOMATION_ARTIFACTS_DIR"]).resolve())
    except (
        KeyError,
        OSError,
        ValueError,
        json.JSONDecodeError,
        subprocess.CalledProcessError,
        subprocess.TimeoutExpired,
    ) as exc:
        print(f"::error::{exc}", file=sys.stderr)
        raise SystemExit(1)
