# Iron.Fleet

Per-tenant deployment units, provisioning jobs, release waves, migration orchestration, server-level backups with per-tenant restore, placement and density, cost attribution. Control plane module. Schema: `fleet`.

Slice S1 (T14): the unit runtime interface with the Compose implementation for the laptop, the provisioning job with its eight steps (resumable), size classes, and a thin de-provision that keeps data.

Folders: `Api/` endpoints (thin), `Services/` business logic (plain classes), `Data/` entities, repositories and migrations for this module's schema, `Contracts/` the public interfaces and DTOs other modules may use (its own project, `Iron.Fleet.Contracts`).

Only `Contracts/` is public. Another module may reference `Iron.Fleet.Contracts` and nothing else from this folder; the build checks enforce it.
