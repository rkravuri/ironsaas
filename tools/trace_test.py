#!/usr/bin/env python3
"""Proves tools/trace.py (ADR-0044): a small repository written to a temporary folder, with one test of every kind and their
result files, builds its matrix; each other test breaks one rule and expects the trace to name it. Each test's docstring
ends with the decision it proves. Standard library only; `make trace` runs it first, with `--junit`."""

from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.dont_write_bytecode = True  # no __pycache__ beside the tools
sys.path.insert(0, str(Path(__file__).resolve().parent))
import trace  # noqa: E402


def requirement(identifier: str, title: str, criteria: int) -> str:
    lines = [f"#### {identifier} {title}", "*Priority:* **Must** · *Tag:* `[Core]` · *Area:* Probe", "", f"**Requirement:** {title}.", "", "**Why:** A reason.", "", "**Acceptance criteria:**", ""]
    lines += [f"- AC{n} Given a case, When it runs, Then it holds.{' (error case)' if n == criteria else ''}" for n in range(1, criteria + 1)]
    return "\n".join(lines) + "\n\n"


SLICE = """# Slice S1: probe

## 2. Requirement coverage

| ID | Requirement | What S1 builds | Continues in |
|---|---|---|---|
| TF-001 | Registry | all | done |
| TF-002 to 003 | Provisioning | all | done |

## 4. Task breakdown

| # | Task | We build | Done when | State |
|---|---|---|---|---|
| T1 | One | it | it holds | done |
| T2 | Two | it | it holds | done, to verify |
"""

CSHARP = """namespace Iron.Probe.Tests;

public class RegistryTests
{
    [Fact]
    [Proves("TF-001 AC1")]
    public void SavesATenant()
    {
        var fixture = \"\"\"
            [Fact]
            public void NotATest() { }
            \"\"\";
    }

    [Theory]
    [Proves("TF-001 AC2", "ADR-0001")]
    [InlineData(1)]
    [InlineData(2)]
    public void RefusesADuplicate(int value)
    {
    }

    [Fact]
    [Untraced("draws nothing a document states")]
    public void Draws()
    {
    }
}
"""

VITEST = """import { describe, it } from "vitest";

const fixture = 'it("is a string, not a test", () => {});';

describe("the registry", () => {
  it("lists the tenant [TF-001 AC1]", () => {});
  // Untraced: a probe.
  it("draws [untraced]", () => {});
});
"""

E2E = """import { test } from "@playwright/test";

test.describe("provisioning", () => {
  for (const scheme of ["light", "dark"]) {
    test(`shows the tenant in the ${scheme} scheme [TF-002 AC1]`, async () => {});
  }
});
"""

A11Y = """import { test } from "@playwright/test";

test("the gallery has no violation [ADR-0001]", async () => {});
"""

SMOKE = """await report.StepAsync("tenant", ["TF-002 AC2"], async () => "ok");
await report.StepAsync("health", ["TF-003 AC1", "ADR-0001"], async () => "ok");
"""

PYTHON = '''import unittest


class ProbeTest(unittest.TestCase):
    def test_a_probe(self) -> None:
        """A probe. [TF-003 AC2]"""
'''

TRX = """<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
<UnitTestResult testId="a" outcome="Passed" /><UnitTestResult testId="b" outcome="Passed" /><UnitTestResult testId="c" outcome="Failed" /><UnitTestResult testId="d" outcome="Passed" />
</Results><TestDefinitions>
<UnitTest id="a"><TestMethod className="Iron.Probe.Tests.RegistryTests" name="SavesATenant" /></UnitTest>
<UnitTest id="b"><TestMethod className="Iron.Probe.Tests.RegistryTests" name="RefusesADuplicate" /></UnitTest>
<UnitTest id="c"><TestMethod className="Iron.Probe.Tests.RegistryTests" name="RefusesADuplicate" /></UnitTest>
<UnitTest id="d"><TestMethod className="Iron.Probe.Tests.RegistryTests" name="Draws" /></UnitTest>
</TestDefinitions></TestRun>"""


