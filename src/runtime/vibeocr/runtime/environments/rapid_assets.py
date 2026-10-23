"""Release-pinned external RapidTable model and ONNX Runtime CUDA plugin."""

from __future__ import annotations

import shutil
import zipfile
from collections.abc import Callable
from pathlib import Path
from urllib.request import urlopen

from vibeocr.runtime.environments.runtime_manifest import sha256_file

RAPIDTABLE_MODEL = "slanet-plus.onnx"
RAPIDTABLE_MODEL_URL = (
    "https://www.modelscope.cn/models/RapidAI/RapidTable/resolve/v2.0.0/"
    + RAPIDTABLE_MODEL
)
# RapidTable 3.0.2 default_models.yaml is the model digest authority.
RAPIDTABLE_MODEL_SHA256 = (
    "d57a942af6a2f57d6a4a0372573c696a2379bf5857c45e2ac69993f3b334514b"
)
CUDA_PLUGIN_ARCHIVE = "cuda_ep_cuda13_0.2.0_win-x64.zip"
CUDA_PLUGIN_URL = (
    "https://github.com/microsoft/onnxruntime/releases/download/"
    "plugin-ep-cuda/v0.2.0/" + CUDA_PLUGIN_ARCHIVE
)
# Microsoft's GitHub Release asset digest binds the ORT 1.30 CUDA 13 plugin.
CUDA_PLUGIN_SHA256 = "e204c362a151e157cc56e047c76a3de54dd12c9a55d6ba3b1068686f4b47a489"
CUDA_PLUGIN_DLL = "onnxruntime_providers_cuda.dll"


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
            raise ValueError(
                f"external Rapid asset digest mismatch: {destination.name}"
            )
        partial.replace(destination)
    finally:
        partial.unlink(missing_ok=True)
    return destination


def install_cuda_plugin(
    runtime_root: Path, cache: Path, check_cancelled: Callable[[], None]
) -> None:
    archive_path = pinned_download(
        CUDA_PLUGIN_URL,
        cache / CUDA_PLUGIN_ARCHIVE,
        CUDA_PLUGIN_SHA256,
        check_cancelled,
    )
    target = runtime_root / "providers" / "cuda"
    target.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive_path) as archive:
        if sorted(archive.namelist()) != [CUDA_PLUGIN_DLL, "version.json"]:
            raise ValueError("CUDA plugin archive contains unexpected members")
        for name in (CUDA_PLUGIN_DLL, "version.json"):
            check_cancelled()
            with archive.open(name) as source, (target / name).open("wb") as output:
                shutil.copyfileobj(source, output)
    check_cancelled()
