#!/usr/bin/env python3
"""Writes the installation record of a release's integration stage (TF-218, ADR-0052): what was installed on the stack
and its checks against the infrastructure code the release was built from.

- the five images, each with its digest (the registry's, or the local image id on a laptop);
- the version the API image says it is (its `version` command) and the controlled configuration it carries (its
  `configuration` command: the catalogue's, the seeds' and the permissions' hashes);
- the migrations applied in the tenant's database, per module's schema;
- the seeds as the tenant's database holds them (its system roles and their permissions), hashed the same way;
- the smoke's and the browser tests' results.

The checks: the API image's version is the release's; the catalogue in the image is the repository's; every schema of
the tenant's database holds exactly the migrations the repository has for it; the seeds in the database are the ones
the image's code makes; the smoke and the browser tests passed. tools/validation_pack.py puts the record into the pack
and refuses to publish a release whose checks did not pass.

Usage: python3 tools/installation_record.py --tenant acme --repository ghcr.io/owner --tag <sha> --version 2026.10.23.57 \
  --smoke-report smoke-report.json --e2e-junit artifacts/test-results/e2e/junit.xml --output artifacts/validation/installation-record.json
Needs docker and the platform's Compose project running. Standard library only.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import sys
import xml.etree.ElementTree as ElementTree
from datetime import datetime, timezone
from pathlib import Path

sys.dont_write_bytecode = True  # no __pycache__ beside the tools

ROOT = Path(__file__).resolve().parent.parent
FORMAT = "iron-installation-record-v1"
IMAGES = ["iron-api", "iron-worker", "iron-control-plane", "iron-fleet", "iron-shell"]
CATALOGUE = Path("src/Kernel/Iron.Kernel.Tenancy/Services/licensing-catalogue.json")
# An attribute at the start of its line: the attribute's own documentation shows an example in a comment.
SCHEMA = re.compile(r'^\s*\[Schema\((?:"([a-z_]+)"|([A-Za-z_][\w.]*))\)\]', re.MULTILINE)
MIGRATION = re.compile(r"^(\d{14}_\w+)\.cs$")


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def declared_schema(project: Path) -> str | None:
    """The schema a project's context declares: `[Schema("kernel_x")]`, or `[Schema(Type.Constant)]` with the constant
    declared in the project (the Data module names `kernel_data` through `RecordShare.Schema`, beside its record)."""
    sources = [path for path in sorted(project.rglob("*.cs")) if "Migrations" not in path.parts and "bin" not in path.parts and "obj" not in path.parts]
    texts = [path.read_text(encoding="utf-8") for path in sources]
    for text in texts:
        found = SCHEMA.search(text)
        if found is None:
            continue
        if found.group(1):
            return found.group(1)
        constant = re.compile(rf'const string {re.escape(found.group(2).split(".")[-1])} = "([a-z_]+)"')
        return next((m.group(1) for other in texts if (m := constant.search(other))), None)
    return None


def expected_migrations(root: Path) -> dict[str, list[str]]:
    """Every module's migrations in the repository, by the schema its context declares."""
    found: dict[str, list[str]] = {}
    for folder in sorted(root.glob("src/**/Data/Migrations")):
        schema = declared_schema(folder.parent.parent)
        if schema is None:
            continue
        found.setdefault(schema, []).extend(m.group(1) for path in sorted(folder.iterdir()) if (m := MIGRATION.match(path.name)))
    return {schema: sorted(ids) for schema, ids in found.items()}


def seeds_text(rows: list[tuple[str, list[str]]]) -> str:
    """The seeds' canonical text, as the API image's configuration command writes it: `code:permission,permission`, by code."""
    return "".join(f"{code}:{','.join(sorted(permissions))}\n" for code, permissions in sorted(rows))


def smoke_steps(path: Path | None) -> list[dict[str, str]]:
    if path is None or not path.exists():
        return []
    report = json.loads(path.read_text(encoding="utf-8"))
    return [{"name": step["name"], "status": step["status"]} for step in report.get("steps", [])]


def junit_counts(path: Path | None) -> dict[str, int]:
    counts = {"passed": 0, "failed": 0, "skipped": 0}
    if path is None or not path.exists():
        return counts
    for case in ElementTree.parse(path).getroot().iter("testcase"):
        if case.find("failure") is not None or case.find("error") is not None:
            counts["failed"] += 1
        elif case.find("skipped") is not None:
            counts["skipped"] += 1
        else:
            counts["passed"] += 1
    return counts


def checks(version: str, said_version: str | None, configuration: dict[str, object], repository_catalogue: str | None, applied: dict[str, list[str]], expected: dict[str, list[str]], database_seeds: str | None, smoke: list[dict[str, str]], browser: dict[str, int]) -> list[dict[str, object]]:
    """Each check of the installation against the infrastructure code, with what it found."""
    found = []
    found.append({"name": "version", "passed": said_version == version, "detail": f"the API image says {said_version}, the release is {version}"})
    image_catalogue = configuration.get("catalogue_sha256")
    found.append({"name": "catalogue", "passed": image_catalogue is not None and image_catalogue == repository_catalogue, "detail": f"image {image_catalogue}, repository {repository_catalogue}"})
    image_seeds = configuration.get("seeds_sha256")
    found.append({"name": "seeds", "passed": image_seeds is not None and image_seeds == database_seeds, "detail": f"the code seeds {image_seeds}, the tenant's database holds {database_seeds}"})
    for schema in sorted(applied):
        wanted = expected.get(schema)
        if wanted is None:
            found.append({"name": f"migrations {schema}", "passed": False, "detail": "the repository has no migrations for this schema"})
            continue
        missing = [m for m in wanted if m not in applied[schema]]
        extra = [m for m in applied[schema] if m not in wanted]
        detail = f"{len(applied[schema])} applied" + (f"; missing {', '.join(missing)}" if missing else "") + (f"; not in the repository {', '.join(extra)}" if extra else "")
        found.append({"name": f"migrations {schema}", "passed": not missing and not extra, "detail": detail})
    if not applied:
        found.append({"name": "migrations", "passed": False, "detail": "the tenant's database answered no migration history"})
    failed_steps = [s["name"] for s in smoke if s["status"] != "passed"]
    found.append({"name": "smoke", "passed": bool(smoke) and not failed_steps, "detail": f"{len(smoke)} steps" + (f"; did not pass: {', '.join(failed_steps)}" if failed_steps else "") if smoke else "no smoke report"})
    found.append({"name": "browser tests", "passed": browser["passed"] > 0 and browser["failed"] == 0, "detail": f"{browser['passed']} passed, {browser['failed']} failed, {browser['skipped']} skipped"})
    return found


