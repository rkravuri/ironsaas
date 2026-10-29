# Iron.Kernel.Tenancy

Kernel module (ADR-0029). Schema `kernel_tenancy`. The tenant registry and the control plane's tenant API (TF-001, TF-002; ADR-0017). Runs in the control plane only.

| Folder | Holds |
|---|---|
| `Contracts/` | `ITenantRegistry` and `TenantRecord` (the fleet reads tenants and moves their status), `ITenantEntitlements` (the fleet's secrets step writes a tenant's entitlements to the store again), `TenantStatus`, `SizeClass`, `TenancyPermissions` (`tenancy.tenants.read`, `tenancy.tenants.manage`, `tenancy.tenants.provision`, `tenancy.entitlements.read`, `tenancy.entitlements.manage`, `tenancy.administrators.recover`, `tenancy.impersonation.start`, `tenancy.break_glass.request`, `tenancy.break_glass.approve`, `tenancy.documents.manage`), `TenancyErrors`, `TenancyAuditEvents` |
| `Data/` | `TenancyDbContext`, `Tenant` (with its plan, grace days, entitlements revision and the revision the store holds), `TenantHostname` (unique across the platform), `Plan` (modules as jsonb, features as text[]), `TenantEntitlement`, `EntitlementChange` (append-only), `BreakGlassAccess` (a request, its approval, the account, the jobs and the review), `PlatformDocument` (each version of the terms of service and the privacy notice), the design-time factory, generated `Migrations/` |
| `Services/` | `TenantRegistry` (create with unique slug and hostnames and the plan's entitlements, find, set status, request provisioning), `PlanService` and `PlanSeeder` (Standard and Enterprise), `EntitlementService` (preview, apply, the document for the store), `EntitlementPublisher` (writes it after a commit) and `EntitlementReconciler` (the fleet's minute loop), `UnitCatalogue` with `licensing-catalogue.json`, `TenancyOptions`; `OperatorImpersonation`, `BreakGlassService` and `BreakGlassWorker` (the fleet's minute loop), `PlatformDocumentService`, `OperatorNames` |
| `Api/` | `GET /api/v1/tenants`, `GET /api/v1/tenants/{slug}`, `POST /api/v1/tenants`, `POST /api/v1/tenants/{slug}/provision`, `POST /api/v1/tenants/{slug}/administrator-recovery`, `GET /api/v1/tenants/{slug}/provisioning`; `GET /api/v1/plans`, `GET /api/v1/plans/{code}`, `PUT /api/v1/plans/{code}`, `GET /api/v1/tenants/{slug}/entitlements`, `POST /api/v1/tenants/{slug}/plan`, `GET /api/v1/tenants/{slug}/entitlements/history`; `POST /api/v1/tenants/{slug}/impersonation`, `POST /api/v1/tenants/{slug}/break-glass`, `GET /api/v1/break-glass`, `GET /api/v1/break-glass/{id}`, `POST /api/v1/break-glass/{id}/approve` and `/end`; `GET` and `POST /api/v1/platform-documents` |

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

`POST /api/v1/tenants/{slug}/administrator-recovery` with `reason`, an optional `login_id` (the tenant's first administrator by default) and an optional `reset_factors` (the administrator's second factors go too, ADR-0047) needs `tenancy.administrators.recover`, a critical permission only the control plane's administrators hold. A tenant that is not active is refused (`tenancy.tenant.not_provisionable`, 409); an empty reason too (400). It writes `tenancy.administrator_recovery_requested` with the reason, the administrator, the operator and `reset_factors`, and creates a `Recover` job carrying them; the answer is the job (202). When the fleet has run the unit's `recover-admin`, the provisioning report (`GET /api/v1/tenants/{slug}/provisioning`) gives its `handover` (`state`, `kind`, `subject`, `link`, `expires_at`) once, to the operator who asked, with `no-store`, then says it was given; anyone else gets `not_yours`. The first administrator's invitation reaches the operator who asked for the provisioning or the update the same way (ADR-0046). The unit audits its side with the same operator and reason. `make recover-admin TENANT=x REASON="..." [RESET_FACTORS=1]` is the call from the laptop.

## The operator's support and emergency access (TF-025, TF-026, ADR-0049)

| Operation | Endpoint | Permission | Refused when |
|---|---|---|---|
| Sign in as a person of a tenant | `POST /api/v1/tenants/{slug}/impersonation` with `login_id`, `reason`, optional `minutes` | `tenancy.impersonation.start` (critical), a fresh token | no reason (`tenancy.impersonation.reason_required`); a tenant that is not active; in the unit: the tenant does not allow it, or the person is not active |
| Request break-glass | `POST /api/v1/tenants/{slug}/break-glass` with `reason`, `ticket`, optional `minutes` (60; 1 to 240) | `tenancy.break_glass.request` (high risk) | no reason or ticket; a duration out of range (`tenancy.break_glass.duration`) |
| Approve it | `POST /api/v1/break-glass/{id}/approve` | `tenancy.break_glass.approve` (critical), a fresh token | the operator who asked (`tenancy.break_glass.same_operator`); a request that is not waiting, or expired (`tenancy.break_glass.not_requested`) |
| End it early | `POST /api/v1/break-glass/{id}/end` |`tenancy.break_glass.request` | an access that is not active (`tenancy.break_glass.not_active`) |
| The accesses, one access | `GET /api/v1/break-glass`, `GET /api/v1/break-glass/{id}` (with its `handover` once, to the operator who asked, and the review) | `tenancy.tenants.read` | |
| Publish a platform document | `POST /api/v1/platform-documents` with `key` (`terms_of_service`, `privacy_notice`), `title`, `body`; `GET` lists them | `tenancy.documents.manage` (high risk) | another key (`tenancy.document.key_unknown`) |

- **Impersonation** writes `tenancy.impersonation_requested` and creates an `Impersonate` job; the fleet runs the unit's `impersonate` command, and the one-time link reaches the operator through the provisioning report's `handover`, as a recovery link does.
- **Break-glass** goes Requested, then Active on approval (the fleet runs `break-glass-open`, and the link goes to the operator who asked, `HandOverTo`), then Ending at its time or when ended early (`break-glass-close`), then Ended with the review: every action of the account, collected once from the job's handover (`IJobHandovers.CollectAsync`) and kept on the record with `review_actions`. A request nobody approved within 30 minutes becomes Expired. A job that fails leaves Failed and the reason. `BreakGlassWorker` in the fleet host moves them every minute. Each change is a `tenancy.break_glass_*` audit event; the unit writes its own, at high severity, with both operators, the ticket and the reason.
- **Platform documents** are versions numbered per key; each publish writes `tenancy.platform_document_published` and the current version of both to the store at `platform/documents`, which every unit's policy may read (the fleet's secrets step) and each unit keeps a minute.
- `make impersonate`, `make break-glass-request`, `make break-glass-approve`, `make break-glass-link`, `make break-glass-end`, `make break-glass-show` and `make platform-document` are the calls from the laptop (the runbook). The control plane's seed makes a second operator, `operator-two`, so a request can be approved there.

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
## Audit chains (TF-061, ADR-0050)

