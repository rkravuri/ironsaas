# ADR-0013: Audit trail, unit of work and the app role

Date: 2026-10-18
Status: Accepted

## Context

TF-060: every security-relevant event becomes an audit row that is never updated or deleted, even by the role the application uses (AC2), and a status change and its audit event are one transaction (AC1). Modules keep their own contexts and schemas (ADR-0008), so "one transaction" has to work across contexts.

## Decision

1. **One connection per scope.** `SharedConnection` (Iron.Data, scoped) holds the scope's `NpgsqlConnection`; every module context is built on it (`DesignTime.Configure`). Entity Framework opens and closes it as it works.
2. **Unit of work.** `IUnitOfWork.BeginAsync` opens the shared connection, starts one transaction and enlists every registered module context (`UseTransaction`). `CommitAsync` commits; disposing without it rolls back; a nested Begin joins, and an inner scope left without Commit makes the outer Commit throw and the whole transaction roll back. Code that does not begin a unit of work keeps today's behaviour: each SaveChanges is its own transaction.
3. **The audit table.** Schema `audit`, table `events`, an `AppendOnlyEntity` (id, created_at, created_by; no updated columns, table comment `append-only`): event_type, outcome, actor_id, impersonator_id (S2), source (`ui`, `api`, `job`), target, record_type, record_id, reason, details (jsonb), correlation_id. Indexes on created_at, actor_id, event_type, correlation_id and (record_type, record_id). The hash chain (TF-061) comes later with its anchor store.
4. **The writer.** `AuditWriter` adds one row and saves on the scope's connection: inside the caller's unit of work when one is open, otherwise at once. The correlation id is the request's, or the current activity's in a job; the source is `api` in a request and `job` outside one. The denial path of Authorization writes through it, so every 403 is a row.
5. **The app role.** Each unit runs with a database role that is not the table owner. `GrantsRunner` derives the role's privileges from the module models: usage on every schema, SELECT, INSERT, UPDATE and DELETE on ordinary tables, SELECT and INSERT only on append-only tables. `migrate` grants them when `Database:AppRole` is configured (the fleet sets it per unit, T14); `grant [role]` grants them again. PostgreSQL refuses UPDATE and DELETE on `audit.events` for the app role: the protection is in the database, not in code.
6. **Event types** are owned and named by the module that raises them, in the permission style: `authz.permission_denied`, `identity.login_failed` (T10). `GET /api/v1/audit-events` (`audit.events.read`) searches by time, actor, type, record and correlation id (TF-064 first step).

## Consequences

- A service that changes rows and records an event wraps both in `BeginAsync` ... `CommitAsync`; the developer guide shows the four lines.
- Within one scope, contexts share one connection, so work in a scope is sequential. Parallel queries need separate scopes, which is how jobs run anyway.
- The owner role still can change audit rows; migrations and seeds run as the owner on purpose, and operators are audited by their own access path. Tamper evidence for operators is TF-061.
- `make migrate` on the laptop has no app role and logs a warning; the fleet always configures one.
