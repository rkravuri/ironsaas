#!/usr/bin/env python3
"""Proves tools/security_gate.py (TF-076, ADR-0055) on fixture reports in the scanners' own shapes: what fails, what an
exception lets through and for how long, the licence policy and the rule scanner. Each test's docstring ends with what
it proves. Standard library only; `make trace` runs it first, with `--junit`."""

from __future__ import annotations

import re
import sys
import unittest
from datetime import date
from pathlib import Path

sys.dont_write_bytecode = True  # no __pycache__ beside the tools
sys.path.insert(0, str(Path(__file__).resolve().parent))
import security_gate  # noqa: E402

TODAY = date(2031, 2, 1)
LICENCES = """
allowed:
  - MIT
  - Apache-2.0
packages:
  - package: axe-core
    licence: MPL-2.0
    reason: A dev dependency, unchanged.
  - package: Old.Package
    version: 1.0.0
    licence: MIT
    reason: Read from its licence file.
"""


def vulnerability(identifier: str, package: str, version: str, severity: str, fixed: str = "", target: str = "shell/package-lock.json") -> dict:
    return {"Target": target, "Type": "npm", "Vulnerabilities": [{"VulnerabilityID": identifier, "PkgName": package, "InstalledVersion": version, "FixedVersion": fixed, "Severity": severity, "Title": "a title"}]}


def packages(target: str, kind: str, *entries: tuple[str, str, list[str]]) -> dict:
    return {"Target": target, "Type": kind, "Packages": [{"Name": name, "Version": version, "Licenses": names} for name, version, names in entries]}


def exceptions(*entries: str) -> str:
    return "exceptions:\n" + "".join(entries) if entries else "exceptions: []\n"


def exception(finding: str, package: str, approved: str = "2031-01-10", expires: str = "2031-03-10", **overrides: str) -> str:
    fields = {"finding": finding, "package": package, "reason": "It waits for its upgrade.", "approver": "The owner", "approved": approved, "expires": expires}
    fields.update(overrides)
    lines = [f"    {key}: {value}" for key, value in fields.items() if value != ""]
    return "  -" + lines[0][3:] + "\n" + "".join(line + "\n" for line in lines[1:])


