#!/usr/bin/env python3
"""Proves tools/validation_pack.py (TF-218, ADR-0052): a small repository with a git history, written to a temporary
folder with one test of each kind and their result files, makes its pack; each test checks one part of it. Each test's
docstring ends with what it proves. Standard library only; `make trace` runs it first, with `--junit`."""

from __future__ import annotations

import hashlib
import json
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

sys.dont_write_bytecode = True  # no __pycache__ beside the tools
sys.path.insert(0, str(Path(__file__).resolve().parent))
import trace_test  # noqa: E402
import validation_pack  # noqa: E402

RISK = "# Risk classes\n\n| Area | Class | Why |\n|---|---|---|\n| Probe | critical | The probe's area holds identity. |\n"

RECORD = {
    "format": "iron-installation-record-v1",
    "tenant": "acme",
    "version": "2031.02.03.7",
    "images": [{"name": "iron-api", "reference": "ghcr.io/o/iron-api:abc", "digest": "sha256:" + "1" * 64}],
    "migrations": {"kernel_audit": ["20310101000000_Initial"]},
    "configuration": {"catalogue_sha256": "c" * 64, "seeds_sha256": "d" * 64},
    "smoke": {"steps": [{"name": "health", "status": "passed"}]},
    "browser_tests": {"passed": 3, "failed": 0, "skipped": 0},
    "checks": [{"name": "version", "passed": True, "detail": "the API image says 2031.02.03.7"}],
}


