#!/usr/bin/env python3
"""Builds the traceability matrix (TF-218, ADR-0044): which tests prove which requirement criteria and decisions, with
each test's result from the last run, and which criteria of a done slice no test proves.

Every test names what it proves, in the form of its kind:

- .NET: `[Proves("TF-033 AC2")]` on the test method (`Iron.Testing`), or `[Untraced("why")]`;
- Vitest and Playwright: the title ends with the bracketed list, `"... [TF-033 AC2, ADR-0029]"`, or `[untraced]`;
- the smoke: each step lists what it proves, `report.StepAsync("login", ["TF-011 AC1"], ...)`;
- Python: the test's docstring ends with the bracketed list.

An identifier is a requirement's criterion (`TF-033 AC2`, `REQ-012 AC1`), a whole requirement when the test proves
every criterion (`TF-033`), or a decision (`ADR-0029`).

The tests and what they prove are read from the sources, so a test that did not run is still in the matrix; the results
are read from the result files of the last run under artifacts/test-results and joined to them: TRX from `make test`,
JUnit from Vitest, the browser tests and the accessibility scan, the smoke's report, and JUnit from these Python tests.
The matrix goes to artifacts/trace (not committed): matrix.md to read, matrix.json for the validation pack (T8).

The command fails, naming each problem, when:

- a test names an identifier no document holds: an unknown prefix, requirement or criterion, or an ADR that does not
  exist; or names nothing;
- a requirement of a done slice (every task of its plan done, the requirements from its coverage table) has a criterion
  no test proves, unless docs/trace/exceptions.md lists it with its reason and the date it was approved;
- an exception names no criterion of a done slice, lacks its reason or date, or is proven by a test now;
- a result names a test the sources do not declare, which means this reader could not read that test.

Standard library only, as tools/prd_check.py, whose parser it shares. Exit status 1 when anything is wrong.
"""

from __future__ import annotations

import argparse
import ast
import json
import re
import subprocess
import sys
import xml.etree.ElementTree as ElementTree
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path

sys.dont_write_bytecode = True  # no __pycache__ beside the tools
sys.path.insert(0, str(Path(__file__).resolve().parent))
import prd_check  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
ADRS = ROOT / "docs" / "adr"
SLICES = ROOT / "docs" / "slices"
EXCEPTIONS = ROOT / "docs" / "trace" / "exceptions.md"
RESULTS = ROOT / "artifacts" / "test-results"
OUTPUT = ROOT / "artifacts" / "trace"

KINDS = ["dotnet", "vitest", "e2e", "a11y", "smoke", "python"]
KIND_NAMES = {
    "dotnet": ".NET (make test)",
    "vitest": "Vitest (make shell-check)",
    "e2e": "Browser tests (make e2e)",
    "a11y": "Accessibility scan (make shell-a11y)",
    "smoke": "Smoke (make smoke)",
    "python": "Python tools (make prd-check, make trace)",
}

# The one grammar of what a test proves; the analyzer (IRON0006) and the shell's lint rule hold the same.
IDENTIFIER = re.compile(r"^(?:([A-Z]{2,3})-(\d{3})(?: AC([1-9]\d*))?|ADR-(\d{4}))$")
UNTRACED = "untraced"
TAGS = re.compile(r"\[([^\[\]]+)\]\s*$")
DATE = re.compile(r"^\d{4}-\d{2}-\d{2}$")


# ---------------------------------------------------------------- the tests


@dataclass
class Test:
    kind: str
    group: str  # the .NET project, the shell file, "smoke", the Python file
    name: str  # what the matrix shows: Class.Method, the title without its tags, the step
    file: str
    line: int
    proves: list[str]
    untraced: str | None = None  # the reason, or "" when a title or docstring says only [untraced]
    key: str = ""  # what a result is joined on
    pattern: re.Pattern[str] | None = None  # for titles with placeholders, which the results show filled in
    results: list[str] = field(default_factory=list)

    @property
    def label(self) -> str:
        return f"{self.group} › {self.name}"

    @property
    def status(self) -> str:
        if not self.results:
            return "not run"
        if "failed" in self.results:
            return "failed"
        if "passed" in self.results:
            return "passed"
        return "skipped"


def split_tags(text: str) -> tuple[str, list[str] | None]:
    """A title or docstring without its trailing bracketed list, and the list (None when there is none)."""
    match = TAGS.search(text)
    if not match:
        return text.strip(), None
    return text[: match.start()].strip(), [part.strip() for part in match.group(1).split(",") if part.strip()]


def shown(path: Path, root: Path) -> str:
    """A path as the matrix shows it: from the root when it is under it."""
    return path.relative_to(root).as_posix() if path.is_relative_to(root) else str(path)


