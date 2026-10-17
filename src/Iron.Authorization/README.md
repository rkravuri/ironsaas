# Iron.Authorization

Permission catalogue, roles, role assignments with scope, record-level policies, segregation of duties, delegation. Schema: `authz`.

Slice S1 (T8): permissions declared in code, the start-up check, roles and assignments tables with a seeded tenant-administrator role, permission evaluation with a 60-second cache, denial handling.

Folders: `Api/` endpoints (thin), `Services/` business logic (plain classes), `Data/` entities, repositories and migrations for this module's schema, `Contracts/` the public interfaces and DTOs other modules may use (its own project, `Iron.Authorization.Contracts`).

Only `Contracts/` is public. Another module may reference `Iron.Authorization.Contracts` and nothing else from this folder; the build checks enforce it.
