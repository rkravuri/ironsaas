#!/usr/bin/env python3
"""The security gate (TF-076, ADR-0055): one decision over every scanner's findings, the same on the laptop and in CI.

It reads the JSON reports the scanners wrote (Trivy's for dependencies, licences, misconfigurations and images;
Semgrep's for the repository's own rules), the exceptions register (security/exceptions.yaml) and the licence policy
(security/licences.yaml), and fails, naming each package and finding:

- on a critical or high vulnerability, misconfiguration or rule finding that no unexpired exception covers;
- on a dependency whose licence the policy does not allow, or whose licence nobody could read;
- on an exception that is expired, malformed or longer than six months, so an approval runs out and comes back to the
  build until it is renewed (TF-076 AC2);
- on a scanner that could not run its rules.

Medium and low findings are reported and kept for the monthly report (tools/security_report.py), not failed. The
.NET analyzers and ESLint fail in their own builds; their false positives are suppressed at the line with a reason.

Usage: python3 tools/security_gate.py --trivy artifacts/security/trivy-repository.json [--trivy more.json ...]
  [--semgrep artifacts/security/semgrep.json] [--summary artifacts/security/summary.json] [--today 2026-10-28]
Standard library only.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import asdict, dataclass, field
from datetime import date, datetime, timezone
from fnmatch import fnmatchcase
from pathlib import Path

sys.dont_write_bytecode = True  # no __pycache__ beside the tools
sys.path.insert(0, str(Path(__file__).resolve().parent))
import policy_yaml  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
EXCEPTIONS = ROOT / "security" / "exceptions.yaml"
LICENCES = ROOT / "security" / "licences.yaml"
SEVERITIES = ["CRITICAL", "HIGH", "MEDIUM", "LOW", "UNKNOWN"]
FAILING = {"CRITICAL", "HIGH"}
EXCEPTION_FIELDS = ["finding", "package", "reason", "approver", "approved", "expires"]
LONGEST_EXCEPTION_DAYS = 183
# Semgrep's own severities, then the newer ones it also writes.
RULE_SEVERITIES = {"ERROR": "HIGH", "WARNING": "MEDIUM", "INFO": "LOW", "CRITICAL": "CRITICAL", "HIGH": "HIGH", "MEDIUM": "MEDIUM", "LOW": "LOW"}
# The lock files Trivy reads packages from; Directory.Packages.props repeats the direct .NET packages without licences.
PACKAGE_TYPES = {"nuget", "npm", "dotnet-core"}
# Parse errors leave the rest of a file scanned; any other error means a rule did not run.
TOLERATED_RULE_ERRORS = {"PartialParsing", "Syntax error", "Lexical error", "Other syntax error"}


@dataclass
class Finding:
    """One finding, from any scanner, in the gate's words."""

    source: str  # dependency, licence, misconfiguration, image, code
    severity: str
    identifier: str  # CVE or GHSA id, a misconfiguration's id, a rule's id, or a licence
    package: str  # the package's name, or the file a misconfiguration or a rule finding is in
    version: str = ""
    fixed: str = ""
    title: str = ""
    where: list[str] = field(default_factory=list)
    excepted_until: str = ""

    def key(self) -> tuple[str, str, str, str]:
        return (self.source, self.identifier, self.package, self.version)

    def describe(self) -> str:
        if self.source == "licence":
            return f"{self.package} {self.version}: {self.title} [{', '.join(sorted(self.where)[:3])}{', ...' if len(self.where) > 3 else ''}]"
        version = f" {self.version}" if self.version else ""
        fixed = f", fixed in {self.fixed}" if self.fixed else ", no fixed version yet"
        if self.source in ("misconfiguration", "code"):
            fixed = ""
        where = sorted(self.where)
        shown = ", ".join(where[:3]) + (f" and {len(where) - 3} more" if len(where) > 3 else "")
        return f"{self.severity.lower()} {self.identifier} in {self.package}{version} ({self.source}{fixed}): {self.title} [{shown}]"


@dataclass
class PolicyException:
    finding: str
    package: str
    reason: str
    approver: str
    approved: date
    expires: date

    def covers(self, item: Finding) -> bool:
        return self.finding.lower() == item.identifier.lower() and fnmatchcase(item.package, self.package)


@dataclass
class LicenceAllowance:
    package: str
    licence: str
    reason: str
    version: str = ""

    def covers(self, package: str, version: str) -> bool:
        return fnmatchcase(package, self.package) and (not self.version or self.version == version)


@dataclass
class Decision:
    findings: list[Finding]
    failures: list[str]
    excepted: list[Finding]
    exceptions: list[PolicyException]
    unused: list[PolicyException]

    @property
    def passed(self) -> bool:
        return not self.failures