# .NET: test methods in tests/**/*.cs, with their attributes on the lines above (one attribute per line, as written).
ATTRIBUTE_START = re.compile(r"^\s*\[\s*[A-Z]\w*\s*(\(|\]|,)")
CLASS = re.compile(r"^(\s*)(?:(?:public|internal|private|protected|static|sealed|abstract|partial)\s+)*class\s+(\w+)")
METHOD = re.compile(r"^(\s*)(?:(?:public|internal|private|protected|static|async|override|virtual)\s+)+[\w<>\[\],.?() ]+?\s+(\w+)\s*\(")
NAMESPACE = re.compile(r"^namespace\s+([\w.]+)\s*;")
STRING = re.compile(r'"((?:[^"\\]|\\.)*)"')


def dotnet_tests(root: Path) -> list[Test]:
    tests = []
    for path in sorted((root / "tests").rglob("*.cs")):
        if {"bin", "obj"} & set(path.relative_to(root).parts):
            continue
        project = next((parent.name for parent in path.parents if any(parent.glob("*.csproj"))), "tests")
        tests += dotnet_file_tests(path, project, root)
    return tests


def dotnet_file_tests(path: Path, project: str, root: Path) -> list[Test]:
    tests = []
    namespace = ""
    classes: list[tuple[int, str]] = []  # (indentation, name) of the classes open above
    attributes: list[str] = []
    pending = ""
    in_raw_string = False
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        # A raw string literal ("""...""") holds source of its own, such as a fixture's tests: none of it is this file's.
        if line.count('"""') % 2 == 1:
            in_raw_string = not in_raw_string
            continue
        if in_raw_string:
            continue
        if pending:
            pending += " " + line.strip()
            if pending.count("[") <= pending.count("]"):
                attributes.append(pending)
                pending = ""
            continue
        if match := NAMESPACE.match(line):
            namespace = match.group(1)
            continue
        if match := CLASS.match(line):
            indent = len(match.group(1).expandtabs(4))
            classes = [(depth, name) for depth, name in classes if depth < indent] + [(indent, match.group(2))]
            attributes = []
            continue
        if ATTRIBUTE_START.match(line):
            text = line.strip()
            if text.count("[") > text.count("]"):
                pending = text
            else:
                attributes.append(text)
            continue
        stripped = line.strip()
        if not stripped or stripped.startswith("//"):
            continue
        if attributes and any(re.match(r"^\[\s*(?:Fact|Theory)\b", attribute) for attribute in attributes):
            if match := METHOD.match(line):
                indent = len(match.group(1).expandtabs(4))
                owners = [name for depth, name in classes if depth < indent]
                class_name = "+".join(owners)
                proves = [value for attribute in attributes if re.match(r"^\[\s*Proves\s*\(", attribute) for value in STRING.findall(attribute)]
                untraced = next((STRING.findall(attribute)[0] if STRING.findall(attribute) else "" for attribute in attributes if re.match(r"^\[\s*Untraced\s*\(", attribute)), None)
                full = f"{namespace}.{class_name}.{match.group(2)}" if namespace else f"{class_name}.{match.group(2)}"
                tests.append(Test("dotnet", project, f"{class_name}.{match.group(2)}", shown(path, root), number, proves, untraced, key=full))
        attributes = []
    return tests


# JavaScript and TypeScript: the calls it(...) and test(...) whose first argument is a string or template literal, found
# in the code with comments, strings, templates and regular expressions masked so that a fixture's source in a string
# is not read as a test.
MODIFIERS = {"skip", "only", "fixme", "fail", "fails", "concurrent", "sequential", "todo", "each", "skipIf", "runIf"}
CALL = re.compile(r"(?<![\w$.])(it|test)((?:\s*\.\s*\w+)*)\s*\(")
REGEX_BEFORE = set("(,=:[!&|?{};+-*%~^>") | {""}


def mask_code(text: str) -> str:
    """The text with the insides of comments, strings, templates and regular expressions replaced by spaces."""
    out = list(text)
    length = len(text)

    def blank(start: int, end: int) -> None:
        for index in range(start, min(end, length)):
            if out[index] != "\n":
                out[index] = " "

    def skip_string(start: int) -> int:
        quote = text[start]
        index = start + 1
        while index < length and text[index] != quote and text[index] != "\n":
            index += 2 if text[index] == "\\" else 1
        blank(start + 1, index)
        return index + 1

    def skip_template(start: int) -> int:
        index = start + 1
        segment = index
        while index < length:
            char = text[index]
            if char == "\\":
                index += 2
            elif char == "`":
                blank(segment, index)
                return index + 1
            elif char == "$" and index + 1 < length and text[index + 1] == "{":
                blank(segment, index)
                index = skip_code(index + 2, closing="}")
                segment = index
            else:
                index += 1
        blank(segment, index)
        return index

    def skip_regex(start: int) -> int:
        index = start + 1
        in_class = False
        while index < length and text[index] != "\n":
            char = text[index]
            if char == "\\":
                index += 2
                continue
            if char == "[":
                in_class = True
            elif char == "]":
                in_class = False
            elif char == "/" and not in_class:
                blank(start + 1, index)
                return index + 1
            index += 1
        return index

    def skip_code(start: int, closing: str | None = None) -> int:
        index = start
        depth = 0
        previous = ""
        while index < length:
            char = text[index]
            if char == "/" and index + 1 < length and text[index + 1] == "/":
                end = text.find("\n", index)
                end = length if end == -1 else end
                blank(index, end)
                index = end
                continue
            if char == "/" and index + 1 < length and text[index + 1] == "*":
                end = text.find("*/", index + 2)
                end = length if end == -1 else end + 2
                blank(index, end)
                index = end
                continue
            if char in "'\"":
                index = skip_string(index)
                previous = "a"
                continue
            if char == "`":
                index = skip_template(index)
                previous = "a"
                continue
            if char == "/" and previous in REGEX_BEFORE:
                index = skip_regex(index)
                previous = "a"
                continue
            if closing and char == "{":
                depth += 1
            elif closing and char == "}":
                if depth == 0:
                    return index + 1
                depth -= 1
            if not char.isspace():
                previous = char if not (char.isalnum() or char in "_$") else "a"
            index += 1
        return index

    skip_code(0)
    return "".join(out)


