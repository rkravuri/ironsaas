#!/usr/bin/env python3
"""Proves that tools/prd_check.py refuses what ADR-0041 forbids: each test breaks one rule in a small set of documents
written to a temporary folder and expects the check to name it. Standard library only; `make prd-check` runs it."""

from __future__ import annotations

import contextlib
import io
import sys
import tempfile
import unittest
from pathlib import Path

sys.dont_write_bytecode = True  # no __pycache__ beside the tools
sys.path.insert(0, str(Path(__file__).resolve().parent))
import prd_check  # noqa: E402


def block(identifier: str, title: str, area: str, criteria: list[str], depends: str = "", notes: str = "") -> str:
    lines = [f"#### {identifier} {title}", f"*Priority:* **Must** · *Tag:* `[Carried]` · *Area:* {area}", "",
             f"**Requirement:** The system shall do {title.lower()}.", "", "**Why:** A reason.", "", "**Acceptance criteria:**", ""]
    lines += [f"- AC{number} {text}" for number, text in enumerate(criteria, start=1)]
    lines.append("")
    if depends:
        lines += [f"*Depends on:* {depends}", ""]
    if notes:
        lines += [f"**Notes:** {notes}", ""]
    return "\n".join(lines)


GOOD = ["Given a thing, When saved, Then it is kept.", "Given a wrong thing, When saved, Then it is refused. (error case)"]
PRODUCT_SECTIONS = "## 7. Supplier views\n\nNone.\n\n## 9. Records and signatures (Part 11)\n\nNone.\n"


