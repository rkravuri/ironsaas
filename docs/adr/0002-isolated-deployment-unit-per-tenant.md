# ADR-0002: One isolated deployment unit per tenant on shared infrastructure

**Status:** Accepted
**Date:** 2026-10-17
**Decided by:** Product owner and architect

## Context

Customers refuse shared databases. PRD decision D-01 chose tenancy model C (section 3.2): each tenant runs its own API and worker containers against its own database, and only infrastructure is shared (cluster, ingress, database server, object storage account, telemetry, secrets store, control plane). This record states what that decision means for the code layout from the first commit.

## Decision

- Application code is single-tenant. A unit learns its tenant once at start, from configuration. There is no tenant switch and no tenant column used for isolation. A request for another tenant is refused.
- Two planes, each with an API host and a worker host. The tenant unit: `Iron.Host.Api` and `Iron.Host.Worker`. The control plane: `Iron.Host.ControlPlane` (API) and `Iron.Host.Fleet` (worker, the fleet orchestrator). The Supplier Network plane (slice S8) follows the same shape later.
- Tenant modules (`Iron.Identity`, `Iron.Authorization`, `Iron.Audit`, `Iron.Files`, later more) run in the tenant unit. Control plane modules (`Iron.Tenancy`, `Iron.Fleet`, later entitlements, flags definitions, billing) run in the control plane. Identity, Authorization and Audit run in both planes, each against its own database.
- `Iron.Tenancy` owns the tenant registry and the request to provision. `Iron.Fleet` owns deployment units and the provisioning job: database and roles, bucket and credentials, secrets path, containers and routes. Nothing is created by hand.
- On the laptop a unit is a Compose project rendered from a template. On Azure it is a Kubernetes namespace rendered from a Kustomize overlay. Same images, same steps, one `IUnitRuntime` interface with two implementations.
- Size classes small, medium and large set the unit's resource limits. A dedicated deployment (model D) uses the same images and tooling on its own infrastructure.

## Options considered

- A shared database with a tenant column and row-level security. Rejected by customers.
- A shared application with one database per tenant. Rejected: the runtime is still shared between customers.
- Fully dedicated infrastructure for every tenant. Too costly for small tenants; kept as the paid option D.

## Consequences

- Easier: isolation by construction; simple single-tenant code; restore and migration per tenant; the fleet host is the one place with rights to create tenant objects.
- Harder: fleet work multiplies with tenants (databases, secrets, backups, release waves). These are first-class requirements (TF-210 to TF-217) and the reason the fleet host exists from slice S1.
- Revisit: density and idle handling (open point O-07) when the first cost figures exist on Azure.
