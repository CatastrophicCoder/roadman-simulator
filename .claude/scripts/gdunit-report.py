#!/usr/bin/env python3
"""Reads gdUnit4 TRX files (dotnet test --logger trx) for docs/specs/test-performance.md.

Not part of /verify. Standard library only; run with python3 -I.

  gdunit-report.py suites <run.trx>                per-suite case count and summed test time
  gdunit-report.py compare <before.trx> <after.trx>
      per-suite case counts and times before and after (criteria 3 and 13), and the
      skip / N/A lines each test printed (criterion 6): exits 1 when the after run printed
      a skip or N/A line, for some test, that the before run didn't print for that test.

A skip line is a stdout line of a test that starts with "skipped " (MapLoaderTest's
IsFullPack) or contains "N/A" (the N/A paths of PlayerOnFootTest, WorldEdgeTest and
WorldEdgeBreakingTest). Other lines that only contain the word "skipped" (the ledge
search's "... midpoint(s) before it skipped near a ring corner") report a search, not
a skipped check, and don't count.
"""

from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict

NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
SKIP_LINE = re.compile(r"(^|:\s)skipped\s|\bN/A\b")


def seconds(duration: str) -> float:
    h, m, s = duration.split(":")
    return int(h) * 3600 + int(m) * 60 + float(s)


def read(path: str) -> dict[str, tuple[str, float, list[str]]]:
    """test name -> (suite, seconds, skip lines)."""
    tests = {}
    for result in ET.parse(path).getroot().iter(f"{NS}UnitTestResult"):
        name = result.get("testName", "")
        parts = name.split(".")
        suite = parts[-2] if len(parts) >= 2 else name
        out = result.find(f"{NS}Output/{NS}StdOut")
        lines = (out.text or "").splitlines() if out is not None else []
        skips = sorted({line.strip() for line in lines if SKIP_LINE.search(line.strip())})
        tests[name] = (suite, seconds(result.get("duration", "0:0:0")), skips)
    return tests


def per_suite(tests: dict[str, tuple[str, float, list[str]]]) -> dict[str, tuple[int, float]]:
    acc: dict[str, list[float]] = defaultdict(lambda: [0, 0.0])
    for suite, secs, _ in tests.values():
        acc[suite][0] += 1
        acc[suite][1] += secs
    return {k: (int(v[0]), v[1]) for k, v in acc.items()}


def cmd_suites(path: str) -> int:
    suites = per_suite(read(path))
    for name, (count, secs) in sorted(suites.items(), key=lambda kv: -kv[1][1]):
        print(f"{name:28} {count:4} {secs:8.1f}")
    count = sum(c for c, _ in suites.values())
    secs = sum(s for _, s in suites.values())
    print(f"{'Total':28} {count:4} {secs:8.1f}")
    return 0


def cmd_compare(before_path: str, after_path: str) -> int:
    before, after = read(before_path), read(after_path)
    b, a = per_suite(before), per_suite(after)
    print(f"{'Suite':28} {'cases':>11} {'before s':>9} {'after s':>9}")
    for name in sorted(set(b) | set(a)):
        bc, bs = b.get(name, (0, 0.0))
        ac, as_ = a.get(name, (0, 0.0))
        mark = "" if bc == ac else "  <- case count changed"
        print(f"{name:28} {bc:5} / {ac:<3} {bs:9.1f} {as_:9.1f}{mark}")
    print(
        f"{'Total':28} {sum(c for c, _ in b.values()):5} / {sum(c for c, _ in a.values()):<3} "
        f"{sum(s for _, s in b.values()):9.1f} {sum(s for _, s in a.values()):9.1f}"
    )
    added = [t for t in after if t not in before]
    removed = [t for t in before if t not in after]
    for t in added:
        print(f"new test: {t}")
    for t in removed:
        print(f"removed test: {t}")
    new_skips = []
    for name, (_, _, skips) in after.items():
        old = set(before[name][2]) if name in before else set()
        new_skips += [(name, line) for line in skips if line not in old]
    b_count = sum(len(v[2]) for v in before.values())
    a_count = sum(len(v[2]) for v in after.values())
    print(f"skip/N/A lines: before {b_count}, after {a_count}")
    for name, line in new_skips:
        print(f"NEW skip/N/A line in {name}: {line}")
    if new_skips:
        print("FAIL: the after run prints skip/N/A lines the before run didn't (criterion 6)")
        return 1
    print("OK: the after run's skip/N/A lines are a subset of the before run's (criterion 6)")
    return 0


def main(argv: list[str]) -> int:
    if len(argv) == 2 and argv[0] == "suites":
        return cmd_suites(argv[1])
    if len(argv) == 3 and argv[0] == "compare":
        return cmd_compare(argv[1], argv[2])
    print(__doc__.strip(), file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
