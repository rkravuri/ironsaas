# ADR-0031: Nobody grants more than they hold

**Status:** Accepted
**Date:** 2026-10-19
**Decided by:** Product owner and architect

## Context

A1 gave assignments a scope (ADR-0030). A2 lets tenant administrators use it: tenant roles (TF-032), assignments at a scope (TF-033), export and import between tenants. Once people other than the first administrator can grant access, a tenant needs a rule that keeps a grant from creating more power than its granter has: otherwise a unit administrator could give a colleague the tenant administrator role, or write a role with permissions nobody in the room holds and assign it to themselves. A tenant also must never lose its last administrator, because no one could then repair access without an operator.

Segregation of duties (TF-036, A9) and delegation (TF-037, A7) are separate rules and come later.

## Decision

1. **A grant at a scope needs every permission of the role at that scope.** The granter holds `authz.assignments.manage` at the scope, and every permission of the role there. What counts as "at the scope" depends on the scope:
   - the tenant: the granter's tenant-wide permissions;
   - one unit: the permissions the granter holds at that unit (tenant-wide, assigned to the unit, or assigned with children to the unit or an ancestor);
   - a unit with its children: only the grants that cover the whole subtree (tenant-wide, or with children at the unit or an ancestor), because an assignment to the unit alone does not reach its children;
   - one record: the granter's permissions at that record.
   A refusal answers `authz.assignment.escalation` (403) and names the permissions the granter lacks. Removing an assignment needs `authz.assignments.manage` at its scope by the same reading; removing never escalates.
2. **A tenant role is saved only with permissions its editor holds tenant-wide.** Creating or changing a role, and applying an import, is refused with `authz.role.escalation` (403) when the role would hold a permission its editor lacks tenant-wide. A role is a tenant-wide definition, so the editor's tenant-wide permissions are the measure.
3. **The last tenant administrator stays.** Removing the last tenant-wide assignment of the `tenant-administrator` system role is refused with `authz.assignment.last_administrator` (409).
4. **The seed is trusted.** `IRoleAssignments.AssignAsync` in Authorization's Contracts is the seed's and other modules' path and has no granter; the rules apply to grants made through the API, where a principal grants.

How A2 applies them, decided with the rules:

- **Roles have a code and a name**, as units do: the code (`tenant-administrator`, lowercase letters, digits and hyphens) identifies the role in assignments, export and import; the name is for people. The S1 column `name` held the code; it is renamed `code`, and `GET /api/v1/roles` answers both. A role is for one principal type, a user or a service account, and an assignment refuses a principal of the other type. The `tenant-administrator` system role is for any type, because the control plane's operator is a service account.
- **The rename is a migration changed once before its first commit.** `make ef-add` cannot tell a rename from a drop and an add: it kept the old column and added an empty `code`, which fails the unique index on a tenant that has roles. The generated `RoleCodeAndName` migration was changed to a rename before it was committed, as EF Core's documentation advises; the new `name` starts as the code until the seed names the system roles. The rule against editing a migration is about committed ones, and this one never was.
- **System roles are read-only** and answer `authz.role.system` (409) to a change or a deletion. A role still assigned is not deleted (`authz.role.in_use`, 409).
- **An import with unknown permissions is refused.** The dry run reports them with the diff; the apply refuses them (`authz.import.unknown_permissions`, 400). TF-032 AC3 said "reported and skipped"; refusing keeps a role from arriving with fewer permissions than its author meant without anyone deciding so. The PRD's AC3 was amended to match on 2026-10-19. System role codes in a document are reported and never imported.
- **Reading role definitions needs `authz.roles.read` at any scope**, not tenant-wide: a unit administrator must see the roles they may grant at their unit. Changing roles stays tenant-wide.
- **An endpoint that filters its own rows declares it** with `ScopedByRow()` (Iron.Kernel.Api): the permission filter lets through a principal who holds the permission at any scope, and the handler keeps the rows the principal may see (`IRecordAccess` and `WhereAccessible`), answering 404 for one outside, so a list and a link never reveal who or what exists. The users list and the user record use it; the organisation tree does too, so a unit's reader sees that unit's part of the tree.
- **Assignments are read with their own permission, `authz.assignments.read`** (low risk, in the read-only and service-default roles with every other read). A reader sees an assignment at a unit where they hold it; an assignment for the whole tenant or for a record needs a tenant-wide reader. So a unit administrator sees who holds what in their part of the tree and nothing above it. A grant to what is granted already answers the existing assignment and writes nothing.
- **The shell offers an action only where it will work.** `GET /api/v1/me/access?unit=<id>` answers the permissions at the unit; with `&include_children=true` it answers those held over the unit and every unit below it, by the same reading as rule 1, so the Access tab shows Remove on a "unit and below" assignment only to someone the API would let remove it.
- **A refusal is audited.** A grant or a removal without `authz.assignments.manage` at the scope writes `authz.permission_denied`; a grant refused by rule 1 writes `authz.role_assigned` with the outcome `denied` and the missing permissions. The last-administrator guard takes a transaction-scoped advisory lock, so two administrators removing each other at once cannot both succeed.
- **Authorization asks Identity through a contract that points down.** `IPrincipals` in Authorization's Contracts answers a principal's kind (`PrincipalKind`: a person or a service account), name and home unit; Identity implements it. Identity keeps depending on Authorization and never the other way round.

## Options considered

- A grant needs only `authz.assignments.manage` at the scope. Rejected: a unit administrator could grant the tenant administrator role at their unit, and with it permissions they do not hold.
- A role is saved with any permission of the catalogue and the check happens only at assignment. Rejected: a role is also exported and imported, and a role nobody could have assigned is a trap for the next editor.
- Count only active administrators for the last-administrator guard. Rejected for now: Authorization does not see a principal's status, and a locked last administrator is still the one to unlock; an operator path covers the rest.
- Skip unknown permissions on import, as TF-032 AC3 says. Rejected, as above; the dry run gives the same information before anything changes.

## Consequences

- Easier: a unit administrator can be given real power over their unit without the risk that it grows; every refusal names the rule and the missing permissions.
- Harder: a tenant administrator who lost a permission tenant-wide can no longer save roles that hold it; that is the point. The seed's path is trusted and must stay internal.
- The evaluator's 60-second cache stays (ADR-0012). A grant or a removal clears the affected principal's entry at once, and a role change clears its holders' entries, on the node that made the change.
- Revisit: with segregation of duties (A9) a grant also checks conflict rules; with delegation (A7) a delegate's grants measure the delegate, not the delegator.
