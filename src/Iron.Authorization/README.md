# Iron.Authorization

Schema `authz`. Permissions, roles and role assignments (TF-030 to TF-033; thin in S1, ADR-0012).

| Folder | Holds |
|---|---|
| `Contracts/` | `AuthorizationPermissions` (`authz.roles.read`, `authz.roles.manage`, `authz.assignments.manage`), `SystemRoles`, `IRoleAssignments` for other modules (Identity assigns the first administrator), `AuthorizationErrors` |
| `Data/` | `AuthorizationDbContext`, `Role` (permissions as a text array), `RoleAssignment` (tenant scope in S1), the design-time factory, generated `Migrations/` |
| `Services/` | `PermissionEvaluator` (60-second cache, fail closed, denial audit, `authz.denials` metric), `RoleAssignments`, `AuthorizationSeeder` (system roles from the catalogue), `PermissionCacheOptions` |
| `Api/` | `GET /api/v1/roles` with the list grammar, permission `authz.roles.read` |

Configuration: `Authorization:CacheSeconds` (default and maximum 60; 0 reads the database on every check).

Model change: edit the entity, then `make ef-add MODULE=Authorization NAME=<WhatChanged>` and commit `Data/Migrations`. The test `ModelTests` fails until the migration exists.