def read_literal(text: str, start: int) -> tuple[str, list[str]] | None:
    """The string or template literal at start: its text with ${...} kept as written, and its fixed parts."""
    quote = text[start]
    if quote not in "'\"`":
        return None
    index = start + 1
    value = []
    parts = []
    current = []
    while index < len(text):
        char = text[index]
        if char == "\\" and index + 1 < len(text):
            current.append(text[index + 1])
            value.append(text[index : index + 2])
            index += 2
            continue
        if char == quote:
            parts.append("".join(current))
            return "".join(value), parts
        if quote == "`" and char == "$" and text[index + 1 : index + 2] == "{":
            depth = 0
            end = index + 2
            while end < len(text):
                if text[end] == "{":
                    depth += 1
                elif text[end] == "}":
                    if depth == 0:
                        break
                    depth -= 1
                end += 1
            parts.append("".join(current))
            current = []
            value.append(text[index : end + 1])
            index = end + 1
            continue
        if quote != "`" and char == "\n":
            return None
        current.append(char)
        value.append(char)
        index += 1
    return None


# Placeholders a title shows filled in: ${...} in a template, and the printf and $name forms of each().
PLACEHOLDER = re.compile(r"\$\{[^}]*\}|%[sdifjoO#%]|\$[A-Za-z_][\w.]*")


def title_pattern(title: str) -> re.Pattern[str]:
    pieces = PLACEHOLDER.split(title)
    return re.compile(".+?".join(re.escape(piece) for piece in pieces) + "$", re.DOTALL)


def js_tests(root: Path) -> list[Test]:
    shell = root / "shell"
    files = [("vitest", path) for path in sorted((shell / "src").rglob("*.test.ts*"))]
    files += [("a11y", path) for path in sorted((shell / "e2e" / "a11y").glob("*.spec.ts"))]
    files += [("e2e", path) for path in sorted((shell / "e2e").glob("*.spec.ts"))]
    tests = []
    for kind, path in files:
        tests += js_file_tests(path, kind, root)
    return tests


def js_file_tests(path: Path, kind: str, root: Path) -> list[Test]:
    text = path.read_text(encoding="utf-8")
    masked = mask_code(text)
    tests = []
    group = path.relative_to(root / "shell").as_posix()
    for match in CALL.finditer(masked):
        members = [member.strip() for member in match.group(2).split(".") if member.strip()]
        if any(member not in MODIFIERS for member in members):
            continue
        start = match.end()
        if "each" in members:
            # it.each(table)("title", ...): skip the table's call to the title's.
            depth = 1
            index = start
            while index < len(masked) and depth:
                depth += {"(": 1, ")": -1}.get(masked[index], 0)
                index += 1
            rest = re.match(r"\s*\(", masked[index:])
            if not rest:
                continue
            start = index + rest.end()
        while start < len(text) and text[start].isspace():
            start += 1
        literal = read_literal(text, start)
        if literal is None:
            continue
        title, _ = literal
        line = text.count("\n", 0, match.start()) + 1
        name, tags = split_tags(title)
        proves, untraced = classify(tags)
        if untraced is not None:
            # The reason of an untraced test is the comment on the line above it: "// Untraced: why".
            above = text.splitlines()[line - 2].strip() if line > 1 else ""
            untraced = above.removeprefix("// Untraced:").strip() if above.startswith("// Untraced:") else ""
        tests.append(Test(kind, group, name, shown(path, root), line, proves, untraced, key=group, pattern=title_pattern(title)))
    return tests


def classify(tags: list[str] | None) -> tuple[list[str], str | None]:
    """[untraced] stands for a test that proves nothing documented; anything else is a list of identifiers."""
    if tags == [UNTRACED]:
        return [], ""
    return tags or [], None


# The smoke: report.StepAsync("name", ["TF-011 AC1", ...], ...) in tools/Iron.Smoke.
STEP = re.compile(r'StepAsync\(\s*"([^"]+)"\s*,\s*\[([^\]]*)\]')


