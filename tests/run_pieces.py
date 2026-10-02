"""Runs the in game suite in pieces, several clients side by side.

Every test file is a session of its own (conftest.py), so each file is one piece.
A piece runs as its own pytest process on its own client slot (rig.SLOT):

    uv run python tests/run_pieces.py                    # every piece
    uv run python tests/run_pieces.py terminal survival  # pieces by name
    uv run python tests/run_pieces.py --jobs 2

A client in a world takes about 5.5 GB. A piece only starts while that leaves
--min-free-gb of RAM available, counting the clients still loading, so the number
of clients follows the machine. The pytest output of a piece goes to
tests/artifacts/logs/<piece>.log, next to what the plugin logged per test
(tests/artifacts/<test file>.log, written by conftest.py).
"""

from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
import time
from pathlib import Path

TESTS = Path(__file__).resolve().parent
ARTIFACTS = TESTS / "artifacts"
LOGS = ARTIFACTS / "logs"
CLIENT_GB = 6.0
# A client reaches its full size about this long after its start
LOADING_S = 150.0


def available_gb() -> float:
    text = Path("/proc/meminfo").read_text()
    return int(re.search(r"MemAvailable:\s+(\d+)", text).group(1)) / 1024 / 1024


def pieces(names: list[str]) -> list[Path]:
    files = sorted(TESTS.glob("test_*.py"))
    if not names:
        return files
    chosen = [f for f in files if f.stem.removeprefix("test_") in names]
    unknown = set(names) - {f.stem.removeprefix("test_") for f in chosen}
    if unknown:
        sys.exit(f"No such piece: {', '.join(sorted(unknown))}")
    return chosen


def stop_client(slot: int) -> None:
    subprocess.run(
        [sys.executable, "-c", "import rig; rig.stop()"],
        cwd=TESTS,
        env={**os.environ, "UNDO_SLOT": str(slot)},
        check=False,
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("names", nargs="*", help="pieces to run, default all")
    parser.add_argument("--jobs", type=int, default=6, help="clients at once, at most")
    parser.add_argument("--min-free-gb", type=float, default=16.0)
    parser.add_argument("--timeout", type=float, default=900.0, help="per piece, s")
    args = parser.parse_args()
    sys.stdout.reconfigure(line_buffering=True)

    pending = pieces(args.names)
    free_slots = list(range(1, args.jobs + 1))
    running: dict[int, tuple[Path, subprocess.Popen, float]] = {}
    results: list[tuple[str, str, float, int]] = []
    started = time.monotonic()

    def finish(slot: int, verdict: str | None = None) -> None:
        piece, process, begun = running.pop(slot)
        log = (LOGS / f"{piece.stem}.log").read_text(errors="replace")
        summary = re.findall(r"=+ (.*?) in [\d.]+s.*=+", log)
        verdict = verdict or (summary[-1] if summary else f"exit {process.returncode}")
        if (
            process.returncode != 0
            and "failed" not in verdict
            and "error" not in verdict
        ):
            verdict += f" (exit {process.returncode})"
        took = time.monotonic() - begun
        results.append((piece.stem, verdict, took, process.returncode))
        print(
            f"[{time.monotonic() - started:5.0f}s] {piece.stem}: {verdict}, {took:.0f}s"
        )
        free_slots.append(slot)
        free_slots.sort()

    try:
        while pending or running:
            now = time.monotonic()
            for slot, (piece, process, begun) in list(running.items()):
                if process.poll() is not None:
                    finish(slot)
                elif now - begun > args.timeout:
                    process.kill()
                    process.wait()
                    stop_client(slot)
                    finish(slot, "timed out")

            loading = sum(
                1 for _, _, begun in running.values() if now - begun < LOADING_S
            )
            headroom = available_gb() - loading * CLIENT_GB - args.min_free_gb
            if pending and free_slots and headroom >= CLIENT_GB:
                piece, slot = pending.pop(0), free_slots.pop(0)
                LOGS.mkdir(parents=True, exist_ok=True)
                log = open(LOGS / f"{piece.stem}.log", "w")
                process = subprocess.Popen(
                    [sys.executable, "-m", "pytest", str(piece), "-v"],
                    cwd=TESTS.parent,
                    env={**os.environ, "UNDO_SLOT": str(slot)},
                    stdout=log,
                    stderr=subprocess.STDOUT,
                    stdin=subprocess.DEVNULL,
                )
                running[slot] = (piece, process, time.monotonic())
                print(
                    f"[{now - started:5.0f}s] {piece.stem}: started on slot {slot}",
                )
                continue
            if pending and not running and headroom < CLIENT_GB:
                sys.exit(
                    f"Not enough free RAM to start a client: {available_gb():.0f} GB"
                )
            time.sleep(1)
    finally:
        for slot, (_, process, _) in running.items():
            process.kill()
            stop_client(slot)

    print(f"\n{len(results)} pieces in {time.monotonic() - started:.0f}s")
    for name, verdict, took, _ in sorted(results):
        print(f"  {name:28} {took:4.0f}s  {verdict}")
    bad = [name for name, _, _, code in results if code != 0]
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
