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
| [0006](0006-object-storage-stand-in.md) | Azurite replaces MinIO as the laptop object storage | Proposed |
| [0007](0007-core-primitives.md) | Core primitives: results and errors, tenant identifier, ids, time, correlation | Accepted |
| [0008](0008-data-conventions.md) | Data conventions | Accepted |

Coming with their tasks in S1: API conventions (T5), telemetry (T6), permission catalogue and audit protection (T8, T9), identity and sessions (T10), secrets and storage interfaces (T12), the unit runtime (T14), the shell and the grid wrapper (T15, T16), the pipeline (T18).
