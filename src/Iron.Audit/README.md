# Iron.Audit

Schema `audit`. The audit trail (TF-060). T8 added the contract (`AuditEvent`, `IAuditWriter`) and a log-line writer; T9 adds the `audit.events` table, the database writer on the caller's connection and transaction, and `GET /api/v1/audit/events`.

| Folder | Holds |
|---|---|
| `Contracts/` | `AuditEvent`, `AuditOutcome`, `IAuditWriter` |
| `Services/` | `LoggingAuditWriter` (until T9) |
