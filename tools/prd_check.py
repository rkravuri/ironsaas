#!/usr/bin/env python3
"""Checks the requirement documents in docs/prd against the rules of ADR-0041, ADR-0042 and ADR-0043.

Every requirements document is a markdown file with requirement blocks and a CSV beside it, written from the markdown
(`make prd-csv`). The check reads them all, with the archived Functional Foundation PRD and the map, and names the file
and the identifier of every problem:

- an identifier used twice across the documents and their CSVs, or one outside its document's prefix or its area's
  block of twenty;
- an FF identifier of the archived PRD without a row in the map, a map row whose new identifier is missing from its
  document or does not name the FF identifier in its `was`, or a requirement whose `was` the map does not list;
- a move after the split (the map's section "Moved after the split", ADR-0043) whose parts do not name the old
  identifier in their `was`, a moved identifier that a document still holds, or a split one that none holds;
- a requirement without acceptance criteria in the Given, When, Then form numbered AC1 onwards, or without one marked
  "(error case)";
- a product PRD of the eProcurement Suite without the sections "Supplier views" and "Records and signatures";
- a CSV that differs from its markdown in any cell, and a map whose markdown and CSV differ;
- a dependency on an identifier no document holds, one that points up a layer (the kernel, the business layer, a
  suite's shared masters, its products, its surfaces), one on a later product of its suite's process order unless it
  is marked "(event)" (ADR-0042), and one from a suite's document on another suite's (ADR-0043);
- a retired requirement (tagged `[Retired]`) whose notes do not say "Replaced by" with successors that a document
  holds and that are not retired themselves, and a dependency on a retired requirement: its identifier is never
  reused (ADR-0041, decision 3), and what depended on it depends on its successors;
- a process reference pack (PM-001), a markdown file in a folder named `process-reference/` beside the documents of its
  suite, whose section 2 names an identifier no document holds, leaves out a live requirement of the document it is
  the pack of (`process-reference/supplier-management.md` is the pack of `supplier-management-PRD.md`), or maps one
  without its PCF element or its map step (PM-001 AC2);
- against the build plan (the technical PRD's section 7.2): a live business or suite requirement in no slice or in two,
  a retired one in a slice, an identifier no document holds, and a requirement that depends on one placed in a later
  slice, except through an event or as a known case listed in KNOWN_LATER with its reason.

The Foundation's documents sit in foundation/, each suite's in suites/<suite>/; the product method, the map and the
archive stay at the top of docs/prd. A README is no requirements document, a suite's module list among them (ADR-0058). A pack's BPMN maps are checked against the BPMN 2.0 schema by
tools/Iron.ProcessMaps, which `make prd-check` runs after this check (ADR-0045).

Standard library only, so CI needs nothing but Python 3. Exit status 1 when anything is wrong.
"""

from __future__ import annotations

import argparse
import csv
import re
import sys
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PRD = ROOT / "docs" / "prd"
ARCHIVE = PRD / "archive" / "functional-foundation-p2p-PRD-v1.md"
MAP_MD = PRD / "requirements-map.md"
MAP_CSV = PRD / "requirements-map.csv"

