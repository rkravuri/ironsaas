#!/usr/bin/env python3
"""Main's branch protection as code (TF-077 AC1, ADR-0055): .github/branch-protection.json holds the settings, this tool
applies them through gh and checks GitHub's live settings against them.

- `apply` sends the settings (`PUT /repos/{repo}/branches/main/protection`) and sets the repository to merge commits only.
- `check` reads the live settings and fails, naming each difference, when main could change without a pull request,
  with a check that is not green, by an administrator's push, by a force push or a deletion, or with fewer approvals than
  the settings ask.

Every required check must be the name of a CI job, which the tests prove, so a renamed job cannot leave a check that
never reports. Usage: python3 tools/branch_protection.py check|apply [--repository owner/name]. Needs gh signed in as an
administrator of the repository. Standard library only.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SETTINGS = ROOT / ".github" / "branch-protection.json"
CI = ROOT / ".github" / "workflows" / "ci.yml"
BRANCH = "main"


def settings(path: Path = SETTINGS) -> dict:
    """The settings to send: the file without its notes (keys that start with an underscore)."""
    return {key: value for key, value in json.loads(path.read_text(encoding="utf-8")).items() if not key.startswith("_")}


def ci_jobs(text: str) -> list[str]:
    """The names of the jobs of the CI workflow, as GitHub reports their checks."""
    names = []
    inside = False
    for line in text.splitlines():
        if re.match(r"^jobs:\s*$", line):
            inside = True
            continue
        if inside and re.match(r"^\S", line):
            break
        match = re.match(r"^    name: (.+)$", line) if inside else None
        if match:
            names.append(match.group(1).strip().strip('"'))
    return names


def enabled(live: dict, key: str) -> bool:
    value = live.get(key)
    return bool(value.get("enabled")) if isinstance(value, dict) else bool(value)


def problems(live: dict, expected: dict) -> list[str]:
    """Each way the live protection falls short of the settings."""
    found = []
    reviews = live.get("required_pull_request_reviews")
    if not isinstance(reviews, dict):
        found.append("main takes a push without a pull request: required_pull_request_reviews is not set")
    else:
        want = expected["required_pull_request_reviews"]["required_approving_review_count"]
        if reviews.get("required_approving_review_count", 0) < want:
            found.append(f"a pull request merges with {reviews.get('required_approving_review_count', 0)} approving reviews, fewer than the {want} the settings ask")
    checks = live.get("required_status_checks")
    if not isinstance(checks, dict):
        found.append("a pull request merges without its checks: required_status_checks is not set")
    else:
        if expected["required_status_checks"]["strict"] and not checks.get("strict"):
            found.append("a pull request merges without being up to date with main: strict is off")
        live_checks = {(c.get("context"), c.get("app_id")) for c in checks.get("checks") or []} | {(c, None) for c in checks.get("contexts") or []}
        for wanted in expected["required_status_checks"]["checks"]:
            if (wanted["context"], wanted["app_id"]) not in live_checks:
                found.append(f"the check '{wanted['context']}' from GitHub Actions is not required")
    if expected["enforce_admins"] and not enabled(live, "enforce_admins"):
        found.append("an administrator may push to main around the rules: enforce_admins is off")
    if not expected["allow_force_pushes"] and enabled(live, "allow_force_pushes"):
        found.append("main may be force-pushed")
    if not expected["allow_deletions"] and enabled(live, "allow_deletions"):
        found.append("main may be deleted")
    if expected["required_conversation_resolution"] and not enabled(live, "required_conversation_resolution"):
        found.append("a pull request merges with unresolved conversations")
    return found


def gh(*args: str, data: str | None = None) -> str:
    return subprocess.run(["gh", *args], input=data, capture_output=True, text=True, check=True).stdout


def repository() -> str:
    return gh("repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner").strip()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Apply or check main's branch protection.")
    parser.add_argument("action", choices=["apply", "check"])
    parser.add_argument("--repository", default=None, help="owner/name; the current repository by default")
    arguments = parser.parse_args(argv)
    repo = arguments.repository or repository()
    expected = settings()
    if arguments.action == "apply":
        gh("api", "--method", "PUT", f"repos/{repo}/branches/{BRANCH}/protection", "--input", "-", data=json.dumps(expected))
        gh("api", "--method", "PATCH", f"repos/{repo}", "-F", "allow_merge_commit=true", "-F", "allow_squash_merge=false", "-F", "allow_rebase_merge=false")
        print(f"applied main's protection to {repo}: a pull request with every check green; merge commits only")
    try:
        live = json.loads(gh("api", f"repos/{repo}/branches/{BRANCH}/protection"))
    except subprocess.CalledProcessError as error:
        print(f"main of {repo} is not protected: {error.stderr.strip()}")
        return 1
    found = problems(live, expected)
    for problem in found:
        print(f"FAIL {problem}")
    print(f"main's protection on {repo}: {'as the settings say' if not found else f'{len(found)} differences'}")
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main())
