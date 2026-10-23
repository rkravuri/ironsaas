#!/usr/bin/env python3
"""Builds a release's validation pack (TF-218, ADR-0052) from the traceability matrix of its run (tools/trace.py,
ADR-0044), the repository's history since the last release, the risk classes of the requirements, and the installation
record the release's integration stage wrote.

The pack is a folder and a ZIP of it, `validation-pack-<version>.zip`, holding:

- traceability: every requirement of every requirements document with its criteria, the tests that prove each one and
  their results from this run (AC1), and each criterion's state; a done slice's criterion without a test or an approved
  exception (docs/trace/exceptions.md) is a gap, flagged (AC2);
- release notes: the version, the commit, the merged tasks and commits since the last release, and the requirements
  each touched;
- risk assessment: each change classified by the risk of the requirements it touched (critical, high or normal, from
  docs/trace/risk.md), with the written assessment its task committed under docs/risk/;
- installation record: the images' digests, the migrations applied per module, the seeds' and the catalogue's hashes,
  the smoke's and the browser tests' results, and the checks that the installation matches the infrastructure code;
- configuration: the hashes of the seeds and of the catalogue;
- a manifest with every file's SHA-256, as an evidence pack has.

Each document comes as JSON to read by machine and as one readable page (HTML); the traceability also as CSV. The pack is
always written, and the command fails, naming why, when the release must not be published: a gap, a failed test, a
problem the trace found, or an installation check that did not pass. Standard library only, as tools/trace.py.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import html
import io
import json
import re
import subprocess
import sys
import zipfile
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path

sys.dont_write_bytecode = True  # no __pycache__ beside the tools
sys.path.insert(0, str(Path(__file__).resolve().parent))
import trace  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
OUTPUT = ROOT / "artifacts" / "validation"
FORMAT = "iron-validation-pack-v1"
DEVELOPMENT = "development"

# A release's version: its UTC date and the release workflow's run number (ADR-0052).
VERSION = re.compile(r"^\d{4}\.\d{2}\.\d{2}\.\d+$")
RISKS = ["critical", "high", "normal"]
REQUIREMENT_ID = re.compile(r"\b([A-Z]{2,3})-(\d{3})\b")
PRD_HEADING = re.compile(r"^#### ([A-Z]{2,3}-\d{3}) ")
HUNK = re.compile(r"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@")
# How far above a test's declaration its attributes or its title reach: a changed line there is that test's.
ABOVE = 6


# ---------------------------------------------------------------- risk


def risk_classes(path: Path) -> dict[str, str]:
    """The areas docs/trace/risk.md classes as critical, from its table (| Area | Class | Why |)."""
    classes: dict[str, str] = {}
    if not path.exists():
        return classes
    for line in path.read_text(encoding="utf-8").splitlines():
        cells = [cell.strip() for cell in line.strip().strip("|").split("|")] if line.startswith("|") else []
        if len(cells) >= 2 and cells[1].lower() in RISKS:
            classes[cells[0]] = cells[1].lower()
    return classes


def requirement_risk(requirement: trace.Requirement, classes: dict[str, str]) -> str:
    """Critical where its area is listed so; otherwise high for a Must and normal for a Should or a Could."""
    if requirement.area in classes:
        return classes[requirement.area]
    return "high" if requirement.priority == "Must" else "normal"


def highest(risks: list[str]) -> str:
    return min(risks, key=RISKS.index) if risks else "normal"


# ---------------------------------------------------------------- traceability


def criterion_state(tests: list[trace.Test], excepted: bool, in_done: bool) -> str:
    """passed, failed or not run by the tests that prove it; excepted, a gap in a done slice, or untested."""
    if tests:
        statuses = {t.status for t in tests}
        if "failed" in statuses:
            return "failed"
        if "passed" in statuses:
            return "passed"
        return "not run"
    if excepted:
        return "excepted"
    return "gap" if in_done else "untested"


def traceability(result: trace.Trace, classes: dict[str, str]) -> dict[str, object]:
    done = {identifier for s in result.slices if s.done for identifier in s.requirements}
    requirements = []
    for requirement in result.requirements.values():
        criteria = []
        for number, criterion in enumerate(trace.criteria_of(requirement), start=1):
            tests = result.proven.get(criterion, [])
            exception = result.excepted.get(criterion)
            criteria.append(
                {
                    "id": criterion,
                    "text": requirement.criteria[number - 1],
                    "state": criterion_state(tests, exception is not None, requirement.id in done),
                    "tests": [{"kind": t.kind, "test": t.label, "file": f"{t.file}:{t.line}", "status": t.status, "results": t.results} for t in tests],
                    "exception": {"reason": exception.reason, "approved": exception.approved} if exception else None,
                }
            )
        requirements.append(
            {
                "id": requirement.id,
                "title": requirement.title,
                "document": requirement.document,
                "area": requirement.area,
                "priority": requirement.priority,
                "risk": requirement_risk(requirement, classes),
                "in_done_slice": requirement.id in done,
                "criteria": criteria,
            }
        )
    states = [c["state"] for r in requirements for c in r["criteria"]]  # type: ignore[index]
    return {
        "summary": {
            "requirements": len(requirements),
            "criteria": len(states),
            **{state: states.count(state) for state in ["passed", "failed", "not run", "excepted", "gap", "untested"]},
            "done_slices": [s.id for s in result.slices if s.done],
            "untraced_tests": sum(1 for t in result.tests if t.untraced is not None),
            "trace_problems": result.problems,
        },
        "gaps": [c["id"] for r in requirements for c in r["criteria"] if c["state"] == "gap"],  # type: ignore[index]
        "failed": [c["id"] for r in requirements for c in r["criteria"] if c["state"] == "failed"],  # type: ignore[index]
        "requirements": requirements,
    }


def traceability_csv(document: dict[str, object]) -> str:
    out = io.StringIO()
    writer = csv.writer(out, quoting=csv.QUOTE_ALL, lineterminator="\n")
    writer.writerow(["requirement", "title", "document", "risk", "criterion", "state", "tests", "results", "exception"])
    for requirement in document["requirements"]:  # type: ignore[union-attr]
        for criterion in requirement["criteria"]:
            exception = criterion["exception"]
            writer.writerow(
                [
                    requirement["id"],
                    requirement["title"],
                    requirement["document"],
                    requirement["risk"],
                    criterion["id"],
                    criterion["state"],
                    "; ".join(t["test"] for t in criterion["tests"]),
                    "; ".join(t["status"] for t in criterion["tests"]),
                    f"{exception['reason']} (approved {exception['approved']})" if exception else "",
                ]
            )
    return out.getvalue()


# ---------------------------------------------------------------- history


def git(root: Path, *args: str) -> str:
    return subprocess.run(["git", *args], cwd=root, capture_output=True, text=True, check=True, encoding="utf-8").stdout


def previous_release(root: Path, head: str, version: str) -> str | None:
    """The newest release tag (v<version>) that the head contains, other than this release's own."""
    try:
        tags = git(root, "tag", "--merged", head, "--list", "v[0-9]*", "--sort=-v:refname").split()
    except subprocess.CalledProcessError:
        return None
    return next((tag for tag in tags if tag != f"v{version}" and VERSION.match(tag[1:])), None)