The control plane keeps every tenant's audit anchors and the fleet's verification records, outside the tenant's database. Both tables are append-only: the app role may insert and select only, and no endpoint changes a row.

| Operation | Endpoint | Permission | Refused when |
|---|---|---|---|
| A unit anchors its chain | `POST /api/v1/audit-anchors` with `kind` (`seal` or `pruned`), `position`, `hash`, `at` | `tenancy.audit_anchors.append`, held only by the `unit-client` role of a unit's own client | a caller that is not a unit's client (`tenancy.audit_anchor.not_a_unit`); an unknown kind; another hash for an anchored position (`tenancy.audit_anchor.conflict`, 409, audited at high severity and counted in `audit.anchor_conflicts`). The same anchor again answers it, 200. |
| Where a unit resumes | `GET /api/v1/audit-anchors/latest` | the same | |
| A unit's latest verification | `GET /api/v1/audit-verifications/latest` | the same | |
| A tenant's anchors, verifications | `GET /api/v1/tenants/{slug}/audit-anchors`, `GET /api/v1/tenants/{slug}/audit-verifications` (list grammar, newest first) | `tenancy.tenants.read` | |
| Verify now | `POST /api/v1/tenants/{slug}/audit-verifications` with `full` | `tenancy.audit_chains.verify` (High) | |
| The request's record | `GET /api/v1/tenants/{slug}/audit-verifications/requests/{id}` | `tenancy.tenants.read` | |
| Accept a restore point | `POST /api/v1/tenants/{slug}/audit-restores` with `ticket` and `reason` | `tenancy.audit_restores.accept` (Critical), with a fresh token | no ticket or reason (`tenancy.audit_restore.invalid`); the latest verification names no restore point, or it was accepted already (`tenancy.audit_restore.none`, 409); no anchor counts at the point (`tenancy.audit_restore.no_anchor`, 409: verify in full first) |
| The accepted restores | `GET /api/v1/tenants/{slug}/audit-restores` | `tenancy.tenants.read` | |

