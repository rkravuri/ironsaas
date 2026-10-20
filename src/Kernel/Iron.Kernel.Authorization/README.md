# Iron.Kernel.Authorization

Kernel module (ADR-0029). Schema `kernel_authz`. Permissions, roles, scoped role assignments and the organisation tree (TF-030 to TF-034; ADR-0012, ADR-0030), delegations of duties and acting for the delegator (TF-037), access reviews (TF-038; ADR-0037), the explanation of a person's access (TF-039) and segregation of duties (TF-036; ADR-0039).

| Folder | Holds |
|---|---|
| `Contracts/` | `AuthorizationFeatures` (`authorization.role_transfer`, role export and import, which a plan licenses: ADR-0034), `AuthorizationPermissions` (`authz.roles.read`, `authz.roles.manage`, `authz.assignments.read`, `authz.assignments.manage`, `authz.units.read`, `authz.units.manage`, `authz.delegations.manage`, `authz.reviews.manage`, `authz.reviews.decide`, `authz.access.explain`, `authz.sod.read`, `authz.sod.manage`), `SystemRoles`, `AuthorizationErrors`, `AuthorizationAuditEvents`; `IDelegations` with `ActiveDelegation` and `DelegationStatus` (the active delegations, for Identity's session and `/me`); the extension point `IReviewEscalation` with `OverdueReview`, `ReviewStatus` and `ReviewDecision`; `IRoleAssignments` (the trusted path for the seed and other modules: assign for a `RoleScope`, permissions at an `AccessScope`), `IOrganisationUnits` (the root and a unit, for Identity), `IPrincipals` with `PrincipalKind` and `PrincipalSummary` (a principal's kind, name, home unit and status, and who is homed at a set of units; implemented by Identity), and the extension point `IOrganisationKinds` with `OrganisationKind` |
| `Data/` | `AuthorizationDbContext`, `Role` (code, name, description, principal kind, permissions as a text array), `RoleAssignment` (scope kind, scope id, record type), `OrganisationUnit` (name, code, kind code, parent, path, depth, status), `Delegation` (delegator, delegate, duties, period, reason, an early end), `AccessReview` with `AccessReviewUnit`, `AccessReviewItem`, `AccessReviewDecision` and `AccessReviewEscalation` (the last four append-only), `ConflictRule`, `ConflictReportRun` (append-only) and `ConflictReportEntry`, the design-time factory, generated `Migrations/` |
| `Services/` | `AccessGrants` (a principal's assignments, cached for 60 seconds, answered per scope and per list), `PermissionEvaluator` (fail closed, denial audit with the scope, `authz.denials` metric), `RoleAssignments`, `RoleService` (tenant roles, export and import), `AssignmentService` (grants and removals through the API, under ADR-0031), `RecordAccessService` (implements `IRecordAccess` of Iron.Kernel.Data, the scope rule that record policies apply first, ADR-0032), `OrganisationUnitService` (the tree and its writes), `OrganisationUnitCheck` (the data layer's owning-unit check), `DefaultOrganisationKinds`, `AuthorizationSeeder` (system roles, the root unit), `PermissionCacheOptions`, `ActingScope` (whom the request's principal acts for, and the duties' permissions), `DelegationService` and `DelegationQueries`, `ReviewService`, `ReviewEscalationPass` with `ReviewEscalationWorker` and the default `CampaignOwnerEscalation`, `AccessExplainer` (the checks of an endpoint, in order, for a person), `ConflictDetector` (the rule of overlapping scopes, pure), `ConflictService` (the rules, the checks on grants and delegations, the report) with `ConflictReportWorker` |
| `Api/` | the roles, the permission catalogue and the role assignments (below), the organisation tree, the duties and delegations, the access reviews, the explanation, and the conflict rules with their report |

## The organisation tree (TF-034, ADR-0030)

