#!/usr/bin/env python3
"""Collect real T0/T3/T6 cold-start metrics from a published Next executable."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import tempfile
from pathlib import Path


def _percentile(values: list[float], percentile: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    index = max(0, round(percentile / 100.0 * (len(ordered) - 1)))
    return ordered[index]


def _directory_size(path: Path) -> int:
    total = 0
    for child in path.rglob("*"):
        if child.is_file():
            try:
                total += child.stat().st_size
            except OSError:
                pass
    return total


def _read_trace(path: Path) -> tuple[float, float]:
    if not path.is_file():
        raise ValueError("startup trace was not created")
    lines = [
        line
        for line in path.read_text(encoding="utf-8-sig").splitlines()
        if line.strip()
    ]
    if not lines:
        raise ValueError("startup trace is empty")
    data = json.loads(lines[-1])
    try:
        t0 = float(data["T0"])
        t3 = float(data["T3"])
        t6 = float(data["T6"])
    except (KeyError, TypeError, ValueError) as error:
        raise ValueError("startup trace must contain numeric T0, T3 and T6") from error
    if not t0 <= t3 <= t6:
        raise ValueError(
            f"startup milestones are not monotonic: T0={t0}, T3={t3}, T6={t6}"
        )
    return (t3 - t0) * 1000.0, (t6 - t0) * 1000.0


def collect(
    target: Path,
    runs: int,
    *,
    timeout_seconds: float,
    zip_bytes: int,
) -> dict[str, object]:
    if runs < 1:
        raise ValueError("runs must be at least 1")
    target = target.resolve(strict=True)
    if target.suffix.lower() != ".exe":
        raise ValueError("target must be a published Next executable")

    environment = os.environ.copy()
    environment["VIBEOCR_SELF_TEST_SMOKE"] = "t6"
    t0_t3_samples: list[float] = []
    t0_t6_samples: list[float] = []

    for index in range(runs):
        with tempfile.TemporaryDirectory(prefix="vibeocr-next-startup-") as temp:
            trace = Path(temp) / "trace.jsonl"
            environment["VIBEOCR_STARTUP_TRACE"] = str(trace)
            try:
                process = subprocess.run(
                    [str(target)],
                    cwd=target.parent,
                    env=environment,
                    timeout=timeout_seconds,
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    check=False,
                )
                if process.returncode != 0:
                    raise ValueError(f"process exited with {process.returncode}")
                t0_t3, t0_t6 = _read_trace(trace)
            except (
                OSError,
                subprocess.TimeoutExpired,
                ValueError,
                json.JSONDecodeError,
            ) as error:
                print(f"run {index + 1}: INVALID ({error})")
                continue
            t0_t3_samples.append(t0_t3)
            t0_t6_samples.append(t0_t6)
            print(f"run {index + 1}: T0-T3={t0_t3:.0f} ms, T0-T6={t0_t6:.0f} ms")

    return {
        "name": "winui",
        "fingerprint": (
            f"{os.environ.get('COMPUTERNAME', 'host')}|"
            f"{os.environ.get('PROCESSOR_ARCHITECTURE', 'x64')}"
        ),
        "samples": len(t0_t3_samples),
        "zip_bytes": zip_bytes,
        "unzipped_bytes": _directory_size(target.parent),
        "t0_t3_p95_ms": round(_percentile(t0_t3_samples, 95), 1),
        "t0_t6_p95_ms": round(_percentile(t0_t6_samples, 95), 1),
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target", required=True, type=Path)
    parser.add_argument("--runs", type=int, default=30)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--zip-bytes", type=int, default=0)
    parser.add_argument("--timeout-seconds", type=float, default=120.0)
    args = parser.parse_args(argv)

    try:
        metrics = collect(
            args.target,
            args.runs,
            timeout_seconds=args.timeout_seconds,
            zip_bytes=args.zip_bytes,
        )
    except ValueError as error:
        parser.error(str(error))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(metrics, indent=2), encoding="utf-8")
    print(
        f"Wrote {args.output}: {metrics['samples']} samples, "
        f"T0-T3 p95={metrics['t0_t3_p95_ms']} ms"
    )
    return 0 if metrics["samples"] == args.runs else 2


if __name__ == "__main__":
    raise SystemExit(main())
