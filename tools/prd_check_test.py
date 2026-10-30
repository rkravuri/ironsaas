#!/usr/bin/env python3
"""Proves that tools/prd_check.py refuses what ADR-0041, ADR-0042 and ADR-0043 forbid: each test breaks one rule in a small set of documents
written to a temporary folder and expects the check to name it; its docstring ends with the decision it proves (ADR-0044). Standard
library only; `make prd-check` runs it, with `--junit` for `make trace`."""

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
        self.saved = {name: getattr(prd_check, name) for name in ["PRD", "ARCHIVE", "MAP_MD", "MAP_CSV", "DOCUMENTS", "KNOWN_LATER"]}
        prd_check.PRD = self.prd
        prd_check.ARCHIVE = self.prd / "archive" / "functional-foundation-p2p-PRD-v1.md"
        prd_check.MAP_MD = self.prd / "requirements-map.md"
        prd_check.MAP_CSV = self.prd / "requirements-map.csv"
        prd_check.DOCUMENTS = {
            "foundation/technical-foundation-PRD.md": "TF",
            "foundation/business-foundation-PRD.md": "BF",
            "suites/procurement/requests-PRD.md": "REQ",
            "suites/procurement/orders-PRD.md": "ORD",
        }
        self.files = {
            "archive/functional-foundation-p2p-PRD-v1.md": "# Old\n\n" + block("FF-001", "Legal entities", "Finance masters", GOOD) + block("FF-002", "Requests", "Requests", GOOD),
            "foundation/technical-foundation-PRD.md": "# Kernel\n\n" + block("TF-001", "Tenant registry", "Tenancy", GOOD),
            "foundation/business-foundation-PRD.md": "# Business\n\n" + block("BF-001", "Legal entities", "Organisations", GOOD, "TF-001", "Was FF-001."),
            "suites/procurement/requests-PRD.md": "# Requests\n\n" + block("REQ-001", "Requests", "Requests", GOOD, "BF-001", "Was FF-002.") + PRODUCT_SECTIONS,
            "suites/procurement/orders-PRD.md": "# Orders\n\n" + block("ORD-001", "Orders", "Orders", GOOD, "REQ-001, BF-001") + PRODUCT_SECTIONS,
        }
        self.map_rows = [
            "| FF-001 | Legal entities | moved | BF-001 | `foundation/business-foundation-PRD.md` |  |  |  |  |",
            "| FF-002 | Requests | moved | REQ-001 | `suites/procurement/requests-PRD.md` |  |  |  |  |",
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
        """Documents that keep every rule pass the check. [ADR-0041]"""
        status, output = self.run_check()
        self.assertEqual(status, 0, output)
        self.assertIn("prd-check: passed", output)

    def test_an_identifier_in_two_documents_is_refused(self) -> None:
        """An identifier is held by one document only. [ADR-0041]"""
        self.files["suites/procurement/requests-PRD.md"] += block("BF-001", "Twice", "Requests", GOOD)
        self.assert_refused("BF-001: in more than one place")

    def test_an_old_identifier_without_a_map_row_is_refused(self) -> None:
        """Every identifier of the archived PRD has its row in the map. [ADR-0041]"""
        self.map_rows.pop()
        self.assert_refused("FF-002 of the archived PRD has no row")

    def test_a_map_row_whose_requirement_does_not_name_it_is_refused(self) -> None:
        """A moved requirement starts its notes with the identifier it was. [ADR-0041]"""
        self.files["foundation/business-foundation-PRD.md"] = "# Business\n\n" + block("BF-001", "Legal entities", "Organisations", GOOD, "TF-001", "Was FF-002.")
        self.assert_refused("BF-001: its notes do not start with Was FF-001")

    def test_a_map_row_naming_a_missing_requirement_is_refused(self) -> None:
        """A map row names only requirements a document holds. [ADR-0041]"""
        self.map_rows[1] = "| FF-002 | Requests | moved | REQ-002 | `suites/procurement/requests-PRD.md` |  |  |  |  |"
        self.assert_refused("REQ-002 is in no document")

    def test_a_criterion_outside_the_form_is_refused(self) -> None:
        """Every criterion is in the Given, When, Then form. [ADR-0041]"""
        self.files["foundation/technical-foundation-PRD.md"] = "# Kernel\n\n" + block("TF-001", "Tenant registry", "Tenancy", ["It works.", GOOD[1]])
        self.assert_refused("TF-001: AC1 is not in the form Given ..., When ..., Then ...")

    def test_a_requirement_without_an_error_case_is_refused(self) -> None:
        """Every requirement has a criterion marked as its error case. [ADR-0041]"""
        self.files["foundation/technical-foundation-PRD.md"] = "# Kernel\n\n" + block("TF-001", "Tenant registry", "Tenancy", GOOD[:1])
        self.assert_refused("TF-001: no criterion is marked (error case)")

    def test_a_product_without_its_sections_is_refused(self) -> None:
        """A product PRD has the sections Supplier views and Records and signatures. [ADR-0041]"""
        self.files["suites/procurement/requests-PRD.md"] = self.files["suites/procurement/requests-PRD.md"].replace("Records and signatures", "Records")
        self.assert_refused("suites/procurement/requests-PRD.md: no section 'Records and signatures'")

    def test_a_csv_that_differs_from_its_markdown_is_refused(self) -> None:
        """A document's CSV equals its markdown in every cell. [ADR-0041]"""
        def edit() -> None:
            path = self.prd / "foundation" / "business-foundation-requirements.csv"
            path.write_text(path.read_text(encoding="utf-8").replace("Legal entities", "Legal persons"), encoding="utf-8")
        self.assert_refused("foundation/business-foundation-requirements.csv: BF-001 title differs from the markdown", edit)

    def test_a_dependency_no_document_holds_is_refused(self) -> None:
        """A dependency names a requirement some document holds. [ADR-0041]"""
        self.files["foundation/business-foundation-PRD.md"] = "# Business\n\n" + block("BF-001", "Legal entities", "Organisations", GOOD, "TF-099", "Was FF-001.")
        self.assert_refused("BF-001 depends on TF-099, which no document holds")

    def test_an_identifier_outside_its_areas_block_is_refused(self) -> None:
        """An area's identifiers stay in its block of twenty. [ADR-0041]"""
        self.files["foundation/business-foundation-PRD.md"] += block("BF-021", "People", "Organisations", GOOD)
        self.assert_refused("BF-021: outside the block of its area 'Organisations' (001 to 020)")

    def test_an_identifier_with_another_documents_prefix_is_refused(self) -> None:
        """A document's identifiers carry its own prefix. [ADR-0041]"""
        self.files["foundation/business-foundation-PRD.md"] += block("REQ-002", "Stray", "Organisations", GOOD)
        self.assert_refused("REQ-002: this document's identifiers start with BF-")


    def test_a_business_requirement_on_a_product_is_refused(self) -> None:
        """A dependency never points up a layer. [ADR-0042]"""
        self.files["foundation/business-foundation-PRD.md"] = "# Business\n\n" + block("BF-001", "Legal entities", "Organisations", GOOD, "REQ-001", "Was FF-001.")
        self.assert_refused("BF-001 depends on REQ-001, which points up from the business layer to the products")

    def test_an_earlier_product_on_a_later_one_is_refused_unless_an_event(self) -> None:
        """An earlier product hears from a later one only through an event. [ADR-0042]"""
        requests = "# Requests\n\n" + block("REQ-001", "Requests", "Requests", GOOD, "BF-001, ORD-001", "Was FF-002.") + PRODUCT_SECTIONS
        self.files["suites/procurement/requests-PRD.md"] = requests
        self.files["suites/procurement/orders-PRD.md"] = "# Orders\n\n" + block("ORD-001", "Orders", "Orders", GOOD, "BF-001") + PRODUCT_SECTIONS
        self.assert_refused("REQ-001 depends on ORD-001 of a later product")
        self.files["suites/procurement/requests-PRD.md"] = requests.replace("ORD-001", "ORD-001 (event)")
        status, output = self.run_check()
        self.assertEqual(status, 0, output)

    def test_an_event_mark_on_anything_but_a_later_product_is_refused(self) -> None:
        """Only a dependency on a later product is marked as an event. [ADR-0042]"""
        self.files["suites/procurement/orders-PRD.md"] = "# Orders\n\n" + block("ORD-001", "Orders", "Orders", GOOD, "REQ-001 (event)") + PRODUCT_SECTIONS
        self.assert_refused("ORD-001: REQ-001 is marked (event), which only a dependency on a later product is")

    def test_a_new_area_after_the_supplier_views_block_is_accepted(self) -> None:
        """An area added later takes the next free block wherever it stands. [ADR-0041]"""
        self.files["suites/procurement/orders-PRD.md"] += block("ORD-041", "Views", "Supplier views", GOOD) + block("ORD-061", "Reconciliation", "Quantities", GOOD)
        status, output = self.run_check()
        self.assertEqual(status, 0, output)

    def with_a_second_suite(self, depends: str) -> None:
        """A School suite's product beside the eProcurement Suite's, depending on what it is given (ADR-0043)."""
        prd_check.DOCUMENTS = {**prd_check.DOCUMENTS, "suites/school/pupils-PRD.md": "PUP"}
        self.files["suites/school/pupils-PRD.md"] = "# Pupils\n\n" + block("PUP-001", "Pupils", "Pupils", GOOD, depends)

    def test_a_suites_document_on_another_suites_is_refused(self) -> None:
        """A suite's document never depends on another suite's. [ADR-0043]"""
        self.with_a_second_suite("BF-001, REQ-001")
        self.assert_refused("PUP-001 depends on REQ-001 of the procurement suite; a suite never depends on another suite")

    def test_a_second_suite_on_the_foundation_alone_is_accepted(self) -> None:
        """A second suite's product depends on the Foundation and needs only its own suite's sections. [ADR-0043]"""
        # A School product needs no supplier views: the sections a product PRD has are its own suite's.
        self.with_a_second_suite("BF-001, TF-001")
        status, output = self.run_check()
        self.assertEqual(status, 0, output)

    def test_the_foundation_on_a_suite_is_refused(self) -> None:
        """The Foundation never depends on a suite. [ADR-0043]"""
        self.files["foundation/technical-foundation-PRD.md"] = "# Kernel\n\n" + block("TF-001", "Tenant registry", "Tenancy", GOOD, "ORD-001")
        self.assert_refused("TF-001 depends on ORD-001, which points up from the kernel to the products")

    def with_a_purchasing_master(self, depends: str = "BF-001", notes: str = "") -> None:
        """The eProcurement Suite's shared master, below its products and above the Foundation (ADR-0043)."""
        prd_check.DOCUMENTS = {**prd_check.DOCUMENTS, "suites/procurement/purchasing-PRD.md": "PUR"}
        self.files["suites/procurement/purchasing-PRD.md"] = "# Purchasing\n\n" + block("PUR-001", "Purchasing groups", "Purchasing groups", GOOD, depends, notes)

    def test_a_product_on_its_suites_master_is_accepted_and_a_master_needs_no_product_sections(self) -> None:
        """A product may depend on its suite's shared master, which has no product sections. [ADR-0043]"""
        self.with_a_purchasing_master()
        self.files["suites/procurement/orders-PRD.md"] = "# Orders\n\n" + block("ORD-001", "Orders", "Orders", GOOD, "REQ-001, PUR-001") + PRODUCT_SECTIONS
        status, output = self.run_check()
        self.assertEqual(status, 0, output)

    def test_a_suites_master_on_a_product_is_refused(self) -> None:
        """A suite's shared master never depends on its products. [ADR-0043]"""
        self.with_a_purchasing_master("BF-001, REQ-001")
        self.assert_refused("PUR-001 depends on REQ-001, which points up from the suite's shared masters to the products")

    def test_a_part_moved_after_the_split_names_its_old_identifiers_and_the_map_lists_it(self) -> None:
        """A part moved after the split names every identifier it had, and the map lists the move. [ADR-0043]"""
        self.with_a_purchasing_master(notes="Was FF-001 and BF-001, the part that moved.")
        self.map_rows[0] = "| FF-001 | Legal entities | split | BF-001, PUR-001 | `foundation/business-foundation-PRD.md`, `suites/procurement/purchasing-PRD.md` |  |  |  |  |"
        self.map_rows.append("| BF-001 | Legal entities | split | BF-001, PUR-001 | `foundation/business-foundation-PRD.md`, `suites/procurement/purchasing-PRD.md` |  |  |  |  |")
        status, output = self.run_check()
        self.assertEqual(status, 0, output)

    def test_a_was_without_its_move_in_the_map_is_refused(self) -> None:
        """A requirement that says what it was has its move in the map. [ADR-0043]"""
        self.with_a_purchasing_master(notes="Was BF-001, the part that moved.")
        self.assert_refused("suites/procurement/purchasing-PRD.md: PUR-001 says it was BF-001, but the map's row does not list it")

    def test_a_moved_identifier_still_held_is_refused(self) -> None:
        """A moved identifier is never held again. [ADR-0043]"""
        self.with_a_purchasing_master(notes="Was BF-001.")
        self.map_rows.append("| BF-001 | Legal entities | moved | PUR-001 | `suites/procurement/purchasing-PRD.md` |  |  |  |  |")
        self.assert_refused("requirements-map.csv: BF-001: moved, but foundation/business-foundation-PRD.md still holds it; a moved identifier is never reused")

    def test_a_move_whose_part_does_not_name_it_is_refused(self) -> None:
        """Each part of a move names the identifier it came from. [ADR-0043]"""
        self.with_a_purchasing_master()
        self.map_rows.append("| BF-001 | Legal entities | split | BF-001, PUR-001 | `foundation/business-foundation-PRD.md`, `suites/procurement/purchasing-PRD.md` |  |  |  |  |")
        self.assert_refused("suites/procurement/purchasing-PRD.md: PUR-001: its notes do not start with Was ... BF-001")

    def test_a_document_outside_its_folder_is_unknown(self) -> None:
        """A document lives in its layer's or its suite's folder. [ADR-0043]"""
        self.files["business-foundation-PRD.md"] = self.files["foundation/business-foundation-PRD.md"].replace("BF-001", "BF-002")
        self.assert_refused("business-foundation-PRD.md: a document the check does not know")

    def test_a_suites_readme_is_not_a_requirements_document(self) -> None:
        """A suite's README holds its module list, not requirements, and the check passes it by. [ADR-0058]"""
        self.files["suites/procurement/README.md"] = "# The eProcurement Suite's modules\n\n| # | Module | Document |\n|---|---|---|\n| 3 | Requests and Intake | REQ-001 |\n"
        status, output = self.run_check()
        self.assertEqual(status, 0, output)

    def retire_the_request(self, notes: str, orders_depend_on: str = "REQ-002, BF-001") -> None:
        """REQ-001 retired, REQ-002 beside it as its successor, and Orders depending on what it is given (ADR-0041)."""
        retired = block("REQ-001", "Requests", "Requests", GOOD, "BF-001", notes).replace("`[Carried]`", "`[Retired]`")
        successor = block("REQ-002", "Requests again", "Requests", GOOD, "BF-001")
        self.files["suites/procurement/requests-PRD.md"] = "# Requests\n\n" + retired + successor + PRODUCT_SECTIONS
        self.files["suites/procurement/orders-PRD.md"] = "# Orders\n\n" + block("ORD-001", "Orders", "Orders", GOOD, orders_depend_on) + PRODUCT_SECTIONS

    def test_a_retired_requirement_keeps_its_identifier_and_names_its_successors(self) -> None:
        """A retired requirement stays in its document under its identifier, never reused, and names what replaced it. [ADR-0041]"""
        self.retire_the_request("Was FF-002. Retired 2026-10-21. Replaced by REQ-002.")
        status, output = self.run_check()
        self.assertEqual(status, 0, output)
        self.assertIn("2 requirements, 1 retired", output)

    def test_a_retired_requirement_without_its_successors_is_refused(self) -> None:
        """A retired requirement says what replaced it. [ADR-0041]"""
        self.retire_the_request("Was FF-002. Retired 2026-10-21.")
        self.assert_refused("REQ-001: retired, but its notes do not say 'Replaced by' and its successors")

    def test_a_retired_requirement_replaced_by_one_no_document_holds_is_refused(self) -> None:
        """The successors a retired requirement names exist. [ADR-0041]"""
        self.retire_the_request("Was FF-002. Retired 2026-10-21. Replaced by REQ-002 to REQ-003.")
        self.assert_refused("REQ-001: replaced by REQ-003, which no document holds")

    def test_a_dependency_on_a_retired_requirement_is_refused(self) -> None:
        """What depended on a retired requirement depends on its successors. [ADR-0041]"""
        self.retire_the_request("Was FF-002. Retired 2026-10-21. Replaced by REQ-002.", orders_depend_on="REQ-001, BF-001")
        self.assert_refused("ORD-001 depends on REQ-001, which is retired; depend on what replaced it (REQ-002)")

    def with_a_pack(self, rows: list[str]) -> None:
        """A process reference pack for Orders, with these rows in its section 2 (PM-001)."""
        table = "| Requirement | PCF | Map step |\n|---|---|---|\n" + "\n".join(rows) + "\n"
        self.files["suites/procurement/process-reference/orders.md"] = "# Pack: Orders\n\n## 1. Scope\n\nOrders.\n\n## 2. APQC PCF mapping\n\n" + table + "\n## 3. Process maps\n\nNone.\n"

    def test_a_pack_that_maps_every_requirement_of_its_document_passes(self) -> None:
        """A pack is not a requirements document, and its section 2 maps every live requirement of its module. [PM-001 AC2]"""
        self.with_a_pack(["| ORD-001 Orders | 4.2.3.1 | OR-01 |", "| BF-001 Legal entities | 4.2.5.1 | OR-02 |"])
        status, output = self.run_check()
        self.assertEqual(status, 0, output)
        self.assertIn("suites/procurement/process-reference/orders.md", output)

    def test_a_pack_that_leaves_out_a_requirement_of_its_document_is_refused(self) -> None:
        """Every live requirement of the module has its row in the pack's section 2; a retired one need not. [PM-001 AC2]"""
        self.files["suites/procurement/orders-PRD.md"] = self.files["suites/procurement/orders-PRD.md"].replace(PRODUCT_SECTIONS, block("ORD-002", "Order lines", "Orders", GOOD, "ORD-001") + PRODUCT_SECTIONS)
        self.with_a_pack(["| ORD-001 Orders | 4.2.3.1 | OR-01 |"])
        self.assert_refused("suites/procurement/process-reference/orders.md: ORD-002 of suites/procurement/orders-PRD.md has no row in section 2 (PM-001 AC2)")

    def test_a_pack_that_maps_an_unknown_requirement_or_no_step_is_refused(self) -> None:
        """A pack maps only requirements a document holds, each to its PCF element and its map step. [PM-001 AC2]"""
        self.with_a_pack(["| ORD-001 to ORD-002 Orders | 4.2.3.1 | OR-01 |"])
        self.assert_refused("section 2 maps ORD-002, which no document holds")
        self.with_a_pack(["| ORD-001 Orders |  | OR-01 |"])
        self.assert_refused("section 2 maps ORD-001 without its PCF element or its map step")

    def with_a_plan(self, rows: list[str]) -> None:
        """The technical PRD's build plan, section 7.2, with these rows (order, slice, builds, needs)."""
        table = "| Order | Slice | Builds | Needs |\n|---|---|---|---|\n" + "\n".join(rows) + "\n"
        self.files["foundation/technical-foundation-PRD.md"] = "# Kernel\n\n" + block("TF-001", "Tenant registry", "Tenancy", GOOD) + "## 7. Build plan\n\n### 7.2 The business and suite slices\n\n" + table + "\n## 8. Decisions\n"

    def test_a_plan_with_every_requirement_in_one_slice_and_no_later_dependency_passes(self) -> None:
        """Every business and suite requirement is in one slice, and none depends on a later one. [ADR-0042]"""
        self.with_a_plan(["| 1 | B1 Masters | BF-001 (its import later) | |", "| 2 | PR2 Requests | REQ-001 | B1 |", "| 3 | PR3 Orders | ORD-001 | PR2 |"])
        status, output = self.run_check()
        self.assertEqual(status, 0, output)
        self.assertIn("3 slices, each requirement in one", output)

    def test_a_dependency_on_a_later_slice_is_refused_unless_known(self) -> None:
        """A requirement depends on none placed in a later slice, except through an event or as a listed known case. [ADR-0042]"""
        self.with_a_plan(["| 1 | B1 Masters | BF-001 | |", "| 2 | PR2 Orders first | ORD-001 | B1 |", "| 3 | PR3 Requests | REQ-001 | PR2 |"])
        self.assert_refused("ORD-001, built in PR2 Orders first, depends on REQ-001, placed in a later slice (PR3 Requests)")
        prd_check.KNOWN_LATER = {("ORD-001", "REQ-001"): "A known case, with its reason."}
        status, output = self.run_check()
        self.assertEqual(status, 0, output)

    def test_a_requirement_in_no_slice_or_a_retired_one_in_a_slice_is_refused(self) -> None:
        """Every live business and suite requirement is in one slice, and a retired one in none. [ADR-0042, ADR-0041]"""
        self.with_a_plan(["| 1 | B1 Masters | BF-001 | |", "| 2 | PR2 Requests | REQ-001 | B1 |"])
        self.assert_refused("ORD-001 is in no slice; every business and suite requirement is in one")
        self.retire_the_request("Was FF-002. Retired 2026-10-21. Replaced by REQ-002.")
        self.with_a_plan(["| 1 | B1 Masters | BF-001 | |", "| 2 | PR2 Requests | REQ-001, REQ-002 | B1 |", "| 3 | PR3 Orders | ORD-001 | PR2 |"])
        self.assert_refused("REQ-001 is retired but placed in PR2 Requests; place its successors")


if __name__ == "__main__":
    import unittest_junit

    sys.exit(unittest_junit.main(sys.modules[__name__], "prd_check_test"))
