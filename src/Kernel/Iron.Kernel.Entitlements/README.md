# Iron.Kernel.Entitlements

Kernel module (ADR-0029). Schema `kernel_entitlements`. The unit's side of entitlements: what this tenant may use, read from the store, kept for a restart, evaluated by one service, and checked in the API (TF-040, TF-041, TF-043, TF-046; ADR-0034). The control plane's side (plans, a tenant's entitlements, the history) is in `Iron.Kernel.Tenancy`.

Placement test: it knows nothing about any business domain, so it is Kernel. It licenses Business and Products modules, which declare their licence in their own code; the kernel itself is never in a plan.

| Folder | Holds |
|---|---|
| `Contracts/` | `IEntitlements` (a module for the tenant, a module for one person, a feature), `EntitlementsDocument` with `ModuleEntitlement` and `FeatureEntitlement` (the store's format, written by the control plane), `EntitlementsPermissions` (`entitlements.subscription.read`, `entitlements.seats.read`, `entitlements.seats.manage`), `EntitlementsErrors`, `EntitlementsAuditEvents` |
| `Data/` | `EntitlementsDbContext`, `LastKnownEntitlements` (one row: the last document and when the store last confirmed it), `SeatAssignment` (module and person, unique), the design-time factory, generated `Migrations/` |
| `Services/` | `IEntitlementSource` (`StoreEntitlementSource`, `ConfigurationEntitlementSource`), `EntitlementCache`, `EntitlementRefresher` (the read loop and the last known answer), `EntitlementRules` (dates, grace, a feature governed by its module), `EntitlementEvaluator` (`IEntitlements` and Iron.Kernel.Api's `IEntitlementGate`), `SeatService`, `EntitlementsMetrics`, `EntitlementsHealthCheck`, `EntitlementsOptions` |
| `Api/` | `EntitlementsContextPart` (the `entitlements` part of `GET /api/v1/me/context`), `GET /api/v1/subscription`, `GET /api/v1/seats`, `GET /api/v1/seats/usage`, `POST /api/v1/seats`, `DELETE /api/v1/seats/{id}` |

## How a unit knows

- The control plane writes the tenant's effective entitlements to the store at `tenants/<slug>/entitlements` (key `document`, the JSON of `EntitlementsDocument`) after every committed change. The unit's token policy reads that path beside its own secret.
- `EntitlementRefresher` reads it every 30 seconds (`Entitlements:RefreshSeconds`, at most 60), and once before the host serves. A new answer replaces the old one in memory and in `kernel_entitlements.last_known`; a confirmation that changed nothing is written at most every ten minutes. A path with nothing in it means nothing is licensed.
- When a read fails, the last answer stands until it is 24 hours old (`Entitlements:StaleAfterHours`). After that, or with no answer at all, every licensed module and feature answers Disabled with the reason `unknown`, and the alert is raised: an error log line that starts with `ALERT: the entitlements are unknown` (once, then hourly), the gauge `entitlements.stale` at 1 (meter `Iron.Entitlements`, with `entitlements.age` in seconds and the counter `entitlements.refresh_failures`), and the readiness check `entitlements` Degraded with the reason. Degraded keeps the probe at 200: the platform still serves.
- A host without a store, or with `Entitlements:Source=configuration`, reads the `Entitlements` section instead: `Features` (codes, enabled), `Modules` (code, status, subscription type, user limit, dates), `GraceDays`. The control plane and the fleet use it, because they are no tenant; `make run-api` gets `authorization.role_transfer` from `appsettings.Development.json`.

## How it answers

- A kernel module is always on and is never asked about. A licensed module is Disabled when the document does not name it (`not_licensed`), before its start date (`not_started`), when the control plane switched it off (`switched_off`), and after its end date and the plan's grace days (`expired`); in the grace days it keeps its status with the reason `in_grace`. Expired is never stored.
- A per-person module also needs the person's seat (`no_seat`); the seat is looked up on every request, so a removed seat counts on the next one.
- A feature is governed by its module: a module that is not available makes its features unavailable (`module_unavailable`), and a read-only module makes them read only. A feature this build does not declare is not licensed.
- The gate (`IEntitlementGate`) answers the API's filters: `entitlements.module_disabled`, `entitlements.read_only` for a write (any method but GET, HEAD and OPTIONS), `entitlements.feature_disabled`, `entitlements.seat_required`, each 403 with `entitlement` and `reason` in the problem. Every refusal is logged, counted in `entitlements.refusals` and audited as `entitlements.access_denied` with the code, the entitlement, the reason and the path.

## Seats (TF-041)

`POST /api/v1/seats` with `module` and `principal_id` gives a person a seat of a per-person module; above the licensed count it answers 409 `entitlements.seat.limit` with the count ("All 50 seats of 'x' are assigned (50 of 50)"). A module that is not per person (`entitlements.seat.not_per_person`), not in the subscription (`entitlements.seat.not_licensed`) or a service account (`entitlements.seat.principal`) is refused. A transaction-scoped advisory lock per module keeps two assignments from passing the count together. Assigning and removing write `entitlements.seat_assigned` and `entitlements.seat_unassigned` in the same transaction. `GET /api/v1/seats/usage` answers per per-person module the limit, the seats assigned and the seats left; the subscription shows the same.

## For the shell

`GET /api/v1/me/context` (any signed-in principal) answers `entitlements`: the revision, the plan, when it was confirmed, whether it is stale, and every licensed module (with the person's seat taken into account) and every feature of this build with its status and reason. The shell reads it with the session and every 30 seconds. A5 adds the flags to it and A6 the settings. `GET /api/v1/subscription` (`entitlements.subscription.read`) answers what the console's Subscription page shows: modules and features with status, reason, dates, the end of the grace period and seat usage.

Model change: edit the entity, then `make ef-add MODULE=Entitlements NAME=<WhatChanged>`, `make format`, and commit `Data/Migrations`.
