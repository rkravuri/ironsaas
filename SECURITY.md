# Security

IronSaaS Foundation and its suites hold the records of regulated businesses, so a weakness in this code is treated as an incident, not as a feature request.

## Reporting a vulnerability

Report it privately, by email to **REAL_ADDRESS_HERE**, never in a public issue, pull request or discussion.

Say what you found, where (file, endpoint or page), how to reproduce it, and what an attacker could do with it. Do not include real personal data or a tenant's records; describe them instead.

The maintainers aim to acknowledge a report within five working days, to agree with you what is affected and when a fix ships, and to credit you in the release notes of the fix unless you ask not to be named. Please give us the time to fix it before you tell anyone else.

## What is in scope

- The code in this repository: the kernel, the business layer, every suite, the hosts, the shell, the tools, and the laptop platform under `infra/`.
- The images the release pipeline builds from it.

Out of scope: weaknesses that need a laptop's development secrets (`infra/compose/.env`, the dev tenant's token), denial of service by volume against a laptop stack, and findings in third-party packages that are already public, which Dependabot and the dependency scan track; report those to the package's own maintainers.

## Supported versions

There is no release outside the development stack yet. Fixes go into `main`; the first deployment outside a laptop will state which versions receive security fixes.

## How the repository protects itself

- Every change runs a secret scan (gitleaks) and a dependency scan (Trivy) in CI; Dependabot proposes updates after a fourteen-day cooldown (ADR-0003).
- Secrets live in the store (OpenBao on the laptop), each tenant with its own path and token, never in the repository or in configuration files (ADR-0016).
- Every endpoint declares its permission and access is denied by default; every denial, sign-in and state change is in the append-only audit trail (ADR-0012, ADR-0013).
- The controls of the five compliance frameworks are platform requirements (the technical PRD's section 5a); S3 adds multi-factor authentication, single sign-on, the audit log's hash chain, security headers, rate limits and the vulnerability pipeline.
