# Iron.Kernel.Audit

Kernel module (ADR-0029). Schema `kernel_audit`. The audit trail (TF-060; thin in S1, ADR-0013).

| Folder | Holds |
|---|---|
| `Contracts/` | `AuditEvent`, `AuditOutcome`, `AuditSource`, `AuditSeverity`, `AuditPermissions` (`audit.events.read`), `IAuditWriter`, `IAuditTrail` and `AuditTrailEntry` |
| `Data/` | `AuditDbContext`, `AuditEventRecord` (append-only: created once, never changed), the design-time factory, generated `Migrations/` |
| `Services/` | `AuditWriter`: one row per event on the scope's connection, inside the caller's unit of work when one is open; `AuditTrail`: a principal's actions in order |
| `Api/` | `GET /api/v1/audit-events` with the list grammar, permission `audit.events.read` |

Both people (TF-037, ADR-0037): while a person acts for a delegator, the writer fills `on_behalf_of_id` with the delegator from the request, so every event names both without the module saying so; The list filters by `on_behalf_of_id` and answers `actor_name` and `on_behalf_of_name` when Identity is in the host.

Impersonation (TF-025, ADR-0049): while someone is signed in as another person, the writer fills `impersonator_id` from the request and adds `details.impersonator` with their name, so every event names both. An operator is nobody in the unit: their id is the control plane's, and the name in the details stands for them. The list filters by `impersonator_id` and answers `impersonator_name`.

Severity (ADR-0049): each event is `normal` or `high`; `high` marks what an administrator must look at (an emergency account's sign-in, a tenant-wide revocation, a break-glass access). The list filters by `severity`. `IAuditTrail.ActionsOfAsync` answers a principal's events in order, for break-glass's review.

The app role has SELECT and INSERT on `kernel_audit.events` and nothing else (`migrate` grants it, TF-060 AC2). Event types are named by the module that raises them: `authz.permission_denied`, `identity.login_failed`.

Model change: edit the entity, then `make ef-add MODULE=Audit NAME=<WhatChanged>` and commit `Data/Migrations`.