class SecurityGateTest(unittest.TestCase):
    def gate(self, results: list[dict], register: str = "exceptions: []\n", licences: str = LICENCES, semgrep: dict | None = None, today: date = TODAY) -> security_gate.Decision:
        findings, listed = security_gate.trivy_findings({"Results": results})
        errors: list[str] = []
        if semgrep is not None:
            found, errors = security_gate.semgrep_findings(semgrep)
            findings += found
        known, problems = security_gate.load_exceptions(register, today)
        allowed, allowances, licence_problems = security_gate.load_licence_policy(licences)
        return security_gate.decide(findings, listed, known, problems, allowed, allowances, licence_problems, errors)

    def test_a_critical_vulnerability_fails_naming_the_package_its_version_and_the_finding(self) -> None:
        """A dependency with a critical vulnerability fails the gate, which names the package, its version, the finding and its fix. [TF-076 AC1]"""
        decision = self.gate([vulnerability("CVE-2031-0001", "left-pad", "1.0.0", "CRITICAL", fixed="1.0.1")])
        self.assertFalse(decision.passed)
        self.assertEqual(decision.failures, ["critical CVE-2031-0001 in left-pad 1.0.0 (dependency, fixed in 1.0.1): a title [shell/package-lock.json]"])

    def test_a_high_vulnerability_fails_and_medium_and_low_ones_are_reported_only(self) -> None:
        """High fails like critical; medium and low stay in the report for the monthly summary without failing. [TF-076 AC1, ADR-0055]"""
        decision = self.gate([
            vulnerability("CVE-2031-0002", "Some.Package", "2.0.0", "HIGH", target="src/A/packages.lock.json"),
            vulnerability("CVE-2031-0003", "other", "1.0.0", "MEDIUM"),
            vulnerability("CVE-2031-0004", "another", "1.0.0", "LOW"),
        ])
        self.assertEqual(len(decision.failures), 1)
        self.assertIn("high CVE-2031-0002 in Some.Package 2.0.0 (dependency, no fixed version yet)", decision.failures[0])
        self.assertEqual(sorted(f.severity for f in decision.findings), ["HIGH", "LOW", "MEDIUM"])

    def test_an_exception_holds_until_it_expires_then_fails_again_until_renewed(self) -> None:
        """An approved exception lets its finding through until its expiry; from that day the finding and the expired entry fail, and a renewal passes again. [TF-076 AC2]"""
        finding = [vulnerability("CVE-2031-0005", "left-pad", "1.0.0", "HIGH")]
        register = exceptions(exception("CVE-2031-0005", "left-pad"))
        before = self.gate(finding, register, today=date(2031, 3, 9))
        on_the_day = self.gate(finding, register, today=date(2031, 3, 10))
        renewed = self.gate(finding, exceptions(exception("CVE-2031-0005", "left-pad", approved="2031-03-10", expires="2031-06-10")), today=date(2031, 3, 10))
        self.assertTrue(before.passed)
        self.assertEqual([f.excepted_until for f in before.excepted], ["2031-03-10"])
        self.assertFalse(on_the_day.passed)
        self.assertEqual(len(on_the_day.failures), 2)
        self.assertIn("expired on 2031-03-10", on_the_day.failures[0])
        self.assertIn("high CVE-2031-0005 in left-pad 1.0.0", on_the_day.failures[1])
        self.assertTrue(renewed.passed)

    def test_an_exception_covers_only_its_own_finding_and_package(self) -> None:
        """An exception names one finding in one package (a wildcard where it says so); the same finding elsewhere still fails. [TF-076 AC2, ADR-0055]"""
        register = exceptions(exception("CVE-2031-0006", "left-pad"), exception("DS-0002", "src/*/Dockerfile"))
        decision = self.gate([
            vulnerability("CVE-2031-0006", "left-pad", "1.0.0", "HIGH"),
            vulnerability("CVE-2031-0006", "right-pad", "1.0.0", "HIGH"),
            {"Target": "src/Fleet/Dockerfile", "Type": "dockerfile", "Misconfigurations": [{"ID": "DS-0002", "Severity": "HIGH", "Status": "FAIL", "Title": "root"}]},
        ], register)
        self.assertEqual(len(decision.failures), 1)
        self.assertIn("right-pad", decision.failures[0])
        self.assertEqual(sorted(f.package for f in decision.excepted), ["left-pad", "src/Fleet/Dockerfile"])

    def test_an_exception_needs_every_field_and_lasts_at_most_six_months(self) -> None:
        """A register entry without its approver, with a date that is not one, approved in the future, or longer than six months is refused. [TF-076 AC2, ADR-0055]"""
        register = exceptions(
            exception("CVE-1", "a", approver=""),
            exception("CVE-2", "b", expires="soon"),
            exception("CVE-3", "c", approved="2031-02-02"),
            exception("CVE-4", "d", approved="2031-01-01", expires="2031-08-01"),
        )
        known, problems = security_gate.load_exceptions(register, TODAY)
        self.assertEqual(known, [])
        self.assertEqual(len(problems), 4)
        self.assertIn("missing approver", problems[0])
        self.assertIn("expires is not a date", problems[1])
        self.assertIn("is after today", problems[2])
        self.assertIn("at most six months", problems[3])

    def test_the_same_vulnerability_in_many_lock_files_is_one_finding_with_every_place(self) -> None:
        """A package every project locks is reported once, with the lock files it is in. [ADR-0055]"""
        decision = self.gate([vulnerability("CVE-2031-0007", "Npgsql", "10.0.3", "HIGH", target=f"src/{name}/packages.lock.json") for name in ("A", "B", "C", "D")])
        self.assertEqual(len(decision.findings), 1)
        self.assertIn("[src/A/packages.lock.json, src/B/packages.lock.json, src/C/packages.lock.json and 1 more]", decision.failures[0])

    def test_a_licence_outside_the_policy_fails_naming_the_package(self) -> None:
        """A dependency under a licence the policy does not allow fails, named with its version, licence and lock file. [ADR-0055, ADR-0003]"""
        decision = self.gate([packages("shell/package-lock.json", "npm", ("copyleft-thing", "2.1.0", ["GPL-3.0-only"]), ("fine", "1.0.0", ["MIT"]))])
        self.assertEqual(decision.failures, ["licence of copyleft-thing 2.1.0: licence GPL-3.0-only is not allowed by security/licences.yaml [shell/package-lock.json]"])

    def test_a_weak_copyleft_licence_counts_only_for_the_package_the_policy_names(self) -> None:
        """MPL-2.0 passes for the package recorded with it and fails for any other. [ADR-0055]"""
        decision = self.gate([packages("shell/package-lock.json", "npm", ("axe-core", "4.13.0", ["MPL-2.0"]), ("something-else", "1.0.0", ["MPL-2.0"]))])
        self.assertEqual(len(decision.failures), 1)
        self.assertIn("something-else", decision.failures[0])

    def test_licence_expressions_need_one_allowed_side_of_an_or_and_every_side_of_an_and(self) -> None:
        """(MIT OR GPL-3.0-only) is allowed; MIT AND GPL-3.0-only is not; a WITH exception reads as its licence. [ADR-0055]"""
        allowed, allowances, _ = security_gate.load_licence_policy(LICENCES)
        self.assertTrue(security_gate.licence_allowed("(MIT OR GPL-3.0-only)", "x", "1", allowed, allowances))
        self.assertFalse(security_gate.licence_allowed("MIT AND GPL-3.0-only", "x", "1", allowed, allowances))
        self.assertTrue(security_gate.licence_allowed("Apache-2.0 WITH LLVM-exception", "x", "1", allowed, allowances))
        self.assertFalse(security_gate.licence_allowed("", "x", "1", allowed, allowances))

    def test_a_package_without_a_readable_licence_fails_unless_read_by_hand_for_its_version(self) -> None:
        """A package the scanner finds no licence for fails, unless the policy records its licence for that very version. [ADR-0055]"""
        decision = self.gate([packages("src/A/packages.lock.json", "nuget", ("Old.Package", "1.0.0", []), ("Old.Package", "2.0.0", []), ("Unread", "3.0.0", []))])
        self.assertEqual(len(decision.failures), 2)
        self.assertIn("Old.Package 2.0.0: no licence could be read", decision.failures[0])
        self.assertIn("Unread 3.0.0", decision.failures[1])

    def test_a_rule_finding_fails_a_partial_parse_does_not_and_a_rule_error_does(self) -> None:
        """A finding of the repository's rules fails with its file, line and rule; a file parsed in part is tolerated; a rule the scanner could not run fails. [TF-076 AC5, ADR-0055]"""
        semgrep = {
            "results": [{"check_id": "security.rules.iron-secret-in-log", "path": "src/A/B.cs", "start": {"line": 12}, "extra": {"severity": "ERROR", "message": "A value named as a secret goes into a log line."}}],
            "errors": [{"type": ["PartialParsing", []], "message": "Syntax error"}, {"type": "InvalidRuleSchemaError", "message": "bad rule"}],
        }
        decision = self.gate([], semgrep=semgrep)
        self.assertEqual(len(decision.failures), 2)
        self.assertIn("the rule scanner could not run: InvalidRuleSchemaError", decision.failures[0])
        self.assertIn("high iron-secret-in-log in src/A/B.cs (code): A value named as a secret goes into a log line. [src/A/B.cs:12]", decision.failures[1])

    def test_the_repositorys_register_and_policy_read_without_a_problem(self) -> None:
        """security/exceptions.yaml and security/licences.yaml are in the form the gate reads, every exception with all its fields. [ADR-0055]"""
        root = Path(__file__).resolve().parent.parent
        register = (root / "security" / "exceptions.yaml").read_text(encoding="utf-8")
        # On the day the register was last written, its latest approval, so the test reads its form whatever day it runs
        # and a new approval does not read as after today (V0); the gate in make security reads its dates against today.
        written = max(date.fromisoformat(day) for day in re.findall(r"^\s+approved: (\d{4}-\d{2}-\d{2})\s*$", register, re.MULTILINE))
        known, problems = security_gate.load_exceptions(register, written)
        self.assertEqual(problems, [])
        self.assertTrue(known)
        allowed, allowances, licence_problems = security_gate.load_licence_policy((root / "security" / "licences.yaml").read_text(encoding="utf-8"))
        self.assertEqual(licence_problems, [])
        self.assertIn("MIT", allowed)
        self.assertTrue(all(a.reason for a in allowances))


if __name__ == "__main__":
    import unittest_junit

    sys.exit(unittest_junit.main(sys.modules[__name__], "security_gate_test"))
