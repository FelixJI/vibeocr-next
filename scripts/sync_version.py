"""同步 Next 的派生版本载体；repository.json 是唯一版本来源。"""

from __future__ import annotations

import argparse
import json
import os
import re
from pathlib import Path


def sync_version(root: Path, version: str) -> None:
    repository = root / "repository.json"
    data = json.loads(repository.read_text(encoding="utf-8"))
    data["version"] = version
    repository.write_text(
        json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    project = root / "src/dotnet/VibeOCR.App/VibeOCR.App.csproj"
    text, count = re.subn(
        r"<Version>[^<]+</Version>",
        f"<Version>{version}</Version>",
        project.read_text(encoding="utf-8"),
        count=1,
    )
    if count != 1:
        raise ValueError("missing App Version")
    project.write_text(text, encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True)
    args = parser.parse_args()
    sync_version(
        Path(
            os.environ.get("AUTOMATION_PROJECT_ROOT", Path(__file__).parents[1])
        ).resolve(),
        args.version,
    )