class ValidationPackTest(unittest.TestCase):
    def setUp(self) -> None:
        self.folder = tempfile.TemporaryDirectory()
        self.root = Path(self.folder.name)
        # The trace's own probe repository: one test of every kind, their results, a done slice.
        probe = trace_test.TraceTest("test_every_kind_is_read_and_joined_to_its_result")
        probe.setUp()
        self.files = dict(probe.files)
        probe.tearDown()
        self.files["docs/trace/risk.md"] = RISK
        self.files["src/Kernel/Iron.Kernel.Tenancy/Services/licensing-catalogue.json"] = "{}\n"
        self.record = self.root / "installation-record.json"
        self.record.write_text(json.dumps(RECORD), encoding="utf-8")

    def tearDown(self) -> None:
        self.folder.cleanup()

    def write(self) -> None:
        for name, text in self.files.items():
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding="utf-8")

    def git(self, *args: str) -> str:
        return subprocess.run(["git", "-c", "user.name=Probe", "-c", "user.email=probe@example.test", "-c", "init.defaultBranch=main", *args], cwd=self.root, capture_output=True, text=True, check=True).stdout

    def history(self) -> None:
        """A release, then a task merged with its risk note and a commit on main after it."""
        self.write()
        self.git("init", "-q")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "Start")
        self.git("tag", "v2031.01.01.1")
        self.git("checkout", "-q", "-b", "slice/s9-t1")
        tests = self.root / "tests/Iron.Probe.Tests/RegistryTests.cs"
        tests.write_text(tests.read_text(encoding="utf-8").replace("public void Draws()\n    {\n    }", "public void Draws()\n    {\n        // drawn again\n    }"), encoding="utf-8")
        smoke = self.root / "tools/Iron.Smoke/Program.cs"
        smoke.write_text(smoke.read_text(encoding="utf-8") + 'await report.StepAsync("again", ["TF-002 AC1"], async () => "ok");\n', encoding="utf-8")
        prd = self.root / "docs/prd/foundation/technical-foundation-PRD.md"
        prd.write_text(prd.read_text(encoding="utf-8").replace("**Requirement:** Registry.", "**Requirement:** Registry, kept.", 1), encoding="utf-8")
        (self.root / "docs/risk").mkdir(parents=True, exist_ok=True)
        (self.root / "docs/risk/s9-t1.md").write_text("The probe's provisioning changed; its smoke step covers it.\n", encoding="utf-8")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "Provision again")
        self.git("checkout", "-q", "main")
        self.git("merge", "-q", "--no-ff", "slice/s9-t1", "-m", "Merge slice/s9-t1: provisioning again")
        (self.root / "docs/STATUS.md").write_text("S9: T1 done\n", encoding="utf-8")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "S9: T1 done")
        results = self.root / "artifacts/test-results/smoke/smoke-report.json"
        results.write_text(json.dumps({"steps": [{"name": "tenant", "status": "passed"}, {"name": "health", "status": "passed"}, {"name": "again", "status": "passed"}]}), encoding="utf-8")

    def build(self, version: str = "2031.02.03.7", sboms: Path | None = None) -> validation_pack.Pack:
        return validation_pack.build(self.root, version, None, self.record, sboms=sboms)

    def boms(self, images: list[str] | None = None) -> Path:
        """A folder with a small CycloneDX document per image, as the release's image scan writes them."""
        folder = self.root / "sbom-in"
        folder.mkdir(exist_ok=True)
        for image in validation_pack.IMAGES if images is None else images:
            document = {"bomFormat": "CycloneDX", "specVersion": "1.6", "metadata": {"component": {"name": f"ghcr.io/o/{image}"}}, "components": [{"type": "library", "name": "zlib", "version": "1.3.1"}]}
            (folder / f"{image}.cdx.json").write_text(json.dumps(document), encoding="utf-8")
        return folder

    def test_a_release_carries_each_images_bill_of_materials_hashed_in_its_manifest(self) -> None:
        """Each of the five images' CycloneDX bills of materials is in the pack under sbom/, listed on its index and hashed in its manifest. [TF-076 AC3, ADR-0055]"""
        self.history()
        pack = self.build(sboms=self.boms())
        archive = validation_pack.write(pack, self.root / "out")
        with zipfile.ZipFile(archive) as zipped:
            manifest = json.loads(zipped.read("validation-pack-2031.02.03.7/manifest.json"))
            listed = {entry["path"]: entry["sha256"] for entry in manifest["files"]}
            for image in validation_pack.IMAGES:
                path = f"sbom/{image}.cdx.json"
                self.assertEqual(listed[path], hashlib.sha256(zipped.read(f"validation-pack-2031.02.03.7/{path}")).hexdigest())
        self.assertFalse([p for p in pack.problems if "bill of materials" in p])
        self.assertIn(b'<a href="sbom/iron-fleet.cdx.json">iron-fleet</a>', pack.files["index.html"])

    def test_a_release_without_a_bill_of_materials_for_an_image_is_refused(self) -> None:
        """A release missing an image's bill of materials, or holding one that is not CycloneDX, may not be published; the command asks for them. [TF-076 AC3, ADR-0055]"""
        self.history()
        folder = self.boms([image for image in validation_pack.IMAGES if image != "iron-shell"])
        (folder / "iron-api.cdx.json").write_text(json.dumps({"spdxVersion": "SPDX-2.3"}), encoding="utf-8")
        pack = self.build(sboms=folder)
        self.assertIn("no bill of materials for the image iron-shell", pack.problems)
        self.assertIn("the bill of materials iron-api.cdx.json is not a CycloneDX document with its components", pack.problems)
        self.assertFalse(pack.passed)
        self.assertEqual(validation_pack.main_for(self.root, ["--version", "2031.02.03.7", "--installation", str(self.record), "--output", str(self.root / "out")]), 2)

    def test_the_pack_lists_every_requirement_with_its_criteria_tests_and_results(self) -> None:
        """Every requirement of the documents is listed with each criterion, the tests that prove it and their results from the run. [TF-218 AC1]"""
        self.history()
        pack = self.build()
        document = pack.documents["traceability"]
        by_id = {r["id"]: r for r in document["requirements"]}  # type: ignore[union-attr, index]
        self.assertEqual(sorted(by_id), ["TF-001", "TF-002", "TF-003"])
        first = {c["id"]: c for c in by_id["TF-001"]["criteria"]}
        self.assertEqual(first["TF-001 AC1"]["state"], "passed")
        self.assertEqual([t["test"] for t in first["TF-001 AC1"]["tests"]], ["Iron.Probe.Tests › RegistryTests.SavesATenant", "src/registry.test.ts › lists the tenant"])
        self.assertEqual(first["TF-001 AC2"]["state"], "failed")
        self.assertIn("TF-001 AC2", document["failed"])  # type: ignore[index]
        self.assertIn(b"Iron.Probe.Tests", pack.files["traceability.csv"])
        self.assertIn(b"<h3 id=\"TF-002\">", pack.files["traceability.html"])

    def test_an_untested_requirement_of_a_done_slice_without_an_approved_exception_fails_the_release(self) -> None:
        """A done slice's criterion without a test is flagged in the pack, and the pack refuses the release. [TF-218 AC2]"""
        self.files["tools/probe_test.py"] = trace_test.PYTHON.replace("[TF-003 AC2]", "[ADR-0001]")
        self.history()
        pack = self.build()
        self.assertEqual(pack.documents["traceability"]["gaps"], ["TF-003 AC2"])  # type: ignore[index]
        self.assertFalse(pack.passed)
        self.assertIn("a gap: TF-003 AC2 of a done slice has no test and no approved exception", pack.problems)
        self.assertIn(b"state-gap", pack.files["traceability.html"])
        self.assertEqual(validation_pack.main_for(self.root, ["--version", "2031.02.03.7", "--installation", str(self.record), "--output", str(self.root / "out"), "--sbom", str(self.boms())]), 1)

    def test_an_approved_exception_lets_the_gap_pass(self) -> None:
        """The same gap with an approved exception is listed as excepted and no longer stops the release. [TF-218 AC2]"""
        self.files["tools/probe_test.py"] = trace_test.PYTHON.replace("[TF-003 AC2]", "[ADR-0001]")
        self.files["docs/trace/exceptions.md"] += "| TF-003 AC2 | The probe resolves nothing yet. | 2031-01-01 |\n"
        self.files["artifacts/test-results/dotnet/run.trx"] = trace_test.TRX.replace('outcome="Failed"', 'outcome="Passed"')
        self.history()
        pack = self.build()
        criteria = {c["id"]: c for r in pack.documents["traceability"]["requirements"] for c in r["criteria"]}  # type: ignore[union-attr, index]
        self.assertEqual(criteria["TF-003 AC2"]["state"], "excepted")
        self.assertEqual(criteria["TF-003 AC2"]["exception"], {"reason": "The probe resolves nothing yet.", "approved": "2031-01-01"})
        self.assertEqual(pack.problems, [])
        self.assertTrue(pack.passed)

    def test_the_manifest_lists_every_file_whose_hash_recomputes_from_the_zip(self) -> None:
        """manifest.json names every file of the pack with its SHA-256, which an auditor recomputes from the ZIP. [ADR-0052]"""
        self.history()
        pack = self.build()
        archive = validation_pack.write(pack, self.root / "out")
        with zipfile.ZipFile(archive) as zipped:
            manifest = json.loads(zipped.read("validation-pack-2031.02.03.7/manifest.json"))
            names = sorted(n.split("/", 1)[1] for n in zipped.namelist())
            for entry in manifest["files"]:
                self.assertEqual(entry["sha256"], hashlib.sha256(zipped.read(f"validation-pack-2031.02.03.7/{entry['path']}")).hexdigest())
        self.assertEqual(sorted([f["path"] for f in manifest["files"]] + ["manifest.json"]), names)
        self.assertEqual((manifest["version"], manifest["previous_release"]), ("2031.02.03.7", "v2031.01.01.1"))
        self.assertIn("installation-record.json", names)
        self.assertIn("configuration.json", names)

    def test_the_release_notes_list_the_merged_tasks_and_commits_since_the_last_release_with_the_requirements_they_touched(self) -> None:
        """The notes list each merged task with its commits and each commit made on main since the last release, and the requirements each touched. [ADR-0052]"""
        self.history()
        notes = self.build().documents["release-notes"]
        changes = notes["changes"]  # type: ignore[index]
        self.assertEqual([(c["kind"], c["name"]) for c in changes], [("task", "slice/s9-t1"), ("commit", "S9: T1 done")])
        # TF-001 by its text in the requirements document, TF-002 by the smoke step the task added.
        self.assertEqual(changes[0]["requirements"], ["TF-001", "TF-002"])
        self.assertEqual([c["subject"] for c in changes[0]["commits"]], ["Provision again"])
        self.assertEqual(changes[1]["requirements"], [])
        self.assertEqual(notes["previous_release"], "v2031.01.01.1")

    def test_each_change_takes_the_highest_risk_of_the_requirements_it_touched_with_its_written_assessment(self) -> None:
        """A change is critical, high or normal by the requirements it touched, by their area's class or their priority, and carries the assessment its task committed. [ADR-0052]"""
        self.history()
        risk = self.build().documents["risk-assessment"]
        task, commit = risk["changes"]  # type: ignore[index]
        self.assertEqual(task["risk"], "critical")
        self.assertEqual(task["assessment"], "The probe's provisioning changed; its smoke step covers it.")
        self.assertEqual(commit["risk"], "normal")
        self.assertIsNone(commit["assessment"])
        self.assertEqual(risk["summary"], {"critical": 1, "high": 0, "normal": 1})  # type: ignore[index]

    def test_an_installation_check_that_did_not_pass_stops_the_release_and_the_record_is_in_the_pack(self) -> None:
        """The installation record goes into the pack with its checks, and a check that did not pass stops the release. [ADR-0052]"""
        self.history()
        failing = dict(RECORD, checks=[{"name": "seeds", "passed": False, "detail": "the code seeds d, the tenant's database holds e"}])
        self.record.write_text(json.dumps(failing), encoding="utf-8")
        pack = self.build()
        self.assertIn("installation check 'seeds' did not pass: the code seeds d, the tenant's database holds e", pack.problems)
        self.assertEqual(json.loads(pack.files["installation-record.json"])["installation_record"]["images"][0]["digest"], "sha256:" + "1" * 64)
        self.assertEqual(json.loads(pack.files["configuration.json"])["configuration"]["measured"]["catalogue_sha256"], "c" * 64)

    def test_a_version_out_of_form_is_refused(self) -> None:
        """A release's version is its date and the run's number, or development on a laptop. [ADR-0052]"""
        self.assertTrue(validation_pack.VERSION.match("2026.10.23.57"))
        self.assertFalse(validation_pack.VERSION.match("2026.10.23"))
        self.history()
        self.assertEqual(validation_pack.main_for(self.root, ["--version", "v1.2.3", "--output", str(self.root / "out")]), 2)


if __name__ == "__main__":
    import unittest_junit

    sys.exit(unittest_junit.main(sys.modules[__name__], "validation_pack_test"))
