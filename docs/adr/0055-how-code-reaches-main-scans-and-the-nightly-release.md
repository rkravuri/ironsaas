# ADR-0055: How code reaches main: pull requests with every check green, the review exception, one security gate with expiring exceptions, bills of materials, threat models, coverage floors and the nightly release

**Status:** Accepted at the design session's review (2026-10-28), with the five fleet-image exceptions and PE-1 approved
**Date:** 2026-10-28
**Decided by:** Product owner and architect, in the brief of S3 T10a; the owner chose, in the session, to make the repository public; the details here are the task's, accepted at the review

## Context

TF-076 asks every pipeline to scan dependencies, run static analysis and scan containers, to produce a bill of materials, to fail on critical or high findings without an approved exception that expires, and for a monthly report of open findings. TF-077 asks that every change pass a second developer's review, automated tests, security checks and a threat-model check for new modules and external interfaces, that security be part of the definition of done, and that a yearly independent penetration test's findings be tracked.

Before this task a task merged on the laptop and pushed main; CI ran gitleaks and Trivy over the lock files (critical and high, unfixed ignored, the exceptions in `.trivyignore.yaml` without expiry), and the release scanned each image for critical findings only. The release ran on every push to main, and the October cycle used about 90% of the 2,000 free Actions minutes.

The brief set two decisions: main changes only through pull requests with green checks, the missing second developer an approved exception; and open-source tools only, run the same way on the laptop and in CI.

## Decision

### 1. Main changes only through a pull request with every check green (TF-077 AC1)

- **The repository is public since 2026-10-28.** GitHub offers branch protection and rulesets on a private repository only with a paid plan (its API answered 403, "Upgrade to GitHub Pro or make this repository public"). Asked to choose, the owner chose a public repository over GitHub Pro and over a detection without refusal. Before the change, gitleaks over every commit and the tree found nothing beyond the four fingerprinted fixtures, no credential file was ever committed, and no Actions secret exists; the owner accepted that the commit email and the absence of a licence file become public. The registry's images keep their own, private, visibility.
- **The protection is code**: `.github/branch-protection.json`, applied by `make protect-main` (`tools/branch_protection.py apply`, through gh) and compared with GitHub's live settings by `make protection-check`. It requires a pull request; the branch up to date with main (`strict`); nine required checks, each a CI job's name reported by GitHub Actions (app 15368), so no other integration can satisfy one; resolved conversations; and refuses force pushes and deletion, administrators included (`enforce_admins`). The repository merges with merge commits only. A test proves every required check is a CI job and every job of a pull request's run is required.
- **A skipped job counts as green.** A documents-only change skips the code jobs by their condition, never by the workflow's path filters, which would leave a required check waiting for ever; "What changed" is itself required, so a failure there cannot skip the rest into a pass.
- **A task now ends with a pull request** (CLAUDE.md, steps 7 and 8): pushed, opened with `gh pr create` linking its prompt section and its risk, every check green, then stopped for the design session; on acceptance the acceptance message is pasted as a comment, STATUS's row is committed on the branch, and `gh pr merge --merge` merges it. STATUS's "done" commit, once pushed to main, now travels in the pull request.
- **Proved once against GitHub**: a push of a commit straight to main is refused by the protection (`docs/security/branch-protection.md`).

### 2. The second developer's review is an approved exception (PE-1)

The team has one developer, and GitHub refuses an author's approval of their own pull request, so one required approving review would block every merge. `docs/security/exceptions.md` records PE-1: approved in the brief on 2026-10-24, checked again on 2027-04-24, and ended the day a second developer joins, by setting `required_approving_review_count` to 1. Until then the design session's review, in each task's report and pasted into its pull request, is the review. The protection already requires a pull request, so the step from none to one approval is a setting.

### 3. The tools, chosen by measurement