# The requirements documents and their prefixes (ADR-0041, decision 1), in their folders (ADR-0043): the Foundation's
# in foundation/, each suite's in suites/<suite>/. The scope pages carry no requirements yet.
DOCUMENTS = {
    "foundation/technical-foundation-PRD.md": "TF",
    "foundation/business-foundation-PRD.md": "BF",
    "product-method.md": "PM",
    "suites/procurement/supplier-management-PRD.md": "SUP",
    "suites/procurement/catalogue-PRD.md": "CAT",
    "suites/procurement/requests-PRD.md": "REQ",
    "suites/procurement/orders-PRD.md": "ORD",
    "suites/procurement/receiving-PRD.md": "RCV",
    "suites/procurement/invoicing-PRD.md": "INV",
    "suites/procurement/contracts-PRD.md": "CON",
    "suites/procurement/supplier-portal-PRD.md": "SPX",
    "suites/procurement/purchasing-PRD.md": "PUR",
}
NOT_REQUIREMENT_DOCUMENTS = {"README.md", "requirements-map.md"}
# A README is never a requirements document, wherever it stands: a suite's README holds its module list (ADR-0058).
README = "README.md"
# Process reference packs (PM-001) live in a folder of this name beside their suite's documents, with their BPMN maps.
PACK_FOLDER = "process-reference"
# The sections every product PRD of a suite has; a later suite states its own when its first PRD is written.
PRODUCT_SECTIONS = {"procurement": ["Supplier views", "Records and signatures"]}
TAGS = {"Core", "Extended", "Carried", "Improved", "Gap", "New", "Decision", "Later", "Retired"}
# A retired requirement says what replaced it: "Replaced by SUP-201 to SUP-212 and SPX-029." A range names every
# identifier between its ends.
REPLACED_BY = re.compile(r"Replaced by ([^.]*)")
# The build plan: the business and suite slices are the rows of the technical PRD's section 7.2, in their order.
PLAN_DOCUMENT = "foundation/technical-foundation-PRD.md"
PLAN_SECTION = "### 7.2 "
PLAN_ROW = re.compile(r"^\| (\d+) \| ([^|]+?) \| ([^|]+?) \|[^|]*\|$")
# Dependencies on a later slice that are known and accepted, each with its reason: (requirement, dependency) -> why.
# None since SUP-gaps' review (2026-10-21), which turned the five it found around.
KNOWN_LATER: dict[tuple[str, str], str] = {}
IDENTIFIER_RANGE = re.compile(r"\b([A-Z]{2,3})-(\d{3})(?: to (?:[A-Z]{2,3}-)?(\d{3}))?")

# Dependencies point down the layers (ADR-0029, ADR-0042, ADR-0043): the kernel, the business layer (the two are the
# Foundation), a suite's shared masters, its products, its surfaces. The product method binds every layer and stands
# outside them. A suite's document never depends on another suite's. Between the products of a suite, its process
# order: a later product may depend on an earlier one; an earlier one hears from a later one only through a domain
# event, written "ORD-001 (event)".
FOUNDATION_LAYERS = {"TF": 0, "BF": 1}
MASTERS = {"PUR"}
SURFACES = {"SPX"}
LAYER_NAMES = {0: "the kernel", 1: "the business layer", 2: "the suite's shared masters", 3: "the products", 4: "the supplier portal surface"}
PROCESS_ORDER = {"procurement": ["SUP", "CON", "CAT", "REQ", "ORD", "RCV", "INV"]}
DEPENDENCY = re.compile(r"^([A-Z]{2,3})-(\d{3})( \(event\))?$")


def suite_of(name: str) -> str | None:
    """The suite a document belongs to, by its folder: suites/procurement/orders-PRD.md is procurement's."""
    parts = name.split("/")
    return parts[1] if len(parts) > 2 and parts[0] == "suites" else None


def layer_of(name: str, prefix: str) -> int | None:
    """The layer of a document: the Foundation's two by prefix, a suite's master, product or surface by folder and prefix."""
    if prefix in FOUNDATION_LAYERS:
        return FOUNDATION_LAYERS[prefix]
    if suite_of(name) is None:
        return None
    return 2 if prefix in MASTERS else 4 if prefix in SURFACES else 3


def identifiers_in(text: str) -> list[str]:
    """'SUP-201 to SUP-203 and SPX-029' -> SUP-201, SUP-202, SUP-203, SPX-029; 'BF-101 to 108' reads the same way."""
    found = []
    for match in IDENTIFIER_RANGE.finditer(text):
        prefix, first = match.group(1), int(match.group(2))
        last = int(match.group(3)) if match.group(3) else first
        found += [f"{prefix}-{number:03d}" for number in range(first, last + 1)]
    return found

COLUMNS = ["id", "area", "title", "priority", "tag", "requirement", "rationale", "acceptance_criteria", "depends_on", "notes", "was"]
ARCHIVE_COLUMNS = COLUMNS[:-1]
MAP_COLUMNS = ["old_id", "old_title", "outcome", "new_ids", "documents", "covered_by", "built_by", "placed_by_test", "notes"]
OUTCOMES = {"moved", "split", "merged", "covered", "retired"}

