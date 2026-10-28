#!/usr/bin/env python3
"""Proves tools/security_report.py (TF-076, TF-077 AC3, ADR-0055) on fixture summaries, registers and a Dockerfile:
what the monthly report lists, what it escalates, and the location database's pin. Each test's docstring ends with what
it proves. Standard library only; `make trace` runs it first, with `--junit`."""

from __future__ import annotations

import json
import sys
import tempfile
import unittest
from datetime import date
from pathlib import Path

sys.dont_write_bytecode = True  # no __pycache__ beside the tools
sys.path.insert(0, str(Path(__file__).resolve().parent))
import security_report  # noqa: E402

TODAY = date(2031, 3, 1)
REGISTER = """# Findings register

| Id | Finding | Source | Severity | Opened | Owner | State | Closed | Notes |
|---|---|---|---|---|---|---|---|---|
| F-001 | Session cookie readable by script | Penetration test 2031 | High | 2031-01-28 | Ravi | open | | |
| F-002 | Verbose error page | Penetration test 2031 | High | 2031-01-30 | Ravi | open | | |
| F-003 | Missing header on a 404 | Review of T12 | Medium | 2030-11-01 | Ravi | open | | |
| F-004 | Weak cipher offered | Penetration test 2031 | Critical | 2030-12-01 | Ravi | fixed | 2031-01-10 | #42 |
| F-005 | Unsigned export | Review of T13 | Critical | 2031-01-15 | Ravi | open | | |
"""
EXCEPTIONS = """exceptions:
  - finding: CVE-2031-0001
    package: left-pad
    reason: Waits for its upgrade.
    approver: The owner
    approved: 2031-02-10
    expires: 2031-03-20
  - finding: CVE-2031-0002
    package: right-pad
    reason: Waits too.
    approver: The owner
    approved: 2031-01-01
    expires: 2031-02-28
"""
DOCKERFILE = "ARG LOCATIONS_MONTH=2031-01\nARG LOCATIONS_SHA256=abc123\n"


def finding(severity: str, source: str, identifier: str = "CVE-2031-0009", excepted: str = "") -> dict:
    return {"source": source, "severity": severity, "identifier": identifier, "package": "pkg", "version": "1.0.0", "fixed": "", "title": "t", "where": ["shell/package-lock.json"], "excepted_until": excepted}


class SecurityReportTest(unittest.TestCase):
    def setUp(self) -> None:
        self.folder = tempfile.TemporaryDirectory()
        self.root = Path(self.folder.name)
        (self.root / "findings.md").write_text(REGISTER, encoding="utf-8")
        (self.root / "exceptions.yaml").write_text(EXCEPTIONS, encoding="utf-8")
        (self.root / "Dockerfile").write_text(DOCKERFILE, encoding="utf-8")
        summary = {"findings": [finding("HIGH", "dependency", "CVE-2031-0001", "2031-03-20"), finding("MEDIUM", "image"), finding("MEDIUM", "dependency"), finding("LOW", "image"), finding("HIGH", "code", "iron-secret-in-log")], "failures": ["high iron-secret-in-log in src/A.cs (code): ..."]}
        (self.root / "summary.json").write_text(json.dumps(summary), encoding="utf-8")

    def tearDown(self) -> None:
        self.folder.cleanup()

    def run_report(self, location: str = "available") -> tuple[str, list[dict[str, str]]]:
        output = self.root / "out" / "report.md"
        arguments = ["--summary", str(self.root / "summary.json"), "--findings", str(self.root / "findings.md"), "--exceptions", str(self.root / "exceptions.yaml"), "--dockerfile", str(self.root / "Dockerfile"), "--check-location", "--today", TODAY.isoformat(), "--output", str(output)]
        self.assertEqual(security_report.main(arguments, check=lambda month: location), 0)
        return output.read_text(encoding="utf-8"), json.loads(output.with_suffix(".issues.json").read_text(encoding="utf-8"))

    def test_a_high_finding_open_for_more_than_30_days_is_escalated_as_an_issue(self) -> None:
        """A critical or high finding of the register open for more than 30 days is escalated, named by its id; one open 30 days or fewer, a medium one or a fixed one is not. [TF-077 AC3]"""
        text, issues = self.run_report()
        self.assertEqual([issue["title"] for issue in issues], ["Escalated: F-001 high finding open 32 days", "Escalated: F-005 critical finding open 45 days"])
        self.assertIn("longer than the 30 days a high finding may stay open (TF-077 AC3)", issues[0]["body"])
        self.assertIn("| F-002 | Verbose error page | Penetration test 2031 | high | 2031-01-30 | 30 | Ravi | no |", text)
        self.assertIn("| F-003 | Missing header on a 404 | Review of T12 | medium | 2030-11-01 | 120 | Ravi | no |", text)
        self.assertNotIn("| F-004 |", text)
        self.assertIn("4 open, 1 closed.", text)

    def test_the_report_lists_open_findings_per_severity_and_the_exceptions_with_their_expiry(self) -> None:
        """The month's report counts the open findings per severity and source, lists the critical and high ones with their exceptions, and the open exceptions with their expiry; an expired one shows as a problem. [TF-076 AC4, ADR-0055]"""
        text, _ = self.run_report()
        self.assertIn("# Security report, 2031-03", text)
        self.assertIn("| High | 1 | 0 | 1 | 0 | 0 | 2 |", text)
        self.assertIn("| Medium | 1 | 0 | 0 | 0 | 1 | 2 |", text)
        self.assertIn("| Low | 0 | 0 | 0 | 0 | 1 | 1 |", text)
        self.assertIn("- high CVE-2031-0001 in pkg 1.0.0, excepted until 2031-03-20 (shell/package-lock.json)", text)
        self.assertIn("- high iron-secret-in-log in src/A.cs (code): ...", text)
        self.assertIn("| CVE-2031-0001 | left-pad | The owner | 2031-02-10 | 2031-03-20 | 19 (renew or close it this month) |", text)
        self.assertIn("expired on 2031-02-28", text)

    def test_a_pinned_location_month_that_is_gone_is_reported_and_raised(self) -> None:
        """The location database's pinned month is checked: published, it is reported as such; gone, the report says to move the pin and an issue is raised. [ADR-0055, ADR-0049]"""
        text, issues = self.run_report("available")
        self.assertIn("DB-IP's IP to City Lite of 2031-01 (SHA-256 `abc123`): available.", text)
        self.assertFalse([i for i in issues if "location" in i["title"]])
        text, issues = self.run_report("gone (HTTP 404)")
        self.assertIn("The month is gone: move LOCATIONS_MONTH and LOCATIONS_SHA256", text)
        self.assertEqual(issues[-1]["title"], "The location database's month 2031-01 is no longer published")

    def test_the_repositorys_register_reads(self) -> None:
        """docs/security/findings.md is in the form the report reads, and the API's Dockerfile pins a month. [ADR-0055]"""
        register, problems = security_report.read_register(security_report.FINDINGS.read_text(encoding="utf-8"))
        self.assertEqual(problems, [])
        self.assertTrue(all(f.identifier.startswith("F-") for f in register))
        month, digest = security_report.location_pin(security_report.API_DOCKERFILE.read_text(encoding="utf-8"))
        self.assertRegex(month or "", r"^\d{4}-\d{2}$")
        self.assertRegex(digest or "", r"^[0-9a-f]{64}$")


if __name__ == "__main__":
    import unittest_junit

    sys.exit(unittest_junit.main(sys.modules[__name__], "security_report_test"))
