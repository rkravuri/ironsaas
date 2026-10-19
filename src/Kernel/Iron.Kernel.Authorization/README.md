# Iron.Kernel.Authorization

Kernel module (ADR-0029). Schema `kernel_authz`. Permissions, roles, scoped role assignments and the organisation tree (TF-030 to TF-034; ADR-0012, ADR-0030).

| Folder | Holds |
|---|---|
| `Contracts/` | `AuthorizationPermissions` (`authz.roles.read`, `authz.roles.manage`, `authz.assignments.manage`, `authz.units.read`, `authz.units.manage`), `SystemRoles`, `AuthorizationErrors`, `AuthorizationAuditEvents`; `IRoleAssignments` (assign for a `RoleScope`, permissions at an `AccessScope`), `IRecordAccess` (what one permission reaches in a list), `IOrganisationUnits` (the root and a unit, for Identity), and the extension point `IOrganisationKinds` with `OrganisationKind` |
| `Data/` | `AuthorizationDbContext`, `Role` (permissions as a text array), `RoleAssignment` (scope kind, scope id, record type), `OrganisationUnit` (name, code, kind code, parent, path, depth, status), the design-time factory, generated `Migrations/` |
| `Services/` | `AccessGrants` (a principal's assignments, cached for 60 seconds, answered per scope and per list), `PermissionEvaluator` (fail closed, denial audit with the scope, `authz.denials` metric), `RoleAssignments`, `RecordAccessService`, `OrganisationUnitService` (the tree and its writes), `OrganisationUnitCheck` (the data layer's owning-unit check), `DefaultOrganisationKinds`, `AuthorizationSeeder` (system roles, the root unit), `PermissionCacheOptions` |
| `Api/` | `GET /api/v1/roles` (`authz.roles.read`); the organisation tree below |

## The organisation tree (TF-034, ADR-0030)

A kernel mechanism with no business meaning: units with a name, a code unique in the tenant, a kind code, a parent, a status, and a materialised path of ids from the root (`/<root>/<parent>/<unit>/`, at most 16 levels below the root). The seed creates the root once, with the provider's root kind, named from `Seed:RootUnit:Name` and coded from `Seed:RootUnit:Code` (the fleet passes the tenant's display name and slug; without them the tenant identifier serves).

The kinds come from `IOrganisationKinds`. The kernel default offers `root` (it may hold `unit`) and `unit` (it may hold `unit`). A business module replaces it with its own kinds and their business attributes, which live in the business module, never on the unit: `services.Replace(ServiceDescriptor.Singleton<IOrganisationKinds, MyKinds>())` (ADR-0029).

| Operation | Endpoint | Permission, checked at | Refused when |
|---|---|---|---|
| The whole tree, depth first, siblings by name | `GET /api/v1/organisation-units` | `authz.units.read`, tenant | |
| One unit | `GET /api/v1/organisation-units/{id}` | `authz.units.read`, the unit | |
| The kinds and what each may hold | `GET /api/v1/organisation-kinds` | `authz.units.read`, tenant | |
| Create a unit under this one | `POST /api/v1/organisation-units/{id}/children` with `name`, `code`, `kind` | `authz.units.manage`, the parent | the parent is inactive; the kind is unknown or the parent's kind may not hold it; the code is taken (409); deeper than 16 |
| Rename | `POST /api/v1/organisation-units/{id}/rename` with `name` | `authz.units.manage`, the unit | |
| Move with its subtree | `POST /api/v1/organisation-units/{id}/move` with `parent_id` | `authz.units.manage`, the unit and the new parent | the root; a cycle (under itself or a descendant); an inactive parent; a kind the parent may not hold; the subtree would pass 16 levels |
| Deactivate | `POST /api/v1/organisation-units/{id}/deactivate` | `authz.units.manage`, the unit | the root; any active unit below it (409, TF-034 AC2) |

Every write takes the tree's transaction-scoped advisory lock and writes its audit event in the same transaction: `authz.unit_created`, `authz.unit_renamed`, `authz.unit_moved`, `authz.unit_deactivated` (record type `organisation_unit`). A move changes the unit's row and rewrites its descendants' paths in one statement. A deactivated unit stays in the tree; no unit, record or role assignment can be placed in it.

## Scopes (TF-033, ADR-0030)

An assignment applies at a `RoleScope`: the tenant, a unit, a unit with its children, or one record (type and id). Access to a unit other than one's home unit is such an assignment. A check happens at an `AccessScope` (in the kernel vocabulary): the tenant, a unit, or a record with the unit that owns it. Tenant-wide grants apply everywhere; a unit grant to its unit's records; a grant with children to the whole subtree, whatever moves later; a record grant to its record. An assignment to a unit that does not exist or is inactive is refused (TF-033 AC4), and a new assignment writes `authz.role_assigned`.

A record or unit check reads the unit's path by its key and takes the ancestors from it. A list check, `IRecordAccess.AccessibleAsync(principal, permission, recordType)`, answers everything (a tenant-wide grant) or the units and records the permission reaches, in one query whatever the list's length; apply it with `WhereAccessible` (Iron.Kernel.Data). `/me` keeps listing the tenant-wide permissions; A2 adds the scoped view for the shell.

Configuration: `Authorization:CacheSeconds` (default and maximum 60; 0 reads the database on every check); `Seed:RootUnit:Name` and `Seed:RootUnit:Code`.

Model change: edit the entity, then `make ef-add MODULE=Authorization NAME=<WhatChanged>`, `make format`, and commit `Data/Migrations`. The test `ModelTests` fails until the migration exists.
