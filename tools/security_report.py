#!/usr/bin/env python3
"""The monthly security report (TF-076, TF-077 AC3, ADR-0055): the open findings per severity from the security gate's
summaries (the repository's scan and the images'), the open exceptions with their expiry, the findings register
(docs/security/findings.md) with what must be escalated, and the location database's pin (ADR-0049).

It writes the report as Markdown and, beside it, a JSON of the issues to open: one per critical or high finding of the
register open for more than 30 days, and one when the location database's pinned month is no longer published. The
monthly workflow (.github/workflows/security-report.yml) opens them, each once, and the report as an issue of its own.

Usage: python3 tools/security_report.py --summary artifacts/security/summary.json [--summary images-summary.json]
  [--check-location] [--today 2026-11-01] --output artifacts/security/report.md
Standard library only.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import urllib.error
import urllib.request
from dataclasses import dataclass
from datetime import date, datetime, timezone
from pathlib import Path
from typing import Callable

sys.dont_write_bytecode = True  # no __pycache__ beside the tools
sys.path.insert(0, str(Path(__file__).resolve().parent))
import security_gate  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
FINDINGS = ROOT / "docs" / "security" / "findings.md"
API_DOCKERFILE = ROOT / "src" / "Hosts" / "Iron.Host.Api" / "Dockerfile"
ESCALATE_AFTER_DAYS = 30
ESCALATED = {"critical", "high"}
SOURCES = ["dependency", "licence", "code", "misconfiguration", "image"]
LOCATIONS_URL = "https://download.db-ip.com/free/dbip-city-lite-{month}.mmdb.gz"


@dataclass
class RegisterFinding:
    identifier: str
    finding: str
    source: str
    severity: str
    opened: date | None
    owner: str
    state: str
    closed: str
    notes: str

    def age(self, today: date) -> int | None:
        return (today - self.opened).days if self.opened else None

    def escalated(self, today: date) -> bool:
        age = self.age(today)
        return self.state == "open" and self.severity in ESCALATED and age is not None and age > ESCALATE_AFTER_DAYS


def read_register(text: str) -> tuple[list[RegisterFinding], list[str]]:
    """The register's rows, by their header's names, and the rows it could not read."""
    lines = text.splitlines()
    header_at = next((i for i, line in enumerate(lines) if line.startswith("| Id |")), None)
    if header_at is None:
        return [], ["docs/security/findings.md has no table headed '| Id |'"]
    names = [cell.strip().lower() for cell in lines[header_at].strip("|").split("|")]
    findings: list[RegisterFinding] = []
    problems: list[str] = []
    for line in lines[header_at + 2:]:
        if not line.startswith("|"):
            break
        cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
        row = dict(zip(names, cells))
        opened_text = row.get("opened", "")
        try:
            opened = date.fromisoformat(opened_text)
        except ValueError:
            opened = None
            problems.append(f"{row.get('id', '?')}: its opening date '{opened_text}' is not a date (YYYY-MM-DD)")
        findings.append(RegisterFinding(row.get("id", "?"), row.get("finding", ""), row.get("source", ""), row.get("severity", "").lower(), opened, row.get("owner", ""), row.get("state", "").lower(), row.get("closed", ""), row.get("notes", "")))
    return findings, problems


def location_pin(dockerfile: str) -> tuple[str | None, str | None]:
    month = re.search(r"^ARG LOCATIONS_MONTH=(\S+)$", dockerfile, re.M)
    digest = re.search(r"^ARG LOCATIONS_SHA256=(\S+)$", dockerfile, re.M)
    return (month.group(1) if month else None, digest.group(1) if digest else None)


def published(month: str) -> str:
    """Whether DB-IP still publishes the month: available, gone (with its status), or unknown (with why)."""
    request = urllib.request.Request(LOCATIONS_URL.format(month=month), method="HEAD", headers={"User-Agent": "ironsaas-security-report"})
    try:
        with urllib.request.urlopen(request, timeout=30) as answer:
            return "available" if answer.status == 200 else f"gone (HTTP {answer.status})"
    except urllib.error.HTTPError as error:
        return f"gone (HTTP {error.code})" if error.code in (403, 404, 410) else f"unknown (HTTP {error.code})"
    except (urllib.error.URLError, TimeoutError) as error:
        return f"unknown ({error})"


def report(summaries: list[dict], register: list[RegisterFinding], register_problems: list[str], exceptions: list[security_gate.PolicyException], exception_problems: list[str], pin: tuple[str | None, str | None], location: str | None, today: date) -> tuple[str, list[dict[str, str]]]:
    """The report's Markdown and the issues to open."""
    findings = [f for s in summaries for f in s.get("findings", [])]
    lines = [f"# Security report, {today:%Y-%m}", "", f"Made on {today.isoformat()} by the monthly workflow (TF-076, TF-077 AC3, ADR-0055).", ""]

    lines += ["## Open findings per severity", "", "From the scans of the repository (dependencies, licences, our rules, misconfigurations) and of the latest release's images. Excepted findings are counted, and listed below with their exception.", ""]
    lines += ["| Severity | " + " | ".join(s.capitalize() for s in SOURCES) + " | All |", "|---|" + "---|" * (len(SOURCES) + 1)]
    for severity in security_gate.SEVERITIES:
        counts = [sum(1 for f in findings if f["severity"] == severity and f["source"] == source) for source in SOURCES]
        lines.append(f"| {severity.capitalize()} | " + " | ".join(str(c) for c in counts) + f" | {sum(counts)} |")
    failing = [failure for s in summaries for failure in s.get("failures", [])]
    lines += ["", "### Failing the build now", ""] + ([f"- {failure}" for failure in failing] or ["None."])
    serious = [f for f in findings if f["severity"] in ("CRITICAL", "HIGH")]
    lines += ["", "### Critical and high findings", ""]
    lines += [f"- {f['severity'].lower()} {f['identifier']} in {f['package']} {f['version']}".rstrip() + (f", excepted until {f['excepted_until']}" if f["excepted_until"] else "") + f" ({', '.join(f['where'][:3])})" for f in serious] or ["None."]

    lines += ["", "## Open exceptions", "", "| Finding | Package | Approved by | Approved | Expires | Days left |", "|---|---|---|---|---|---|"]
    for e in sorted(exceptions, key=lambda e: e.expires):
        left = (e.expires - today).days
        lines.append(f"| {e.finding} | {e.package} | {e.approver} | {e.approved} | {e.expires} | {left}{' (renew or close it this month)' if left <= 30 else ''} |")
    if not exceptions:
        lines.append("| None | | | | | |")
    if exception_problems:
        lines += ["", "Problems of the register, which fail the build:", ""] + [f"- {p}" for p in exception_problems]

    issues: list[dict[str, str]] = []
    lines += ["", "## Findings register", "", "Penetration tests and reviews (docs/security/findings.md). A critical or high finding open for more than 30 days is escalated.", ""]
    still_open = [f for f in register if f.state == "open"]
    if still_open:
        lines += ["| Id | Finding | Source | Severity | Opened | Days open | Owner | Escalated |", "|---|---|---|---|---|---|---|---|"]
        for f in still_open:
            lines.append(f"| {f.identifier} | {f.finding} | {f.source} | {f.severity} | {f.opened} | {f.age(today)} | {f.owner} | {'yes' if f.escalated(today) else 'no'} |")
    else:
        lines.append("No open finding.")
    closed = sum(1 for f in register if f.state != "open")
    lines += ["", f"{len(still_open)} open, {closed} closed."]
    if register_problems:
        lines += ["", "Rows the report could not read:", ""] + [f"- {p}" for p in register_problems]
    for f in register:
        if f.escalated(today):
            issues.append({
                "title": f"Escalated: {f.identifier} {f.severity} finding open {f.age(today)} days",
                "key": f"Escalated: {f.identifier} ",
                "body": f"{f.identifier}, rated {f.severity} by {f.source}, has been open since {f.opened} ({f.age(today)} days), longer than the {ESCALATE_AFTER_DAYS} days a {f.severity} finding may stay open (TF-077 AC3).\n\nFinding: {f.finding}\nOwner: {f.owner}\nNotes: {f.notes or 'none'}\n\nFix it, or accept it with an exception and its expiry, and update docs/security/findings.md.",
            })

    month, digest = pin
    lines += ["", "## The location database", ""]
    if month is None:
        lines.append("The API's Dockerfile pins no LOCATIONS_MONTH.")
    else:
        lines.append(f"The API image pins DB-IP's IP to City Lite of {month} (SHA-256 `{digest}`): {location or 'not checked'}.")
        if location and location.startswith("gone"):
            lines.append("")
            lines.append("The month is gone: move LOCATIONS_MONTH and LOCATIONS_SHA256 to a published month, as the runbook says, before the next release builds the API image.")
            issues.append({
                "title": f"The location database's month {month} is no longer published",
                "key": f"The location database's month {month} ",
                "body": f"DB-IP no longer publishes IP to City Lite of {month}, which src/Hosts/Iron.Host.Api/Dockerfile pins ({location}). The next API image will not build. Move LOCATIONS_MONTH and LOCATIONS_SHA256 to a published month, as the runbook says (ADR-0049).",
            })
    return "\n".join(lines) + "\n", issues