| What | Measured | Chosen |
|---|---|---|
| Dependencies | Trivy 0.76.0 over the source read only the 30 packages `Directory.Packages.props` declares, none of the transitive ones and no NuGet licence; with `packages.lock.json` per project (61) it reads 161 NuGet packages and 324 npm packages (dev ones with `--include-dev-deps`) with their licences, in 16 seconds | Trivy over the lock files; NuGet lock files on, restored in locked mode in CI |
| Static analysis, C# | the .NET analyzers' security rules at full strength found 24 places, 15 of them CA2100 (SQL text), and cost nothing in build time (46 s against 48 s) | AnalysisModeSecurity All for the product's code, as errors; CA2100 off, IRON0010 being the same rule and stricter; tests and tools outside, as for IRON0010 |
| Static analysis, TypeScript | eslint-plugin-security 4.2.0's recommended rules found 65 places in 6.3 s: 52 of `detect-object-injection` (34 in the shell's code), 10 non-literal regular expressions (9 in tests), 2 unsafe regular expressions, 1 file name in a test | the recommended set as errors over the shell's code, `detect-object-injection` off: every `object[variable]` is reported, and the shell's objects come from Zod schemas or its own constants |
| Rule-based, both | Semgrep CE 1.180.0 with our 12 rules over 717 files: 6.3 s, its rules' test 12 of 12; 207 of 479 C# files parse in part, 1,354 of 58,002 lines (2.3%) skipped, mostly class headers with primary constructors; TypeScript fully | Semgrep CE (LGPL-2.1), from its image, with our own rules in `security/rules/`, each proved by its test file |
| Containers | Trivy at critical and high on the laptop's five images: 8 findings, all with a fix somewhere | Trivy, as before, at critical and high through the gate |
| Bills of materials | Trivy's `convert` of the same image report, CycloneDX 1.7 | Trivy, one tool for scan and bill |
| Coverage, .NET | coverlet.MTP 10.1.0 (MIT) on Microsoft Testing Platform; Microsoft's own extension carries a proprietary licence | coverlet.MTP |
| Coverage, shell | @vitest/coverage-v8 5.0.5 (MIT), the version Vitest 5.0.5 needs | V8 coverage; accepted at the review one day short of ADR-0003's two weeks (published 2026-10-15), as the only version Vitest 5.0.5 accepts |

- **Opengrep**, the fork of Semgrep's engine, was not measured: this session refused to download and run its binary. Its rules and ours are the same; the comparison stays open.
- **Registry rules are not vendored.** Semgrep's registry carries the Semgrep Rules License, and the archived Opengrep fork of it the Commons Clause; neither is open source. Our rules each cover a risk the other checks leave open: certificate validation turned off, a secret's value in a log, a process's arguments as one string, an exception's text in an answer, a cookie that loses its protection, the store reached outside the Security library; code run from text, navigation to an unchecked address, a message to any origin, a secret in browser storage, a cookie written by script, a call outside `request()`.
- **The fixes the measurement asked for**: the shell's colour parser matches a run and splits it, instead of a repetition inside an optional group; the shell image takes Alpine's fixes at build (curl, libcurl and brotli waited in the base); the two token validators and TOTP keep their deliberate settings with a pragma and the reason at the line.

### 4. One gate, and exceptions that expire (TF-076 AC1, AC2, AC5)

- **`tools/security_gate.py` decides once** over every scanner's JSON, on the laptop (`make security`, `make images-security`) and in CI: a critical or high vulnerability, misconfiguration or rule finding fails, naming the package, its version, the finding and its fix, or the file, the line and the rule; medium and low are kept for the monthly report. The same finding in many lock files is one finding with its places. A rule the scanner could not run fails; a file parsed in part does not.
- **`security/exceptions.yaml`**: each exception has its finding, package, reason, approver, approval date and expiry, at most six months later. From its expiry, the finding fails again and so does the expired entry, until it is renewed with a new approval or removed. Trivy's own `expired_at` is not used: one register and one reading of it serve every scanner, and one test proves the expiry for all. DS-0002 moved in from `.trivyignore.yaml`, which is gone.
- **A false positive in our own code is not an exception**: it is suppressed at its line with the reason beside it (a pragma for an analyzer, `nosemgrep` for our rules), and the review reads it like code. An exception is a true finding that may wait.
- **NuGet's own audit at restore stays**, as before: with warnings as errors it fails a restore on any known advisory, earlier and stricter than the gate, though it cannot read the register. A NuGet advisory that must wait is suppressed there too (`NuGetAuditSuppress`), with the same reason.
- **The first exceptions, approved at the review**: five Go findings in the fleet image's Docker CLI and Compose plugin (built with Go 1.26.8; no release fixes them yet, measured on 2026-10-28), which talk to the daemon over a Unix socket and run none of the server or chain-building code the findings are in; until 2026-11-28.