def smoke_tests(root: Path) -> list[Test]:
    tests = []
    for path in sorted((root / "tools" / "Iron.Smoke").glob("*.cs")):
        text = path.read_text(encoding="utf-8")
        for match in STEP.finditer(text):
            line = text.count("\n", 0, match.start()) + 1
            proves = STRING.findall(match.group(2))
            tests.append(Test("smoke", "smoke", match.group(1), shown(path, root), line, proves, key=match.group(1)))
    return tests


def python_tests(root: Path) -> list[Test]:
    """unittest methods in tools/*_test.py; the docstring ends with what the test proves."""
    tests = []
    for path in sorted((root / "tools").glob("*_test.py")):
        tree = ast.parse(path.read_text(encoding="utf-8"))
        for node in tree.body:
            if not isinstance(node, ast.ClassDef):
                continue
            for item in node.body:
                if isinstance(item, ast.FunctionDef) and item.name.startswith("test"):
                    docstring = ast.get_docstring(item) or ""
                    _, tags = split_tags(docstring)
                    proves, untraced = classify(tags)
                    key = f"{path.stem}.{node.name}.{item.name}"
                    tests.append(Test("python", shown(path, root), f"{node.name}.{item.name}", shown(path, root), item.lineno, proves, untraced, key=key))
    return tests


def all_tests(root: Path) -> list[Test]:
    return dotnet_tests(root) + js_tests(root) + smoke_tests(root) + python_tests(root)


# ---------------------------------------------------------------- the results


TRX = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
OUTCOMES = {"passed": "passed", "failed": "failed", "error": "failed", "timeout": "failed", "aborted": "failed", "notexecuted": "skipped", "skipped": "skipped", "inconclusive": "skipped"}


def trx_results(folder: Path) -> list[tuple[str, str]]:
    """(Namespace.Class.Method, status) for every result in the TRX files under folder."""
    results = []
    for path in sorted(folder.rglob("*.trx")):
        root = ElementTree.parse(path).getroot()
        methods = {}
        for unit in root.iter(f"{TRX}UnitTest"):
            method = unit.find(f"{TRX}TestMethod")
            if method is not None:
                methods[unit.get("id")] = f"{method.get('className')}.{method.get('name')}"
        for result in root.iter(f"{TRX}UnitTestResult"):
            key = methods.get(result.get("testId"))
            if key:
                results.append((key, OUTCOMES.get((result.get("outcome") or "").lower(), "failed")))
    return results


def junit_results(path: Path) -> list[tuple[str, str, str]]:
    """(classname, name, status) for every test case of a JUnit file."""
    if not path.exists():
        return []
    results = []
    for case in ElementTree.parse(path).getroot().iter("testcase"):
        if case.find("failure") is not None or case.find("error") is not None:
            status = "failed"
        elif case.find("skipped") is not None:
            status = "skipped"
        else:
            status = "passed"
        results.append((case.get("classname") or "", case.get("name") or "", status))
    return results


def smoke_results(path: Path) -> list[tuple[str, str]]:
    if not path.exists():
        return []
    report = json.loads(path.read_text(encoding="utf-8"))
    return [(step["name"], OUTCOMES.get(step["status"], "failed")) for step in report.get("steps", [])]


def join_results(tests: list[Test], results: Path, root: Path) -> tuple[list[str], dict[str, bool]]:
    """Gives every test its results; answers the results no test claims, and which kinds had result files."""
    problems = []
    ran = {kind: False for kind in KINDS}
    by_key: dict[tuple[str, str], list[Test]] = defaultdict(list)
    for test in tests:
        by_key[(test.kind, test.key)].append(test)

    dotnet = trx_results(results / "dotnet")
    ran["dotnet"] = bool(dotnet)
    for key, status in dotnet:
        claimed = by_key.get(("dotnet", key))
        if claimed:
            claimed[0].results.append(status)
        else:
            problems.append(f"artifacts/test-results/dotnet: a result for {key}, which no test method in tests/ declares as this reader reads them")

    for kind, path in [("vitest", results / "shell" / "vitest.xml"), ("e2e", results / "e2e" / "junit.xml"), ("a11y", results / "a11y" / "junit.xml")]:
        cases = junit_results(path)
        ran[kind] = bool(cases)
        candidates = [test for test in tests if test.kind == kind]
        for classname, name, status in cases:
            test = match_title(candidates, classname, name)
            if test:
                test.results.append(status)
            else:
                problems.append(f"{shown(path, root)}: a result for '{name}' in {classname}, which no test in the shell declares as this reader reads them")

    smoke = smoke_results(results / "smoke" / "smoke-report.json")
    ran["smoke"] = bool(smoke)
    for name, status in smoke:
        claimed = by_key.get(("smoke", name))
        if claimed:
            claimed[0].results.append(status)
        else:
            problems.append(f"artifacts/test-results/smoke: a result for the step '{name}', which tools/Iron.Smoke does not declare")

    for path in sorted((results / "python").glob("*.xml")) if (results / "python").exists() else []:
        for classname, name, status in junit_results(path):
            ran["python"] = True
            claimed = by_key.get(("python", f"{classname}.{name}"))
            if claimed:
                claimed[0].results.append(status)
            else:
                problems.append(f"{shown(path, root)}: a result for {classname}.{name}, which no Python test declares")
    return problems, ran


