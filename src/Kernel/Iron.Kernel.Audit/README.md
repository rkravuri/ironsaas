# Iron.Kernel.Audit

Kernel module (ADR-0029). Schema `kernel_audit`. The audit trail (TF-060; thin in S1, ADR-0013).

| Folder | Holds |
|---|---|
| `Contracts/` | `AuditEvent`, `AuditOutcome`, `AuditSource`, `AuditPermissions` (`audit.events.read`), `IAuditWriter` |
| `Data/` | `AuditDbContext`, `AuditEventRecord` (append-only: created once, never changed), the design-time factory, generated `Migrations/` |
| `Services/` | `AuditWriter`: one row per event on the scope's connection, inside the caller's unit of work when one is open |
| `Api/` | `GET /api/v1/audit-events` with the list grammar, permission `audit.events.read` |

Both people (TF-037, ADR-0037): while a person acts for a delegator, the writer fills `on_behalf_of_id` with the delegator from the request, so every event names both without the module saying so; `impersonator_id` stays for the operator's support access, a different act. The list filters by `on_behalf_of_id` and answers `actor_name` and `on_behalf_of_name` when Identity is in the host.

The app role has SELECT and INSERT on `kernel_audit.events` and nothing else (`migrate` grants it, TF-060 AC2). Event types are named by the module that raises them: `authz.permission_denied`, `identity.login_failed`.

Model change: edit the entity, then `make ef-add MODULE=Audit NAME=<WhatChanged>` and commit `Data/Migrations`.
