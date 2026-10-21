# Iron.Kernel.Fleet

Kernel module (ADR-0029). Schema `kernel_fleet`. Provisioning (TF-002, TF-210; ADR-0018). The control plane API lists the module for jobs; the fleet worker lists it and runs them.

| Folder | Holds |
|---|---|
| `Contracts/` | `IProvisioningJobs`, `IJobHandovers` (a job's link, once, to whoever asked), `ProvisioningJobInfo` with its steps, `JobKind` (Provision, Deprovision, Update, Recover), `JobRequest` (whom a job is about, why, who asked), `ProvisioningStatus`, `FleetPermissions`, `FleetErrors` |
| `Data/` | `FleetDbContext`, `ProvisioningJob` (who asked, by name and id, when its handover was given, and a recovery job's subject and reason), `ProvisioningStep`, the design-time factory, generated `Migrations/` |
| `Services/` | `ProvisioningRunner` (the steps in order, recorded, resumable), `ProvisioningWorker` (the loop), `ProvisioningJobs`, `IUnitRuntime` (Compose in T14b), `FleetOptions`, `SizeClasses` |
| `Services/Steps/` | reserve, database, storage, secrets, migrate, seed, unit, verify; the secrets step writes the unit's policy on every run (the unit secret and `tenants/<slug>/entitlements`, ADR-0034) and the tenant's entitlements through Tenancy's `ITenantEntitlements`; remove (de-provisioning); recover, the one step of a recovery job |

A link from the operator's path (ADR-0046) never travels through a job's output, a log line or a trace. The seed and recover-admin steps give their command the job's one-time place, `tenants/<slug>/handover/<job id>`, emptied first, as `Handover__Path`. They also give `Handover__Token`, which lives 10 minutes under the policy `tenant-<slug>-handover` and may write the tenant's handover places and nothing else. The command writes the link there once, for its lifetime, and says so. `JobHandovers` gives it once, after the job completed, to the principal who asked (`requested_by_id`), deletes the place and stamps `handed_over_at`.

A recovery job (TF-017, ADR-0046) runs the image's `recover-admin` command in an active tenant's unit, as the owner role like the seed, with the administrator, the operator and the reason in `Recovery__LoginId`, `Recovery__Operator` and `Recovery__Reason`, and its handover place. A refusal in the unit (not a tenant administrator, not active) fails the step with the command's reason. The tenant's status never changes, and a job with a request is never reopened: a new request is a new job.

Configuration (`Fleet` section): `Database:AdminConnectionString`, `Database:UnitHost`, `Database:UnitPort`; `Storage:AccountConnectionString`, `Storage:UnitBlobEndpoint`, `Storage:SasValidDays`; `Secrets:UnitAddress`, `Secrets:UnitTokenValidDays`; `Units:ImageTag`, `Units:OtlpEndpoint`, `Units:ReadyTimeoutSeconds`; `PollSeconds`. The fleet's own store comes from the `Secrets` section as for any unit.

Model change: edit the entity, then `make ef-add MODULE=Fleet NAME=<WhatChanged>` and commit `Data/Migrations`.