@dataclass
class Change:
    kind: str  # "task" for a merge, "commit" for a commit made on main itself
    name: str  # the branch a merge brought in, or the commit's subject
    commit: str
    subject: str
    commits: list[tuple[str, str]]  # (short hash, subject) of the commits it brought
    requirements: list[str] = field(default_factory=list)
    risk: str = "normal"
    assessment: str | None = None
    files: int = 0


MERGE_NAME = re.compile(r"^Merge (?:branch '([^']+)'|(slice/[\w./-]+)|pull request #\d+ from [\w.-]+/([\w./-]+))")


def changes(root: Path, base: str | None, head: str) -> list[Change]:
    """The first-parent history from the last release to the head, oldest first: each merge is a task with its commits."""
    span = f"{base}..{head}" if base else head
    found = []
    for record in git(root, "log", "--first-parent", "--reverse", "--format=%H%x1f%P%x1f%s%x1e", span).split("\x1e"):
        record = record.strip()
        if not record:
            continue
        commit, parents, subject = record.split("\x1f")
        parents_list = parents.split()
        if len(parents_list) > 1:
            match = MERGE_NAME.match(subject)
            name = next((group for group in match.groups() if group), subject) if match else subject
            brought = [tuple(line.split("\x1f", 1)) for line in git(root, "log", "--reverse", "--format=%h%x1f%s", f"{parents_list[0]}..{parents_list[1]}").splitlines() if line]
            found.append(Change("task", name, commit, subject, brought))  # type: ignore[arg-type]
        else:
            found.append(Change("commit", subject, commit, subject, [(commit[:7], subject)]))
    return found