### 5. Licences

`security/licences.yaml` allows permissive licences for any package (MIT, MIT-0, Apache-2.0, BSD-2-Clause, BSD-3-Clause, ISC, 0BSD, BlueOak-1.0.0, PostgreSQL, Unlicense, Zlib, CC0-1.0) and names the packages allowed another: MPL-2.0 for axe-core, its Playwright adapter and lightningcss, all build-time and unchanged; OFL-1.1 for the two fonts; CC-BY-4.0 for caniuse-lite's data. Four NuGet packages carry no licence Trivy reads; each was read by hand for its version: Apache.Arrow (Apache-2.0, a licence file), NETStandard.Library and System.Security.Cryptography.ProtectedData (MIT), and Microsoft.NETCore.Platforms 1.1.0, under the free but not open "Microsoft .NET Library" terms, holding only `runtime.json` and reaching only the analyzers' build. A disallowed or unreadable licence fails, naming the package.

### 6. Images and their bills of materials (TF-076 AC3)

Each image the release pushes is pulled, scanned through the gate at critical and high, and its CycloneDX bill of materials written from the same report (`make image-scan`). The bills go with the run, into the validation pack under `sbom/` with their SHA-256 in its manifest (a release without one per image is refused), and beside the pack on the GitHub release.

### 7. Threat models (TF-077 AC2)

`docs/security/threat-models/` holds a one-page template (what it protects, who can reach it, a threat per category of STRIDE with its controls and requirements, what remains) and a record per area: sign-in and sessions, break-glass and impersonation, the audit chain, the fleet and the control plane, the secret store and keys, the shell's public pages, protection, access and authorization, signatures with evidence and held changes, configuration, and files. Each names the modules (by `IModule.Name`) and the anonymous endpoints (as the host describes them) it covers. The architecture tests fail on a module without a record, the host tests on an anonymous endpoint without one, and both on a record that names what does not exist or leaves a category out. The definition of done asks for the record before the module or interface is built.

### 8. The findings register and the monthly report (TF-077 AC3, TF-076 AC4)

`docs/security/findings.md` records the findings of penetration tests and reviews (source, severity, opened, owner, state). The yearly independent penetration test is the owner's to commission, the first before the first customer goes live. The scheduled workflow `security-report.yml`, on the first of each month and on demand, scans the repository and the images of the last passing release, and `tools/security_report.py` writes the report: open findings per severity and source, what fails now, the critical and high ones with their exceptions, the open exceptions with their expiry, the register's open findings with their age, and the location database's pinned month, asked of DB-IP (T5's later item). The report is kept 400 days and opened as an issue; a critical or high finding open more than 30 days, and a pinned month that is gone, each become an issue, opened once while it stays open.

### 9. Coverage floors (TF-077 AC4)

CI measures line coverage of the product's .NET code (`make test COVERAGE=1`, the generated migrations left out) and of the shell's (`npm run check`), merged line by line across the test projects. `security/coverage.yaml` holds each floor, set at today's value rounded down: 90 for .NET (90.35% of 23,777 lines on 2026-10-28) and 85 for the shell (85.80% of 5,310 lines). Each report names its files from its own source roots, which differ between test projects (441 files were named two ways, and a merge by name first read 56.0%), so the merge resolves each name against its report's roots. `make coverage` fails below a floor, naming the area, the value and the floor; with the base branch's floors, a pull request that lowers a floor fails too, unless an exception names `coverage-floor` and the area. A floor may rise, never fall. Coverage cost the .NET suite 78 seconds on the laptop (525 against 447, 17%).

- **What coverage instruments, a test that reads assemblies must not read.** Coverlet instruments the copies of the product's assemblies beside each test project, and its tracker reads the system clock, which failed the clock rule (ADR-0007). That rule now reads each project's own build output, which is what ships.

### 10. The nightly release and the minutes

