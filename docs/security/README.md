# Security of how the code is made

How a change reaches main, what the pipeline scans and refuses, and where the security records live (TF-076, TF-077,
ADR-0055). SECURITY.md at the repository's root says how to report a vulnerability.

## How a change reaches main

Only through a pull request that is up to date with main and whose every check is green: main's branch protection
refuses a push, a force push, a deletion and a merge with a check that is not green, administrators included. The
settings are code, `.github/branch-protection.json`; `make protect-main` applies them, `make protection-check` compares
GitHub's with them. A pull request merges with a merge commit, after the design session's acceptance is pasted into it
as a comment. `branch-protection.md` records the proof that a direct push is refused.

The review by a second developer is a process exception while the team has one developer: PE-1 in `exceptions.md`.

## What the pipeline checks

| Check | Tool | Fails on | Where |
|---|---|---|---|
| Dependencies, with the transitive ones, dev packages included | Trivy over every `packages.lock.json` and `shell/package-lock.json` | a critical or high vulnerability no unexpired exception covers | `make security`, CI's Security scans |
| Licences of every dependency | Trivy, against `security/licences.yaml` | a licence the policy does not allow, or none readable | `make security` |
| Misconfigurations (Dockerfiles) | Trivy | a critical or high finding without an exception | `make security` |
| The repository's own rules for C# and TypeScript | Semgrep CE with `security/rules/` | any finding; the rules' own test file failing | `make security` |
| .NET security rules (CA5350 to CA5404 and the rest) | the .NET analyzers, AnalysisModeSecurity All | any finding in the product's code | every build |
| TypeScript security rules | eslint-plugin-security | any finding in the shell's code | `make shell-check` |
| Images | Trivy over each pushed image | a critical or high finding without an exception | the release's image jobs, `make images-security` |
| Bills of materials | Trivy, CycloneDX per image | a release without one per image | the release, its validation pack and its GitHub release |
| Secrets in the history | gitleaks, pinned | any finding | CI's Secret scan |
| Coverage | coverlet and Vitest's V8 coverage, against `security/coverage.yaml` | line coverage below its floor; a floor that falls | `make coverage`, CI |
| Threat models | the architecture and host tests | a module or an anonymous endpoint without its record | `make test` |

`tools/security_gate.py` makes the one decision over every scanner's report, so the laptop and CI refuse the same
thing for the same reason, naming the package and the finding.

## The records

- `security/exceptions.yaml`: the scanners' findings that may wait, each with its finding, package, reason, approver,
  approval date and expiry, at most six months. An expired exception fails the build again until it is renewed.
- `security/licences.yaml`: the licences allowed for any package, and the packages allowed another (weak copyleft, a
  font's or data's licence) or whose licence was read by hand.
- `security/coverage.yaml`: the coverage floors, which may rise and never fall without an exception.
- `security/rules/`: the repository's rules for the rule-based scanner, each proved by the test file beside it.
- `exceptions.md`: the process exceptions, which no scanner reports, with their re-check dates.
- `findings.md`: the findings of penetration tests and reviews, with the yearly independent penetration test.
- `threat-models/`: a record per module and external interface, from `0000-template.md`.

## The dependency policy and its fix times

- **Versions** are exact, declared once (`Directory.Packages.props`, `shell/package.json`), at least two weeks old
  (ADR-0003); NuGet's lock files fix the transitive ones.
- **Dependabot** proposes the updates of the actions, the .NET packages, the shell's packages and the base images once a
  week, after its 14-day cooldown, minor and patch versions grouped per ecosystem, a major on its own. It is told to
  leave two, each with its reason in `.github/dependabot.yml`: `Microsoft.CodeAnalysis.CSharp`, raised by hand with the
  EF Core that asks for it, and TypeScript majors, until typescript-eslint accepts them. Its pull requests pass the
  same checks and merge the same way as a task's.
- **Fix times**, from the day a scan first reports the finding: critical within 7 days, high within 30, medium within
  90, low at the package's next update. A critical or high finding blocks every pull request from that day, so it is
  fixed at once or excepted for the time its fix needs, within those days where a fix exists; one without a fix is
  excepted until one is published and two weeks old, at most six months. Medium and low findings appear in the monthly
  report, which is where their time is watched.
- **Base images** move with Dependabot too; the shell's takes Alpine's own fixes at each build.

## Each month

The scheduled workflow `security-report.yml` (the first of each month, and on demand) scans the repository and the
latest release's images, and writes the report: the open findings per severity, the open exceptions with their expiry,
the findings register, and the location database's pin. The report is an artifact and an issue; a critical or high
finding of the register open for more than 30 days, and a pinned location month that is gone, become issues of their
own.

## When a scan fails

1. Read the failing line: the package, its version, the finding and the fix, or the file and the rule.
2. Fix it: raise the package to the fixed version (at least two weeks old, ADR-0003), change the code, or fix the
   Dockerfile.
3. When it cannot be fixed now, write an exception in `security/exceptions.yaml` with its reason and an expiry of at
   most six months, and have the design session approve it in the task's review.
4. A false positive in our code is suppressed at its line with the reason beside it: `#pragma warning disable` for an
   analyzer, `// nosemgrep: <rule>` for our rules.
