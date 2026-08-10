"""验证发布 WebAssets 的离线资源闭包与内容安全策略。"""

from __future__ import annotations

import argparse
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit


class _DocumentResources(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self.resources: list[str] = []
        self.csp: str | None = None

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        values = {name.lower(): value for name, value in attrs}
        if tag in {"script", "link"}:
            reference = values.get("src") if tag == "script" else values.get("href")
            if reference is not None:
                self.resources.append(reference)
        if tag == "meta" and values.get("http-equiv", "").lower() == (
            "content-security-policy"
        ):
            self.csp = values.get("content")


def _verify_strict_csp(csp: str | None) -> None:
    if not csp:
        raise ValueError("WebAssets index.html is missing a Content-Security-Policy")
    normalized = csp.lower()
    if "default-src 'none'" not in normalized:
        raise ValueError("WebAssets CSP must set default-src 'none'")
    if "unsafe-inline" in normalized or "unsafe-eval" in normalized:
        raise ValueError("WebAssets CSP must not allow unsafe-inline or unsafe-eval")


def _verify_local_resources(root: Path, index: Path, references: list[str]) -> None:
    for reference in references:
        parsed = urlsplit(reference)
        if parsed.scheme in {"http", "https"} or parsed.netloc:
            raise ValueError(f"WebAssets contains an external resource: {reference}")
        if parsed.scheme or Path(parsed.path).is_absolute() or not parsed.path:
            raise ValueError(
                f"WebAssets resource is not a local relative file: {reference}"
            )
        candidate = (index.parent / unquote(parsed.path)).resolve()
        try:
            candidate.relative_to(root)
        except ValueError as error:
            raise ValueError(
                f"WebAssets resource escapes the dist directory: {reference}"
            ) from error
        if not candidate.is_file():
            raise ValueError(f"WebAssets resource is missing: {reference}")


def _verify_no_forbidden_files(root: Path) -> None:
    for path in root.rglob("*"):
        relative_parts = path.relative_to(root).parts
        if "node_modules" in relative_parts:
            raise ValueError("WebAssets dist must not contain node_modules")
        if path.is_file() and path.suffix.lower() in {".map", ".ts", ".tsx"}:
            raise ValueError(
                f"WebAssets dist contains forbidden source file: {path.name}"
            )
        if path.is_file() and path.suffix.lower() in {".css", ".js"}:
            contents = path.read_text(encoding="utf-8")
            if "sourcemappingurl=" in contents.lower():
                raise ValueError(
                    f"WebAssets dist contains a source map reference: {path.name}"
                )


def verify_web_assets(dist: Path) -> None:
    """Fail closed unless ``dist`` is a strict, complete offline Web bundle."""
    root = dist.resolve()
    index = root / "index.html"
    if not index.is_file():
        raise ValueError("WebAssets dist/index.html is required")

    document = _DocumentResources()
    document.feed(index.read_text(encoding="utf-8"))
    document.close()
    _verify_strict_csp(document.csp)
    _verify_local_resources(root, index, document.resources)
    _verify_no_forbidden_files(root)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="验证 VibeOCR WebAssets 的离线生产闭包。"
    )
    parser.add_argument("dist", type=Path)
    args = parser.parse_args()
    verify_web_assets(args.dist)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