- **The release runs at 02:23 UTC when main has changed since the last release tag, and on demand** (from main only). It no longer runs on every push. Every release still gets its version and its validation pack.
- **CI runs in full on each pull request; a push to main runs only "Merge commit builds".** Main's protection makes a branch up to date before it merges, so the pull request's run tested the merge's tree. The release takes CI's results from the newest full run whose tree differs from main's by documents only, walking back along main's merges; when none does, it asks CI to run in full on main and waits.
- **A change of documents only** (`docs/` and markdown files, the threat models excepted, which a test reads) skips the build, test and scan jobs; the requirement documents, the secret scan and the trace still run. The secret scan stays on every change, against the brief's list, because a document holds a secret as easily as code.
- **A newer push cancels the older run** of the same workflow and pull request or branch; a release is never cancelled.
- **Caches**: NuGet packages (by the lock files), npm (by its lock), the Playwright browsers (by the lock, the system libraries installed each time), Trivy's database (daily), and the images' layers (Buildx in Actions' cache).
- **The minutes**, measured with gh from each job's start and end, each job rounded up to the minute as GitHub bills.

  Before, every run from the repository's first (2026-10-17) to 2026-10-28: 137 runs, 2,155 minutes in twelve days.

  | Workflow and trigger | Runs | Minutes | Per run |
  |---|---|---|---|
  | Release, on every push to main | 43 | 856 | 19.9 |
  | CI, on every push to main | 47 | 835 | 17.8 |
  | CI, on Dependabot's pull requests | 19 | 235 | 12.4 |
  | Dependabot's own update jobs | 27 | 226 | 8.4 |

  After, on this task's pull request (run 40717829454, the caches cold): CI 30 minutes, ten of wall time: the .NET build and tests 10 (with coverage), the accessibility scan 9, the shell 4, the security scans 2, five jobs of one minute each; the merge build on main about 5; the release about 20, at most once a night.

  For a month of 20 tasks at October's pace (about 3.6 pushes to main and 3.3 releases a task before): before, about 130 minutes a task, 2,600 for the tasks; after, about 3.5 full pull request runs a task (the first push, a round of fixes, the accepted status row) at 25 to 30 minutes and a merge build, about 100 a task, 2,000 for the tasks, plus at most 20 releases, 400. The release's share falls from about 66 minutes a task to about 20; CI's rises, since every push of a pull request runs in full and the full run now scans and measures coverage. Dependabot's own update jobs are unchanged. The saving is modest, and the free minutes no longer bind: a public repository's standard runners are not counted. Running the shell's jobs only when `shell/` changed, and the .NET ones only when .NET code did, would take another 10 to 15 minutes off a run; it is left for when minutes matter again.

### 11. The definition of done

Three lines join it: the security checks pass; a new module or external interface has its threat model before it is built; and a new exception has its approver and its expiry, at most six months on.

## Options considered

- **GitHub Pro instead of a public repository**: keeps the code private for about four dollars a month; the owner chose public. **A workflow that detects a direct push** without refusing it: rejected, TF-077 AC1 asks for the merge to be blocked.
- **One required approval with an administrator's bypass**: rejected, a bypass every merge uses is no control; PE-1 names the gap instead.
- **Trivy's `expired_at` for its findings and another check for the rest**: two forms of one rule; one register and one gate read by every scanner.
- **A licence checker of our own over `project.assets.json` and the nuspecs**: Trivy reads the same with the lock files, which also make restores repeatable.
- **Semgrep's registry rules**, **Opengrep's archived fork of them**: not open source (above). **CodeQL**: free for public repositories, but its licence is not open source, against the brief.
- **Microsoft's coverage extension**: proprietary licence. **Coverage per test project with a floor each**: one merged figure per area says what the product is covered by.
- **The release running CI's tests itself**: doubles the minutes; the pull request's run already tested the same tree.
- **Path filters on the workflow for documents**: a required check would wait for ever on a skipped workflow; job conditions report a skip, which counts as green.

## Consequences

- Main cannot change without a pull request whose every check passed, nor be forced or deleted, by anyone.
- A critical or high finding stops the build where it is found, named; an exception is visible, owned and runs out.
- Every release carries a bill of materials per image, in its pack and on its GitHub release.
- A new module or anonymous endpoint fails the tests until its threat model exists.
- Harder: every task ends in a pull request and waits for its checks; a package change commits its lock files; a document-only change still waits for the trace; the coverage floors make a change without tests visible.
- Revisit: PE-1 on 2027-04-24 or when a second developer joins; the fleet's Go exceptions by 2026-11-28; Opengrep when it can be measured; the floors as they rise; the release's walk-back once main's history is all pull requests.