HEADING = re.compile(r"^#{1,4} ")
REQUIREMENT_HEADING = re.compile(r"^#### ([A-Z]{2,3})-(\d{3}) (.+?)\s*$")
META = re.compile(r"^\*Priority:\* \*\*(Must|Should|Could)\*\* · \*Tag:\* ((?:`\[[A-Za-z]+\]` ?)+) · \*Area:\* (.+?)\s*$")
CRITERION = re.compile(r"^- AC(\d+) (.+?)\s*$")
GIVEN_WHEN_THEN = re.compile(r"^Given .+?, When .+?, Then .+")
# A moved requirement names every identifier it had, the archived FF one and any of the split's documents (ADR-0043).
WAS = re.compile(r"^Was ([A-Z]{2,3}-\d{3}(?:(?:, | and )[A-Z]{2,3}-\d{3})*)")
IDENTIFIER = re.compile(r"\b[A-Z]{2,3}-\d{3}\b")
MAP_ROW = re.compile(r"^\| [A-Z]{2,3}-\d{3} \|")


@dataclass
class Requirement:
    prefix: str
    number: int
    title: str
    line: int
    priority: str = ""
    tags: list[str] = field(default_factory=list)
    area: str = ""
    requirement: str = ""
    rationale: str = ""
    criteria: list[tuple[int, str]] = field(default_factory=list)
    depends_on: list[str] = field(default_factory=list)
    notes: str = ""

    @property
    def id(self) -> str:
        return f"{self.prefix}-{self.number:03d}"

    @property
    def was(self) -> list[str]:
        match = WAS.match(self.notes)
        return IDENTIFIER.findall(match.group(1)) if match else []

    @property
    def retired(self) -> bool:
        return "Retired" in self.tags

    @property
    def replaced_by(self) -> list[str]:
        match = REPLACED_BY.search(self.notes)
        return identifiers_in(match.group(1)) if match else []

    def row(self) -> dict[str, str]:
        return {
            "id": self.id,
            "area": self.area,
            "title": self.title,
            "priority": self.priority,
            "tag": "|".join(self.tags),
            "requirement": self.requirement,
            "rationale": self.rationale,
            "acceptance_criteria": "|".join(text for _, text in self.criteria),
            "depends_on": "|".join(self.depends_on),
            "notes": self.notes,
            "was": "|".join(self.was),
        }


@dataclass
class Document:
    path: Path
    requirements: list[Requirement]
    headings: list[str]
    problems: list[str]

    @property
    def name(self) -> str:
        return self.path.relative_to(PRD).as_posix()

    @property
    def csv_path(self) -> Path:
        """`requests-PRD.md` has `requests-requirements.csv`; `product-method.md` has `product-method-requirements.csv`."""
        if self.path == ARCHIVE:
            return self.path.with_name("functional-foundation-p2p-requirements-v1.csv")
        return self.path.with_name(f"{self.path.stem.removesuffix('-PRD')}-requirements.csv")


def parse(path: Path) -> Document:
    """Reads the requirement blocks of one markdown document."""
    lines = path.read_text(encoding="utf-8").splitlines()
    requirements: list[Requirement] = []
    headings: list[str] = []
    problems: list[str] = []
    current: Requirement | None = None
    in_criteria = False
    where = path.relative_to(PRD).as_posix()
    for number, line in enumerate(lines, start=1):
        if HEADING.match(line):
            headings.append(line.lstrip("#").strip())
            in_criteria = False
            match = REQUIREMENT_HEADING.match(line)
            current = Requirement(match.group(1), int(match.group(2)), match.group(3), number) if match else None
            if current:
                requirements.append(current)
            continue
        if current is None:
            continue
        if line.startswith("*Priority:*"):
            meta = META.match(line)
            if not meta:
                problems.append(f"{where}:{number}: {current.id}: the priority, tag and area line is not in the form of the others")
                continue
            current.priority = meta.group(1)
            current.tags = re.findall(r"\[([A-Za-z]+)\]", meta.group(2))
            current.area = meta.group(3)
        elif line.startswith("**Requirement:** "):
            current.requirement = line.removeprefix("**Requirement:** ").strip()
        elif line.startswith("**Why:** "):
            current.rationale = line.removeprefix("**Why:** ").strip()
        elif line.startswith("**Acceptance criteria:**"):
            in_criteria = True
        elif line.startswith("*Depends on:* "):
            in_criteria = False
            current.depends_on = [part.strip() for part in line.removeprefix("*Depends on:* ").split(",") if part.strip()]
        elif line.startswith("**Notes:** "):
            in_criteria = False
            current.notes = line.removeprefix("**Notes:** ").strip()
        elif in_criteria and line.startswith("- "):
            criterion = CRITERION.match(line)
            if criterion:
                current.criteria.append((int(criterion.group(1)), criterion.group(2)))
            else:
                problems.append(f"{where}:{number}: {current.id}: a criterion that does not start with its number (AC1)")
    return Document(path, requirements, headings, problems)