def _date(value: object, what: str) -> date:
    if not isinstance(value, str) or not re.fullmatch(r"\d{4}-\d{2}-\d{2}", value):
        raise ValueError(f"{what} is not a date (YYYY-MM-DD)")
    return date.fromisoformat(value)


def load_exceptions(text: str, today: date) -> tuple[list[PolicyException], list[str]]:
    """The register's exceptions, and its problems: a missing field, a bad date, an approval longer than six months, an
    expired exception (TF-076 AC2)."""
    document = policy_yaml.load(text) or {}
    if not isinstance(document, dict) or not isinstance(document.get("exceptions", []), list):
        return [], ["security/exceptions.yaml: expected a list under 'exceptions'"]
    exceptions: list[PolicyException] = []
    problems: list[str] = []
    for number, entry in enumerate(document.get("exceptions") or [], start=1):
        name = f"exception {number}"
        if not isinstance(entry, dict):
            problems.append(f"{name}: expected the fields {', '.join(EXCEPTION_FIELDS)}")
            continue
        name = f"exception {number} ({entry.get('finding', '?')} in {entry.get('package', '?')})"
        missing = [key for key in EXCEPTION_FIELDS if not isinstance(entry.get(key), str) or not str(entry.get(key)).strip()]
        unknown = sorted(set(entry) - set(EXCEPTION_FIELDS))
        if missing or unknown:
            problems.append(f"{name}: " + "; ".join(filter(None, [f"missing {', '.join(missing)}" if missing else "", f"unknown {', '.join(unknown)}" if unknown else ""])))
            continue
        try:
            approved = _date(entry["approved"], "approved")
            expires = _date(entry["expires"], "expires")
        except ValueError as error:
            problems.append(f"{name}: {error}")
            continue
        exception = PolicyException(entry["finding"].strip(), entry["package"].strip(), " ".join(entry["reason"].split()), entry["approver"].strip(), approved, expires)
        if approved > today:
            problems.append(f"{name}: approved {approved} is after today")
        elif expires <= approved:
            problems.append(f"{name}: expires {expires} is not after its approval {approved}")
        elif (expires - approved).days > LONGEST_EXCEPTION_DAYS:
            problems.append(f"{name}: runs {(expires - approved).days} days; an exception lasts at most six months ({LONGEST_EXCEPTION_DAYS} days) before it is approved again")
        elif expires <= today:
            problems.append(f"{name}: expired on {expires}. The finding fails the build again until the exception is renewed with a new approval, or removed once the finding is gone (TF-076 AC2)")
        else:
            exceptions.append(exception)
    return exceptions, problems


def load_licence_policy(text: str) -> tuple[set[str], list[LicenceAllowance], list[str]]:
    """The allowed licences, the packages allowed another licence or read by hand, and the policy's problems."""
    document = policy_yaml.load(text) or {}
    problems: list[str] = []
    if not isinstance(document, dict):
        return set(), [], ["security/licences.yaml: expected 'allowed' and 'packages'"]
    allowed = {str(name) for name in document.get("allowed") or []}
    allowances: list[LicenceAllowance] = []
    for number, entry in enumerate(document.get("packages") or [], start=1):
        if not isinstance(entry, dict) or not all(isinstance(entry.get(key), str) and entry.get(key) for key in ("package", "licence", "reason")):
            problems.append(f"licence allowance {number}: expected package, licence and reason")
            continue
        allowances.append(LicenceAllowance(entry["package"], entry["licence"], " ".join(entry["reason"].split()), str(entry.get("version") or "")))
    return allowed, allowances, problems


def licence_allowed(expression: str, package: str, version: str, allowed: set[str], allowances: list[LicenceAllowance]) -> bool:
    """An SPDX expression is allowed when its OR has an allowed side and its AND only allowed sides; a licence outside
    the allowed list counts only for the packages the policy names with it."""

    def atom(name: str) -> bool:
        return name in allowed or any(a.licence == name and a.covers(package, version) for a in allowances)

    tokens = re.findall(r"\(|\)|[^\s()]+", expression)
    position = 0

    def parse_or() -> bool:
        nonlocal position
        value = parse_and()
        while position < len(tokens) and tokens[position].upper() == "OR":
            position += 1
            right = parse_and()
            value = value or right
        return value

    def parse_and() -> bool:
        nonlocal position
        value = parse_atom()
        while position < len(tokens) and tokens[position].upper() == "AND":
            position += 1
            right = parse_atom()
            value = value and right
        return value

    def parse_atom() -> bool:
        nonlocal position
        if position >= len(tokens):
            return False
        token = tokens[position]
        position += 1
        if token == "(":
            value = parse_or()
            if position < len(tokens) and tokens[position] == ")":
                position += 1
            return value
        if position < len(tokens) and tokens[position].upper() == "WITH":
            position += 2
            return atom(token)
        return atom(token)

    if not tokens:
        return False
    result = parse_or()
    return result and position == len(tokens)


