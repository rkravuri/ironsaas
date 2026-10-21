# Iron.Kernel.Tenancy

Kernel module (ADR-0029). Schema `kernel_tenancy`. The tenant registry and the control plane's tenant API (TF-001, TF-002; ADR-0017). Runs in the control plane only.

| Folder | Holds |
|---|---|
| `Contracts/` | `ITenantRegistry` and `TenantRecord` (the fleet reads tenants and moves their status), `ITenantEntitlements` (the fleet's secrets step writes a tenant's entitlements to the store again), `TenantStatus`, `SizeClass`, `TenancyPermissions` (`tenancy.tenants.read`, `tenancy.tenants.manage`, `tenancy.tenants.provision`, `tenancy.entitlements.read`, `tenancy.entitlements.manage`, `tenancy.administrators.recover`), `TenancyErrors`, `TenancyAuditEvents` |
| `Data/` | `TenancyDbContext`, `Tenant` (with its plan, grace days, entitlements revision and the revision the store holds), `TenantHostname` (unique across the platform), `Plan` (modules as jsonb, features as text[]), `TenantEntitlement`, `EntitlementChange` (append-only), the design-time factory, generated `Migrations/` |
| `Services/` | `TenantRegistry` (create with unique slug and hostnames and the plan's entitlements, find, set status, request provisioning), `PlanService` and `PlanSeeder` (Standard and Enterprise), `EntitlementService` (preview, apply, the document for the store), `EntitlementPublisher` (writes it after a commit) and `EntitlementReconciler` (the fleet's minute loop), `UnitCatalogue` with `licensing-catalogue.json`, `TenancyOptions` |
| `Api/` | `GET /api/v1/tenants`, `GET /api/v1/tenants/{slug}`, `POST /api/v1/tenants`, `POST /api/v1/tenants/{slug}/provision`, `POST /api/v1/tenants/{slug}/administrator-recovery`, `GET /api/v1/tenants/{slug}/provisioning`; `GET /api/v1/plans`, `GET /api/v1/plans/{code}`, `PUT /api/v1/plans/{code}`, `GET /api/v1/tenants/{slug}/entitlements`, `POST /api/v1/tenants/{slug}/plan`, `GET /api/v1/tenants/{slug}/entitlements/history` |

## Plans and entitlements (TF-040 to TF-045, ADR-0034)

What a tenant may use is decided here and nowhere else. Kernel modules are the platform: always on and never in a plan. A Business or suite module is licensed per module, and a feature in any layer, as their modules declare in code; the control plane knows them from `Services/licensing-catalogue.json`, which `make catalogue` writes from the unit host's modules and a host test keeps equal to them, embedded in this assembly. A plan or a tenant never names a code the catalogue does not know.

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


## A tenant administrator's recovery (TF-017, ADR-0046)

`POST /api/v1/tenants/{slug}/administrator-recovery` with `reason` and an optional `login_id` (the tenant's first administrator by default) needs `tenancy.administrators.recover`, a critical permission only the control plane's administrators hold. A tenant that is not active is refused (`tenancy.tenant.not_provisionable`, 409); an empty reason too (400). It writes `tenancy.administrator_recovery_requested` with the reason, the administrator and the operator, and creates a `Recover` job carrying them; the answer is the job (202), whose output becomes the link when the fleet has run the unit's `recover-admin`. The unit audits its side with the same operator and reason. `make recover-admin TENANT=x REASON="..."` is the call from the laptop.

## Feature flags and kill switches (TF-050 to TF-053, ADR-0035)

The rules of the flags the units' modules declare. A rule has a scope and its target (global; a region; a tenant; an organisation unit or a user of a tenant), an optional condition (a percentage of tenants or users by stable hash; an attribute among values: `principal_type`, `plan`, `region`) and a value of the flag's type; a flag's rules are tried most specific scope first, then in their order. The control plane's own flags are evaluated from its `Flags` configuration section, not from these rules: it is no tenant.

| Operation | Endpoint | Permission | Refused when |
|---|---|---|---|
| The flags of the catalogue | `GET /api/v1/flags` (declaration, status, rule count) | `tenancy.flags.read` | |
| The report | `GET /api/v1/flags/report`: flags past their removal date with their owner, rules for flags the catalogue no longer has | `tenancy.flags.read` | |
| A flag's rules | `GET /api/v1/flags/{key}/rules` (with the revision) | `tenancy.flags.read` | |
| Replace a flag's rules | `PUT /api/v1/flags/{key}/rules` with `revision`, `reason`, `rules` | `tenancy.flags.manage`; for a kill switch also `tenancy.kill_switches.manage` | a flag the catalogue does not have (`tenancy.flag.unknown`); a value of another type (`tenancy.flag.value_type`); a target that does not exist or a bad condition (`tenancy.flag.rule_invalid`); another revision (`tenancy.flag.rules_changed`, 409); a kill switch without a reason (`tenancy.kill_switch.reason_required`); a kill switch rule that says false (`tenancy.kill_switch.false_rule`: removing the rule turns a switch off, and nothing exempts anyone from one that is on, ADR-0035) |
| A flag's history | `GET /api/v1/flags/{key}/history` (list grammar, oldest first) | `tenancy.flags.read` | no route writes it |
| The kill switches | `GET /api/v1/kill-switches` (on for the platform, since when, why; the tenants it is on for) | `tenancy.flags.read` | |
| Flip a kill switch | `PUT /api/v1/kill-switches/{path}` with `on`, `reason` and optional `tenant` | `tenancy.kill_switches.manage` (Critical; only the control plane's administrators hold it) | no reason; an unknown path (`tenancy.kill_switch.unknown`) |

- **A change** is one transaction: the rules (an unchanged rule keeps its row, so a kill switch's "since" stays true), one append-only row in `kernel_tenancy.flag_rule_history` with the rules before and after, the revision, the kind (`rules` or `kill_switch`) and the reason, the audit event `tenancy.flag_rules_changed` or `tenancy.kill_switch_flipped`, and the store paths it touches marked behind (`kernel_tenancy.flag_publications`).
- **To the units.** After the commit the paths behind are written: `platform/flags` with every global and region rule, `tenants/<slug>/flags` with the tenant's own rules, its region and its plan (written again when a plan is applied). When the store refuses, the fleet worker's `FlagReconciler` writes them every minute. The fleet's secrets step widens the unit policy to both paths and writes them again on every provisioning and update.
- **The report** is logged when the control plane starts and once a week by the fleet worker (TF-052 AC1).
- `make flag KEY=flags.example_tone VALUE=warm TENANT=echo` sets the unconditional rule for a tenant (or the platform without `TENANT`) and prints it; `make kill-switch PATH=imports ON=true REASON="..."` flips a kill switch for the platform (or `TENANT=x`).
Configuration (`Tenancy` section): `BaseDomain` (`localhost`; a tenant without hostnames gets `<slug>.<BaseDomain>`), `DefaultRegion` (`local`), `DefaultPlan` (`standard`).

Model change: edit the entity, then `make ef-add MODULE=Tenancy NAME=<WhatChanged>` and commit `Data/Migrations`.
