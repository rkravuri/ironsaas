# Iron.Tenancy

Tenant registry, provisioning requests, lifecycle, isolation, data residency, tenant-level settings and limits. Control plane module. Schema: `tenancy`.

Slice S1 (T13): the tenant registry (TF-001), the control plane API to create, list and read tenants and to request provisioning, and the hostname uniqueness rule. Provisioning itself is executed by Iron.Fleet.

Folders: `Api/` endpoints (thin), `Services/` business logic (plain classes), `Data/` entities, repositories and migrations for this module's schema, `Contracts/` the public interfaces and DTOs other modules may use (its own project, `Iron.Tenancy.Contracts`).

Only `Contracts/` is public. Another module may reference `Iron.Tenancy.Contracts` and nothing else from this folder; the build checks enforce it.