def changed_lines(root: Path, parent: str | None, commit: str) -> dict[str, set[int]]:
    """For each file a change touched, the lines of its new version that it added or changed (a removal marks its place)."""
    if parent is None:
        parent = git(root, "hash-object", "-t", "tree", "/dev/null").strip()
    lines: dict[str, set[int]] = {}
    current = None
    for line in git(root, "diff", "--unified=0", "--no-renames", parent, commit).splitlines():
        if line.startswith("+++ "):
            current = None if line == "+++ /dev/null" else line[6:] if line.startswith("+++ b/") else line[4:]
            if current is not None:
                lines.setdefault(current, set())
        elif current is not None and (match := HUNK.match(line)):
            start, count = int(match.group(1)), int(match.group(2) or "1")
            lines[current].update(range(start, start + count) if count else {max(start, 1)})
    return lines


def tests_touched(tests: list[trace.Test], changed: dict[str, set[int]]) -> list[trace.Test]:
    """The tests whose lines a change touched: its title or attributes just above its declaration, or its body below."""
    by_file: dict[str, list[trace.Test]] = {}
    for test in tests:
        by_file.setdefault(test.file, []).append(test)
    touched = []
    for path, numbers in changed.items():
        in_file = sorted(by_file.get(path, []), key=lambda t: t.line)
        for number in numbers:
            owner = next((t for t in in_file if 0 <= t.line - number <= ABOVE), None)
            if owner is None:
                owner = next((t for t in reversed(in_file) if t.line < number), None)
            if owner is not None and owner not in touched:
                touched.append(owner)
    return touched


def requirements_in_text(root: Path, commit: str, changed: dict[str, set[int]]) -> set[str]:
    """The requirements whose text a change touched in a requirements document: the heading above each changed line."""
    found: set[str] = set()
    for path, numbers in changed.items():
        if not (path.startswith("docs/prd/") and path.endswith(".md")) or not numbers:
            continue
        try:
            text = git(root, "show", f"{commit}:{path}")
        except subprocess.CalledProcessError:
            continue
        heading = None
        headings: dict[int, str | None] = {}
        for number, line in enumerate(text.splitlines(), start=1):
            if line.startswith("#"):
                match = PRD_HEADING.match(line)
                heading = match.group(1) if match else None
            headings[number] = heading
        found |= {headings[n] for n in numbers if headings.get(n)}  # type: ignore[misc]
    return found


def classify(root: Path, change: Change, result: trace.Trace, classes: dict[str, str], notes: Path) -> None:
    """The requirements a change touched (its tests, its requirement text, the identifiers its messages name), its risk
    as the highest of theirs, and the written assessment its task committed (docs/risk/<task>.md)."""
    parents = git(root, "rev-list", "--parents", "-n", "1", change.commit).split()[1:]
    changed = changed_lines(root, parents[0] if parents else None, change.commit)
    change.files = len(changed)
    named = {requirement for test in tests_touched(result.tests, changed) for identifier in test.proves if (requirement := identifier.split(" ")[0]) in result.requirements}
    named |= requirements_in_text(root, change.commit, changed) & result.requirements.keys()
    messages = git(root, "log", "--format=%B", f"{parents[0]}..{change.commit}" if parents else change.commit)
    named |= {f"{m.group(1)}-{m.group(2)}" for m in REQUIREMENT_ID.finditer(messages)} & result.requirements.keys()
    change.requirements = sorted(named)
    change.risk = highest([requirement_risk(result.requirements[r], classes) for r in change.requirements])
    if change.kind == "task":
        note = notes / f"{change.name.removeprefix('slice/')}.md"
        try:
            change.assessment = git(root, "show", f"{change.commit}:{note.relative_to(root).as_posix()}").strip() or None
        except (subprocess.CalledProcessError, ValueError):
            change.assessment = None


