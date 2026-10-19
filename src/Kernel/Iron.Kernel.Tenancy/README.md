# Iron.Kernel.Tenancy

Kernel module (ADR-0029). Schema `kernel_tenancy`. The tenant registry and the control plane's tenant API (TF-001, TF-002; ADR-0017). Runs in the control plane only.

| Folder | Holds |
|---|---|
| `Contracts/` | `ITenantRegistry` and `TenantRecord` (the fleet reads tenants and moves their status), `ITenantEntitlements` (the fleet's secrets step writes a tenant's entitlements to the store again), `TenantStatus`, `SizeClass`, `TenancyPermissions` (`tenancy.tenants.read`, `tenancy.tenants.manage`, `tenancy.tenants.provision`, `tenancy.entitlements.read`, `tenancy.entitlements.manage`), `TenancyErrors`, `TenancyAuditEvents` |
| `Data/` | `TenancyDbContext`, `Tenant` (with its plan, grace days, entitlements revision and the revision the store holds), `TenantHostname` (unique across the platform), `Plan` (modules as jsonb, features as text[]), `TenantEntitlement`, `EntitlementChange` (append-only), the design-time factory, generated `Migrations/` |
| `Services/` | `TenantRegistry` (create with unique slug and hostnames and the plan's entitlements, find, set status, request provisioning), `PlanService` and `PlanSeeder` (Standard and Enterprise), `EntitlementService` (preview, apply, the document for the store), `EntitlementPublisher` (writes it after a commit) and `EntitlementReconciler` (the fleet's minute loop), `UnitCatalogue` with `licensing-catalogue.json`, `TenancyOptions` |
| `Api/` | `GET /api/v1/tenants`, `GET /api/v1/tenants/{slug}`, `POST /api/v1/tenants`, `POST /api/v1/tenants/{slug}/provision`, `GET /api/v1/tenants/{slug}/provisioning`; `GET /api/v1/plans`, `GET /api/v1/plans/{code}`, `PUT /api/v1/plans/{code}`, `GET /api/v1/tenants/{slug}/entitlements`, `POST /api/v1/tenants/{slug}/plan`, `GET /api/v1/tenants/{slug}/entitlements/history` |

## Plans and entitlements (TF-040 to TF-045, ADR-0034)

What a tenant may use is decided here and nowhere else. Kernel modules are the platform: always on and never in a plan. A Business or Products module is licensed per module, and a feature in any layer, as their modules declare in code; the control plane knows them from `Services/licensing-catalogue.json`, which `make catalogue` writes from the unit host's modules and a host test keeps equal to them, embedded in this assembly. A plan or a tenant never names a code the catalogue does not know.

| Operation | Endpoint | Permission | Refused when |
|---|---|---|---|
| The plans, one plan | `GET /api/v1/plans`, `GET /api/v1/plans/{code}` | `tenancy.entitlements.read` | |
| Create or replace a plan | `PUT /api/v1/plans/{code}` with `name`, `grace_days`, `modules` (`code`, `status`, `user_limit`) and `features` | `tenancy.entitlements.manage` | a module or feature the catalogue does not know, or a platform module (`tenancy.plan.unknown_entitlements`, 400); a per-person module without seats (`tenancy.plan.user_limit`) |
| A tenant's entitlements | `GET /api/v1/tenants/{slug}/entitlements` (plan, grace days, revision, the revision the store holds, each module and feature) | `tenancy.entitlements.read` | |
| Apply a plan | `POST /api/v1/tenants/{slug}/plan` with `plan`, `dry_run`, optional `starts_on` and `ends_on` | `tenancy.entitlements.manage` | an unknown plan (`tenancy.plan.not_found`, 404); a stored plan with a code this build cannot license (`tenancy.plan.unknown_entitlements`, TF-042 AC3); an end before the start (`tenancy.plan.dates_invalid`) |
| The history | `GET /api/v1/tenants/{slug}/entitlements/history` (list grammar, oldest first) | `tenancy.entitlements.read` | no route writes it |

- **The preview** (`dry_run: true`) answers the plan and grace days before and after, the entitlements `added`, `removed` and `changed` with their values before and after, and the `warnings`: a module that leaves the subscription keeps its data, hidden until it is licensed again; one that becomes read only or switched off says so. The counts of affected data wait for the first module that has data.
- **The apply** is one transaction: the tenant's plan, grace days and revision, the entitlements, one history row per change (the plan, the grace days, each module and feature, with before and after as JSON and the reason), and `tenancy.plan_applied` in the audit trail. A new entitlement starts on `starts_on` or today; one the tenant has keeps its start unless a date is given; `ends_on` applies to every entitlement of the plan, and none means no end. A plan that changes nothing writes nothing. Expired is never stored.
- **A new tenant** gets its plan's entitlements in the transaction that creates it (`plan` in the request, `Tenancy:DefaultPlan` otherwise, `standard`); an unknown plan refuses the tenant.
- **The history** (`kernel_tenancy.entitlement_history`) is append-only: the app role may insert and select only (as `kernel_audit.events`), the save interceptor refuses a change or a delete, and no endpoint writes it. Each row names the operator (`created_by`), the time, the revision, the kind and code, before and after.
- **To the unit.** After every committed change the effective entitlements go to the store at `tenants/<slug>/entitlements` (key `document`, Iron.Kernel.Entitlements' `EntitlementsDocument`), and the tenant's `published_revision` follows. When the store refuses, the fleet worker's `EntitlementReconciler` writes every tenant whose copy is behind, every minute (`Tenancy:Reconcile:Seconds`). The fleet's secrets step writes them again on every provisioning and update, so a store that lost them heals with the unit.
- `make apply-plan TENANT=echo PLAN=Enterprise` shows the preview and applies it (`DRY_RUN=1` previews only; `STARTS` and `ENDS` set the dates); the seeded plans are `standard` (no `authorization.role_transfer`, 14 grace days) and `enterprise` (with it, 30 grace days).

Configuration (`Tenancy` section): `BaseDomain` (`localhost`; a tenant without hostnames gets `<slug>.<BaseDomain>`), `DefaultRegion` (`local`), `DefaultPlan` (`standard`).

Model change: edit the entity, then `make ef-add MODULE=Tenancy NAME=<WhatChanged>` and commit `Data/Migrations`.
