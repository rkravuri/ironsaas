#!/usr/bin/env python3
"""Proves tools/branch_protection.py (TF-077 AC1, ADR-0055) on GitHub's shape of a branch's protection: what lets a
change reach main without a pull request with every check green is named, and every required check is a CI job. The
refusal of a real push to main is proved once against GitHub and recorded in docs/security/branch-protection.md. Each
test's docstring ends with what it proves. Standard library only; `make trace` runs it first, with `--junit`."""

from __future__ import annotations

import copy
import sys
import unittest
from pathlib import Path

sys.dont_write_bytecode = True  # no __pycache__ beside the tools
sys.path.insert(0, str(Path(__file__).resolve().parent))
import branch_protection  # noqa: E402

EXPECTED = branch_protection.settings()


def live() -> dict:
    """GitHub's answer for a branch protected as the settings say."""
    checks = EXPECTED["required_status_checks"]["checks"]
    return {
        "required_status_checks": {"strict": True, "contexts": [c["context"] for c in checks], "checks": [dict(c) for c in checks]},
        "required_pull_request_reviews": {"dismiss_stale_reviews": True, "require_code_owner_reviews": False, "required_approving_review_count": 0},
        "enforce_admins": {"enabled": True},
        "required_linear_history": {"enabled": False},
        "allow_force_pushes": {"enabled": False},
        "allow_deletions": {"enabled": False},
        "required_conversation_resolution": {"enabled": True},
    }


class BranchProtectionTest(unittest.TestCase):
    def test_protection_as_declared_passes(self) -> None:
        """Main protected as .github/branch-protection.json says has nothing to report. [TF-077 AC1, ADR-0055]"""
        self.assertEqual(branch_protection.problems(live(), EXPECTED), [])

    def test_whatever_lets_a_change_reach_main_without_a_green_pull_request_is_named(self) -> None:
        """No pull request required, a check not required, a branch that need not be up to date, administrators exempt, force pushes or deletion allowed: each is named. [TF-077 AC1]"""
        changed = live()
        del changed["required_pull_request_reviews"]
        changed["required_status_checks"]["strict"] = False
        changed["required_status_checks"]["checks"] = [c for c in changed["required_status_checks"]["checks"] if c["context"] != "Security scans"]
        changed["required_status_checks"]["contexts"].remove("Security scans")
        changed["enforce_admins"] = {"enabled": False}
        changed["allow_force_pushes"] = {"enabled": True}
        changed["allow_deletions"] = {"enabled": True}
        self.assertEqual(branch_protection.problems(changed, EXPECTED), [
            "main takes a push without a pull request: required_pull_request_reviews is not set",
            "a pull request merges without being up to date with main: strict is off",
            "the check 'Security scans' from GitHub Actions is not required",
            "an administrator may push to main around the rules: enforce_admins is off",
            "main may be force-pushed",
            "main may be deleted",
        ])

    def test_fewer_approvals_than_the_settings_ask_are_named(self) -> None:
        """Once PE-1 ends and one approving review is required, protection asking none is a difference. [TF-077 AC1, ADR-0055]"""
        expected = copy.deepcopy(EXPECTED)
        expected["required_pull_request_reviews"]["required_approving_review_count"] = 1
        self.assertEqual(branch_protection.problems(live(), expected), ["a pull request merges with 0 approving reviews, fewer than the 1 the settings ask"])

    def test_every_required_check_is_a_job_of_the_ci_workflow(self) -> None:
        """Each required check names a job of ci.yml, so none waits for a check that never reports, and every job of a pull request's run is required. [TF-077 AC1, ADR-0055]"""
        jobs = branch_protection.ci_jobs(branch_protection.CI.read_text(encoding="utf-8"))
        required = [c["context"] for c in EXPECTED["required_status_checks"]["checks"]]
        self.assertEqual(sorted(set(required) - set(jobs)), [])
        # Every job but the merge build, which runs on main after the merge, never on a pull request.
        self.assertEqual(sorted(set(jobs) - set(required)), ["Merge commit builds"])
        self.assertTrue(all(c["app_id"] == 15368 for c in EXPECTED["required_status_checks"]["checks"]))
        self.assertTrue(EXPECTED["enforce_admins"])


if __name__ == "__main__":
    import unittest_junit

    sys.exit(unittest_junit.main(sys.modules[__name__], "branch_protection_test"))