# ---------------------------------------------------------------- the pack


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def installation_problems(record: dict[str, object] | None) -> list[str]:
    if record is None:
        return ["no installation record: the release's integration stage wrote none"]
    return [f"installation check '{check.get('name')}' did not pass: {check.get('detail', '')}".rstrip(": ") for check in record.get("checks", []) if not check.get("passed")]  # type: ignore[union-attr]


def configuration(root: Path, record: dict[str, object] | None) -> dict[str, object]:
    """The controlled configuration a tenant runs (TF-218): the catalogue as the repository holds it, and the hashes the
    installation measured, the seeds' among them."""
    catalogue = root / "src" / "Kernel" / "Iron.Kernel.Tenancy" / "Services" / "licensing-catalogue.json"
    measured = (record or {}).get("configuration", {})
    return {
        "catalogue": {"path": catalogue.relative_to(root).as_posix(), "sha256": sha256(catalogue.read_bytes()) if catalogue.exists() else None},
        "measured": measured,
    }


@dataclass
class Pack:
    version: str
    commit: str
    built_at: str
    previous: str | None
    documents: dict[str, dict[str, object]]
    problems: list[str]
    files: dict[str, bytes] = field(default_factory=dict)

    @property
    def passed(self) -> bool:
        return not self.problems


def build(root: Path = ROOT, version: str = DEVELOPMENT, results: Path | None = None, installation: Path | None = None, head: str = "HEAD", base: str | None = None, exceptions_path: Path | None = None) -> Pack:
    result = trace.build(root, results, exceptions_path)
    classes = risk_classes(root / "docs" / "trace" / "risk.md")
    commit = git(root, "rev-parse", head).strip()
    previous = base if base is not None else previous_release(root, head, version)
    record = json.loads(installation.read_text(encoding="utf-8")) if installation and installation.exists() else None

    trace_document = traceability(result, classes)
    found = changes(root, previous, head)
    for change in found:
        classify(root, change, result, classes, root / "docs" / "risk")

    problems = [f"a gap: {criterion} of a done slice has no test and no approved exception" for criterion in trace_document["gaps"]]  # type: ignore[union-attr]
    problems += [f"a failed test proves {criterion}" for criterion in trace_document["failed"]]  # type: ignore[union-attr]
    problems += [f"the trace: {problem}" for problem in result.problems if "has no test and no exception" not in problem]
    problems += installation_problems(record)

    built_at = datetime.now(timezone.utc).isoformat(timespec="seconds")
    notes = {
        "version": version,
        "commit": commit,
        "previous_release": previous,
        "changes": [{"kind": c.kind, "name": c.name, "commit": c.commit[:12], "subject": c.subject, "commits": [{"commit": h, "subject": s} for h, s in c.commits], "requirements": c.requirements} for c in found],
        "requirements_touched": sorted({r for c in found for r in c.requirements}),
    }
    risk = {
        "classes": {"critical": sorted(a for a, c in classes.items() if c == "critical"), "rule": "An area docs/trace/risk.md lists is its class; otherwise a Must is high and a Should or a Could normal. A change takes the highest class of the requirements it touched; one that touched none is normal."},
        "changes": [{"name": c.name, "kind": c.kind, "commit": c.commit[:12], "risk": c.risk, "requirements": [{"id": r, "risk": requirement_risk(result.requirements[r], classes)} for r in c.requirements], "files": c.files, "assessment": c.assessment} for c in found],
        "summary": {risk_class: sum(1 for c in found if c.risk == risk_class) for risk_class in RISKS},
    }
    documents = {
        "traceability": trace_document,
        "release-notes": notes,
        "risk-assessment": risk,
        "installation-record": record or {"missing": True},
        "configuration": configuration(root, record),
    }
    pack = Pack(version, commit, built_at, previous, documents, problems)
    header = {"format": FORMAT, "version": version, "commit": commit, "built_at": built_at}
    for name, document in documents.items():
        pack.files[f"{name}.json"] = (json.dumps({**header, name.replace("-", "_"): document}, indent=2, ensure_ascii=False) + "\n").encode("utf-8")
        pack.files[f"{name}.html"] = page(pack, name, document).encode("utf-8")
    pack.files["traceability.csv"] = traceability_csv(trace_document).encode("utf-8")
    pack.files["index.html"] = index_page(pack).encode("utf-8")
    pack.files["manifest.json"] = manifest(pack)
    return pack


