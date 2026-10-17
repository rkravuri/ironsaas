# ADR-0003: Open source first, every dependency listed once, decided platform versions

**Status:** Accepted
**Date:** 2026-10-17
**Decided by:** Product owner and architect

## Context

PRD principle P3 and section 6: every library, tool and runtime is open source with a licence that allows commercial use at no cost, unless a commercial component is approved by a written decision. The product owner decided the platform versions for Foundation on 2026-10-17.

## Decision

- Every NuGet package used anywhere in the repository is declared once in `Directory.Packages.props`, with a comment naming its licence. Projects reference packages by name only.
- Frontend packages follow the same rule in the shell's `package.json`: exact versions, no ranges.
- A commercial component needs its own ADR. Approved exception so far: AG Grid Enterprise, later. AG Grid Community comes first, wrapped in one Foundation grid component so the switch does not touch products.
- Excluded on licence grounds: Duende IdentityServer, Hangfire Pro, Z.EntityFramework.Extensions, commercial APM agents.
- Platform versions decided: .NET 10 (LTS); PostgreSQL 19; Node.js v26.12.0; React 19.3; Mantine 9.7.3; AG Grid Community, current release at the time of T16.
- Exact versions are pinned where they are used and upgraded on purpose, never by range. A version at least two weeks old is preferred over a release of the last days.
- Lower-level library choices (OpenIddict, EF Core and Npgsql, OpenTelemetry, Serilog, Quartz.NET, MinIO, OpenBao, Traefik, Testcontainers, Playwright) get their own records when the task that introduces them lands.

## Options considered

- Version ranges for faster upgrades. Rejected: a build must be reproducible from a tag.
- Lock files only. Not enough on its own; `packages.lock.json` can be enabled in addition if restores drift.

## Consequences

- Easier: one place to review licences and versions; reproducible builds; a licence review is a file diff.
- Harder: upgrades are manual and deliberate. A monthly dependency review becomes an operations task (slice S6).
