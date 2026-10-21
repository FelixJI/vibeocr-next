from __future__ import annotations

import json
import subprocess
from pathlib import Path

import pytest

from scripts.check_release_plan import verify


@pytest.fixture
def repository(tmp_path: Path):
    def git(*args: str) -> str:
        return subprocess.run(
            ["git", *args], cwd=tmp_path, check=True, capture_output=True, text=True
        ).stdout.strip()

    git("init", "-b", "main")
    git("config", "user.name", "Test")
    git("config", "user.email", "test@example.invalid")
    git("config", "commit.gpgsign", "false")
    (tmp_path / "app.txt").write_text("initial", encoding="utf-8")
    git("add", ".")
    git("commit", "-m", "initial")
    return tmp_path, git


def prepare(root: Path, git) -> None:
    base = git("rev-parse", "HEAD")
    git("switch", "-c", "automation/release")
    (root / ".release").mkdir()
    (root / ".release/plan.json").write_text(
        json.dumps({"prepared_from_sha": base}), encoding="utf-8"
    )
    git("add", ".")
    git("commit", "-m", "prepare release")


@pytest.mark.parametrize("event", ["pull_request", "push"])
@pytest.mark.parametrize("advance_main", [False, True])
def test_release_test_merge_requires_fresh_preparation(repository, advance_main, event):
    root, git = repository
    prepare(root, git)
    git("switch", "main")
    if advance_main:
        (root / "app.txt").write_text("new change", encoding="utf-8")
        git("commit", "-am", "advance main")
        git("switch", "automation/release")
        git("merge", "--no-ff", "main", "-m", "update branch without prepare")
        git("switch", "main")
    if event == "push":
        git("merge", "--squash", "automation/release")
        git("commit", "-m", "release squash")
    else:
        git("merge", "--no-ff", "automation/release", "-m", "GitHub test merge")
    args = dict(
        event=event,
        source_sha=git("rev-parse", "HEAD"),
        head_ref="automation/release",
    )
    if advance_main:
        with pytest.raises(ValueError, match="刷新 automation/release"):
            verify(root, **args)
    else:
        verify(root, **args)


def test_squash_release_and_following_ordinary_push(repository):
    root, git = repository
    prepare(root, git)
    git("switch", "main")
    git("merge", "--squash", "automation/release")
    git("commit", "-m", "release squash")
    verify(root, event="push", source_sha=git("rev-parse", "HEAD"), head_ref="")
    (root / "app.txt").write_text("ordinary update", encoding="utf-8")
    git("commit", "-am", "ordinary update")
    verify(root, event="push", source_sha=git("rev-parse", "HEAD"), head_ref="")


def test_ordinary_pr_does_not_require_a_release_plan(repository):
    root, git = repository
    verify(
        root,
        event="pull_request",
        source_sha=git("rev-parse", "HEAD"),
        head_ref="codex/fix",
    )