def trivy_findings(report: dict, image: str = "") -> tuple[list[Finding], list[tuple[str, str, str, list[str], str]]]:
    """Findings from a Trivy report, and every package with its licences, its type and where it was found."""
    findings: list[Finding] = []
    packages: list[tuple[str, str, str, list[str], str]] = []
    for result in report.get("Results") or []:
        target = image or result.get("Target", "")
        kind = result.get("Type", "")
        for vulnerability in result.get("Vulnerabilities") or []:
            findings.append(Finding(
                "image" if image else "dependency",
                str(vulnerability.get("Severity", "UNKNOWN")).upper(),
                vulnerability.get("VulnerabilityID", "?"),
                vulnerability.get("PkgName", "?"),
                vulnerability.get("InstalledVersion", ""),
                vulnerability.get("FixedVersion", ""),
                vulnerability.get("Title") or vulnerability.get("Description", "")[:120],
                [target if image else result.get("Target", "")],
            ))
        for misconfiguration in result.get("Misconfigurations") or []:
            if misconfiguration.get("Status", "FAIL") != "FAIL":
                continue
            findings.append(Finding(
                "misconfiguration",
                str(misconfiguration.get("Severity", "UNKNOWN")).upper(),
                misconfiguration.get("ID") or misconfiguration.get("AVDID", "?"),
                result.get("Target", "?"),
                title=misconfiguration.get("Title", ""),
                where=[result.get("Target", "")],
            ))
        if kind in PACKAGE_TYPES and not image:
            for package in result.get("Packages") or []:
                packages.append((package.get("Name", "?"), package.get("Version", ""), kind, list(package.get("Licenses") or []), result.get("Target", "")))
    return findings, packages


def semgrep_findings(report: dict) -> tuple[list[Finding], list[str]]:
    """Findings from a Semgrep report, and the errors that mean a rule did not run."""
    findings = []
    for result in report.get("results") or []:
        rule = str(result.get("check_id", "?")).split(".")[-1]
        extra = result.get("extra") or {}
        severity = RULE_SEVERITIES.get(str(extra.get("severity", "ERROR")).upper(), "HIGH")
        line = (result.get("start") or {}).get("line", 0)
        findings.append(Finding("code", severity, rule, result.get("path", "?"), title=" ".join(str(extra.get("message", "")).split())[:160], where=[f"{result.get('path', '?')}:{line}"]))
    errors = []
    for error in report.get("errors") or []:
        kind = error.get("type")
        kind = kind[0] if isinstance(kind, list) and kind else kind
        if kind not in TOLERATED_RULE_ERRORS:
            errors.append(f"the rule scanner could not run: {kind}: {' '.join(str(error.get('message', '')).split())[:200]}")
    return findings, errors


def merge(findings: list[Finding]) -> list[Finding]:
    """One finding per package, version and identifier, with every place it was found."""
    merged: dict[tuple[str, str, str, str], Finding] = {}
    for item in findings:
        existing = merged.get(item.key())
        if existing is None:
            merged[item.key()] = item
        else:
            existing.where = sorted(set(existing.where) | set(item.where))
    return sorted(merged.values(), key=lambda f: (SEVERITIES.index(f.severity) if f.severity in SEVERITIES else 9, f.source, f.package, f.identifier))


def decide(
    findings: list[Finding],
    packages: list[tuple[str, str, str, list[str], str]],
    exceptions: list[PolicyException],
    exception_problems: list[str],
    allowed: set[str],
    allowances: list[LicenceAllowance],
    licence_problems: list[str],
    scanner_errors: list[str],
) -> Decision:
    failures = [f"exception: {problem}" for problem in exception_problems]
    failures += [f"licence policy: {problem}" for problem in licence_problems]
    failures += scanner_errors
    used: set[int] = set()
    excepted: list[Finding] = []
    merged = merge(findings)
    for item in merged:
        cover = next((index for index, exception in enumerate(exceptions) if exception.covers(item)), None)
        if cover is not None:
            used.add(cover)
            item.excepted_until = exceptions[cover].expires.isoformat()
            excepted.append(item)
        elif item.severity in FAILING:
            failures.append(item.describe())

    licences: dict[tuple[str, str], Finding] = {}
    for name, version, kind, names, target in packages:
        if kind == "dotnet-core":
            continue  # a build's deps.json repeats the lock files' packages without licences
        found = [n for n in names if n]
        if found and all(licence_allowed(n, name, version, allowed, allowances) for n in found):
            continue
        if not found and any(a.covers(name, version) for a in allowances):
            continue
        title = "no licence could be read; read it from the package and record it in security/licences.yaml" if not found else f"licence {' AND '.join(found)} is not allowed by security/licences.yaml"
        key = (name, version)
        if key in licences:
            licences[key].where = sorted(set(licences[key].where) | {target})
            continue
        licence_finding = Finding("licence", "HIGH", " AND ".join(found) or "unknown", name, version, title=title, where=[target])
        cover = next((index for index, exception in enumerate(exceptions) if exception.covers(licence_finding)), None)
        if cover is not None:
            used.add(cover)
            licence_finding.excepted_until = exceptions[cover].expires.isoformat()
            excepted.append(licence_finding)
            continue
        licences[key] = licence_finding
    for item in licences.values():
        failures.append(f"licence of {item.describe()}")
        merged.append(item)
    unused = [exception for index, exception in enumerate(exceptions) if index not in used]
    return Decision(merged, failures, excepted, exceptions, unused)


