# Iron.Kernel.Fleet

Kernel module (ADR-0029). Schema `kernel_fleet`. Provisioning (TF-002, TF-210; ADR-0018). The control plane API lists the module for jobs; the fleet worker lists it and runs them.

| Folder | Holds |
|---|---|
| `Contracts/` | `IProvisioningJobs`, `ProvisioningJobInfo` with its steps, `JobKind` (Provision, Deprovision, Update, Recover), `JobRequest` (whom a job is about, why, who asked), `ProvisioningStatus`, `FleetPermissions`, `FleetErrors` |
| `Data/` | `FleetDbContext`, `ProvisioningJob` (with a recovery job's subject, reason and requester), `ProvisioningStep`, the design-time factory, generated `Migrations/` |
| `Services/` | `ProvisioningRunner` (the steps in order, recorded, resumable), `ProvisioningWorker` (the loop), `ProvisioningJobs`, `IUnitRuntime` (Compose in T14b), `FleetOptions`, `SizeClasses` |
| `Services/Steps/` | reserve, database, storage, secrets, migrate, seed, unit, verify; the secrets step writes the unit's policy on every run (the unit secret and `tenants/<slug>/entitlements`, ADR-0034) and the tenant's entitlements through Tenancy's `ITenantEntitlements`; remove (de-provisioning); recover, the one step of a recovery job |

A recovery job (TF-017, ADR-0046) runs the image's `recover-admin` command in an active tenant's unit, as the owner role like the seed, with the administrator, the operator and the reason in `Recovery__LoginId`, `Recovery__Operator` and `Recovery__Reason`. It reads the code from the line `Recovery token: <code>` and answers the link, `https://<primary hostname>/recovery/<code>`, valid 15 minutes and once, which is the job's output. A refusal in the unit (not a tenant administrator, not active) fails the step with the command's reason. The tenant's status never changes, and a job with a request is never reopened: a new request is a new job.

Configuration (`Fleet` section): `Database:AdminConnectionString`, `Database:UnitHost`, `Database:UnitPort`; `Storage:AccountConnectionString`, `Storage:UnitBlobEndpoint`, `Storage:SasValidDays`; `Secrets:UnitAddress`, `Secrets:UnitTokenValidDays`; `Units:ImageTag`, `Units:OtlpEndpoint`, `Units:ReadyTimeoutSeconds`; `PollSeconds`. The fleet's own store comes from the `Secrets` section as for any unit.

Model change: edit the entity, then `make ef-add MODULE=Fleet NAME=<WhatChanged>` and commit `Data/Migrations`.