class PrdCheckTest(unittest.TestCase):
    def setUp(self) -> None:
        self.folder = tempfile.TemporaryDirectory()
        self.prd = Path(self.folder.name)
        self.saved = {name: getattr(prd_check, name) for name in ["PRD", "ARCHIVE", "MAP_MD", "MAP_CSV", "DOCUMENTS"]}
        prd_check.PRD = self.prd
        prd_check.ARCHIVE = self.prd / "archive" / "functional-foundation-p2p-PRD-v1.md"
        prd_check.MAP_MD = self.prd / "requirements-map.md"
        prd_check.MAP_CSV = self.prd / "requirements-map.csv"
        prd_check.DOCUMENTS = {"technical-foundation-PRD.md": "TF", "business-foundation-PRD.md": "BF", "products/requests-PRD.md": "REQ", "products/orders-PRD.md": "ORD"}
        self.files = {
            "archive/functional-foundation-p2p-PRD-v1.md": "# Old\n\n" + block("FF-001", "Legal entities", "Finance masters", GOOD) + block("FF-002", "Requests", "Requests", GOOD),
            "technical-foundation-PRD.md": "# Kernel\n\n" + block("TF-001", "Tenant registry", "Tenancy", GOOD),
            "business-foundation-PRD.md": "# Business\n\n" + block("BF-001", "Legal entities", "Organisations", GOOD, "TF-001", "Was FF-001."),
            "products/requests-PRD.md": "# Requests\n\n" + block("REQ-001", "Requests", "Requests", GOOD, "BF-001", "Was FF-002.") + PRODUCT_SECTIONS,
            "products/orders-PRD.md": "# Orders\n\n" + block("ORD-001", "Orders", "Orders", GOOD, "REQ-001, BF-001") + PRODUCT_SECTIONS,
        }
        self.map_rows = [
            "| FF-001 | Legal entities | moved | BF-001 | `business-foundation-PRD.md` |  |  |  |  |",
            "| FF-002 | Requests | moved | REQ-001 | `products/requests-PRD.md` |  |  |  |  |",
        ]

    def tearDown(self) -> None:
        for name, value in self.saved.items():
            setattr(prd_check, name, value)
        self.folder.cleanup()

    def run_check(self, after_csv=None) -> tuple[int, str]:
        """Writes the documents and their CSVs, lets the test break something, then runs the check."""
        for name, text in self.files.items():
            path = self.prd / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding="utf-8")
        table = "| Old | Title | Outcome | New | Documents | Covered by | Built by | Placed by the test | Notes |\n|---|---|---|---|---|---|---|---|---|\n"
        prd_check.MAP_MD.write_text("# Map\n\n" + table + "\n".join(self.map_rows) + "\n", encoding="utf-8")
        archive = prd_check.parse(prd_check.ARCHIVE)
        prd_check.write_csv(archive.csv_path, prd_check.ARCHIVE_COLUMNS, [{k: v for k, v in r.row().items() if k != "was"} for r in archive.requirements])
        with contextlib.redirect_stdout(io.StringIO()):
            prd_check.write_csvs()
        if after_csv:
            after_csv()
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            status = prd_check.check()
        return status, output.getvalue()

    def assert_refused(self, expected: str, after_csv=None) -> None:
        status, output = self.run_check(after_csv)
        self.assertEqual(status, 1, output)
        self.assertIn(expected, output)

    def test_the_documents_as_written_pass(self) -> None:
        status, output = self.run_check()
        self.assertEqual(status, 0, output)
        self.assertIn("prd-check: passed", output)

    def test_an_identifier_in_two_documents_is_refused(self) -> None:
        self.files["products/requests-PRD.md"] += block("BF-001", "Twice", "Requests", GOOD)
        self.assert_refused("BF-001: in more than one place")

    def test_an_old_identifier_without_a_map_row_is_refused(self) -> None:
        self.map_rows.pop()
        self.assert_refused("FF-002 of the archived PRD has no row")

    def test_a_map_row_whose_requirement_does_not_name_it_is_refused(self) -> None:
        self.files["business-foundation-PRD.md"] = "# Business\n\n" + block("BF-001", "Legal entities", "Organisations", GOOD, "TF-001", "Was FF-002.")
        self.assert_refused("BF-001: its notes do not start with Was FF-001")

    def test_a_map_row_naming_a_missing_requirement_is_refused(self) -> None:
        self.map_rows[1] = "| FF-002 | Requests | moved | REQ-002 | `products/requests-PRD.md` |  |  |  |  |"
        self.assert_refused("REQ-002 is in no document")

    def test_a_criterion_outside_the_form_is_refused(self) -> None:
        self.files["technical-foundation-PRD.md"] = "# Kernel\n\n" + block("TF-001", "Tenant registry", "Tenancy", ["It works.", GOOD[1]])
        self.assert_refused("TF-001: AC1 is not in the form Given ..., When ..., Then ...")

    def test_a_requirement_without_an_error_case_is_refused(self) -> None:
        self.files["technical-foundation-PRD.md"] = "# Kernel\n\n" + block("TF-001", "Tenant registry", "Tenancy", GOOD[:1])
        self.assert_refused("TF-001: no criterion is marked (error case)")

    def test_a_product_without_its_sections_is_refused(self) -> None:
        self.files["products/requests-PRD.md"] = self.files["products/requests-PRD.md"].replace("Records and signatures", "Records")
        self.assert_refused("products/requests-PRD.md: no section 'Records and signatures'")

    def test_a_csv_that_differs_from_its_markdown_is_refused(self) -> None:
        def edit() -> None:
            path = self.prd / "business-foundation-requirements.csv"
            path.write_text(path.read_text(encoding="utf-8").replace("Legal entities", "Legal persons"), encoding="utf-8")
        self.assert_refused("business-foundation-requirements.csv: BF-001 title differs from the markdown", edit)

    def test_a_dependency_no_document_holds_is_refused(self) -> None:
        self.files["business-foundation-PRD.md"] = "# Business\n\n" + block("BF-001", "Legal entities", "Organisations", GOOD, "TF-099", "Was FF-001.")
        self.assert_refused("BF-001 depends on TF-099, which no document holds")

    def test_an_identifier_outside_its_areas_block_is_refused(self) -> None:
        self.files["business-foundation-PRD.md"] += block("BF-021", "People", "Organisations", GOOD)
        self.assert_refused("BF-021: outside the block of its area 'Organisations' (001 to 020)")

    def test_an_identifier_with_another_documents_prefix_is_refused(self) -> None:
        self.files["business-foundation-PRD.md"] += block("REQ-002", "Stray", "Organisations", GOOD)
        self.assert_refused("REQ-002: this document's identifiers start with BF-")


    def test_a_business_requirement_on_a_product_is_refused(self) -> None:
        self.files["business-foundation-PRD.md"] = "# Business\n\n" + block("BF-001", "Legal entities", "Organisations", GOOD, "REQ-001", "Was FF-001.")
        self.assert_refused("BF-001 depends on REQ-001, which points up from the business layer to the products")

    def test_an_earlier_product_on_a_later_one_is_refused_unless_an_event(self) -> None:
        requests = "# Requests\n\n" + block("REQ-001", "Requests", "Requests", GOOD, "BF-001, ORD-001", "Was FF-002.") + PRODUCT_SECTIONS
        self.files["products/requests-PRD.md"] = requests
        self.files["products/orders-PRD.md"] = "# Orders\n\n" + block("ORD-001", "Orders", "Orders", GOOD, "BF-001") + PRODUCT_SECTIONS
        self.assert_refused("REQ-001 depends on ORD-001 of a later product")
        self.files["products/requests-PRD.md"] = requests.replace("ORD-001", "ORD-001 (event)")
        status, output = self.run_check()
        self.assertEqual(status, 0, output)

    def test_an_event_mark_on_anything_but_a_later_product_is_refused(self) -> None:
        self.files["products/orders-PRD.md"] = "# Orders\n\n" + block("ORD-001", "Orders", "Orders", GOOD, "REQ-001 (event)") + PRODUCT_SECTIONS
        self.assert_refused("ORD-001: REQ-001 is marked (event), which only a dependency on a later product is")

    def test_a_new_area_after_the_supplier_views_block_is_accepted(self) -> None:
        self.files["products/orders-PRD.md"] += block("ORD-041", "Views", "Supplier views", GOOD) + block("ORD-061", "Reconciliation", "Quantities", GOOD)
        status, output = self.run_check()
        self.assertEqual(status, 0, output)


if __name__ == "__main__":
    unittest.main(verbosity=1)