def read_csv(path: Path) -> list[dict[str, str]]:
    with path.open(encoding="utf-8", newline="") as file:
        return list(csv.DictReader(file))


def write_csv(path: Path, columns: list[str], rows: list[dict[str, str]]) -> None:
    with path.open("w", encoding="utf-8", newline="") as file:
        writer = csv.DictWriter(file, fieldnames=columns, quoting=csv.QUOTE_ALL, lineterminator="\n")
        writer.writeheader()
        writer.writerows(rows)


def read_map_markdown(path: Path) -> list[dict[str, str]]:
    """The map's tables: the rows whose first cell is an identifier, FF ones first, cells split on ' | '."""
    rows = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if MAP_ROW.match(line):
            cells = [cell.strip() for cell in line.strip().strip("|").split(" | ")]
            row = dict(zip(MAP_COLUMNS, cells))
            row["new_ids"] = "|".join(part.strip() for part in row.get("new_ids", "").split(",") if part.strip())
            row["documents"] = "|".join(part.strip().strip("`") for part in row.get("documents", "").split(",") if part.strip())
            row["covered_by"] = "|".join(part.strip() for part in row.get("covered_by", "").split(",") if part.strip())
            rows.append(row)
    return rows


def documents() -> tuple[list[Document], list[str]]:
    problems = []
    found = {path.relative_to(PRD).as_posix() for path in PRD.rglob("*.md") if not path.relative_to(PRD).as_posix().startswith("archive/")}
    found -= NOT_REQUIREMENT_DOCUMENTS
    found = {name for name in found if not name.endswith(("-scope.md", f"/{README}")) and f"/{PACK_FOLDER}/" not in f"/{name}"}
    for name in sorted(found - DOCUMENTS.keys()):
        problems.append(f"{name}: a document the check does not know; add it with its prefix to DOCUMENTS in tools/prd_check.py")
    for name in sorted(DOCUMENTS.keys() - found):
        problems.append(f"{name}: listed in tools/prd_check.py but missing")
    return [parse(PRD / name) for name in DOCUMENTS if name in found], problems


def packs() -> list[Path]:
    """The process reference packs: the markdown files of every process-reference folder (PM-001)."""
    return sorted(path for path in PRD.rglob(f"{PACK_FOLDER}/*.md"))


def check_pack(path: Path, live: dict[str, tuple[Document, Requirement]]) -> list[str]:
    """A pack's section 2 maps every live requirement of its document to a PCF element and a map step (PM-001 AC2)."""
    name = path.relative_to(PRD).as_posix()
    document_name = f"{path.parent.parent.relative_to(PRD).as_posix()}/{path.stem}-PRD.md"
    problems = []
    rows: list[list[str]] = []
    in_section = False
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("## "):
            in_section = line.startswith("## 2.")
        elif in_section and line.startswith("| ") and IDENTIFIER_RANGE.match(line[2:]):
            rows.append([cell.strip() for cell in line.strip().strip("|").split(" | ")])
    if not rows:
        return [f"{name}: its section 2 maps no requirement"]
    mapped: set[str] = set()
    for cells in rows:
        # The cell starts with the identifier or the range it maps; the title after it may cite others.
        identifiers = identifiers_in(IDENTIFIER_RANGE.match(cells[0]).group(0))
        for identifier in identifiers:
            if identifier not in live:
                problems.append(f"{name}: section 2 maps {identifier}, which no document holds")
        if len(cells) < 3 or not cells[1] or not cells[2]:
            problems.append(f"{name}: section 2 maps {cells[0].split(' ')[0]} without its PCF element or its map step")
        mapped.update(identifiers)
    owned = [requirement for document, requirement in live.values() if document.name == document_name]
    if not owned:
        problems.append(f"{name}: a pack of {document_name}, which the check does not know")
    for requirement in owned:
        if not requirement.retired and requirement.id not in mapped:
            problems.append(f"{name}: {requirement.id} of {document_name} has no row in section 2 (PM-001 AC2)")
    return problems