A kernel mechanism with no business meaning: units with a name, a code unique in the tenant, a kind code, a parent, a status, and a materialised path of ids from the root (`/<root>/<parent>/<unit>/`, at most 16 levels below the root). The seed creates the root once, with the provider's root kind, named from `Seed:RootUnit:Name` and coded from `Seed:RootUnit:Code` (the fleet passes the tenant's display name and slug; without them the tenant identifier serves).

The kinds come from `IOrganisationKinds`. The kernel default offers `root` (it may hold `unit`) and `unit` (it may hold `unit`). A business module replaces it with its own kinds and their business attributes, which live in the business module, never on the unit: `services.Replace(ServiceDescriptor.Singleton<IOrganisationKinds, MyKinds>())` (ADR-0029).

| Operation | Endpoint | Permission, checked at | Refused when |
|---|---|---|---|
| The tree the reader may see, depth first, siblings by name | `GET /api/v1/organisation-units` | `authz.units.read`, any scope; the list keeps the units where the reader holds it | |
| One unit | `GET /api/v1/organisation-units/{id}` | `authz.units.read`, the unit | |
| The kinds and what each may hold | `GET /api/v1/organisation-kinds` | `authz.units.read`, any scope | |
| Create a unit under this one | `POST /api/v1/organisation-units/{id}/children` with `name`, `code`, `kind` | `authz.units.manage`, the parent | the parent is inactive; the kind is unknown or the parent's kind may not hold it; the code is taken (409); deeper than 16 |
| Rename | `POST /api/v1/organisation-units/{id}/rename` with `name` | `authz.units.manage`, the unit | |
| Move with its subtree | `POST /api/v1/organisation-units/{id}/move` with `parent_id` | `authz.units.manage`, the unit and the new parent | the root; a cycle (under itself or a descendant); an inactive parent; a kind the parent may not hold; the subtree would pass 16 levels |
| Deactivate | `POST /api/v1/organisation-units/{id}/deactivate` | `authz.units.manage`, the unit | the root; any active unit below it (409, TF-034 AC2) |
| Reactivate | `POST /api/v1/organisation-units/{id}/reactivate` | `authz.units.manage`, the unit | a deactivated parent (`authz.unit.parent_inactive`, 409); a merged unit (`authz.unit.merged`, 409) |
| What the modules need of the unit | `GET /api/v1/organisation-units/{id}/readiness` | `authz.units.read`, the unit | |
| Preview a merge into another unit | `GET /api/v1/organisation-units/{id}/merge-preview?into=<id>` | `authz.units.manage`, both units | as the merge |
| Merge into another unit | `POST /api/v1/organisation-units/{id}/merge` with `into_id` | `authz.units.manage`, both units | the root; into itself or a unit below it (`authz.unit.merge_into_own`); an inactive unit; a child kind the other may not hold; too deep; merged already |

Every write takes the tree's transaction-scoped advisory lock and writes its audit event in the same transaction: `authz.unit_created`, `authz.unit_renamed`, `authz.unit_moved`, `authz.unit_deactivated`, `authz.unit_reactivated`, `authz.unit_merged` (record type `organisation_unit`). A move changes the unit's row and rewrites its descendants' paths in one statement. A deactivated unit stays in the tree; no unit, record or role assignment can be placed in it.

## A unit's readiness and merging units (TF-223, ADR-0038)

Readiness: a module that needs something of each unit (a numbering series, a ledger, a default warehouse) registers `IUnitReadiness` from this module's Contracts, `services.TryAddEnumerable(ServiceDescriptor.Scoped<IUnitReadiness, MyChecklist>())`, and answers its items in words, each done or not. The kernel registers none, so a unit is ready until a product asks for something.

