# ADR-0018: Provisioning as resumable steps

Date: 2026-10-18
Status: Accepted

## Context

TF-002: one job provisions a tenant, every step is reported with its duration, a failed step leaves the tenant in Provisioning and the job can resume from that step, and an Active tenant is refused. The S1 plan lists eight steps and the fleet runs on a laptop (Compose) first and on Kubernetes later (ADR-0002).

## Decision

1. **Eight steps, in this order:** reserve, database, storage, secrets, migrate, seed, unit, verify. The plan had storage and secrets after seed; they come first here because the image's `migrate` and `seed` commands run with the unit's own environment (the store address, the unit token and the path), so the unit secret must exist before them. The commands get the owner connection string as an environment override, which wins over the unit secret (ADR-0016 updated: the secret sits below the environment).
2. **Nothing generated lives only in memory.** The database step keeps the owner and app passwords at `tenants/<slug>/database`, the storage step the signed container URL at `tenants/<slug>/storage`, the secrets step the unit token and later the smoke secret and the invitation link at `tenants/<slug>/fleet`, and the unit's own secret at `tenants/<slug>/unit`. A step that runs again reads what an earlier run left, so a job resumes in any process, and every step is idempotent (roles are altered rather than created twice, containers and policies are created if missing, the signing key is kept).
3. **Records.** `fleet.provisioning_steps` holds one row per step and job with status, start, finish, duration, output and error; the job carries the current step, the error and, when complete, the invitation link. A Failed job asked to run again is reopened, not duplicated, and completed steps are skipped.
4. **Isolation by construction (TF-005, TF-073).** The database step revokes PUBLIC's CONNECT on the tenant database, so another tenant's role cannot reach it. The secrets step creates a policy that reads `tenants/<slug>/unit` and nothing else and a unit token with only that policy. The tests prove both with a second tenant.
5. **Two views of every platform service.** `Fleet:Database`, `Fleet:Storage` and `Fleet:Secrets` carry the address the fleet uses and the address the units see (`UnitHost`, `UnitBlobEndpoint`, `UnitAddress`); connection strings and signed URLs are written for the units' view.
6. **The runtime is an interface.** `IUnitRuntime` runs an image command, applies a unit, checks readiness through the ingress and removes a unit. The steps use nothing else, so they are the same on Compose (T14b) and Kubernetes. Tests use a recording runtime; a fleet without a configured runtime fails the image and unit steps with a clear message.
7. **The worker** is a hosted service in the fleet host only: every few seconds the oldest Requested job runs to its end, one at a time; errors are logged and never stop the host.

## Consequences

- A re-run after a fixed infrastructure problem costs only the failed step and the ones after it.
- The store holds generated credentials in plain form under the tenant's path, readable by the fleet's token only; rotating them is a matter of changing a secret and re-running the affected step (S3).
- The in-process steps (database, storage, secrets) are tested on real containers; the image and unit steps are tested through the recording runtime until T14b runs them on Compose.