def plan_slices() -> list[tuple[int, str, list[str]]]:
    """The rows of the build plan: their order, their slice and the requirements they build, remarks in brackets aside."""
    path = PRD / PLAN_DOCUMENT
    text = path.read_text(encoding="utf-8") if path.exists() else ""
    if PLAN_SECTION not in text:
        return []
    section = text[text.index(PLAN_SECTION):]
    following = re.search(r"^## ", section, flags=re.MULTILINE)
    rows = []
    for line in section[: following.start() if following else None].splitlines():
        match = PLAN_ROW.match(line)
        if match:
            rows.append((int(match.group(1)), match.group(2).strip(), identifiers_in(re.sub(r"\([^)]*\)", "", match.group(3)))))
    return rows


def check_plan(live: dict[str, tuple[Document, Requirement]]) -> list[str]:
    """Every live business and suite requirement is in one slice, and depends on none placed in a later one, except
    through an event: the plan's own rule, "no requirement depends on one in a later row"."""
    rows = plan_slices()
    if not rows:
        return []
    where = f"{PLAN_DOCUMENT} section 7.2"
    placed: dict[str, list[tuple[int, str]]] = defaultdict(list)
    problems = []
    for order, name, identifiers in rows:
        for identifier in identifiers:
            placed[identifier].append((order, name))
            if identifier not in live:
                problems.append(f"{where}: {name} builds {identifier}, which no document holds")
    for identifier, (document, requirement) in live.items():
        # The plan's rows hold the business layer and the suites; the kernel's slices are section 7.1's, the method stands outside.
        if requirement.prefix != "BF" and suite_of(document.name) is None:
            continue
        slices = placed.get(identifier, [])
        if requirement.retired:
            if slices:
                problems.append(f"{where}: {identifier} is retired but placed in {slices[0][1]}; place its successors")
            continue
        if len(slices) != 1:
            problems.append(f"{where}: {identifier} is in {'no slice' if not slices else 'more than one slice'}; every business and suite requirement is in one")
            continue
        order, name = slices[0]
        for dependency in requirement.depends_on:
            if dependency.endswith("(event)") or dependency not in placed:
                continue
            their_order, their_name = placed[dependency][0]
            if their_order > order and (identifier, dependency) not in KNOWN_LATER:
                problems.append(f"{document.name}: {identifier}, built in {name}, depends on {dependency}, placed in a later slice ({their_name}); depend on it through an event, turn the dependency around, or move one of them")
    return problems


def check_retired(document: Document, requirement: Requirement, live: dict[str, tuple[Document, Requirement]]) -> list[str]:
    """A retired requirement names its successors, each held and live; the identifier stays, never reused (ADR-0041)."""
    where = f"{document.name}: {requirement.id}"
    if not requirement.replaced_by:
        return [f"{where}: retired, but its notes do not say 'Replaced by' and its successors"]
    problems = []
    for successor in requirement.replaced_by:
        if successor not in live:
            problems.append(f"{where}: replaced by {successor}, which no document holds")
        elif live[successor][1].retired:
            problems.append(f"{where}: replaced by {successor}, which is retired too")
    return problems