def match_title(candidates: list[Test], classname: str, name: str) -> Test | None:
    """The test whose file the result names and whose title ends its full name (after the describe titles)."""
    filename = classname.replace("\\", "/")
    in_file = [test for test in candidates if filename.endswith(test.group) or test.group.endswith(filename)] or candidates
    found = []
    for test in in_file:
        assert test.pattern is not None
        for start in [0] + [m.end() for m in re.finditer(r" > | › ", name)]:
            if test.pattern.match(name, start):
                found.append(test)
                break
    if not found:
        return None
    # The most specific title wins: the one with the most fixed text.
    return max(found, key=lambda test: len(PLACEHOLDER.sub("", test.pattern.pattern if test.pattern else "")))


# ---------------------------------------------------------------- the documents


@dataclass
class Requirement:
    id: str
    title: str
    document: str
    criteria: list[str]
    priority: str = ""  # Must, Should or Could: the validation pack's risk class reads it with the area (ADR-0052)
    area: str = ""


def requirement_documents(root: Path) -> list[prd_check.Document]:
    saved = prd_check.PRD
    prd_check.PRD = root / "docs" / "prd"
    try:
        documents, _ = prd_check.documents()
    finally:
        prd_check.PRD = saved
    return documents


def requirements(root: Path) -> dict[str, Requirement]:
    """Every live requirement of every document prd_check knows, with its criteria. A retired one is not counted and
    no test proves it: a test names what replaced it (ADR-0041)."""
    return {
        r.id: Requirement(r.id, r.title, d.path.relative_to(root).as_posix(), [text for _, text in r.criteria], r.priority, r.area)
        for d in requirement_documents(root)
        for r in d.requirements
        if not r.retired
    }


def retired_requirements(root: Path) -> dict[str, list[str]]:
    """Each retired requirement with its successors."""
    return {r.id: r.replaced_by for d in requirement_documents(root) for r in d.requirements if r.retired}


def decisions(root: Path) -> dict[str, str]:
    """ADR-0029 -> its title, from the files in docs/adr."""
    found = {}
    for path in sorted((root / "docs" / "adr").glob("[0-9][0-9][0-9][0-9]-*.md")):
        if path.name.startswith("0000-"):
            continue
        first = path.read_text(encoding="utf-8").splitlines()[0] if path.stat().st_size else ""
        title = re.sub(r"^#\s*ADR-\d{4}:\s*", "", first).strip()
        found[f"ADR-{path.name[:4]}"] = title
    return found


@dataclass
class Slice:
    id: str
    title: str
    path: str
    done: bool
    requirements: list[str]


# A slice is numbered (S1) or named by a letter (V, the slice between S3 and S4).
SLICE_TITLE = re.compile(r"^# Slice ([A-Z][A-Za-z0-9]*):\s*(.+)$")


def requirement_cell(cell: str) -> list[str]:
    """'TF-040 to 043, 045, 046' -> TF-040, TF-041, TF-042, TF-043, TF-045, TF-046."""
    found: list[str] = []
    prefix = None
    for match in re.finditer(r"([A-Z]{2,3})-(\d{3})(?:\s+to\s+(\d{3}))?|(?<=[\s,])(\d{3})(?:\s+to\s+(\d{3}))?", cell):
        if match.group(1):
            prefix = match.group(1)
            first, last = int(match.group(2)), int(match.group(3) or match.group(2))
        elif prefix:
            first, last = int(match.group(4)), int(match.group(5) or match.group(4))
        else:
            continue
        found += [f"{prefix}-{number:03d}" for number in range(first, last + 1)]
    return found


def slices(root: Path) -> list[Slice]:
    """Every slice plan: done when its task table's every state is done; its requirements from its coverage table."""
    found = []
    for path in sorted((root / "docs" / "slices").glob("*.md")):
        lines = path.read_text(encoding="utf-8").splitlines()
        title = SLICE_TITLE.match(lines[0]) if lines else None
        if not title:
            continue
        found.append(Slice(title.group(1), title.group(2), shown(path, root), slice_done(lines), slice_requirements(lines)))
    return found


def table_after(lines: list[str], heading: re.Pattern[str]) -> list[list[str]]:
    """The rows of the first table under the first heading that matches, header first, separator left out."""
    start = next((index for index, line in enumerate(lines) if line.startswith("#") and heading.search(line)), None)
    if start is None:
        return []
    rows = []
    for line in lines[start + 1 :]:
        if line.startswith("#"):
            break
        if line.startswith("|"):
            cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
            if not all(re.fullmatch(r":?-+:?", cell) for cell in cells if cell):
                rows.append(cells)
        elif rows:
            break
    return rows


def slice_done(lines: list[str]) -> bool:
    rows = table_after(lines, re.compile(r"Task breakdown"))
    if len(rows) < 2 or "State" not in rows[0]:
        return False
    state = rows[0].index("State")
    tasks = [row for row in rows[1:] if any(row)]
    return bool(tasks) and all(len(row) > state and row[state].lower().startswith("done") for row in tasks)