def main(argv: list[str] | None = None, check: Callable[[str], str] = published) -> int:
    parser = argparse.ArgumentParser(description="Write the monthly security report and the issues to open.")
    parser.add_argument("--summary", type=Path, action="append", default=[], help="a summary the security gate wrote (--summary)")
    parser.add_argument("--findings", type=Path, default=FINDINGS)
    parser.add_argument("--exceptions", type=Path, default=security_gate.EXCEPTIONS)
    parser.add_argument("--dockerfile", type=Path, default=API_DOCKERFILE)
    parser.add_argument("--check-location", action="store_true", help="ask DB-IP whether the pinned month is still published")
    parser.add_argument("--today", type=date.fromisoformat, default=datetime.now(timezone.utc).date())
    parser.add_argument("--output", type=Path, required=True, help="the report's Markdown; the issues go beside it as <name>.issues.json")
    arguments = parser.parse_args(argv)

    summaries = [json.loads(path.read_text(encoding="utf-8")) for path in arguments.summary if path.exists()]
    register, register_problems = read_register(arguments.findings.read_text(encoding="utf-8"))
    exceptions, exception_problems = security_gate.load_exceptions(arguments.exceptions.read_text(encoding="utf-8"), arguments.today)
    pin = location_pin(arguments.dockerfile.read_text(encoding="utf-8"))
    location = check(pin[0]) if arguments.check_location and pin[0] else None
    text, issues = report(summaries, register, register_problems, exceptions, exception_problems, pin, location, arguments.today)

    arguments.output.parent.mkdir(parents=True, exist_ok=True)
    arguments.output.write_text(text, encoding="utf-8")
    arguments.output.with_suffix(".issues.json").write_text(json.dumps(issues, indent=2) + "\n", encoding="utf-8")
    print(f"security report at {arguments.output}: {sum(len(s.get('findings', [])) for s in summaries)} findings, {len(exceptions)} exceptions, {sum(1 for f in register if f.state == 'open')} open in the register, {len(issues)} issues to open")
    return 0


if __name__ == "__main__":
    sys.exit(main())
