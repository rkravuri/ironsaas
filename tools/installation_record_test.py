#!/usr/bin/env python3
"""Proves tools/installation_record.py (TF-218, ADR-0052): the record's fields and its checks of an installation against
the infrastructure code, on a small repository written to a temporary folder. Each test's docstring ends with what it
proves. Standard library only; `make trace` runs it first, with `--junit`."""

from __future__ import annotations

import hashlib
import sys
import tempfile
import unittest
from pathlib import Path

sys.dont_write_bytecode = True  # no __pycache__ beside the tools
sys.path.insert(0, str(Path(__file__).resolve().parent))
import installation_record  # noqa: E402

CONTEXT = 'namespace Iron.Kernel.Probe.Data;\n\n[Schema("kernel_probe")]\npublic sealed class ProbeDbContext { }\n'
SEEDS = [("tenant-administrator", ["b.thing.read", "a.thing.manage"]), ("read-only", ["b.thing.read"])]


class InstallationRecordTest(unittest.TestCase):
    def setUp(self) -> None:
        self.folder = tempfile.TemporaryDirectory()
        self.root = Path(self.folder.name)
        files = {
            "src/Kernel/Iron.Kernel.Probe/Data/ProbeDbContext.cs": CONTEXT,
            "src/Kernel/Iron.Kernel.Probe/Data/Migrations/20310101000000_Initial.cs": "// a migration",
            "src/Kernel/Iron.Kernel.Probe/Data/Migrations/20310101000000_Initial.Designer.cs": "// its designer",
            "src/Kernel/Iron.Kernel.Probe/Data/Migrations/20310202000000_Things.cs": "// another",
            "src/Kernel/Iron.Kernel.Probe/Data/Migrations/ProbeDbContextModelSnapshot.cs": "// the snapshot",
        }
        for name, text in files.items():
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding="utf-8")
        self.seeds = hashlib.sha256(installation_record.seeds_text(SEEDS).encode("utf-8")).hexdigest()

    def tearDown(self) -> None:
        self.folder.cleanup()

    def record(self, **changes: object) -> dict[str, object]:
        values: dict[str, object] = {
            "said_version": "2031.02.03.7",
            "configuration": {"catalogue_sha256": "c" * 64, "seeds_sha256": self.seeds, "permissions_sha256": "p" * 64, "seeds": ["ignored"]},
            "repository_catalogue": "c" * 64,
            "applied": {"kernel_probe": ["20310101000000_Initial", "20310202000000_Things"]},
            "database_seeds": self.seeds,
            "smoke": [{"name": "health", "status": "passed"}],
            "browser": {"passed": 32, "failed": 0, "skipped": 0},
        }
        values.update(changes)
        return installation_record.build(
            "acme", "2031.02.03.7", "abc123", [{"name": "iron-api", "reference": "ghcr.io/o/iron-api:abc", "digest": "sha256:" + "1" * 64}],
            values["said_version"], values["configuration"], values["repository_catalogue"], values["applied"],  # type: ignore[arg-type]
            installation_record.expected_migrations(self.root), values["database_seeds"], values["smoke"], values["browser"],  # type: ignore[arg-type]
        )

    def test_the_migrations_of_the_code_are_found_per_schema(self) -> None:
        """The repository's migrations, without designers and snapshots, by the schema the module's context declares. [ADR-0052]"""
        self.assertEqual(installation_record.expected_migrations(self.root), {"kernel_probe": ["20310101000000_Initial", "20310202000000_Things"]})

    def test_the_record_holds_the_images_migrations_hashes_and_results_and_passes_when_they_match_the_code(self) -> None:
        """The record names the images' digests, the migrations per module, the seeds' and catalogue's hashes and the smoke's and browser tests' results, and its checks pass when the installation matches the code. [ADR-0052]"""
        record = self.record()
        self.assertEqual(record["format"], "iron-installation-record-v1")
        self.assertEqual(record["images"][0]["digest"], "sha256:" + "1" * 64)  # type: ignore[index]
        self.assertEqual(record["migrations"], {"kernel_probe": ["20310101000000_Initial", "20310202000000_Things"]})
        self.assertEqual(record["configuration"], {"catalogue_sha256": "c" * 64, "seeds_sha256": self.seeds, "permissions_sha256": "p" * 64, "database_seeds_sha256": self.seeds})
        self.assertEqual(record["browser_tests"], {"passed": 32, "failed": 0, "skipped": 0})
        self.assertTrue(all(check["passed"] for check in record["checks"]), record["checks"])  # type: ignore[union-attr, index]

    def test_an_installation_that_differs_from_the_code_fails_its_checks(self) -> None:
        """A missing migration, other seeds, another catalogue, another version or a failed smoke step each fail a check of the record. [ADR-0052]"""
        record = self.record(
            said_version="development",
            repository_catalogue="e" * 64,
            applied={"kernel_probe": ["20310101000000_Initial"]},
            database_seeds="f" * 64,
            smoke=[{"name": "health", "status": "failed"}],
            browser={"passed": 0, "failed": 1, "skipped": 0},
        )
        failed = {check["name"]: check["detail"] for check in record["checks"] if not check["passed"]}  # type: ignore[union-attr, index]
        self.assertEqual(sorted(failed), ["browser tests", "catalogue", "migrations kernel_probe", "seeds", "smoke", "version"])
        self.assertIn("missing 20310202000000_Things", failed["migrations kernel_probe"])

    def test_the_seeds_text_is_the_images(self) -> None:
        """The seeds' canonical text: one line per system role by code, its permissions in order, as the API image writes it. [ADR-0052]"""
        self.assertEqual(installation_record.seeds_text(SEEDS), "read-only:b.thing.read\ntenant-administrator:a.thing.manage,b.thing.read\n")


if __name__ == "__main__":
    import unittest_junit

    sys.exit(unittest_junit.main(sys.modules[__name__], "installation_record_test"))
