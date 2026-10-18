# ADR-0011: Hosts, tenant binding and unit commands

Date: 2026-10-18
Status: Accepted

## Context

A tenant unit is four processes built from four images: API, worker, and for the control plane the control-plane API and the fleet (ADR-0002). Each process must be bound to exactly one tenant, refuse requests meant for another unit (TF-003), apply its own migrations and seed data when the fleet creates the unit (T14), and tell the container runtime whether it is healthy. The runtime image must stay small and safe.

## Decision

1. **Explicit composition.** `Program.cs` of each host lists its modules in an `IReadOnlyList<IModule>`; there is no assembly scanning. `AddModules` and `MapModules` (Iron.Api) call each module in order.
2. **Tenant binding from configuration.** `TenantBinding.FromConfiguration` reads `Tenant:Id` and `Tenant:Hostnames` and returns the unit's `TenantContext`. A missing or invalid binding stops the process at start. The same tenant id goes into the telemetry resource (`tenant.id`). The control plane hosts default to `control` in their `appsettings.json`; tenant units get the values from the fleet as environment variables.
3. **Hostname check.** `TenantHostMiddleware` answers 421 Misdirected Request with a problem (`tenant.host.not_served`) for a Host header the unit does not serve. `/health/*` is exempt because probes arrive with the container's own address. The token tenant claim check is added by Identity (T11).
4. **Commands in the image.** The host executable answers `migrate`, `seed` and `schema-check` (Iron.Data `DatabaseCommands`) and `health` (Iron.Telemetry `HealthProbe`). `migrate` applies the pending migrations of every module context registered with `AddIronDbContext`, each with its own history table in its schema. `seed` runs every `ISeeder` in order. `schema-check` runs the TF-080 schema check over the unit's schemas and exits 1 on violations. `health` asks `/health/ready` and exits 0 on 200; it runs before the host is built.
5. **Images.** One Dockerfile per host, built from the repository root. Build stage `mcr.microsoft.com/dotnet/sdk:10.0`, runtime stage `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled`: no shell, no package manager, non-root user `app`, port 8080. `HEALTHCHECK` runs the `health` command, so Compose and Kubernetes can use the image without extra tools. `ENTRYPOINT` is the executable, so `docker run image migrate` runs a command.
6. **Worker heartbeat.** `HeartbeatService` records a `worker.heartbeat` span and a log line at start and then every interval (default 60 s), tagged with the tenant. Every `ActivitySource` named `Iron.*` is traced.
7. **Default principal.** `AddIronDatabase` registers the system principal as the default `ICurrentPrincipal`. Hosts that serve people replace it with the request principal (Identity, T10); workers and the commands keep it.

## Consequences

- A unit cannot start unbound, and a request for a different hostname never reaches business code. Isolation does not depend on the gateway alone.
- `migrate` and `seed` need no SDK or tooling at run time; the fleet runs the same image it deploys.
- The chiseled runtime image has no shell, so debugging inside a container means reading logs and traces, not `exec`. This is intended.
- Hand-written migrations: EF Core refuses to migrate a context whose model has changes without a migration. Every model change needs a migration (T8 onwards); the sample test context switches that warning off because it uses `EnsureCreated`.
