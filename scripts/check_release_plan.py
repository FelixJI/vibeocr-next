"""Reject a release plan carried forward by merging main into its PR."""

from __future__ import annotations

import argparse
import json
import subprocess
from pathlib import Path

PLAN = ".release/plan.json"


def verify(root: Path, *, event: str, source_sha: str, head_ref: str) -> None:
    def git(*args: str) -> str:
        return subprocess.run(
            ["git", *args], cwd=root, check=True, capture_output=True, text=True
        ).stdout.strip()

    if event == "pull_request" and head_ref != "automation/release":
        return
    # PR checkout is GitHub's test merge; push checkout is the squash commit.
    base = git("rev-parse", f"{source_sha}^1")
    if (
        event == "push"
        and PLAN
        not in git("diff", "--name-only", base, source_sha, "--", PLAN).splitlines()
    ):
        return
    plan = json.loads(git("show", f"{source_sha}:{PLAN}"))
    if plan.get("prepared_from_sha") != base:
        raise ValueError(
            "发布计划未基于本次 main 准备。请在 main 手动运行 CD，沿用原 bump "
            "刷新 automation/release；不要用合并 main 或手改 plan.json 同步发布 PR。"
        )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--event", required=True, choices=("pull_request", "push"))
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--head-ref", default="")
    args = parser.parse_args()
    verify(
        Path(__file__).resolve().parents[1],
        event=args.event,
        source_sha=args.source_sha,
        head_ref=args.head_ref,
    )


if __name__ == "__main__":
    main()