def slice_requirements(lines: list[str]) -> list[str]:
    rows = table_after(lines, re.compile(r"Requirement coverage"))
    found: list[str] = []
    for row in rows[1:]:
        for identifier in requirement_cell(row[0]):
            if identifier not in found:
                found.append(identifier)
    return found


@dataclass
class ListedException:
    target: str  # "TF-001 AC3", or "TF-160" for every criterion
    reason: str
    approved: str
    line: int


def exceptions(path: Path) -> list[ListedException]:
    if not path.exists():
        return []
    found = []
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        if not line.startswith("| ") or line.startswith("| Criterion") or set(line) <= set("|- :"):
            continue
        cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
        if len(cells) >= 3:
            found.append(ListedException(cells[0].strip("`"), cells[1], cells[2], number))
    return found


# ---------------------------------------------------------------- the matrix


@dataclass
class Trace:
    tests: list[Test]
    requirements: dict[str, Requirement]
    decisions: dict[str, str]
    slices: list[Slice]
    exceptions: list[ListedException]
    ran: dict[str, bool]
    problems: list[str] = field(default_factory=list)
    proven: dict[str, list[Test]] = field(default_factory=lambda: defaultdict(list))  # "TF-001 AC1" or "ADR-0029"
    gaps: dict[str, list[str]] = field(default_factory=dict)  # slice -> criteria without a test or an exception
    retired: dict[str, list[str]] = field(default_factory=dict)  # a retired requirement -> what replaced it
    excepted: dict[str, ListedException] = field(default_factory=dict)  # criterion -> its exception


def criteria_of(requirement: Requirement) -> list[str]:
    return [f"{requirement.id} AC{number}" for number in range(1, len(requirement.criteria) + 1)]


def check_identifier(identifier: str, trace: Trace) -> str | None:
    """None when the identifier names something that exists, else why not."""
    match = IDENTIFIER.match(identifier)
    if not match:
        return f"'{identifier}' is not a criterion (TF-033 AC2), a requirement (TF-033) or a decision (ADR-0029)"
    if match.group(4):
        return None if identifier in trace.decisions else f"{identifier} is not an ADR in docs/adr"
    requirement_id = f"{match.group(1)}-{match.group(2)}"
    requirement = trace.requirements.get(requirement_id)
    if requirement is None and requirement_id in trace.retired:
        return f"{requirement_id} is retired; name what replaced it ({', '.join(trace.retired[requirement_id])})"
    if requirement is None:
        return f"{requirement_id} is in no requirements document"
    if match.group(3) and int(match.group(3)) > len(requirement.criteria):
        return f"{requirement_id} has {len(requirement.criteria)} criteria, so it has no AC{match.group(3)}"
    return None


def build(root: Path = ROOT, results: Path | None = None, exceptions_path: Path | None = None) -> Trace:
    tests = all_tests(root)
    trace = Trace(tests, requirements(root), decisions(root), slices(root), exceptions(exceptions_path or root / "docs" / "trace" / "exceptions.md"), {})
    trace.retired = retired_requirements(root)
    join_problems, trace.ran = join_results(tests, results or root / "artifacts" / "test-results", root)
    trace.problems += join_problems

    for test in tests:
        where = f"{test.file}:{test.line}: {test.name}"
        if test.untraced is not None:
            continue
        if not test.proves:
            trace.problems.append(f"{where}: names nothing it proves; add the criteria or the decision (ADR-0044), or mark it untraced with the reason")
            continue
        for identifier in test.proves:
            problem = check_identifier(identifier, trace)
            if problem:
                trace.problems.append(f"{where}: {problem}")
                continue
            requirement = trace.requirements.get(identifier)
            targets = criteria_of(requirement) if requirement else [identifier]
            for target in targets:
                if test not in trace.proven[target]:
                    trace.proven[target].append(test)

    done = [s for s in trace.slices if s.done]
    in_done = {identifier for s in done for identifier in s.requirements}
    for exception in trace.exceptions:
        where = f"docs/trace/exceptions.md:{exception.line}: {exception.target}"
        if exception.target.startswith("ADR-"):
            trace.problems.append(f"{where}: an exception is for a requirement's criteria, not a decision")
            continue
        problem = check_identifier(exception.target, trace)
        if problem:
            trace.problems.append(f"{where}: {problem}")
            continue
        requirement_id = exception.target.split(" ")[0]
        if requirement_id not in in_done:
            trace.problems.append(f"{where}: {requirement_id} is in no done slice's coverage table; an exception is for a done slice's gap")
            continue
        if not exception.reason:
            trace.problems.append(f"{where}: no reason")
        if not DATE.match(exception.approved):
            trace.problems.append(f"{where}: the date it was approved is not a date (yyyy-mm-dd): '{exception.approved}'")
        whole = " " not in exception.target
        targets = criteria_of(trace.requirements[requirement_id]) if whole else [exception.target]
        proven_now = [target for target in targets if trace.proven.get(target)]
        if proven_now and not whole:
            trace.problems.append(f"{where}: proven by {trace.proven[exception.target][0].label} now; remove the exception")
            continue
        if proven_now:
            trace.problems.append(f"{where}: {', '.join(proven_now)} proven by a test now; list the criteria still without one instead of the whole requirement")
            continue
        for target in targets:
            if target in trace.excepted:
                trace.problems.append(f"{where}: {target} is listed twice")
            else:
                trace.excepted[target] = exception

    for s in done:
        missing = []
        for requirement_id in s.requirements:
            requirement = trace.requirements.get(requirement_id)
            if requirement is None:
                trace.problems.append(f"{s.path}: {requirement_id} in the coverage table is in no requirements document")
                continue
            missing += [criterion for criterion in criteria_of(requirement) if not trace.proven.get(criterion) and criterion not in trace.excepted]
        trace.gaps[s.id] = missing
        for criterion in missing:
            trace.problems.append(f"{s.path}: {criterion} of done slice {s.id} has no test and no exception in docs/trace/exceptions.md")
    return trace


