#!/usr/bin/env python3
"""Proves tools/coverage_check.py (TF-077 AC4, ADR-0055) on Cobertura files in coverlet's and Vitest's shape: the merge
line by line, the floor, and a floor that falls. Each test's docstring ends with what it proves. Standard library only;
`make trace` runs it first, with `--junit`."""

from __future__ import annotations

import sys
import tempfile
import unittest
from datetime import date
from pathlib import Path

sys.dont_write_bytecode = True  # no __pycache__ beside the tools
sys.path.insert(0, str(Path(__file__).resolve().parent))
import coverage_check  # noqa: E402

TODAY = date(2031, 2, 1)


def cobertura(*classes: tuple[str, dict[int, int]], source: str = "/repo/") -> str:
    """A Cobertura document: each class with its own lines, and a method repeating them, as coverlet writes it."""
    body = ""
    for filename, lines in classes:
        own = "".join(f'<line number="{n}" hits="{h}" branch="False"/>' for n, h in lines.items())
        body += f'<class name="{filename}" filename="{filename}" line-rate="0"><methods><method name="M" signature="()"><lines>{own}</lines></method></methods><lines>{own}</lines></class>'
    return f'<?xml version="1.0"?><coverage line-rate="0" version="1.9"><sources><source>{source}</source></sources><packages><package name="Iron.Kernel"><classes>{body}</classes></package></packages></coverage>'


class CoverageCheckTest(unittest.TestCase):
    def setUp(self) -> None:
        self.folder = tempfile.TemporaryDirectory()
        self.root = Path(self.folder.name)
        # Two test projects: the first covers lines 1 and 2 of A, the second line 3 of A; B's two lines are uncovered.
        (self.root / "coverage.cobertura.1.xml").write_text(cobertura(("src/A.cs", {1: 4, 2: 1, 3: 0})), encoding="utf-8")
        (self.root / "coverage.cobertura.2.xml").write_text(cobertura(("src/A.cs", {1: 0, 2: 0, 3: 2}), ("src/B.cs", {1: 0, 2: 0})), encoding="utf-8")
        (self.root / "exceptions.yaml").write_text("exceptions: []\n", encoding="utf-8")

    def tearDown(self) -> None:
        self.folder.cleanup()

    def floors(self, name: str, dotnet: float) -> Path:
        path = self.root / name
        path.write_text(f"floors:\n  dotnet: {dotnet:g}\n  shell: 80\n", encoding="utf-8")
        return path

    def run_check(self, floors: Path, base: Path | None = None, exceptions: Path | None = None) -> int:
        arguments = ["--area", "dotnet", "--report", str(self.root / "coverage.cobertura.*.xml"), "--floors", str(floors), "--exceptions", str(exceptions or self.root / "exceptions.yaml"), "--today", TODAY.isoformat()]
        if base is not None:
            arguments += ["--base-floors", str(base)]
        return coverage_check.main(arguments)

    def test_the_reports_merge_line_by_line_and_each_line_counts_once(self) -> None:
        """A line covered by any test project counts as covered, and the method's copy of a class's lines is not counted again. [ADR-0055]"""
        lines = coverage_check.merged_lines(sorted(self.root.glob("coverage.cobertura.*.xml")))
        self.assertEqual(len(lines), 5)
        self.assertEqual(sum(lines.values()), 3)
        self.assertAlmostEqual(coverage_check.percent(lines), 60.0)

    def test_a_file_named_from_two_source_roots_is_one_file(self) -> None:
        """Two test projects' reports name the same file from different roots (src/Kernel/ and src/); its lines count once, covered by either. [ADR-0055]"""
        first = self.root / "kernel.xml"
        second = self.root / "src.xml"
        first.write_text(cobertura(("Iron.Kernel/A.cs", {1: 1, 2: 0}), source="/repo/src/Kernel/"), encoding="utf-8")
        second.write_text(cobertura(("Kernel/Iron.Kernel/A.cs", {1: 0, 2: 3}), source="/repo/src/"), encoding="utf-8")
        lines = coverage_check.merged_lines([first, second])
        self.assertEqual(lines, {("/repo/src/Kernel/Iron.Kernel/A.cs", 1): True, ("/repo/src/Kernel/Iron.Kernel/A.cs", 2): True})

    def test_a_change_that_lowers_coverage_below_its_floor_is_refused(self) -> None:
        """Coverage of 60% passes a floor of 60 and is refused by a floor of 61, naming the area, the coverage and the floor. [TF-077 AC4]"""
        self.assertEqual(self.run_check(self.floors("at.yaml", 60)), 0)
        self.assertEqual(self.run_check(self.floors("above.yaml", 61)), 1)
        # A base branch that has no floors yet compares with nothing.
        (self.root / "empty.yaml").write_text("", encoding="utf-8")
        self.assertEqual(self.run_check(self.floors("at.yaml", 60), self.root / "empty.yaml"), 0)
        problems = coverage_check.check("dotnet", 60.0, {"dotnet": 61.0}, None, [])
        self.assertEqual(problems, ["line coverage of dotnet is 60.00%, below its floor of 61% (security/coverage.yaml): a change may not lower it; add the tests that cover what it changed (TF-077 AC4)"])

    def test_a_floor_may_rise_but_never_fall_without_an_exception(self) -> None:
        """A floor lower than the base branch's fails, unless an unexpired exception names coverage-floor for the area; a higher floor passes. [TF-077 AC4, ADR-0055]"""
        base = self.floors("base.yaml", 60)
        self.assertEqual(self.run_check(self.floors("risen.yaml", 60), base), 0)
        self.assertEqual(self.run_check(self.floors("fallen.yaml", 55), base), 1)
        excepted = self.root / "excepted.yaml"
        excepted.write_text("exceptions:\n  - finding: coverage-floor\n    package: dotnet\n    reason: A module moved out.\n    approver: The owner\n    approved: 2031-01-20\n    expires: 2031-03-20\n", encoding="utf-8")
        self.assertEqual(self.run_check(self.floors("fallen.yaml", 55), base, excepted), 0)

    def test_the_repositorys_floors_read(self) -> None:
        """security/coverage.yaml names a floor for the .NET code and one for the shell. [ADR-0055]"""
        floors = coverage_check.read_floors(coverage_check.FLOORS.read_text(encoding="utf-8"))
        self.assertEqual(sorted(floors), ["dotnet", "shell"])
        self.assertTrue(all(0 < value <= 100 for value in floors.values()))


if __name__ == "__main__":
    import unittest_junit

    sys.exit(unittest_junit.main(sys.modules[__name__], "coverage_check_test"))
