"""Release-pinned external assets shared by engine environments."""

from __future__ import annotations

from collections.abc import Callable
from pathlib import Path
from urllib.request import urlopen

from vibeocr.runtime.environments.runtime_manifest import sha256_file


def pinned_download(
    url: str, destination: Path, digest: str, check_cancelled: Callable[[], None]
) -> Path:
    check_cancelled()
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.is_file() and sha256_file(destination) == digest:
        return destination
    partial = destination.with_suffix(destination.suffix + ".partial")
    try:
        with urlopen(url, timeout=30) as response, partial.open("wb") as output:
            while chunk := response.read(1024 * 1024):
                check_cancelled()
                output.write(chunk)
        check_cancelled()
        if sha256_file(partial) != digest:
            raise ValueError(f"external ONNX asset digest mismatch: {destination.name}")
        partial.replace(destination)
    finally:
        partial.unlink(missing_ok=True)
    return destination
