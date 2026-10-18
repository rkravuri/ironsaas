# Iron.Fleet

Schema `fleet`. Provisioning (TF-002, TF-210; ADR-0018). The control plane API lists the module for jobs; the fleet worker lists it and runs them.

| Folder | Holds |
|---|---|
| `Contracts/` | `IProvisioningJobs`, `ProvisioningJobInfo` with its steps, `ProvisioningStatus`, `FleetPermissions`, `FleetErrors` |
| `Data/` | `FleetDbContext`, `ProvisioningJob`, `ProvisioningStep`, the design-time factory, generated `Migrations/` |
| `Services/` | `ProvisioningRunner` (the steps in order, recorded, resumable), `ProvisioningWorker` (the loop), `ProvisioningJobs`, `IUnitRuntime` (Compose in T14b), `FleetOptions`, `SizeClasses` |
| `Services/Steps/` | reserve, database, storage, secrets, migrate, seed, unit, verify |

Configuration (`Fleet` section): `Database:AdminConnectionString`, `Database:UnitHost`, `Database:UnitPort`; `Storage:AccountConnectionString`, `Storage:UnitBlobEndpoint`, `Storage:SasValidDays`; `Secrets:UnitAddress`, `Secrets:UnitTokenValidDays`; `Units:ImageTag`, `Units:OtlpEndpoint`, `Units:ReadyTimeoutSeconds`; `PollSeconds`. The fleet's own store comes from the `Secrets` section as for any unit.

Model change: edit the entity, then `make ef-add MODULE=Fleet NAME=<WhatChanged>` and commit `Data/Migrations`.
