from __future__ import annotations

import json
from typing import TYPE_CHECKING

import pytest

from scripts.collect_startup_metrics import _read_trace

if TYPE_CHECKING:
    from pathlib import Path


def test_read_trace_returns_real_milestone_durations(tmp_path: Path) -> None:
    trace = tmp_path / "trace.jsonl"
    trace.write_text(
        json.dumps({"T0": 10.0, "T3": 10.125, "T6": 10.5}) + "\n",
        encoding="utf-8",
    )

    assert _read_trace(trace) == pytest.approx((125.0, 500.0))


def test_read_trace_rejects_non_monotonic_milestones(tmp_path: Path) -> None:
    trace = tmp_path / "trace.jsonl"
    trace.write_text(
        json.dumps({"T0": 10.0, "T3": 9.0, "T6": 11.0}) + "\n",
        encoding="utf-8",
    )

    with pytest.raises(ValueError, match="not monotonic"):
        _read_trace(trace)