Merging B into A: `UnitMergeService` previews and merges. It asks every `IUnitRecordOwners` (the data library's, which a module with business records registers with `AddUnitRecordOwners<TContext>(module)`) for B's records, Identity and Settings through `IUnitMergeParticipant` (the people homed at B; B's settings, A's own values winning), the data library for the shares that list B, and moves the assignments scoped to B (dropping one the person already holds at A) and B's children itself, each child as an audited move. One transaction under the tree's lock; one `authz.unit_merged` lists every id moved and what was dropped; B ends inactive with `merged_into_id`. A move keeps a unit's scoped roles and its settings, which are keyed by its id.

## The working unit (TF-222, ADR-0038)

`IWorkingUnits` answers the units a person may work for: their home unit and the units their own assignments name, active ones, in tree order with their paths, and whether "All my units" is open to them (more than one unit, and a role at a parent with children below it or at the tenant). Identity keeps the choice in the session. `IUnitReach` (the data library's) answers the reader's home unit and the units below it, outside which a read is audited, and the working unit and the units below it, which lists keep to; it answers no narrowing for a token, the consolidated view, while acting, or for another principal.

## Sharing a record (TF-222, ADR-0038)

`GET` and `PUT /api/v1/record-sharing/{type}/{id}` read and change the sharing of any business record with a policy, through the data library's `IRecordSharing`. Each needs a signed-in principal; the record's policy decides the rest (reading needs the record readable, changing is the action `share`). The answer names the owning unit and the listed units, says whether the reader may change it, and to one who may, lists the tenant's active units to choose from.

## Scopes (TF-033, ADR-0030)

An assignment applies at a `RoleScope`: the tenant, a unit, a unit with its children, or one record (type and id). Access to a unit other than one's home unit is such an assignment. A check happens at an `AccessScope` (in the kernel vocabulary): the tenant, a unit, or a record with the unit that owns it. Tenant-wide grants apply everywhere; a unit grant to its unit's records; a grant with children to the whole subtree, whatever moves later; a record grant to its record. An assignment to a unit that does not exist or is inactive is refused (TF-033 AC4), and a new assignment writes `authz.role_assigned`.

A record or unit check reads the unit's path by its key and takes the ancestors from it. A list check, `IRecordAccess.AccessibleAsync(principal, permission, recordType)` (declared in Iron.Kernel.Data, implemented here), answers everything (a tenant-wide grant) or the units and records the permission reaches, in one query whatever the list's length. A business record's list and reads by id apply it through its record policy (`IRecordPolicies`, ADR-0032); other rows apply it with `WhereAccessible`. Identity's `/me` lists the tenant-wide permissions and `permissions_anywhere`, and `/me/access` the permissions at one unit or record, for the shell (ADR-0031).

## Roles (TF-032, ADR-0031)

A role has a code (lowercase letters, digits and hyphens; it identifies the role in assignments, export and import and never changes), a name for people, a description, the kind of principal it is for (people or service accounts; only the `tenant-administrator` system role is for both) and permission names from the catalogue. System roles are seeded from the catalogue and read-only. A role holds only permissions its editor holds tenant-wide (rule 2).

| Operation | Endpoint | Permission, checked at | Refused when |
|---|---|---|---|
| List, one role | `GET /api/v1/roles`, `GET /api/v1/roles/{id}` | `authz.roles.read`, any scope | |
| The catalogue for the editor | `GET /api/v1/permissions` (name, module, description, risk) | `authz.roles.read`, any scope | |
| Create | `POST /api/v1/roles` with `code`, `name`, `description`, `principal_type`, `permissions` | `authz.roles.manage`, tenant | the code is taken (409); a permission is unknown (400); the editor lacks one tenant-wide (`authz.role.escalation`, 403) |
| Change | `PUT /api/v1/roles/{id}` with `name`, `description`, `principal_type`, `permissions` | `authz.roles.manage`, tenant | a system role (`authz.role.system`, 409); the principal type of an assigned role; as for create |
| Delete | `DELETE /api/v1/roles/{id}` | `authz.roles.manage`, tenant | a system role; a role still assigned (`authz.role.in_use`, 409) |
| Export | `GET /api/v1/roles/export` (`format_version` 1 and the tenant roles) | `authz.roles.read`, any scope; the feature `authorization.role_transfer` | the tenant's plan lacks the feature (`entitlements.feature_disabled`, 403, ADR-0034) |
| Import | `POST /api/v1/roles/import` with `dry_run` and `document` | `authz.roles.manage`, tenant; the feature `authorization.role_transfer`; the kill switch `kill.imports` off | the kill switch on (`flags.killed`, 503, ADR-0035); the feature, as for export; an apply with unknown permissions (`authz.import.unknown_permissions`); a document of another format; an escalation |

Each change writes `authz.role_created`, `authz.role_changed` or `authz.role_deleted` with `permissions_before` and `permissions_after` in the same transaction, and clears the cached grants of the role's holders. The dry run answers the diff: created, changed (permissions added and removed, fields), unchanged, unknown permissions and the system role codes it skipped; an apply writes one event per role with the reason `import`.

## Assignments (TF-033, ADR-0031)

| Operation | Endpoint | Permission, checked at |
|---|---|---|
| List, for a principal or a unit | `GET /api/v1/role-assignments?filter=principal_id:eq:<id>` or `filter=unit_id:eq:<id>` | `authz.assignments.read`, any scope; each row where the reader holds it at the assignment's unit (a tenant or record assignment needs a tenant-wide reader) |
| One | `GET /api/v1/role-assignments/{id}` | as the list; outside, 404 |
| My own | `GET /api/v1/role-assignments/mine` | any signed-in principal: it answers only the reader's own assignments, for My profile (ADR-0033) |
| Grant | `POST /api/v1/role-assignments` with `principal_id`, `role_id`, `scope_kind` (`tenant`, `unit`, `unit_with_children`, `record`) and `unit_id` or `record_type` and `record_id` | `authz.assignments.manage` over the whole scope, checked by the service |
| Remove | `DELETE /api/v1/role-assignments/{id}` | `authz.assignments.manage` over the assignment's scope |

The rules, each with its error code: a grant needs every permission of the role over the scope (`authz.assignment.escalation`, 403; rule 1); the principal must exist (`authz.principal.not_found`) and be of the role's kind (`authz.assignment.principal_type`); the last tenant-wide `tenant-administrator` assignment stays (`authz.assignment.last_administrator`, 409; rule 3, behind an advisory lock). A grant or removal writes `authz.role_assigned` or `authz.role_unassigned` in the same transaction and clears the principal's cached grants on this node at once; a refusal is audited as a denial with what was missing. Granting what is granted already answers the existing assignment.

## Delegations and acting for the delegator (TF-037, ADR-0037)

A module declares its duties in `IModule.Duties` (a code, a name in words, a description, the permissions it covers); the kernel declares `all`. A delegation hands duties for a period with a reason; the delegate acts for the delegator by choosing it, and their requests then carry the delegator's rights for those duties only, at the delegator's scopes: `AccessGrants` answers the delegator's grants, limited to the duties' permissions, for the acting principal (`ActingScope`). Identity keeps the choice on the session and clears it on the first request after the delegation ends.

| Operation | Endpoint | Permission, checked at |
|---|---|---|
| The duties, with their permissions | `GET /api/v1/duties` | any signed-in principal |
| Your delegations, both ways | `GET /api/v1/delegations/mine` | any signed-in principal |
| Others' delegations, a paged list | `GET /api/v1/delegations` | `authz.delegations.manage`; every row tenant-wide, otherwise the delegations of the people homed at units where the reader holds it |
| Delegate | `POST /api/v1/delegations` with `delegate_id`, `duties`, `starts_at`, `ends_at`, `reason`, and `delegator_id` for someone else | your own: none; someone else's: `authz.delegations.manage` at their home unit |
| End early | `POST /api/v1/delegations/{id}/end` with an optional `reason` | the delegator, the delegate, or `authz.delegations.manage` at the delegator's home unit |

Refused, each with its code under `authz.delegation.`: `self`, `service_account` (either side), `overlap` (the same delegator and a shared duty in overlapping periods; `all` shares every duty), `cycle` (of any length, among delegations overlapping the period), `onward` (while acting, or a duty the delegator holds by an overlapping delegation to them), `period` (ended, or ends before it starts), `duty_unknown`, `not_found`, `not_running` (ending one that has ended). Creation takes an advisory lock. `authz.delegation_created` and `authz.delegation_ended` are audited with both people and the duties.

## Access reviews (TF-038, ADR-0037)

A campaign covers units, each with or without the units below it, and has a due date. Starting it writes its items: one per unit assignment in those units (and tenant-wide assignment, at the root, when the root is covered), with the principal, the role, the scope and the unit's path. A reviewer is whoever holds `authz.reviews.decide` at an item's unit.

| Operation | Endpoint | Permission, checked at |
|---|---|---|
| The campaigns, a paged list with progress | `GET /api/v1/access-reviews` | managers (`authz.reviews.manage` tenant-wide) see all; reviewers those with items at their units; anyone else 403, audited |
| One campaign, with progress per unit and escalations | `GET /api/v1/access-reviews/{id}` | as the list |
| Start | `POST /api/v1/access-reviews` with `name`, `due_on`, `units` (`unit_id`, `include_children`) | `authz.reviews.manage`, tenant |
| The items to decide, a paged list; `state=open` or `decided` narrows it | `GET /api/v1/access-reviews/{id}/items` | the rows at units where the reader holds `authz.reviews.decide`, and for the campaign's owner the rows nobody but their own person may review; anyone else 403, audited. Each row says `yours` and `with_owner` |
| Decide one | `POST /api/v1/access-reviews/{id}/items/{itemId}/decision` with `decision` (`confirm`, `revoke`) and an optional `comment` | `authz.reviews.decide` at the item's unit, or the owner for a row nobody else may review; never a row about the reader's own access |
| Confirm a page | `POST /api/v1/access-reviews/{id}/confirm` with `item_ids` (at most 200) and an optional `comment` | as one decision, for every item |
| Close | `POST /api/v1/access-reviews/{id}/close` | `authz.reviews.manage`, tenant |

A decision is written once (`authz.review.item_decided` for a second). A revoke removes the assignment through `AssignmentService`'s removal path in the same transaction, without asking `authz.assignments.manage`, and the last tenant administrator stays (`authz.assignment.last_administrator`, 409, nothing recorded). Nobody decides their own access (`authz.review.own_item`, 403): such an item stays with the other reviewers of its unit and of the units above it (`UnitReviewers`), and goes to the campaign's owner when there is none. Other codes under `authz.review.`: `not_found`, `closed`, `units_required`, `due_passed`, `item_not_found`. Events: `authz.review_started`, `authz.review_decided` (one per item), `authz.review_closed`, `authz.review_escalated`; a revoke also writes `authz.role_unassigned` with the campaign's id.

Overdue reviewers: the unit's worker host calls `AddAccessReviewEscalation()`, which runs `ReviewEscalationPass` a minute after it starts and then daily. Each unit of an open campaign 7 days past its due date with items open is escalated once, to the targets `IReviewEscalation` names: the kernel's default names the campaign's owner; a business module replaces it with `services.Replace(ServiceDescriptor.Scoped<IReviewEscalation, MyEscalation>())`. Sending the message is S4.

Configuration: `Authorization:CacheSeconds` (default and maximum 60; 0 reads the database on every check); `Seed:RootUnit:Name` and `Seed:RootUnit:Code`.

Model change: edit the entity, then `make ef-add MODULE=Authorization NAME=<WhatChanged>`, `make format`, and commit `Data/Migrations`. The test `ModelTests` fails until the migration exists.

## Explaining access (TF-039, ADR-0039)

Why a person can or cannot perform an action, at a unit or on a record. An action is one of the API's named endpoints that declares a permission or a signed-in principal; the explanation reads its metadata and calls the same decisions a request does (`DecideAsync` of the entitlement and flag gates, `IPermissionEvaluator.ExplainAsync`, `IRecordPolicies.ExplainAsync`), for the explained person, so it cannot disagree with the real call and writes no denial of theirs.

| Operation | Endpoint | Permission, checked at |
|---|---|---|
| The actions, by module, with the route values each needs | `GET /api/v1/access/actions` | `authz.access.explain`, any scope |
| Explain | `GET /api/v1/access/explain?principal=<id>&action=<name>`, with `acting_for=<id>` and each route value the action needs (`id=<unit or record id>`) | `authz.access.explain` at the explained person's home unit |

The answer is the person, the action, whom they act for, `allowed`, the `status` the real call would answer, and the checks in order: `principal`, `entitlement`, `flags`, `permission`, `record_policy`. Each has a `result` (`passed`, `failed`, `not_reached`, `not_applicable`), a `reason`, the `status` and `error_code` of a refusal, `facts` (the permission and its words, the scope, the unit or record, the flag, the delegator), and for an allowed permission `granted_by`: each assignment that grants it, with its role and scope. The first failure decides and the checks after it are not reached. Reasons: the principal `active`, `invited`, `locked`, `disabled` (401); the entitlement `entitled` or the decision's own (403); the flags `no_flags`, `flags_allow`, `killed` (503), `flag_off` (404); the permission `signed_in`, `granted`, `not_granted`, `no_active_delegation`, `unavailable` (403); the record policy `no_record`, `allowed`, `forbidden` (403), `not_visible`, `no_such_record` (404), `no_policy`.

Refused: a person not in this tenant (`authz.principal.not_found`, 404), which a person of another tenant is; an unknown action (`authz.explain.action_unknown`, 404); a missing `principal` or `action` (400). Every explanation writes `authz.access_explained` with the person, the action, the outcome and status, the failing check and its reason, and the route values. Checks a handler makes on its own, after the endpoint's (a move's new parent, a merge's target, the no-escalation rule of a grant), are not explained.

## Conflict rules and their report (TF-036, ADR-0039)

A rule names two sides that one person should not hold over the same unit, each a permission (by name) or a role (by id), whether it blocks or warns, why, and its owner. The kernel ships no rules.

| Operation | Endpoint | Permission |
|---|---|---|
| The rules, a paged list with each side's words | `GET /api/v1/conflict-rules` | `authz.sod.read` |
| Add, change, remove | `POST /api/v1/conflict-rules`, `PUT` and `DELETE /api/v1/conflict-rules/{id}`, with `name`, `first` and `second` (`kind`, `value`), `mode` (`block`, `warn`), `reason`, `owner_id` | `authz.sod.manage` |
| The conflicts, a paged list | `GET /api/v1/conflict-report` | `authz.sod.read` |
| The last full run | `GET /api/v1/conflict-report/runs/latest` | `authz.sod.read` |
| Run the report now | `POST /api/v1/conflict-report/runs` | `authz.sod.manage` |
| What a grant would meet, before it is made | `POST /api/v1/role-assignments/conflicts` with the grant's body | `authz.assignments.manage`, as for granting |

A grant holds a permission side when its role gives the permission, and a role side when it is that role with all its permissions. A person holds both sides when some unit lies within the scope of both grants: a tenant grant reaches every unit, a unit grant its unit, a grant with children its subtree, a record grant only its record (so it meets a tenant grant or a grant on the same record). Rights at unrelated units are not a conflict.

A grant (through the API or `IRoleAssignments`) that would complete a blocking rule is refused with `authz.sod.conflict` (409) and the rule named in the message; a warning rule lets it through. A delegation is checked with the delegate's own grants and the delegator's grants limited to the duties' permissions, held together, the same way. Rule errors: `authz.sod.rule_not_found`, `authz.sod.rule_invalid`, `authz.sod.side_unknown`, `authz.sod.sides_same`. Events: `authz.sod_rule_created`, `authz.sod_rule_changed`, `authz.sod_rule_deleted`, `authz.sod_report_run`.

The report: the unit's worker host calls `AddConflictReport()`, which computes it in full two minutes after it starts and then daily, replacing the rows; `POST .../runs` does the same at once. A grant, a removal and a delegation's start or end compute that person's rows at once. Each row names the person, the rule and its mode, the two grants (an assignment or a delegation, with role, scope and unit) and the unit both reach. An access review item lists the rules its assignment takes part in (`conflicts`).