def manifest(pack: Pack) -> bytes:
    """Every file's SHA-256 and size, so anyone can check that nothing in the pack changed (ADR-0052)."""
    files = [{"path": name, "sha256": sha256(data), "bytes": len(data)} for name, data in sorted(pack.files.items()) if name != "manifest.json"]
    body = {"format": FORMAT, "version": pack.version, "commit": pack.commit, "previous_release": pack.previous, "built_at": pack.built_at, "passed": pack.passed, "problems": pack.problems, "files": files}
    return (json.dumps(body, indent=2, ensure_ascii=False) + "\n").encode("utf-8")


def write(pack: Pack, output: Path) -> Path:
    folder = output / pack.version
    folder.mkdir(parents=True, exist_ok=True)
    for name, data in pack.files.items():
        (folder / name).write_bytes(data)
    archive = output / f"validation-pack-{pack.version}.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as zipped:
        for name, data in sorted(pack.files.items()):
            # A fixed time keeps the archive the same for the same files.
            zipped.writestr(zipfile.ZipInfo(f"validation-pack-{pack.version}/{name}", date_time=(2026, 1, 1, 0, 0, 0)), data)
    return archive


# ---------------------------------------------------------------- the readable pages


STYLE = """body{font-family:system-ui,sans-serif;margin:2rem auto;max-width:72rem;padding:0 1rem;color:#1b1f24;background:#fff;line-height:1.45}
h1{font-size:1.6rem}h2{font-size:1.25rem;margin-top:2rem}h3{font-size:1.05rem}table{border-collapse:collapse;width:100%;margin:.75rem 0}
th,td{border:1px solid #c9ced6;padding:.35rem .5rem;text-align:left;vertical-align:top}th{background:#eef1f5}
.state-gap,.state-failed,.verdict-failed{font-weight:700;color:#8a1c12}.state-passed,.verdict-passed{color:#1d5e2b}
.muted{color:#5a6270}code{font-size:.9em}pre{white-space:pre-wrap}"""

TITLES = {
    "traceability": "Traceability",
    "release-notes": "Release notes",
    "risk-assessment": "Risk assessment",
    "installation-record": "Installation record",
    "configuration": "Configuration",
}


def e(value: object) -> str:
    return html.escape("" if value is None else str(value))


def table(headers: list[str], rows: list[list[object]], raw: bool = False) -> str:
    head = "".join(f"<th scope=\"col\">{e(h)}</th>" for h in headers)
    body = "".join("<tr>" + "".join(f"<td>{cell if raw else e(cell)}</td>" for cell in row) + "</tr>" for row in rows)
    return f"<table><thead><tr>{head}</tr></thead><tbody>{body}</tbody></table>"


def frame(pack: Pack, title: str, body: str) -> str:
    verdict = "passed" if pack.passed else "failed"
    return (
        f"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>{e(title)}: {e(pack.version)}</title><style>{STYLE}</style></head><body>"
        f"<h1>{e(title)}</h1><p>Version <strong>{e(pack.version)}</strong>, commit <code>{e(pack.commit[:12])}</code>, built {e(pack.built_at)}. "
        f"The pack <span class=\"verdict-{verdict}\">{'passed' if pack.passed else 'did not pass'}</span>.</p>{body}</body></html>\n"
    )