# ---------------------------------------------------------------- the output


def counts(tests: list[Test]) -> dict[str, int]:
    found = {"tests": len(tests), "passed": 0, "failed": 0, "skipped": 0, "not run": 0}
    for test in tests:
        found[test.status] += 1
    return found


def commit(root: Path) -> str:
    try:
        return subprocess.run(["git", "rev-parse", "--short", "HEAD"], cwd=root, capture_output=True, text=True, check=True).stdout.strip()
    except (OSError, subprocess.CalledProcessError):
        return "unknown"


def cell(text: str) -> str:
    return text.replace("|", "\\|").replace("\n", " ")


def test_line(test: Test) -> str:
    return f"{KIND_NAMES[test.kind].split(' (')[0]}: {cell(test.label)} ({test.status})"


def summary(trace: Trace) -> dict[str, object]:
    done = [s for s in trace.slices if s.done]
    done_requirements = [r for s in done for r in s.requirements if r in trace.requirements]
    done_criteria = sorted({c for r in done_requirements for c in criteria_of(trace.requirements[r])})
    all_criteria = [c for r in trace.requirements.values() for c in criteria_of(r)]
    return {
        "requirements": len(trace.requirements),
        "requirements_with_tests": sum(1 for r in trace.requirements.values() if any(trace.proven.get(c) for c in criteria_of(r))),
        "criteria": len(all_criteria),
        "criteria_with_tests": sum(1 for c in all_criteria if trace.proven.get(c)),
        "done_slices": [s.id for s in done],
        "done_requirements": len(set(done_requirements)),
        "done_criteria": len(done_criteria),
        "done_criteria_with_tests": sum(1 for c in done_criteria if trace.proven.get(c)),
        "done_criteria_excepted": sum(1 for c in done_criteria if c in trace.excepted),
        "done_criteria_missing": sum(1 for c in done_criteria if not trace.proven.get(c) and c not in trace.excepted),
        "decisions_with_tests": sum(1 for d in trace.decisions if trace.proven.get(d)),
        "tests": {kind: counts([t for t in trace.tests if t.kind == kind]) for kind in KINDS},
        "untraced": sum(1 for t in trace.tests if t.untraced is not None),
        "problems": len(trace.problems),
    }


