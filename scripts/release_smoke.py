"""验证 Next 真实桌面包及其 Backend/Protocol identity 资产。"""

from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

try:
    from scripts.verify_release_assets import verify_release_assets
except ModuleNotFoundError:  # 直接执行 scripts/release_smoke.py
    from verify_release_assets import verify_release_assets


def verify(artifacts: Path) -> None:
    names = verify_release_assets(
        artifacts,
        required=("component-lock.json", "component-identities.json", "SBOM.spdx.json"),
        require_one=("VibeOCR-Next-v*-win64.zip", "VibeOCR-Next-v*-win64.zip.sha256"),
        require_index=False,
    )
    identity = json.loads(
        (artifacts / "component-identities.json").read_text(encoding="utf-8")
    )
    for component in ("backend", "protocol"):
        record = identity.get(component, {})
        if not record.get("version") or len(str(record.get("source_sha", ""))) != 40:
            raise ValueError(f"missing actual {component} version/source identity")
    archive = next(artifacts.glob("VibeOCR-Next-v*-win64.zip"))
    expected_names = {
        archive.name,
        f"{archive.name}.sha256",
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
    subprocess.run(
        [
            "pwsh",
            "-File",
            str(root / "scripts/verify_winui_artifact.ps1"),
            "-Artifact",
            str(archive),
        ],
        check=True,
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
    ) as exc:
        print(f"::error::{exc}", file=sys.stderr)
        raise SystemExit(1)