def index_page(pack: Pack) -> str:
    links = "".join(f"<li><a href=\"{name}.html\">{e(title)}</a> (<a href=\"{name}.json\">JSON</a>)</li>" for name, title in TITLES.items())
    problems = "".join(f"<li>{e(p)}</li>" for p in pack.problems) or "<li>None.</li>"
    previous = f"since release {e(pack.previous)}" if pack.previous else "since the start of the history"
    body = f"<p>The validation pack of this release, {previous} (TF-218, ADR-0052). <code>manifest.json</code> holds every file's SHA-256.</p><ul>{links}<li><a href=\"traceability.csv\">Traceability as CSV</a></li></ul><h2>Why the release may not be published</h2><ul>{problems}</ul>"
    return frame(pack, "Validation pack", body)


def page(pack: Pack, name: str, document: dict[str, object]) -> str:
    title = TITLES[name]
    if name == "traceability":
        s = document["summary"]  # type: ignore[index]
        body = "<p>Every requirement of every requirements document with its criteria, the tests that prove each one and their results in this release's run. A gap is a criterion of a done slice with no test and no approved exception; it fails the release.</p>"
        body += table(["Requirements", "Criteria", "Passed", "Failed", "Not run", "Excepted", "Gaps", "Untested (not in a done slice)"], [[s["requirements"], s["criteria"], s["passed"], s["failed"], s["not run"], s["excepted"], s["gap"], s["untested"]]])  # type: ignore[index]
        if document["gaps"]:
            body += "<h2>Gaps</h2><ul>" + "".join(f"<li class=\"state-gap\">{e(g)}</li>" for g in document["gaps"]) + "</ul>"  # type: ignore[union-attr]
        for requirement in document["requirements"]:  # type: ignore[union-attr]
            rows = []
            for c in requirement["criteria"]:
                tests = "<br>".join(f"{e(t['kind'])}: {e(t['test'])} ({e(t['status'])})" for t in c["tests"])
                if c["exception"]:
                    tests = f"Exception approved {e(c['exception']['approved'])}: {e(c['exception']['reason'])}"
                rows.append([e(c["id"]), e(c["text"]), f"<span class=\"state-{e(c['state']).replace(' ', '-')}\">{e(c['state'])}</span>", tests])
            body += f"<h3 id=\"{e(requirement['id'])}\">{e(requirement['id'])} {e(requirement['title'])}</h3><p class=\"muted\">{e(requirement['document'])}, {e(requirement['area'])}, {e(requirement['priority'])}, risk {e(requirement['risk'])}</p>"
            body += table(["Criterion", "Text", "State", "Tests and results"], rows, raw=True)
        return frame(pack, title, body)
    if name == "release-notes":
        body = f"<p>Since {e(document['previous_release']) if document['previous_release'] else 'the start of the history'}.</p>"
        rows = [[c["kind"], c["name"], c["commit"], "<br>".join(f"{e(x['commit'])} {e(x['subject'])}" for x in c["commits"]), ", ".join(c["requirements"]) or "none"] for c in document["changes"]]  # type: ignore[union-attr]
        body += table(["Kind", "Change", "Commit", "Commits", "Requirements touched"], [[e(r[0]), e(r[1]), e(r[2]), r[3], e(r[4])] for r in rows], raw=True)
        body += f"<h2>Requirements touched</h2><p>{e(', '.join(document['requirements_touched']) or 'None.')}</p>"  # type: ignore[arg-type]
        return frame(pack, title, body)
    if name == "risk-assessment":
        body = f"<p>{e(document['classes']['rule'])} Critical areas: {e(', '.join(document['classes']['critical']))}.</p>"  # type: ignore[index]
        for change in document["changes"]:  # type: ignore[union-attr]
            requirements = ", ".join(f"{r['id']} ({r['risk']})" for r in change["requirements"]) or "none"
            assessment = f"<pre>{e(change['assessment'])}</pre>" if change["assessment"] else "<p class=\"muted\">No written assessment: the change committed none under docs/risk.</p>" if change["kind"] == "task" else ""
            body += f"<h2>{e(change['name'])}: {e(change['risk'])}</h2><p>Commit <code>{e(change['commit'])}</code>, {e(change['files'])} files changed. Requirements touched: {e(requirements)}.</p>{assessment}"
        return frame(pack, title, body)
    if name == "installation-record":
        if document.get("missing"):
            return frame(pack, title, "<p class=\"state-failed\">No installation record: the release's integration stage wrote none.</p>")
        body = f"<p>Tenant <code>{e(document.get('tenant'))}</code>, recorded {e(document.get('recorded_at'))}.</p><h2>Checks against the infrastructure code</h2>"
        body += table(["Check", "Passed", "Detail"], [[c.get("name"), "yes" if c.get("passed") else "no", c.get("detail", "")] for c in document.get("checks", [])])  # type: ignore[union-attr]
        body += "<h2>Images</h2>" + table(["Image", "Reference", "Digest"], [[i.get("name"), i.get("reference"), i.get("digest")] for i in document.get("images", [])])  # type: ignore[union-attr]
        body += "<h2>Migrations applied per module</h2>" + table(["Schema", "Migrations"], [[schema, ", ".join(ids)] for schema, ids in sorted(document.get("migrations", {}).items())])  # type: ignore[union-attr]
        body += "<h2>Configuration measured</h2>" + table(["Item", "SHA-256"], [[k, v] for k, v in sorted(document.get("configuration", {}).items())])  # type: ignore[union-attr]
        smoke = document.get("smoke", {})
        body += "<h2>Smoke</h2>" + table(["Step", "Result"], [[s.get("name"), s.get("status")] for s in smoke.get("steps", [])])  # type: ignore[union-attr]
        browser = document.get("browser_tests", {})
        body += "<h2>Browser tests</h2>" + table(["Passed", "Failed", "Skipped"], [[browser.get("passed"), browser.get("failed"), browser.get("skipped")]])  # type: ignore[union-attr]
        return frame(pack, title, body)
    catalogue = document["catalogue"]  # type: ignore[index]
    body = f"<p>The controlled configuration of this release: what the units license, switch and set (the catalogue), and what the seeds put into every tenant.</p>{table(['Item', 'SHA-256'], [['catalogue (' + catalogue['path'] + ')', catalogue['sha256']]] + [[k, v] for k, v in sorted(document['measured'].items())])}"  # type: ignore[index, union-attr]
    return frame(pack, title, body)


