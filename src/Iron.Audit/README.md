# Iron.Audit

Append-only audit log, change history, electronic signatures, hash chaining, retention, export. Schema: `audit`.

Slice S1 (T9): the `audit.events` table, a writer that runs in the caller's transaction, database grants that block update and delete, and the S1 event types (login, denial, invitation, provisioning).

Folders: `Api/` endpoints (thin), `Services/` business logic (plain classes), `Data/` entities, repositories and migrations for this module's schema, `Contracts/` the public interfaces and DTOs other modules may use (its own project, `Iron.Audit.Contracts`).

Only `Contracts/` is public. Another module may reference `Iron.Audit.Contracts` and nothing else from this folder; the build checks enforce it.
