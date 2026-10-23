# Iron.Kernel.Changes

Changes of a tenant's sensitive configuration that wait for a second administrator (TF-068): the four-eyes setting, the pending changes with their state before and after, the decisions, and the expiry. Decisions: ADR-0052. Schema `kernel_changes`.

Placement (ADR-0029): no business meaning, so Kernel. It is the module TF-247 names for governed changes (the technical PRD's section 2.1); T8 builds its first form, a maker and a checker on configuration, and S5 extends it to records with field classes and the workflow engine's routing.

## How a save becomes a pending change

The kernel vocabulary declares the question every sensitive save asks, `IFourEyes` (`Iron.Kernel`, beside `IRecentAuthentication`), with a default that never holds anything; this module answers it. A module's service checks its own rules first, as for a direct save (validation, escalation, conflict rules, step-up), then asks `AppliesAsync(area)`. When the tenant lists the area, the service hands over a `ChangeProposal`: the area, the kind, the subject (one pending change per subject, kept by a partial unique index), a title, the state before and after as people read them, the payload it needs to apply later, and the permission an approver must hold at the same scope. `HoldAsync` stores it, writes `changes.change_requested`, and answers an error of the kind `Held`, which the API turns into **202** with `error_code` `changes.change.held` and the change's id. Nothing is written yet.

| Area | Code | Where the gate is | Approver needs |
|---|---|---|---|
| Roles and assignments | `roles` | `RoleService` (create, change, delete, import), `AssignmentService` (grant, remove) | `authz.roles.manage` tenant-wide; `authz.assignments.manage` at the assignment's unit |
| Single sign-on | `single_sign_on` | `SsoProviderService` (add, change, remove) | `identity.sso.manage` |
| Retention | `retention` | `RetentionPolicy.ChooseAsync` | `audit.retention.manage` |
| MFA and password policies | `mfa_and_password_policies` | `SettingsWriter`, for the settings declared `.GovernedBy(...)` (`identity.password.*`, `identity.lockout.*`, `identity.mfa.*`) | `settings.values.manage` |
| Legal holds | `legal_holds` | `LegalHolds` (place, release) | `audit.legal_holds.manage` |
| Signing policy | `signing_policy` | `SettingsWriter` (`compliance.signing.*`) | `settings.values.manage` |
| The four-eyes setting itself | `four_eyes` | `SettingsWriter` (`changes.four_eyes.*`), whenever any area is listed | `settings.values.manage` |

Entitlements are the operator's and not in the list. An access review's revoke is not held: it is a decision of the review, with its own reviewer and record (ADR-0037). A single sign-on change's new client secret waits in the store under a staging id of its own, never in the row; it moves to the provider when the change applies and leaves the store when the change ends otherwise.

## Deciding

`PendingChanges` decides. Another person, holding the change's permission at its place, approves or rejects (a rejection needs a reason), after a confirmation that it is them (step-up, TF-248), or with an electronic signature meaning `approved` or `rejected` when the tenant lists the area in `changes.four_eyes.signed_areas` or its signing policy requires one for `configuration_change` (TF-063). Refused, and audited as `changes.approval_refused` at high severity before any credential is looked at: the requester (`changes.change.own`, TF-068 AC2), anyone signed in as someone else, acting for someone, a service account, or an operator's break-glass account.

On approval, the module that proposed the change answers its subject's state now (`IConfigurationChangeApplier.CurrentAsync`). When it differs from the change's before, something else changed it meanwhile: nothing applies, the change takes the current state as its before, counts one more review, keeps a note of both values, writes `changes.change_restarted`, and waits for a new decision. Otherwise the module applies it (`ApplyAsync`) through the same path a direct save takes, as the approver, with the approver's own rules, without asking for a second administrator again; the decision, the module's own events and field history, and `changes.change_approved`, which names both people, commit in one transaction (TF-068 AC3). A module that refuses (the approver lacks a permission the role holds, a conflict rule blocks) leaves the change pending.

The requester withdraws their own change. A change nobody decides within `changes.four_eyes.expiry_days` (7) expires: the worker's pass every five minutes, and every list and decision, end it with `changes.change_expired`.

## Settings

| Key | Default | Meaning |
|---|---|---|
| `changes.four_eyes.areas` | `[]` | The areas whose changes wait for a second administrator |
| `changes.four_eyes.signed_areas` | `[]` | The areas whose decisions are electronic signatures |
| `changes.four_eyes.expiry_days` | 7 | How many days a change waits before it expires (1 to 30) |

Each needs a recent authentication to change, and while any area is listed a change of them waits for a second administrator too.

## Permissions

| Permission | Risk | What it allows |
|---|---|---|
| `changes.changes.read` | Medium | See every pending and decided change; without it a person sees their own and the ones they may decide |

## Endpoints

| Method and path | Needs | What it does |
|---|---|---|
| `GET /api/v1/changes?view=mine\|to_approve\|all&status=` | signed in (`all`: `changes.changes.read`) | Changes, newest first, in the list envelope |
| `GET /api/v1/changes/{id}` | the requester, an approver, or `changes.changes.read` | One change with its before and after |
| `POST /api/v1/changes/{id}/approve` | the change's permission at its place | Approve; it applies at once unless its subject changed |
| `POST /api/v1/changes/{id}/reject` | the change's permission at its place | Reject with a reason |
| `POST /api/v1/changes/{id}/withdraw` | the requester | Take it back |

Both decisions take `{ "reason": "...", "signature": { ... } }`, the signature's credentials as the signing dialog sends them.

## Model change

Edit the entity, then `make ef-add MODULE=Changes NAME=<WhatChanged>` and `make format`, and commit `Data/Migrations` with it.