- **Which client is which unit** is `kernel_tenancy.unit_clients`, written by the fleet's secrets step through `ITenantAuditChains`; the caller's tenant is never taken from the request.
- **Segments** (`kernel_tenancy.audit_restores`): an accepted restore starts the next segment of the chain from the position after its restore point. An anchor is kept in the segment that covers its position when it arrives (`audit_anchors.segment`, unique with tenant, kind and position), so a position the restore lost keeps its old anchor as a record. The unit's `latest` answer gives the current segment, its latest seal anchor (or the restore point while it has none) and every accepted restore; the rules are `AuditChainSegments` in Audit's Contracts, shared with the fleet's check.
- **What is due**: each active tenant once a day, in full once every 30 days, after a failure, and at once when an operator asks; the fleet asks every minute (`ITenantAuditChains.DueAsync`).
- **A record that did not pass** raises the alert: an error line starting `ALERT:`, the counter `audit.verification_failures` (meter `Iron.AuditChains`), and `tenancy.audit_chain_broken` (or `tenancy.audit_chain_restored`) at high severity in the control plane's trail.
- `make verify-audit TENANT=x [FULL=1]` asks for a run and prints its record; `make audit-accept-restore TENANT=x TICKET=y REASON="..."` accepts a restore point and waits for the full verification it asks for; `make audit-chain TENANT=x` lists the latest records, accepted restores and anchors.

Configuration (`Tenancy` section): `BaseDomain` (`localhost`; a tenant without hostnames gets `<slug>.<BaseDomain>`), `DefaultRegion` (`local`), `DefaultPlan` (`standard`).

Model change: edit the entity, then `make ef-add MODULE=Tenancy NAME=<WhatChanged>` and commit `Data/Migrations`.

## The control plane's own role and chain (ADR-0053)

The control plane runs with `control_app`, which its migrations, run as `control_owner` with `Secrets__Path=control-migrate`, grant only what it needs: append-only on the audit tables, no schema change anywhere. The fleet's `prepare-control` makes the roles and the secrets (Fleet's README). The control plane's own chain is sealed in its API and anchored in object storage it may only add to; the fleet verifies it daily with `control_audit` and records the result in `audit_verifications` under `control`, so `make audit-chain TENANT=control` lists it beside a tenant's, and a run that does not pass raises the same alert. The administrator recovery (`POST /api/v1/tenants/{slug}/administrator-recovery`) is under the recovery limit per tenant (TF-072, ADR-0053).

## Keys and their rotation (TF-073, TF-075, ADR-0054)

`KeyRotationService` (`ITenantKeys` for the fleet) keeps each tenant's keys and database password (`tenant_keys`: versions, last rotation, days between, from which the next rotation follows) and every rotation (`key_rotations`: who asked, why, the versions, the unit's progress, finished and retired). `POST /api/v1/tenants/{slug}/keys/{key}/rotations` (`tenancy.keys.manage`, critical, the key officer's; a fresh token; a reason) asks for one, one per key at a time; `GET /api/v1/tenants/{slug}/keys` and `/keys/rotations/{id}` read them. A unit's own client (`tenancy.keys.report`, for its own tenant only) reads its keys and rotations (`GET /api/v1/key-rotations`), reports its re-encryption (`POST /api/v1/key-rotations/{id}/progress`) and sets its policy for its data and index keys (`PUT /api/v1/key-rotations/policy`), kept within the operator's bounds (`Keys:Rotation`: `DataDaysMinimum` 30, `DataDaysMaximum` 730, `DataDaysDefault` 365, `SigningDays` 365, `DatabaseDays` 90, `SigningOverlapMinutes` 30). Audit events: `tenancy.key_rotation_requested`, `key_rotated`, `key_retired`, `key_rotation_failed`, each with who asked and the reason.

## The units' security alerts and incidents (TF-079, ADR-0056)

A unit sends each of its security alerts after every change to `PUT /api/v1/alert-copies/{id}`
(`tenancy.alert_copies.append`, the unit-client role's); the tenant is the client's binding, and the copy goes to the
Monitoring module's store (`IAlertCopies`), from which the control plane's alerting forwards it.

Incidents (`incidents`, `incident_tenants`, `incident_entries`, the last written once), by the runbook
`docs/security/incident-response.md`:

| Operation | Endpoint | Permission | Refused when |
|---|---|---|---|
| Declare | `POST /api/v1/incidents` | `tenancy.incidents.manage` | no summary or lead, a severity outside 1 to 4, a tenant that does not exist, a breach known in the future or more than 30 days ago |
| Note, communication, notice | `POST /api/v1/incidents/{id}/entries` | `tenancy.incidents.manage` | a notice to a tenant whose personal data is not affected, or that the incident does not name |
| A tenant affected | `POST /api/v1/incidents/{id}/tenants` | `tenancy.incidents.manage` | as for declaring |
| Close | `POST /api/v1/incidents/{id}/close` | `tenancy.incidents.manage` | a tenant whose personal data was affected is not told |
| Review | `POST /api/v1/incidents/{id}/review` | `tenancy.incidents.manage` | the incident is open, or its review is recorded |
| Read | `GET /api/v1/incidents`, `GET /api/v1/incidents/{id}` | `tenancy.incidents.read` | |

Each tenant whose personal data is affected has a notice due 72 hours after it became known; the review is due 10 days
after the close. `IncidentEscalation`, every five minutes in the control plane (`AddIncidentEscalation`), escalates each
once when it is due and missing: `tenancy.incident_notice_overdue` and `tenancy.incident_review_overdue` at high
severity, which the control plane's rules raise to the operator's alerting.
