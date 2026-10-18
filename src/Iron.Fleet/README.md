# Iron.Fleet

Schema `fleet`. Provisioning jobs (TF-002, TF-210). T13 adds the job record and `IProvisioningJobs` (request, latest); T14 adds the steps, the fleet worker that runs them, the unit runtime (Compose on the laptop) and de-provisioning.

| Folder | Holds |
|---|---|
| `Contracts/` | `IProvisioningJobs`, `ProvisioningJobInfo`, `ProvisioningStatus`, `FleetPermissions` (`fleet.jobs.read`), `FleetErrors` |
| `Data/` | `FleetDbContext`, `ProvisioningJob`, the design-time factory, generated `Migrations/` |
| `Services/` | `ProvisioningJobs`: one open job per tenant at a time |

Model change: edit the entity, then `make ef-add MODULE=Fleet NAME=<WhatChanged>` and commit `Data/Migrations`.
