# Architecture decision records

Every `[Decision]` in the PRD, and every decision made while building, gets one record here (TF-209). A pull request that introduces a decision without its ADR is not merged.

Copy `0000-template.md` to the next free number. Keep the record short. Never edit an accepted decision; write a new one that supersedes it.

| ADR | Title | Status |
|---|---|---|
| [0001](0001-modular-monolith-with-contracts-projects.md) | Modular monolith with one Contracts project per module | Accepted |
| [0002](0002-isolated-deployment-unit-per-tenant.md) | One isolated deployment unit per tenant on shared infrastructure | Accepted |
| [0003](0003-open-source-first-and-decided-versions.md) | Open source first, every dependency listed once, decided platform versions | Accepted |
| [0004](0004-build-and-test-conventions.md) | Build and test conventions | Accepted |
| [0005](0005-laptop-platform-stack.md) | The laptop platform stack on Compose | Accepted |
| [0006](0006-object-storage-stand-in.md) | Azurite replaces MinIO as the laptop object storage | Accepted |
| [0007](0007-core-primitives.md) | Core primitives: results and errors, tenant identifier, ids, time, correlation | Accepted |
| [0008](0008-data-conventions.md) | Data conventions | Accepted |
| [0009](0009-api-conventions.md) | API conventions | Accepted |
| [0010](0010-telemetry-and-security-baseline.md) | Telemetry and security baseline | Accepted |

Coming with their tasks in S1: permission catalogue and audit protection (T8, T9), identity and sessions (T10), secrets and storage interfaces (T12), the unit runtime (T14), the shell and the grid wrapper (T15, T16), the pipeline (T18).
| [0011](0011-hosts-and-unit-commands.md) | Hosts, tenant binding and unit commands | Accepted |
| [0012](0012-authorization-thin.md) | Permissions, roles and the migration workflow | Accepted |
| [0013](0013-audit-trail-and-unit-of-work.md) | Audit trail, unit of work and the app role | Accepted |
| [0014](0014-identity-sessions-and-passwords.md) | Principals, passwords and sessions | Accepted |
| [0015](0015-access-tokens.md) | Access tokens for API clients | Accepted |
| [0016](0016-secrets-and-object-storage.md) | Secrets at start and object storage | Accepted |
| [0017](0017-control-plane-tenant-registry.md) | The tenant registry and the control plane API | Accepted |
| [0018](0018-provisioning-steps.md) | Provisioning as resumable steps | Accepted |
| [0019](0019-compose-unit-runtime.md) | Compose as the laptop unit runtime | Accepted |
| [0020](0020-shell.md) | The shell | Accepted |
| [0021](0021-data-grid.md) | DataGrid over AG Grid Community | Accepted |
