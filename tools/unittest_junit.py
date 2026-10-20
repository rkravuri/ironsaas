"""Runs a tool's unittest tests, as `unittest.main` does, and with `--junit PATH` also writes their results as a JUnit file,
which `make trace` reads (ADR-0044): `python3 tools/prd_check_test.py --junit artifacts/test-results/python/prd-check.xml`.
Each case's class name is `<module>.<TestCase>`, as `make trace` reads the tests from the source. Standard library only."""

from __future__ import annotations

import argparse
import sys
import time
import unittest
import xml.etree.ElementTree as ElementTree
from pathlib import Path
from types import ModuleType


class _Result(unittest.TextTestResult):
    """Keeps each case's outcome and time beside the console's report."""

    module = ""

    def __init__(self, *args, **kwargs) -> None:  # type: ignore[no-untyped-def]
        super().__init__(*args, **kwargs)
        self.cases: list[tuple[str, str, str, float, str]] = []
        self._started = 0.0

    def startTest(self, test: unittest.TestCase) -> None:
        self._started = time.perf_counter()
        super().startTest(test)

    def _record(self, test: unittest.TestCase, status: str, message: str = "") -> None:
        name = getattr(test, "_testMethodName", str(test))
        self.cases.append((f"{self.module}.{type(test).__name__}", name, status, time.perf_counter() - self._started, message))

    def addSuccess(self, test: unittest.TestCase) -> None:
        super().addSuccess(test)
        self._record(test, "passed")

    def addFailure(self, test: unittest.TestCase, err) -> None:  # type: ignore[no-untyped-def]
        super().addFailure(test, err)
        self._record(test, "failed", self._exc_info_to_string(err, test))

    def addError(self, test: unittest.TestCase, err) -> None:  # type: ignore[no-untyped-def]
        super().addError(test, err)
        self._record(test, "failed", self._exc_info_to_string(err, test))

    def addSkip(self, test: unittest.TestCase, reason: str) -> None:
        super().addSkip(test, reason)
        self._record(test, "skipped", reason)


def write_junit(path: Path, suite_name: str, cases: list[tuple[str, str, str, float, str]]) -> None:
    root = ElementTree.Element("testsuites")
    suite = ElementTree.SubElement(root, "testsuite", name=suite_name, tests=str(len(cases)), failures=str(sum(1 for c in cases if c[2] == "failed")), skipped=str(sum(1 for c in cases if c[2] == "skipped")))
    for classname, name, status, seconds, message in cases:
        case = ElementTree.SubElement(suite, "testcase", classname=classname, name=name, time=f"{seconds:.3f}")
        if status == "failed":
            ElementTree.SubElement(case, "failure", message=message.splitlines()[-1] if message else "failed").text = message
        elif status == "skipped":
            ElementTree.SubElement(case, "skipped", message=message)
    path.parent.mkdir(parents=True, exist_ok=True)
    ElementTree.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)


def main(module: ModuleType, name: str) -> int:
    """Runs the module's tests; answers the exit status. `name` is the module's file name without .py."""
    parser = argparse.ArgumentParser(description=f"Run the tests of {name}.")
    parser.add_argument("--junit", type=Path, help="also write the results as JUnit XML to this file")
    arguments = parser.parse_args()
    _Result.module = name
    suite = unittest.defaultTestLoader.loadTestsFromModule(module)
    result = unittest.TextTestRunner(verbosity=1, resultclass=_Result).run(suite)
    if arguments.junit:
        write_junit(arguments.junit, name, result.cases)  # type: ignore[attr-defined]
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    sys.exit("Run a test module instead, for example python3 tools/prd_check_test.py --junit <file>.")
