"""Mutable state root used by recognition services and PDF sidecars."""

import os
from pathlib import Path

from vibeocr.runtime.environments.runtime_layout import resolve_app_paths


def get_project_root() -> Path:
    if os.environ.get("VIBEOCR_PRODUCT_RUNTIME") == "1":
        return Path(os.environ["VIBEOCR_RUNTIME_STATE_ROOT"])
    return resolve_app_paths().install_root
