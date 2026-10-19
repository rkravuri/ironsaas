# ADR-0030: The organisation tree as a materialised path, and how a scope is evaluated

**Status:** Accepted
**Date:** 2026-10-19
**Decided by:** Product owner and architect

## Context

TF-034 asks for one tree of organisation units per tenant, used for role scope, settings overrides and data visibility; TF-033 asks that a role assigned to a unit (and optionally its children) applies to the records of that unit and not to others, and that an assignment follows its unit when the unit moves (TF-034 AC1). ADR-0029 makes the tree a kernel mechanism: units, parent, path, status and a kind code, with the kinds supplied through `IOrganisationKinds`. Two questions have to be answered with performance in mind, because they sit on every request and every list:

- **A record check:** may this principal use this permission on this record? The record carries its owning unit; the answer needs that unit's ancestors (an assignment to an ancestor with children applies).
- **A list check:** which records of a list may this principal see with this permission? The answer must be one query whatever the length of the list, never one per row.

A tenant's tree is small next to its records: tens to a few thousand units, a depth rarely above ten. Units are read on almost every request and written by administrators a few times a week; a move is rarer still.

## Decision

1. **A materialised path of ids.** `kernel_authz.organisation_units` holds `parent_id`, `depth` and `path`, the ids of the unit's ancestors and its own, root first, as 32 lowercase hex characters each between slashes: `/<root>/<parent>/<unit>/`. The root is the only unit without a parent; the seed creates it with the kind of the provider's root. A unit's subtree is every unit whose path starts with its path; a B-tree index with `text_pattern_ops` on `path` serves that prefix. The depth is limited to 16 below the root.
2. **A record check reads one row.** The evaluator reads the record's unit's path by its primary key and takes the ancestors from it; it matches them against the principal's assignments, which are cached per principal for at most 60 seconds as the tenant-wide permissions were (ADR-0012). No recursion, no join. Because the ancestors come from the current path and assignments name units by id, an assignment follows its unit when the unit moves, with no work at move time.
3. **A list check is one query.** `IRecordAccess.AccessibleAsync(principal, permission, recordType)` answers either "every unit" (a tenant-wide grant) or the set of units and records the permission reaches: the units named by unit assignments, and every unit whose path starts with the path of a unit named by a with-children assignment, in one query over `organisation_units`. The list applies the answer as one predicate (`owning_organisation_id = any(...)` or the record's id in the record grants) inside its own query. The cost does not depend on the length of the list.
4. **A move is two statements.** Moving a unit changes its own row (parent, path, depth) and rewrites the path prefix and the depth of all its descendants in one `UPDATE`, inside the unit of work that writes the audit event (ADR-0013). A move under the unit itself or under one of its descendants is a cycle and is refused before any write; the check is a prefix comparison of two paths. Every write to the tree first takes a transaction-scoped advisory lock, so two moves at the same moment cannot pass each other's cycle check.
5. **Two scopes, two types.** Where an assignment applies is a `RoleScope` in Authorization's Contracts: the tenant, a unit with or without its children, or one record (type and id). Where an access happens is an `AccessScope` in the kernel vocabulary, so `Iron.Kernel.Api`'s permission filter can pass it: the tenant, a unit, or a record with its owning unit. An endpoint declares how to find its scope with a marker (`ScopedToUnit`, `ScopedToRecord`); without one the check is tenant-wide, as before.
6. **Access to other units is an assignment.** TF-034's "may be granted access to others" is a role assignment scoped to the other unit. There is no second table for memberships; the home unit is the one unit a principal belongs to, kept on the principal by Identity.

## Options considered

- **Closure table** (a row per ancestor and descendant pair). Rejected: it answers both questions with an indexed equality join, as well as the path does with a prefix, but it is a second table to keep in step, it holds the subtree times the depth in rows, and a move deletes and inserts a pair for every unit of the subtree under every new ancestor. The path answers a record check from one row the closure table needs several for.
- **Adjacency list with recursive queries** (only `parent_id`). Rejected: every record check and every list check recurses up or down the tree in SQL; the cost grows with depth on the hottest path, and a list check with children is a recursive query joined to the list.
- **PostgreSQL `ltree`.** Rejected for now: an extension to install in every tenant database, labels limited in shape, and a type Entity Framework maps only through the provider's plugin; a text path with a pattern index gives the same prefix queries with no extension.
- **A membership table for "access to other units".** Rejected: it would say again what a scoped assignment says, and the evaluator would have two sources of truth.

## Consequences

- Easier: a record check costs one primary-key read and a lookup in the cached assignments; a list costs one query for its units whatever its length; a move costs two statements and needs no rewrite of assignments.
- Harder: the path must stay consistent with `parent_id`. Only `OrganisationUnits` writes it, in the same transaction as the parent, and the integration tests compare every path with the parent chain after moves.
- A permission revoked from a role, or an assignment removed, may still work for up to 60 seconds through the cache (ADR-0012); a grant through `IRoleAssignments` clears the principal's entry at once, as before.
- Revisit: when a tenant's tree passes ten thousand units or a list's unit set passes a few thousand ids, measure the `any(...)` predicate against a join to a temporary table, and the cache against per-unit invalidation.