def markdown(trace: Trace, root: Path) -> str:
    facts = summary(trace)
    lines = [
        "# Traceability matrix",
        "",
        f"Built {datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M UTC')} from commit {commit(root)} by `make trace` (TF-218, ADR-0044). Each test's result is from the last run of its kind; a kind with no result file shows its tests as not run.",
        "",
        "## Summary",
        "",
        "| | |",
        "|---|---|",
        f"| Requirements with a test | {facts['requirements_with_tests']} of {facts['requirements']} |",
        f"| Criteria with a test | {facts['criteria_with_tests']} of {facts['criteria']} |",
        f"| Done slices | {', '.join(facts['done_slices']) or 'none'} |",
        f"| Their requirements | {facts['done_requirements']}, with {facts['done_criteria']} criteria |",
        f"| Their criteria with a test | {facts['done_criteria_with_tests']} |",
        f"| Their criteria listed as exceptions | {facts['done_criteria_excepted']} |",
        f"| Their criteria missing | {facts['done_criteria_missing']} |",
        f"| Decisions with a test | {facts['decisions_with_tests']} of {len(trace.decisions)} |",
        f"| Tests that prove no requirement and no decision | {facts['untraced']} |",
        f"| Problems | {facts['problems']} |",
        "",
        "| Kind | Tests | Passed | Failed | Skipped | Not run |",
        "|---|---|---|---|---|---|",
    ]
    for kind in KINDS:
        c = facts["tests"][kind]  # type: ignore[index]
        lines.append(f"| {KIND_NAMES[kind]} | {c['tests']} | {c['passed']} | {c['failed']} | {c['skipped']} | {c['not run']} |")
    if trace.problems:
        lines += ["", "## Problems", ""] + [f"- {cell(problem)}" for problem in trace.problems]

    for s in [s for s in trace.slices if s.done]:
        lines += ["", f"## Done slice {s.id}: {s.title}", "", "| Criterion | Proven by |", "|---|---|"]
        for requirement_id in s.requirements:
            requirement = trace.requirements.get(requirement_id)
            if requirement is None:
                continue
            for criterion in criteria_of(requirement):
                proving = trace.proven.get(criterion, [])
                if proving:
                    text = "<br>".join(test_line(t) for t in proving)
                elif criterion in trace.excepted:
                    exception = trace.excepted[criterion]
                    text = f"Exception, approved {exception.approved}: {cell(exception.reason)}"
                else:
                    text = "**Missing**"
                lines.append(f"| {criterion} | {text} |")

    lines += ["", "## Requirements and their tests", "", "Every requirement that a test names, in document order.", ""]
    for requirement in trace.requirements.values():
        if not any(trace.proven.get(c) for c in criteria_of(requirement)):
            continue
        lines += [f"### {requirement.id} {cell(requirement.title)}", "", "| Criterion | Proven by |", "|---|---|"]
        for number, criterion in enumerate(criteria_of(requirement), start=1):
            proving = trace.proven.get(criterion, [])
            text = "<br>".join(test_line(t) for t in proving) or ("exception" if criterion in trace.excepted else "none")
            lines.append(f"| AC{number} {cell(requirement.criteria[number - 1])} | {text} |")
        lines.append("")

    lines += ["## Decisions and their tests", "", "| Decision | Proven by |", "|---|---|"]
    for identifier, title in trace.decisions.items():
        proving = trace.proven.get(identifier, [])
        if proving:
            lines.append(f"| {identifier} {cell(title)} | {'<br>'.join(test_line(t) for t in proving)} |")

    untraced = [t for t in trace.tests if t.untraced is not None]
    lines += ["", "## Tests that prove no requirement and no decision", ""]
    lines += [f"- {cell(t.label)} ({t.file}:{t.line}){': ' + cell(t.untraced) if t.untraced else ''}" for t in untraced] or ["None."]

    not_run = [t for t in trace.tests if t.status == "not run" and trace.ran.get(t.kind)]
    lines += ["", "## Tests without a result although their kind ran", ""]
    lines += [f"- {cell(t.label)} ({t.file}:{t.line})" for t in not_run] or ["None."]
    return "\n".join(lines) + "\n"


def as_json(trace: Trace, root: Path) -> dict[str, object]:
    return {
        "commit": commit(root),
        "built_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "summary": summary(trace),
        "tests": [
            {"kind": t.kind, "group": t.group, "name": t.name, "file": t.file, "line": t.line, "proves": t.proves, "untraced": t.untraced, "status": t.status, "results": t.results}
            for t in trace.tests
        ],
        "requirements": {
            r.id: {"title": r.title, "document": r.document, "priority": r.priority, "area": r.area, "criteria": {c: [t.label for t in trace.proven.get(c, [])] for c in criteria_of(r)}}
            for r in trace.requirements.values()
        },
        "decisions": {d: [t.label for t in trace.proven.get(d, [])] for d in trace.decisions},
        "done_slices": {s.id: {"requirements": s.requirements, "missing": trace.gaps.get(s.id, [])} for s in trace.slices if s.done},
        "exceptions": {criterion: {"reason": e.reason, "approved": e.approved} for criterion, e in trace.excepted.items()},
        "problems": trace.problems,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Build the traceability matrix (TF-218, ADR-0044) into artifacts/trace.")
    parser.add_argument("--results", type=Path, default=RESULTS, help="the result files' folder (default artifacts/test-results)")
    parser.add_argument("--output", type=Path, default=OUTPUT, help="where the matrix goes (default artifacts/trace)")
    arguments = parser.parse_args(argv)
    trace = build(ROOT, arguments.results)
    arguments.output.mkdir(parents=True, exist_ok=True)
    (arguments.output / "matrix.md").write_text(markdown(trace, ROOT), encoding="utf-8")
    (arguments.output / "matrix.json").write_text(json.dumps(as_json(trace, ROOT), indent=2) + "\n", encoding="utf-8")
    facts = summary(trace)
    for kind in KINDS:
        c = facts["tests"][kind]  # type: ignore[index]
        print(f"{KIND_NAMES[kind]:44} {c['tests']:5} tests: {c['passed']} passed, {c['failed']} failed, {c['skipped']} skipped, {c['not run']} not run")
    print(f"requirements with a test: {facts['requirements_with_tests']} of {facts['requirements']}; criteria with a test: {facts['criteria_with_tests']} of {facts['criteria']}")
    print(f"done slices {', '.join(facts['done_slices']) or 'none'}: {facts['done_criteria']} criteria, {facts['done_criteria_with_tests']} with a test, {facts['done_criteria_excepted']} exceptions, {facts['done_criteria_missing']} missing")
    print(f"tests that prove no requirement and no decision: {facts['untraced']}")
    print(f"matrix: {shown(arguments.output / 'matrix.md', ROOT)}")
    for problem in trace.problems:
        print(problem)
    if trace.problems:
        print(f"\ntrace: {len(trace.problems)} problems")
        return 1
    print("trace: passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
