"""Run one CI command with a bounded Windows process-tree lifetime."""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys


def terminate_process_tree(process: subprocess.Popen[bytes]) -> None:
    if process.poll() is not None:
        return
    if os.name == "nt":
        try:
            subprocess.run(
                ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                check=False,
                timeout=30,
            )
        except subprocess.TimeoutExpired:
            pass
    else:
        process.terminate()
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    parser.add_argument("--label", required=True)
    parser.add_argument("--timeout-seconds", required=True, type=int)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    if args.timeout_seconds <= 0:
        raise SystemExit("--timeout-seconds must be positive")
    if not command:
        raise SystemExit("a command is required after --")
    command[0] = shutil.which(command[0]) or command[0]

    print(f"::notice title=CI command::{args.label} started", flush=True)
    process = subprocess.Popen(command)
    try:
        returncode = process.wait(timeout=args.timeout_seconds)
    except subprocess.TimeoutExpired:
        print(
            f"::error title=CI command timeout::{args.label} exceeded "
            f"{args.timeout_seconds} seconds",
            file=sys.stderr,
            flush=True,
        )
        terminate_process_tree(process)
        return 124
    print(
        f"::notice title=CI command::{args.label} completed with {returncode}",
        flush=True,
    )
    return returncode


if __name__ == "__main__":
    raise SystemExit(main())