def check_requirements(document: Document, prefix: str) -> list[str]:
    problems = list(document.problems)
    name = document.name
    blocks: dict[str, int] = {}
    previous: dict[str, int] = {}
    for requirement in document.requirements:
        where = f"{name}:{requirement.line}: {requirement.id}"
        if requirement.prefix != prefix:
            problems.append(f"{where}: this document's identifiers start with {prefix}-")
        if prefix != "TF":
            # The kernel's PRD keeps its historical order (TF-240 sits in section 4.22, before TF-210). In the documents
            # of ADR-0041 identifiers rise through each area, and an area added later takes the next free block wherever
            # it stands in the document (a product's new area may follow its supplier views' block).
            if requirement.number <= previous.get(requirement.area, 0):
                problems.append(f"{where}: out of order; identifiers rise through their area")
            previous[requirement.area] = requirement.number
            # Blocks of twenty per area (ADR-0041, decision 3): the area's first identifier names its block.
            block = (requirement.number - 1) // 20
            first = blocks.setdefault(requirement.area, block)
            if block != first:
                problems.append(f"{where}: outside the block of its area {requirement.area!r} ({first * 20 + 1:03d} to {first * 20 + 20:03d})")
            elif list(blocks.values()).count(block) > 1:
                problems.append(f"{where}: its block is already another area's")
        if not requirement.priority:
            problems.append(f"{where}: no priority, tag and area line")
        unknown = [tag for tag in requirement.tags if tag not in TAGS]
        if unknown:
            problems.append(f"{where}: unknown tag {', '.join(unknown)}")
        if not requirement.requirement or not requirement.rationale:
            problems.append(f"{where}: the requirement or its reason (Why) is missing")
        if not requirement.criteria:
            problems.append(f"{where}: no acceptance criteria")
        if [number for number, _ in requirement.criteria] != list(range(1, len(requirement.criteria) + 1)):
            problems.append(f"{where}: criteria are not numbered AC1, AC2, ... in order")
        for number, text in requirement.criteria:
            if not GIVEN_WHEN_THEN.match(text):
                problems.append(f"{where}: AC{number} is not in the form Given ..., When ..., Then ...")
        if requirement.criteria and not any("(error case)" in text for _, text in requirement.criteria):
            problems.append(f"{where}: no criterion is marked (error case)")
    if prefix not in SURFACES and prefix not in MASTERS:
        for section in PRODUCT_SECTIONS.get(suite_of(name) or "", []):
            if not any(re.sub(r"^[\d.]+\s*", "", heading).startswith(section) for heading in document.headings):
                problems.append(f"{name}: no section {section!r}")
    return problems


def compare(document: Document, columns: list[str]) -> list[str]:
    """Names every identifier and column where the CSV differs from the markdown."""
    name = document.csv_path.relative_to(PRD).as_posix()
    if not document.csv_path.exists():
        return [f"{name}: missing; make prd-csv writes it from the markdown"]
    rows = read_csv(document.csv_path)
    problems = []
    if rows and list(rows[0].keys()) != columns:
        problems.append(f"{name}: the columns are not {', '.join(columns)}")
    expected = {requirement.id: requirement.row() for requirement in document.requirements}
    found = {row["id"]: row for row in rows}
    for missing in sorted(expected.keys() - found.keys()):
        problems.append(f"{name}: {missing} is in the markdown but not in the CSV")
    for extra in sorted(found.keys() - expected.keys()):
        problems.append(f"{name}: {extra} is in the CSV but not in the markdown")
    for identifier in sorted(expected.keys() & found.keys()):
        for column in columns:
            if found[identifier].get(column, "") != expected[identifier][column]:
                problems.append(f"{name}: {identifier} {column} differs from the markdown")
    if not problems and [row["id"] for row in rows] != [requirement.id for requirement in document.requirements]:
        problems.append(f"{name}: the rows are not in the order of the markdown")
    return problems


def check_dependencies(document: Document, requirement: Requirement, live: dict[str, tuple[Document, Requirement]]) -> list[str]:
    """Every dependency exists, stays in its suite, points down a layer or sideways, and reaches a later product only as an event."""
    problems = []
    where = f"{document.name}: {requirement.id}"
    for dependency in requirement.depends_on:
        match = DEPENDENCY.match(dependency)
        if not match:
            problems.append(f"{where}: dependency {dependency!r} is not an identifier, or an identifier followed by (event)")
            continue
        identifier, event = f"{match.group(1)}-{match.group(2)}", bool(match.group(3))
        if identifier not in live:
            problems.append(f"{where} depends on {identifier}, which no document holds")
            continue
        if live[identifier][1].retired and not requirement.retired:
            successors = ", ".join(live[identifier][1].replaced_by) or "its successors"
            problems.append(f"{where} depends on {identifier}, which is retired; depend on what replaced it ({successors})")
            continue
        own, theirs = requirement.prefix, match.group(1)
        their_document = live[identifier][0]
        own_suite, their_suite = suite_of(document.name), suite_of(their_document.name)
        if own_suite and their_suite and own_suite != their_suite:
            problems.append(f"{where} depends on {identifier} of the {their_suite} suite; a suite never depends on another suite, and what both need belongs in the Foundation (ADR-0043)")
            continue
        own_layer, their_layer = layer_of(document.name, own), layer_of(their_document.name, theirs)
        if own_layer is None or their_layer is None:
            if event:
                problems.append(f"{where}: {identifier} is marked (event), which only a dependency on a later product is")
            continue
        if their_layer > own_layer:
            problems.append(f"{where} depends on {identifier}, which points up from {LAYER_NAMES[own_layer]} to {LAYER_NAMES[their_layer]}")
            continue
        order = PROCESS_ORDER.get(own_suite or "", [])
        later = own in order and theirs in order and order.index(theirs) > order.index(own)
        if later and not event:
            problems.append(f"{where} depends on {identifier} of a later product; an earlier product hears from it only through an event, written '{identifier} (event)'")
        if event and not later:
            problems.append(f"{where}: {identifier} is marked (event), which only a dependency on a later product is")
    return problems


