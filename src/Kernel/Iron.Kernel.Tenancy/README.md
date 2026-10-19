# Iron.Kernel.Tenancy

Kernel module (ADR-0029). Schema `kernel_tenancy`. The tenant registry and the control plane's tenant API (TF-001, TF-002; ADR-0017). Runs in the control plane only.

| Folder | Holds |
|---|---|
| `Contracts/` | `ITenantRegistry` and `TenantRecord` (the fleet reads tenants and moves their status), `TenantStatus`, `SizeClass`, `TenancyPermissions` (`tenancy.tenants.read`, `tenancy.tenants.manage`, `tenancy.tenants.provision`), `TenancyErrors`, `TenancyAuditEvents` |
| `Data/` | `TenancyDbContext`, `Tenant`, `TenantHostname` (unique across the platform), the design-time factory, generated `Migrations/` |
| `Services/` | `TenantRegistry` (create with unique slug and hostnames, find, set status, request provisioning), `TenancyOptions` |
| `Api/` | `GET /api/v1/tenants`, `GET /api/v1/tenants/{slug}`, `POST /api/v1/tenants`, `POST /api/v1/tenants/{slug}/provision`, `GET /api/v1/tenants/{slug}/provisioning` |

Configuration (`Tenancy` section): `BaseDomain` (`localhost`; a tenant without hostnames gets `<slug>.<BaseDomain>`), `DefaultRegion` (`local`), `DefaultPlan` (`standard`).

Model change: edit the entity, then `make ef-add MODULE=Tenancy NAME=<WhatChanged>` and commit `Data/Migrations`.