def junit(classname: str, *cases: tuple[str, str]) -> str:
    body = "".join(f'<testcase classname="{classname}" name="{name}">{"<failure/>" if status == "failed" else "<skipped/>" if status == "skipped" else ""}</testcase>' for name, status in cases)
    return f"<testsuites><testsuite>{body}</testsuite></testsuites>"


class TraceTest(unittest.TestCase):
    def setUp(self) -> None:
        self.folder = tempfile.TemporaryDirectory()
        self.root = Path(self.folder.name)
        self.files = {
            "docs/prd/foundation/technical-foundation-PRD.md": "# Kernel\n\n" + requirement("TF-001", "Registry", 2) + requirement("TF-002", "Provisioning", 2) + requirement("TF-003", "Resolution", 2),
            "docs/adr/0001-probe.md": "# ADR-0001: A probe decision\n",
            "docs/slices/S1-probe.md": SLICE,
            "docs/trace/exceptions.md": "| Criterion | Reason | Approved |\n|---|---|---|\n",
            "tests/Iron.Probe.Tests/Iron.Probe.Tests.csproj": "<Project />",
            "tests/Iron.Probe.Tests/RegistryTests.cs": CSHARP,
            "shell/src/registry.test.ts": VITEST,
            "shell/e2e/provisioning.spec.ts": E2E,
            "shell/e2e/a11y/gallery.spec.ts": A11Y,
            "tools/Iron.Smoke/Program.cs": SMOKE,
            "tools/probe_test.py": PYTHON,
            "artifacts/test-results/dotnet/run.trx": TRX,
            "artifacts/test-results/shell/vitest.xml": junit("src/registry.test.ts", ("the registry > lists the tenant [TF-001 AC1]", "passed"), ("the registry > draws [untraced]", "passed")),
            "artifacts/test-results/e2e/junit.xml": junit("provisioning.spec.ts", ("provisioning › shows the tenant in the light scheme [TF-002 AC1]", "passed"), ("provisioning › shows the tenant in the dark scheme [TF-002 AC1]", "passed")),
            "artifacts/test-results/a11y/junit.xml": junit("gallery.spec.ts", ("the gallery has no violation [ADR-0001]", "passed")),
            "artifacts/test-results/smoke/smoke-report.json": json.dumps({"steps": [{"name": "tenant", "status": "passed"}, {"name": "health", "status": "failed"}]}),
            "artifacts/test-results/python/probe.xml": junit("probe_test.ProbeTest", ("test_a_probe", "passed")),
        }

    def tearDown(self) -> None:
        self.folder.cleanup()

    def build(self) -> trace.Trace:
        for name, text in self.files.items():
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding="utf-8")
        return trace.build(self.root)

    def test_every_kind_is_read_and_joined_to_its_result(self) -> None:
        """The tests of every kind are read from the sources, with what they prove, and joined to their results. [ADR-0044]"""
        result = self.build()
        self.assertEqual(result.problems, [])
        by_name = {(t.kind, t.name): t for t in result.tests}
        self.assertEqual(sorted(kind for kind, _ in by_name), ["a11y", "dotnet", "dotnet", "dotnet", "e2e", "python", "smoke", "smoke", "vitest", "vitest"])
        self.assertEqual(by_name[("dotnet", "RegistryTests.SavesATenant")].status, "passed")
        self.assertEqual(by_name[("dotnet", "RegistryTests.RefusesADuplicate")].status, "failed")
        self.assertEqual(by_name[("dotnet", "RegistryTests.RefusesADuplicate")].proves, ["TF-001 AC2", "ADR-0001"])
        self.assertEqual(by_name[("e2e", "shows the tenant in the ${scheme} scheme")].results, ["passed", "passed"])
        self.assertEqual(by_name[("smoke", "health")].status, "failed")
        self.assertEqual(by_name[("python", "ProbeTest.test_a_probe")].proves, ["TF-003 AC2"])
        self.assertEqual([t.label for t in result.proven["TF-001 AC1"]], ["Iron.Probe.Tests › RegistryTests.SavesATenant", "src/registry.test.ts › lists the tenant"])
        self.assertEqual(result.gaps, {"S1": []})

    def test_the_matrix_lists_each_criterion_with_its_tests_and_results(self) -> None:
        """The matrix shows every criterion of a done slice with the tests that prove it and their results. [ADR-0044]"""
        result = self.build()
        text = trace.markdown(result, self.root)
        self.assertIn("| TF-001 AC2 | .NET: Iron.Probe.Tests › RegistryTests.RefusesADuplicate (failed) |", text)
        self.assertIn("| TF-003 AC1 | Smoke: smoke › health (failed) |", text)
        self.assertIn("- src/registry.test.ts › draws (shell/src/registry.test.ts:8): a probe.", text)
        self.assertIn("- Iron.Probe.Tests › RegistryTests.Draws (tests/Iron.Probe.Tests/RegistryTests.cs:25): draws nothing a document states", text)
        document = trace.as_json(result, self.root)
        self.assertEqual(document["requirements"]["TF-002"]["criteria"]["TF-002 AC2"], ["smoke › tenant"])  # type: ignore[index]

    def test_an_identifier_that_does_not_exist_is_refused(self) -> None:
        """A test naming a requirement, a criterion or a decision no document holds fails the trace. [ADR-0044]"""
        self.files["tools/Iron.Smoke/Program.cs"] = SMOKE + 'await report.StepAsync("more", ["TF-009 AC1", "TF-001 AC3", "ADR-0999", "AC1"], async () => "ok");\n'
        problems = "\n".join(self.build().problems)
        self.assertIn("more: TF-009 is in no requirements document", problems)
        self.assertIn("more: TF-001 has 2 criteria, so it has no AC3", problems)
        self.assertIn("more: ADR-0999 is not an ADR in docs/adr", problems)
        self.assertIn("more: 'AC1' is not a criterion (TF-033 AC2), a requirement (TF-033) or a decision (ADR-0029)", problems)

    def test_a_test_that_names_nothing_is_refused(self) -> None:
        """A test whose title or docstring names nothing it proves fails the trace. [ADR-0044]"""
        self.files["shell/src/registry.test.ts"] = VITEST.replace(" [TF-001 AC1]", "")
        self.files["artifacts/test-results/shell/vitest.xml"] = junit("src/registry.test.ts", ("the registry > lists the tenant", "passed"))
        problems = "\n".join(self.build().problems)
        self.assertIn("shell/src/registry.test.ts:6: lists the tenant: names nothing it proves", problems)

    def test_a_done_slices_criterion_without_a_test_is_refused_unless_listed(self) -> None:
        """A done slice's criterion that no test proves fails the trace, unless the exceptions list it with a reason and a date. [ADR-0044]"""
        self.files["tools/probe_test.py"] = PYTHON.replace("[TF-003 AC2]", "[ADR-0001]")
        self.assertIn("docs/slices/S1-probe.md: TF-003 AC2 of done slice S1 has no test and no exception in docs/trace/exceptions.md", self.build().problems)
        self.files["docs/trace/exceptions.md"] += "| TF-003 AC2 | The probe resolves nothing yet. | 2026-10-21 |\n"
        result = self.build()
        self.assertEqual(result.problems, [])
        self.assertEqual(result.excepted["TF-003 AC2"].reason, "The probe resolves nothing yet.")

    def test_a_whole_requirement_proves_or_excepts_each_criterion(self) -> None:
        """A test or an exception that names a whole requirement covers each of its criteria. [ADR-0044]"""
        self.files["tools/probe_test.py"] = PYTHON.replace("[TF-003 AC2]", "[TF-003]")
        self.files["tools/Iron.Smoke/Program.cs"] = SMOKE.replace('"TF-003 AC1", ', "")
        result = self.build()
        self.assertEqual(result.problems, [])
        self.assertEqual([t.name for t in result.proven["TF-003 AC1"]], ["ProbeTest.test_a_probe"])

    def test_an_exception_a_test_now_proves_is_refused(self) -> None:
        """An exception for a criterion a test proves now fails the trace, so the list stays true. [ADR-0044]"""
        self.files["docs/trace/exceptions.md"] += "| TF-001 AC1 | Not built. | 2026-10-21 |\n"
        problems = "\n".join(self.build().problems)
        self.assertIn("docs/trace/exceptions.md:3: TF-001 AC1: proven by Iron.Probe.Tests › RegistryTests.SavesATenant now; remove the exception", problems)

    def test_an_exception_without_a_date_or_outside_a_done_slice_is_refused(self) -> None:
        """An exception needs a reason and the date it was approved, and is only for a done slice's gap. [ADR-0044]"""
        self.files["docs/prd/foundation/technical-foundation-PRD.md"] += requirement("TF-004", "Lifecycle", 2)
        self.files["tools/probe_test.py"] = PYTHON.replace("[TF-003 AC2]", "[ADR-0001]")
        self.files["docs/trace/exceptions.md"] += "| TF-003 AC2 | Not built. | soon |\n| TF-004 AC1 | Not in S1. | 2026-10-21 |\n"
        problems = "\n".join(self.build().problems)
        self.assertIn("TF-003 AC2: the date it was approved is not a date (yyyy-mm-dd): 'soon'", problems)
        self.assertIn("TF-004 AC1: TF-004 is in no done slice's coverage table", problems)

    def test_a_slice_with_a_task_not_done_is_not_held_to_its_criteria(self) -> None:
        """Only a slice whose every task is done is held to a test for each criterion. [ADR-0044]"""
        self.files["docs/slices/S1-probe.md"] = SLICE.replace("| T2 | Two | it | it holds | done, to verify |", "| T2 | Two | it | it holds | in progress |")
        self.files["tools/probe_test.py"] = PYTHON.replace("[TF-003 AC2]", "[ADR-0001]")
        result = self.build()
        self.assertEqual(result.problems, [])
        self.assertEqual(result.gaps, {})

    def test_a_result_no_test_declares_is_refused(self) -> None:
        """A result the trace cannot place on a test it read fails it, so a test this reader misses cannot hide. [ADR-0044]"""
        self.files["artifacts/test-results/shell/vitest.xml"] = junit("src/registry.test.ts", ("the registry > lists the tenant [TF-001 AC1]", "passed"), ("the registry > a test the reader missed [ADR-0001]", "passed"))
        problems = "\n".join(self.build().problems)
        self.assertIn("a result for 'the registry > a test the reader missed [ADR-0001]' in src/registry.test.ts, which no test in the shell declares", problems)

    def test_a_kind_without_results_shows_its_tests_as_not_run(self) -> None:
        """A kind whose result file is missing still shows its tests, as not run, and they still prove what they name. [ADR-0044]"""
        del self.files["artifacts/test-results/e2e/junit.xml"]
        result = self.build()
        self.assertEqual(result.problems, [])
        self.assertEqual([t.status for t in result.proven["TF-002 AC1"]], ["not run"])
        self.assertFalse(result.ran["e2e"])

    def test_requirement_cells_expand_ranges_and_lists(self) -> None:
        """A coverage table's cell names its requirements in the plans' short forms. [ADR-0044]"""
        self.assertEqual(trace.requirement_cell("TF-040 to 043, 045, 046"), ["TF-040", "TF-041", "TF-042", "TF-043", "TF-045", "TF-046"])
        self.assertEqual(trace.requirement_cell("TF-057 to 059, 221"), ["TF-057", "TF-058", "TF-059", "TF-221"])
        self.assertEqual(trace.requirement_cell("TF-021"), ["TF-021"])

    def test_code_in_strings_and_comments_is_not_read_as_a_test(self) -> None:
        """A fixture's source inside a string, a template or a comment is not a test of the file. [ADR-0044]"""
        source = 'const a = "it(\'no\', () => {})";\n// it("no [ADR-0001]")\nconst b = `test("no") ${x}`;\nconst r = /it\\("no"/;\nit("yes [ADR-0001]", () => {});\n'
        path = self.root / "shell" / "src" / "probe.test.ts"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(source, encoding="utf-8")
        found = trace.js_file_tests(path, "vitest", self.root)
        self.assertEqual([(t.name, t.line) for t in found], [("yes", 5)])


if __name__ == "__main__":
    import unittest_junit

    sys.exit(unittest_junit.main(sys.modules[__name__], "trace_test"))