def check_map(archive: Document, live: dict[str, tuple[Document, Requirement]]) -> list[str]:
    problems = []
    if not MAP_CSV.exists() or not MAP_MD.exists():
        return ["requirements-map.md or requirements-map.csv: missing"]
    rows = read_csv(MAP_CSV)
    if read_map_markdown(MAP_MD) != rows:
        problems.append("requirements-map.md: its table differs from requirements-map.csv")
    old = {requirement.id: requirement for requirement in archive.requirements}
    seen: dict[str, dict[str, str]] = {}
    for row in rows:
        identifier = row["old_id"]
        where = f"requirements-map.csv: {identifier}"
        if identifier in seen:
            problems.append(f"{where}: two rows")
        seen[identifier] = row
        if not identifier.startswith("FF-"):
            problems += check_later_move(row, live)
            continue
        if identifier not in old:
            problems.append(f"{where}: not an identifier of the archived PRD")
            continue
        if row["old_title"] != old[identifier].title:
            problems.append(f"{where}: the title differs from the archived PRD's")
        outcome = row["outcome"]
        new_ids = [part for part in row["new_ids"].split("|") if part]
        covered_by = [part for part in row["covered_by"].split("|") if part]
        if outcome not in OUTCOMES:
            problems.append(f"{where}: outcome {outcome!r} is not one of {', '.join(sorted(OUTCOMES))}")
        if outcome in {"moved", "split", "merged"} and not new_ids:
            problems.append(f"{where}: {outcome} but no new identifier")
        if outcome == "moved" and len(new_ids) != 1:
            problems.append(f"{where}: moved names one new identifier; a requirement in several is split")
        if outcome == "covered" and not (covered_by and row["built_by"]):
            problems.append(f"{where}: covered names the kernel requirement and what built it")
        if outcome == "retired" and not row["notes"]:
            problems.append(f"{where}: retired without a reason")
        if row["placed_by_test"] not in {"", "yes"}:
            problems.append(f"{where}: placed_by_test is yes or empty")
        if row["placed_by_test"] == "yes" and not row["notes"]:
            problems.append(f"{where}: placed by the test without a sentence of reason")
        for kernel in covered_by:
            if kernel not in live or not kernel.startswith("TF-"):
                problems.append(f"{where}: covered by {kernel}, which is not a kernel requirement")
        documents_named = {part for part in row["documents"].split("|") if part}
        for new_id in new_ids:
            if new_id not in live:
                problems.append(f"{where}: {new_id} is in no document")
                continue
            document, requirement = live[new_id]
            if document.name not in documents_named:
                problems.append(f"{where}: {new_id} is in {document.name}, which the row does not name")
            if identifier not in requirement.was:
                problems.append(f"{document.name}: {new_id}: its notes do not start with Was {identifier}")
    for identifier in sorted(old.keys() - seen.keys()):
        problems.append(f"requirements-map.csv: {identifier} of the archived PRD has no row")
    for new_id, (document, requirement) in live.items():
        for identifier in requirement.was:
            row = seen.get(identifier)
            if row is None or new_id not in row["new_ids"].split("|"):
                problems.append(f"{document.name}: {new_id} says it was {identifier}, but the map's row does not list it")
    return problems


