from __future__ import annotations

from pathlib import Path

import pytest

from scripts.verify_web_assets import verify_web_assets


def write_dist(tmp_path: Path, html: str) -> Path:
    dist = tmp_path / "dist"
    assets = dist / "assets"
    assets.mkdir(parents=True)
    (dist / "index.html").write_text(html, encoding="utf-8")
    (assets / "app.js").write_text("export {};", encoding="utf-8")
    (assets / "app.css").write_text("body {}", encoding="utf-8")
    return dist


def test_accepts_strict_offline_web_asset_closure(tmp_path: Path) -> None:
    dist = write_dist(
        tmp_path,
        """<!doctype html><html><head>
        <meta http-equiv="Content-Security-Policy"
              content="default-src 'none'; script-src 'self'; style-src 'self'">
        <link rel="stylesheet" href="assets/app.css">
        </head><body><script type="module" src="assets/app.js"></script></body></html>""",
    )

    verify_web_assets(dist)


def test_rejects_inline_source_map_comment(tmp_path: Path) -> None:
    dist = write_dist(
        tmp_path,
        """<!doctype html><html><head>
        <meta http-equiv="Content-Security-Policy"
              content="default-src 'none'; script-src 'self'; style-src 'self'">
        <link rel="stylesheet" href="assets/app.css">
        </head><body><script type="module" src="assets/app.js"></script></body></html>""",
    )
    (dist / "assets" / "app.js").write_text(
        "//# sourceMappingURL=data:application/json;base64,AAAA", encoding="utf-8"
    )

    with pytest.raises(ValueError, match="source map"):
        verify_web_assets(dist)


def test_requires_dist_index_html(tmp_path: Path) -> None:
    with pytest.raises(ValueError, match="dist/index.html"):
        verify_web_assets(tmp_path / "dist")


def test_rejects_external_resource(tmp_path: Path) -> None:
    dist = write_dist(
        tmp_path,
        """<!doctype html><meta http-equiv="Content-Security-Policy"
        content="default-src 'none'; script-src 'self'">
        <script src="https://cdn.example.test/app.js"></script>""",
    )

    with pytest.raises(ValueError, match="external resource"):
        verify_web_assets(dist)


def test_requires_referenced_local_resource_to_exist(tmp_path: Path) -> None:
    dist = write_dist(
        tmp_path,
        """<!doctype html><meta http-equiv="Content-Security-Policy"
        content="default-src 'none'; script-src 'self'">
        <script src="assets/missing.js"></script>""",
    )

    with pytest.raises(ValueError, match="resource is missing"):
        verify_web_assets(dist)


@pytest.mark.parametrize(
    "csp",
    [
        "script-src 'self'",
        "default-src 'none'; script-src 'unsafe-eval'",
        "default-src 'none'; style-src 'unsafe-inline'",
    ],
)
def test_rejects_missing_or_unsafe_csp(tmp_path: Path, csp: str) -> None:
    dist = write_dist(
        tmp_path,
        f"""<!doctype html><meta http-equiv="Content-Security-Policy"
        content="{csp}"><script src="assets/app.js"></script>""",
    )

    with pytest.raises(ValueError, match="CSP"):
        verify_web_assets(dist)


@pytest.mark.parametrize(
    "forbidden_path, message",
    [
        ("assets/app.js.map", "forbidden source file"),
        ("assets/source.ts", "forbidden source file"),
        ("assets/source.tsx", "forbidden source file"),
        ("node_modules/library/index.js", "node_modules"),
    ],
)
def test_rejects_nonproduction_files(
    tmp_path: Path, forbidden_path: str, message: str
) -> None:
    dist = write_dist(
        tmp_path,
        """<!doctype html><meta http-equiv="Content-Security-Policy"
        content="default-src 'none'; script-src 'self'">
        <script src="assets/app.js"></script>""",
    )
    forbidden = dist / forbidden_path
    forbidden.parent.mkdir(parents=True, exist_ok=True)
    forbidden.write_text("ignored", encoding="utf-8")

    with pytest.raises(ValueError, match=message):
        verify_web_assets(dist)
