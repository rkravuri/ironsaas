# Iron.Audit

Schema `audit`. The audit trail (TF-060; thin in S1, ADR-0013).

| Folder | Holds |
|---|---|
| `Contracts/` | `AuditEvent`, `AuditOutcome`, `AuditSource`, `AuditPermissions` (`audit.events.read`), `IAuditWriter` |
| `Data/` | `AuditDbContext`, `AuditEventRecord` (append-only: created once, never changed), the design-time factory, generated `Migrations/` |
| `Services/` | `AuditWriter`: one row per event on the scope's connection, inside the caller's unit of work when one is open |
| `Api/` | `GET /api/v1/audit-events` with the list grammar, permission `audit.events.read` |

The app role has SELECT and INSERT on `audit.events` and nothing else (`migrate` grants it, TF-060 AC2). Event types are named by the module that raises them: `authz.permission_denied`, `identity.login_failed`.

Model change: edit the entity, then `make ef-add MODULE=Audit NAME=<WhatChanged>` and commit `Data/Migrations`.