def build(tenant: str, version: str, commit: str, images: list[dict[str, str]], said_version: str | None, configuration: dict[str, object], repository_catalogue: str | None, applied: dict[str, list[str]], expected: dict[str, list[str]], database_seeds: str | None, smoke: list[dict[str, str]], browser: dict[str, int]) -> dict[str, object]:
    return {
        "format": FORMAT,
        "tenant": tenant,
        "version": version,
        "commit": commit,
        "recorded_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "images": images,
        "migrations": applied,
        "configuration": {k: v for k, v in configuration.items() if k.endswith("_sha256")} | {"database_seeds_sha256": database_seeds},
        "smoke": {"steps": smoke},
        "browser_tests": browser,
        "checks": checks(version, said_version, configuration, repository_catalogue, applied, expected, database_seeds, smoke, browser),
    }


# ---------------------------------------------------------------- the stack


def run(*command: str) -> str:
    return subprocess.run(list(command), capture_output=True, text=True, check=True, encoding="utf-8").stdout


def compose(root: Path) -> list[str]:
    folder = root / "infra" / "compose"
    return ["docker", "compose", "--file", str(folder / "compose.yaml"), "--env-file", str(folder / ".env")]


def environment(root: Path) -> dict[str, str]:
    values = {}
    for line in (root / "infra" / "compose" / ".env").read_text(encoding="utf-8").splitlines():
        if "=" in line and not line.lstrip().startswith("#"):
            key, value = line.split("=", 1)
            values[key.strip()] = value.strip()
    return values


def psql(root: Path, database: str, sql: str) -> list[list[str]]:
    user = environment(root)["POSTGRES_SUPERUSER"]
    output = run(*compose(root), "exec", "-T", "postgres", "psql", "-U", user, "-d", database, "-At", "-F", "\t", "-c", sql)
    return [line.split("\t") for line in output.splitlines() if line.strip()]


def image_digest(reference: str) -> str:
    digests = json.loads(run("docker", "image", "inspect", reference, "--format", "{{json .RepoDigests}}") or "[]")
    return digests[0].split("@", 1)[1] if digests else run("docker", "image", "inspect", reference, "--format", "{{.Id}}").strip()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Write the installation record of a tenant installed from a release's images (TF-218, ADR-0052).")
    parser.add_argument("--tenant", required=True)
    parser.add_argument("--repository", default="foundation")
    parser.add_argument("--tag", default="dev")
    parser.add_argument("--version", default="development")
    parser.add_argument("--smoke-report", type=Path, default=None)
    parser.add_argument("--e2e-junit", type=Path, default=None)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts" / "validation" / "installation-record.json")
    arguments = parser.parse_args(argv)

    images = [{"name": name, "reference": f"{arguments.repository}/{name}:{arguments.tag}", "digest": image_digest(f"{arguments.repository}/{name}:{arguments.tag}")} for name in IMAGES]
    api = f"{arguments.repository}/iron-api:{arguments.tag}"
    said = run("docker", "run", "--rm", api, "version").strip().splitlines()[-1]
    configuration = json.loads(run("docker", "run", "--rm", api, "configuration"))
    catalogue = ROOT / CATALOGUE
    schemas = [row[0] for row in psql(ROOT, arguments.tenant, "select table_schema from information_schema.tables where table_name = '__ef_migrations_history' order by 1")]
    applied = {schema: [row[0] for row in psql(ROOT, arguments.tenant, f'select migration_id from "{schema}".__ef_migrations_history order by 1')] for schema in schemas}
    roles = psql(ROOT, arguments.tenant, "select code, array_to_string(permissions, ',') from kernel_authz.roles where is_system order by code")
    seeds = sha256(seeds_text([(row[0], [p for p in row[1].split(",") if p]) for row in roles]).encode("utf-8"))
    commit = run("git", "-C", str(ROOT), "rev-parse", "HEAD").strip()
    record = build(
        arguments.tenant, arguments.version, commit, images, said, configuration, sha256(catalogue.read_bytes()) if catalogue.exists() else None,
        applied, expected_migrations(ROOT), seeds, smoke_steps(arguments.smoke_report), junit_counts(arguments.e2e_junit),
    )
    arguments.output.parent.mkdir(parents=True, exist_ok=True)
    arguments.output.write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
    for check in record["checks"]:  # type: ignore[union-attr]
        print(f"{'passed' if check['passed'] else 'FAILED'}  {check['name']}: {check['detail']}")
    print(f"installation record: {arguments.output}")
    return 0 if all(check["passed"] for check in record["checks"]) else 1  # type: ignore[union-attr]


if __name__ == "__main__":
    sys.exit(main())
