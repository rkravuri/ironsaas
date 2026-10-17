# Architecture decision records

Every `[Decision]` in the PRD, and every decision made while building, gets one record here (TF-209). A pull request that introduces a decision without its ADR is not merged.

Copy `0000-template.md` to the next free number. Keep the record short. Never edit an accepted decision; write a new one that supersedes it.

| ADR | Title | Status |
|---|---|---|
| [0001](0001-modular-monolith-with-contracts-projects.md) | Modular monolith with one Contracts project per module | Accepted |
| [0002](0002-isolated-deployment-unit-per-tenant.md) | One isolated deployment unit per tenant on shared infrastructure | Accepted |
| [0003](0003-open-source-first-and-decided-versions.md) | Open source first, every dependency listed once, decided platform versions | Accepted |
| [0004](0004-build-and-test-conventions.md) | Build and test conventions | Accepted |

Coming with their tasks in S1: identity and sessions (T10), permission catalogue and audit protection (T8, T9), data conventions (T4), API conventions (T5), telemetry and correlation (T6), secrets and storage interfaces (T12), the laptop stack and the unit runtime (T2, T14), the shell and the grid wrapper (T15, T16), the pipeline (T18).