def check_later_move(row: dict[str, str], live: dict[str, tuple[Document, Requirement]]) -> list[str]:
    """A move after the split (ADR-0043): its parts name the old identifier, and a moved identifier is never reused."""
    identifier = row["old_id"]
    where = f"requirements-map.csv: {identifier}"
    problems = []
    outcome = row["outcome"]
    new_ids = [part for part in row["new_ids"].split("|") if part]
    if outcome not in {"moved", "split"}:
        problems.append(f"{where}: a move after the split is moved or split, not {outcome!r}")
    if outcome == "moved" and identifier in live:
        problems.append(f"{where}: moved, but {live[identifier][0].name} still holds it; a moved identifier is never reused")
    if outcome == "split" and identifier not in live:
        problems.append(f"{where}: split, but no document holds it any more; a requirement whose every part moved is moved")
    if outcome == "split" and identifier not in new_ids:
        problems.append(f"{where}: split, but the part that stays is not among its new identifiers")
    documents_named = {part for part in row["documents"].split("|") if part}
    for new_id in new_ids:
        if new_id not in live:
            problems.append(f"{where}: {new_id} is in no document")
            continue
        document, requirement = live[new_id]
        if document.name not in documents_named:
            problems.append(f"{where}: {new_id} is in {document.name}, which the row does not name")
        if new_id != identifier and identifier not in requirement.was:
            problems.append(f"{document.name}: {new_id}: its notes do not start with Was ... {identifier}")
    return problems


def check() -> int:
    problems: list[str] = []
    live_documents, found_problems = documents()
    problems += found_problems
    archive = parse(ARCHIVE) if ARCHIVE.exists() else None
    if archive is None:
        problems.append(f"{ARCHIVE.relative_to(PRD)}: missing")
    markdown_homes: dict[str, list[str]] = defaultdict(list)
    csv_homes: dict[str, list[str]] = defaultdict(list)
    live: dict[str, tuple[Document, Requirement]] = {}
    for document in live_documents + ([archive] if archive else []):
        for requirement in document.requirements:
            markdown_homes[requirement.id].append(document.name)
            if document is not archive:
                live[requirement.id] = (document, requirement)
        if document.csv_path.exists():
            for row in read_csv(document.csv_path):
                csv_homes[row["id"]].append(document.csv_path.relative_to(PRD).as_posix())
    for identifier, homes in sorted(markdown_homes.items()):
        if len(homes) > 1:
            problems.append(f"{identifier}: in more than one place: {', '.join(homes)}")
    for identifier, homes in sorted(csv_homes.items()):
        if len(homes) > 1:
            problems.append(f"{identifier}: in more than one CSV: {', '.join(homes)}")
    for document in live_documents:
        problems += check_requirements(document, DOCUMENTS[document.name])
        problems += compare(document, COLUMNS)
        for requirement in document.requirements:
            problems += check_dependencies(document, requirement, live)
            if requirement.retired:
                problems += check_retired(document, requirement, live)
    if archive:
        problems += compare(archive, ARCHIVE_COLUMNS)
        problems += check_map(archive, live)
    for pack in packs():
        problems += check_pack(pack, live)
    problems += check_plan(live)
    for problem in problems:
        print(problem)
    if problems:
        print(f"\nprd-check: {len(problems)} problems")
        return 1
    for document in live_documents:
        retired = sum(1 for requirement in document.requirements if requirement.retired)
        print(f"{document.name:48} {len(document.requirements):4} requirements" + (f", {retired} retired" if retired else ""))
    for pack in packs():
        print(f"{pack.relative_to(PRD).as_posix():48} a process reference pack, its section 2 complete")
    slices = plan_slices()
    if slices:
        print(f"{PLAN_DOCUMENT + ' section 7.2':48} {len(slices):4} slices, each requirement in one, none depending on a later one")
    map_rows = read_csv(MAP_CSV)
    later = sum(1 for row in map_rows if not row["old_id"].startswith("FF-"))
    print(f"{'requirements-map.csv':48} {len(map_rows) - later:4} rows, one per FF identifier, and {later} moves after the split")
    print("prd-check: passed")
    return 0


def write_csvs() -> int:
    live_documents, problems = documents()
    for problem in problems:
        print(problem)
    for document in live_documents:
        write_csv(document.csv_path, COLUMNS, [requirement.row() for requirement in document.requirements])
        print(f"wrote {document.csv_path.relative_to(PRD)}")
    if MAP_MD.exists():
        write_csv(MAP_CSV, MAP_COLUMNS, read_map_markdown(MAP_MD))
        print(f"wrote {MAP_CSV.relative_to(PRD)}")
    return 1 if problems else 0


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Check the requirement documents in docs/prd (ADR-0041, ADR-0043).")
    parser.add_argument("--write-csv", action="store_true", help="write every document's CSV, and the map's, from its markdown")
    sys.exit(write_csvs() if parser.parse_args().write_csv else check())