def summary(decision: Decision, today: date) -> dict[str, object]:
    """What the monthly report reads: open findings by severity and source, and the exceptions with their expiry."""
    counts: dict[str, dict[str, int]] = {}
    for item in decision.findings:
        counts.setdefault(item.severity, {}).setdefault(item.source, 0)
        counts[item.severity][item.source] += 1
    return {
        "format": "iron-security-summary-v1",
        "made": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "today": today.isoformat(),
        "passed": decision.passed,
        "counts": counts,
        "findings": [asdict(item) for item in decision.findings],
        "failures": decision.failures,
        "exceptions": [
            {"finding": e.finding, "package": e.package, "reason": e.reason, "approver": e.approver, "approved": e.approved.isoformat(), "expires": e.expires.isoformat(), "days_left": (e.expires - today).days, "used": e not in decision.unused}
            for e in decision.exceptions
        ],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Fail on a critical or high finding no unexpired exception covers, and on a licence the policy does not allow.")
    parser.add_argument("--trivy", type=Path, action="append", default=[], help="a Trivy JSON report of the repository")
    parser.add_argument("--image", action="append", default=[], help="NAME=REPORT: a Trivy JSON report of an image")
    parser.add_argument("--semgrep", type=Path, help="Semgrep's JSON report")
    parser.add_argument("--exceptions", type=Path, default=EXCEPTIONS)
    parser.add_argument("--licences", type=Path, default=LICENCES)
    parser.add_argument("--summary", type=Path, help="write the summary the monthly report reads")
    parser.add_argument("--today", type=date.fromisoformat, default=datetime.now(timezone.utc).date())
    arguments = parser.parse_args()

    findings: list[Finding] = []
    packages: list[tuple[str, str, str, list[str], str]] = []
    errors: list[str] = []
    for path in arguments.trivy:
        found, listed = trivy_findings(json.loads(path.read_text(encoding="utf-8")))
        findings += found
        packages += listed
    for pair in arguments.image:
        name, _, path = pair.partition("=")
        found, _ = trivy_findings(json.loads(Path(path).read_text(encoding="utf-8")), image=name)
        findings += found
    if arguments.semgrep:
        found, scanner_errors = semgrep_findings(json.loads(arguments.semgrep.read_text(encoding="utf-8")))
        findings += found
        errors += scanner_errors

    try:
        exceptions, exception_problems = load_exceptions(arguments.exceptions.read_text(encoding="utf-8"), arguments.today)
        allowed, allowances, licence_problems = load_licence_policy(arguments.licences.read_text(encoding="utf-8"))
    except policy_yaml.PolicyYamlError as error:
        print(f"Security gate: a policy file could not be read: {error}")
        return 1
    decision = decide(findings, packages, exceptions, exception_problems, allowed, allowances, licence_problems, errors)

    if arguments.summary:
        arguments.summary.parent.mkdir(parents=True, exist_ok=True)
        arguments.summary.write_text(json.dumps(summary(decision, arguments.today), indent=2) + "\n", encoding="utf-8")
    for item in decision.excepted:
        print(f"excepted until {item.excepted_until}: {item.describe()}")
    for exception in decision.unused:
        print(f"note: the exception for {exception.finding} in {exception.package} matched nothing in these reports (until {exception.expires})")
    for failure in decision.failures:
        print(f"FAIL {failure}")
    counts = {severity: sum(1 for f in decision.findings if f.severity == severity) for severity in SEVERITIES}
    shown = ", ".join(f"{severity.lower()} {count}" for severity, count in counts.items() if count)
    distinct = len({(name, version) for name, version, kind, _, _ in packages if kind != "dotnet-core"})
    print(f"Security gate: {len(decision.findings)} findings ({shown or 'none'}), of which {len(decision.excepted)} excepted; {len(decision.failures)} failing; the licences of {distinct} packages checked.")
    return 0 if decision.passed else 1


if __name__ == "__main__":
    sys.exit(main())
