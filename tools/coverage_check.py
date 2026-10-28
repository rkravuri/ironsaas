#!/usr/bin/env python3
"""The coverage floor (TF-077 AC4, ADR-0055): a change that lowers line coverage below its floor is refused.

It merges the Cobertura files a test run wrote, line by line (a line counts as covered when any test project's run
covered it), and compares the total with the area's floor in security/coverage.yaml: below it, the check fails, naming
the area, the coverage and the floor. With --base-floors (the floors on the branch the change goes into) it also
refuses a floor that falls, unless an unexpired exception in security/exceptions.yaml names the finding
`coverage-floor` and the area as its package: a floor may rise, never fall.

Usage: python3 tools/coverage_check.py --area dotnet --report 'artifacts/test-results/dotnet/coverage.cobertura.*.xml'
  [--base-floors base-coverage.yaml] [--today 2026-10-28]
Standard library only.
"""

from __future__ import annotations

import argparse
import glob
import math
import sys
import xml.etree.ElementTree as ElementTree
from datetime import date, datetime, timezone
from pathlib import Path

sys.dont_write_bytecode = True  # no __pycache__ beside the tools
sys.path.insert(0, str(Path(__file__).resolve().parent))
import policy_yaml  # noqa: E402
import security_gate  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
FLOORS = ROOT / "security" / "coverage.yaml"
FLOOR_FINDING = "coverage-floor"


def resolved(filename: str, sources: list[str]) -> str:
    """A class's file as one path whatever the report: each report names its files from its own source roots, which
    differ between test projects (src/ in one, src/Kernel/ in another), so the name is joined to the root that holds it,
    or to the first."""
    if filename.startswith("/") or not sources:
        return filename
    for source in sources:
        candidate = str(Path(source) / filename)
        if Path(candidate).exists():
            return candidate
    return str(Path(sources[0]) / filename)


def merged_lines(reports: list[Path]) -> dict[tuple[str, int], bool]:
    """Every line of every class in the reports, covered when any report covered it. The class's own lines only: the
    methods repeat them."""
    lines: dict[tuple[str, int], bool] = {}
    for report in reports:
        root = ElementTree.parse(report).getroot()
        sources = [source.text.strip() for source in root.iter("source") if source.text and source.text.strip()]
        for klass in root.iter("class"):
            filename = resolved(klass.get("filename", ""), sources)
            own = klass.find("lines")
            for line in own.findall("line") if own is not None else []:
                key = (filename, int(line.get("number", "0")))
                lines[key] = lines.get(key, False) or int(line.get("hits", "0")) > 0
    return lines


def percent(lines: dict[tuple[str, int], bool]) -> float:
    return 100.0 * sum(lines.values()) / len(lines) if lines else 0.0


def read_floors(text: str) -> dict[str, float]:
    document = policy_yaml.load(text) or {}
    floors = document.get("floors") if isinstance(document, dict) else None
    if not isinstance(floors, dict) or not all(isinstance(v, (int, float)) for v in floors.values()):
        raise ValueError("security/coverage.yaml: expected 'floors' with a number per area")
    return {str(area): float(value) for area, value in floors.items()}


def check(area: str, coverage: float, floors: dict[str, float], base: dict[str, float] | None, exceptions: list[security_gate.PolicyException]) -> list[str]:
    """The problems of the area: coverage below its floor, or a floor lower than the base's without an exception."""
    problems = []
    floor = floors.get(area)
    if floor is None:
        return [f"security/coverage.yaml has no floor for '{area}'"]
    if coverage < floor:
        problems.append(f"line coverage of {area} is {coverage:.2f}%, below its floor of {floor:g}% (security/coverage.yaml): a change may not lower it; add the tests that cover what it changed (TF-077 AC4)")
    if base is not None and area in base and floor < base[area]:
        allowed = any(e.finding == FLOOR_FINDING and e.package == area for e in exceptions)
        if not allowed:
            problems.append(f"the floor of {area} falls from {base[area]:g}% to {floor:g}%: a floor may rise, never fall, except by an approved exception in security/exceptions.yaml (finding {FLOOR_FINDING}, package {area})")
    return problems


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Refuse line coverage below its floor, and a floor that falls.")
    parser.add_argument("--area", required=True, help="dotnet or shell, as security/coverage.yaml names them")
    parser.add_argument("--report", action="append", default=[], help="a Cobertura file, or a glob of them")
    parser.add_argument("--floors", type=Path, default=FLOORS)
    parser.add_argument("--base-floors", type=Path, help="the floors file of the branch the change goes into")
    parser.add_argument("--exceptions", type=Path, default=security_gate.EXCEPTIONS)
    parser.add_argument("--today", type=date.fromisoformat, default=datetime.now(timezone.utc).date())
    arguments = parser.parse_args(argv)

    reports = sorted({Path(p) for pattern in arguments.report for p in glob.glob(pattern)})
    if not reports:
        print(f"coverage of {arguments.area}: no Cobertura file matches {', '.join(arguments.report)}; run the tests with coverage first")
        return 1
    lines = merged_lines(reports)
    coverage = percent(lines)
    floors = read_floors(arguments.floors.read_text(encoding="utf-8"))
    base = read_floors(arguments.base_floors.read_text(encoding="utf-8")) if arguments.base_floors and arguments.base_floors.exists() else None
    exceptions, _ = security_gate.load_exceptions(arguments.exceptions.read_text(encoding="utf-8"), arguments.today)
    problems = check(arguments.area, coverage, floors, base, exceptions)

    floor = floors.get(arguments.area, 0.0)
    print(f"coverage of {arguments.area}: {coverage:.2f}% of {len(lines)} lines from {len(reports)} reports; floor {floor:g}%")
    if not problems and math.floor(coverage) > floor:
        print(f"note: the floor of {arguments.area} may rise to {math.floor(coverage)}% in security/coverage.yaml")
    for problem in problems:
        print(f"FAIL {problem}")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
