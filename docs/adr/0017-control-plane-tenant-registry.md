# ADR-0017: The tenant registry and the control plane API

Date: 2026-10-18
Status: Accepted

## Context

TF-001: a registry of tenants with a stable identifier, hostnames unique across the platform, status and the location of the tenant's database and storage. TF-002: provisioning as one job, refused for an Active tenant. The control plane is a unit like any other (ADR-0002) with its own modules; operators reach it with client credentials (S1 plan, decision 8).

## Decision

1. **Iron.Tenancy owns the registry.** Schema `tenancy`: `tenants` (slug, display name, region, plan, size class, status, first administrator, database name, storage container, activation time) and `tenant_hostnames` (one row per hostname, unique index across all tenants). The slug is the identifier used everywhere (ADR-0007) and names the database and the container; the row id is the key. A tenant without hostnames gets `<slug>.<Tenancy:BaseDomain>`.
2. **The API** lives in the control plane only: `GET /api/v1/tenants` (list grammar), `GET /api/v1/tenants/{slug}`, `POST /api/v1/tenants` (Provisioning status, 409 `tenancy.tenant.slug_taken` or `tenancy.hostname.taken` naming the hostname), `POST /api/v1/tenants/{slug}/provision` (202 with the job; 409 `tenancy.tenant.already_active` for an Active tenant, `fleet.job.in_progress` while a job is open) and `GET /api/v1/tenants/{slug}/provisioning`. Three permissions: read, manage, provision (Critical).
3. **Iron.Fleet owns the jobs**, from T13 as a record: `fleet.provisioning_jobs` with status Requested, Running, Failed, Completed, the current step and the error. `IProvisioningJobs` (Fleet.Contracts) creates at most one open job per tenant; Tenancy calls it inside its own transaction with the audit event. T14 adds the steps, the worker that runs them and the unit runtime.
4. **`ITenantRegistry`** (Tenancy.Contracts) gives the fleet what it needs: the tenant record and a way to move its status, audited.
5. **The operator is a seeded service account.** The Identity seeder takes a list, `Seed:ServiceAccounts`, each with a client id, a secret and a role: a tenant unit seeds `smoke-client` with service-default; the control plane seeds `operator` with tenant-administrator, which holds every permission of the control plane host. `make provision TENANT=acme` gets a token with those credentials, registers the tenant if needed and requests provisioning; in T13 the job stays Requested until the worker exists.
6. **Laptop targets.** `make migrate-control`, `make seed-control` and `make run-control` run the control plane against its own database `control` in the laptop PostgreSQL (Entity Framework creates it on first migrate), on http://localhost:5200 for hostnames `localhost` and `control.localhost`.

## Consequences

- Hostname uniqueness is a database constraint, not only a check in code; two tenants cannot share a hostname however they are created.
- A provisioning request is cheap and idempotent in effect: a second request while one is open answers 409 with the open job, so the fleet never runs two jobs for one tenant.
- The control plane's own identity, roles and audit trail are the same modules a tenant runs, which is why operator access needed no new code beyond a seed entry.
- Suspended tenants are in the registry but not yet enforced at the unit (TF-001 AC3); that arrives with the gateway rules in T14 and S2.
