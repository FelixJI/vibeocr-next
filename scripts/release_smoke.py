"""验证 Next Velopack Portable 与单产品 identity 资产。"""

from __future__ import annotations

import hashlib
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


def _digest(path: Path, algorithm: str) -> str:
    digest = hashlib.new(algorithm)
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def _verify_velopack_feed(artifacts: Path, version: str) -> tuple[Path, Path | None]:
    full = artifacts / f"VibeOCRNext-{version}-full.nupkg"
    delta = artifacts / f"VibeOCRNext-{version}-delta.nupkg"
    document = json.loads((artifacts / "releases.win.json").read_text(encoding="utf-8"))
    assets = document.get("Assets") if isinstance(document, dict) else None
    if not isinstance(assets, list) or not all(
        isinstance(asset, dict) for asset in assets
    ):
        raise ValueError("Velopack feed assets are invalid")
    current = [
        asset
        for asset in assets
        if asset.get("PackageId") == "VibeOCRNext" and asset.get("Version") == version
    ]
    expected_paths = {"Full": full}
    if delta.is_file():
        expected_paths["Delta"] = delta
    if {asset.get("Type") for asset in current} != set(expected_paths) or len(
        current
    ) != len(expected_paths):
        raise ValueError("Velopack feed must bind the current full and optional delta")
    for asset in current:
        path = expected_paths[asset["Type"]]
        expected = {
            "FileName": path.name,
            "SHA1": _digest(path, "sha1"),
            "SHA256": _digest(path, "sha256"),
            "Size": path.stat().st_size,
        }
        if any(asset.get(field) != value for field, value in expected.items()):
            raise ValueError(f"Velopack feed does not bind {asset['Type']} package")
    historical = [asset for asset in assets if asset not in current]
    if historical:
        raise ValueError("published Velopack feed must not contain historical assets")
    return full, delta if delta.is_file() else None


def _verify_runtime_code(product_root: Path, version: str) -> None:
    code_root = product_root / "runtime/backend/runtime-code"
    script = (
        "import importlib.metadata,sys\n"
        "from pathlib import Path\n"
        "root=Path(sys.argv[1]).resolve()\n"
        "sys.path.insert(0,str(root))\n"
        "import vibeocr.runtime.host.main as host\n"
        "import vibeocr.runtime.environments.env_config as config\n"
        "import vibeocr.runtime_contracts as contracts\n"
        "assert all(Path(module.__file__).resolve().is_relative_to(root) "
        "for module in (host,config,contracts))\n"
        "assert Path(config.__file__).with_name('dependency_profiles.json').is_file()\n"
        "assert importlib.metadata.version('vibeocr-next-runtime')==sys.argv[2]\n"
    )
    subprocess.run(
        [sys.executable, "-I", "-B", "-c", script, str(code_root), version],
        check=True,
        timeout=30,
        cwd=product_root,
    )


def verify(artifacts: Path, version: str) -> None:
    portable_name = f"VibeOCRNext-v{version}-win-x64.zip"
    names = verify_release_assets(
        artifacts,
        required=(
            portable_name,
            "releases.win.json",
            "product-identity.json",
            "SBOM.spdx.json",
        ),
        require_one=("VibeOCRNext-*-full.nupkg",),
        require_index=False,
    )
    identity = json.loads(
        (artifacts / "product-identity.json").read_text(encoding="utf-8")
    )
    project = identity.get("project", {})
    if (
        project.get("component") != "next"
        or project.get("repository") != "FelixJI/vibeocr-next"
        or project.get("version") != version
        or len(str(project.get("source_sha", ""))) != 40
    ):
        raise ValueError("Next product identity does not bind this candidate")
    full, delta = _verify_velopack_feed(artifacts, version)
    expected_names = {
        full.name,
        portable_name,
        "releases.win.json",
        "product-identity.json",
        "SBOM.spdx.json",
    }
    if delta is not None:
        expected_names.add(delta.name)
    if set(names) != expected_names:
        raise ValueError(
            "release asset set mismatch; "
            f"expected={sorted(expected_names)}, actual={sorted(names)}"
        )
    root = Path(os.environ.get("AUTOMATION_PROJECT_ROOT", Path(__file__).parents[1]))
    with tempfile.TemporaryDirectory(prefix="vibeocr-web-smoke-") as temporary:
        extracted = Path(temporary)
        product_root = _extract_product(artifacts / portable_name, extracted)
        embedded_identity = json.loads(
            (product_root / "app/metadata/component-identities.json").read_text(
                encoding="utf-8"
            )
        )
        if embedded_identity != identity:
            raise ValueError("Portable product identity differs from release asset")
        _verify_runtime_code(product_root, version)
        subprocess.run(
            [
                str(product_root / "VibeOCR.exe"),
                "--self-test-prerequisites",
            ],
            check=True,
            timeout=30,
            cwd=product_root,
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


if __name__ == "__main__":
    try:
        verify(
            Path(os.environ["AUTOMATION_ARTIFACTS_DIR"]).resolve(),
            os.environ["AUTOMATION_VERSION"],
        )
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