def main(argv: list[str] | None = None) -> int:
    return main_for(ROOT, argv)


def main_for(root: Path, argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Build a release's validation pack (TF-218, ADR-0052).")
    parser.add_argument("--version", default=DEVELOPMENT, help="the release's version, or development on a laptop")
    parser.add_argument("--results", type=Path, default=root / "artifacts" / "test-results", help="the result files' folder (default artifacts/test-results)")
    parser.add_argument("--installation", type=Path, default=None, help="the installation record the integration stage wrote")
    parser.add_argument("--output", type=Path, default=root / "artifacts" / "validation", help="where the pack goes (default artifacts/validation)")
    parser.add_argument("--base", default=None, help="the last release's tag (default: the newest v* tag the head holds)")
    arguments = parser.parse_args(argv)
    if arguments.version != DEVELOPMENT and not VERSION.match(arguments.version):
        print(f"'{arguments.version}' is not a release version (2026.10.23.57) nor {DEVELOPMENT}")
        return 2
    pack = build(root, arguments.version, arguments.results, arguments.installation, base=arguments.base)
    archive = write(pack, arguments.output)
    summary = pack.documents["traceability"]["summary"]  # type: ignore[index]
    print(f"validation pack {pack.version} at {trace.shown(archive, root)}: {summary['requirements']} requirements, {summary['criteria']} criteria, {summary['passed']} passed, {summary['failed']} failed, {summary['excepted']} excepted, {summary['gap']} gaps")  # type: ignore[index]
    print(f"changes since {pack.previous or 'the start'}: {len(pack.documents['release-notes']['changes'])}")  # type: ignore[arg-type]
    for problem in pack.problems:
        print(problem)
    if pack.problems:
        print(f"\nvalidation pack: {len(pack.problems)} problems; the release may not be published")
        return 1
    print("validation pack: passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
