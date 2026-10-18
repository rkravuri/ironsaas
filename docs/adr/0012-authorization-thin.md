# ADR-0012: Permissions, roles and the migration workflow

Date: 2026-10-18
Status: Accepted

## Context

TF-030 and TF-031: every operation declares a permission or is explicitly anonymous, the host refuses to start otherwise, the check runs before any business code, denial is a 403 with a stable code and an audit event, and a failing check denies. S1 needs the thin version: tenant-wide roles and assignments; organisation units, record policies and the role editor come in S2. The first module with tables also sets the migration workflow for every module after it.

## Decision

1. **`Permission` is a Core type.** `Iron.Core.Permissions.Permission(name, description, risk)` validates the `module.resource.action` pattern. Modules declare permissions as static fields in their Contracts project and list them in `IModule.Permissions`; a permission must start with the name of the module that declares it. `AddModules` builds the `PermissionCatalogue`.
2. **Endpoints declare and enforce in one call.** `.RequirePermission(permission)` (Iron.Api) puts `PermissionMetadata` on an endpoint or a group and adds `PermissionFilter`, which runs before the handler: nobody signed in gets 401 `auth.not_authenticated`; a principal without the permission gets 403 `authz.permission_denied` with the permission named. `.AllowAnonymous()` is the explicit anonymous marker.
3. **Start-up check.** `EndpointPermissionCheck`, a startup filter registered by `AddIronApi` (hosted services run before the pipeline exists, so a filter is the right hook), walks every endpoint once the pipeline is built: no marker, both markers, a permission no module declared, or protected endpoints without an `IPermissionEvaluator` stop the host with the operations named. Health probes and the Development API browser carry the anonymous marker; a host test keeps the anonymous list fixed at the two health probes outside Development.
4. **Evaluation in Iron.Authorization.** `PermissionEvaluator` implements `IPermissionEvaluator` (Iron.Api): the principal's tenant-wide assignments join to roles, the union of their permission arrays is cached per principal for at most 60 seconds (`Authorization:CacheSeconds`), and any exception denies with reason `unavailable`, an error log and the `authz.denials` counter. Every denial writes an audit event `authz.permission_denied` through `IAuditWriter`. A grant made through `IRoleAssignments` clears the principal's cache entry.
5. **Tables.** Schema `authz`: `roles` (name unique, description, is_system, principal_type, permissions as `text[]`) and `role_assignments` (role_id, principal_id, scope_kind, scope_id; unique with nulls not distinct). Permissions as an array keep one table per concept; S2 adds scopes and the role editor, not new tables for permissions.
6. **System roles.** The seeder creates `tenant-administrator` (every permission of the host), `read-only` and `service-default` (every `*.read` permission) and keeps them in step with the catalogue on every `seed` run. They cannot be edited.
7. **Audit contract first.** `Iron.Audit.Contracts` defines `AuditEvent` and `IAuditWriter` now; `Iron.Audit` registers a log-line writer until T9 adds the `audit.events` table and the database writer. Hosts list Audit before Authorization.
8. **Migrations are generated, never hand-written.** `make ef-add MODULE=<Module> NAME=<Change>` runs `dotnet ef migrations add` with the module's design-time factory, which uses the same provider options as the host (`DesignTime` in Iron.Data), so generated migrations match what runs. The tool comes from `.config/dotnet-tools.json` (`dotnet tool restore`). A test per module asserts `HasPendingModelChanges()` is false, so a model change without a migration fails the build; EF also refuses to migrate such a model at run time.

## Consequences

- A new endpoint cannot be forgotten: it does not start without a permission or the anonymous marker, and the permission must exist in the catalogue.
- Until Identity registers the request principal (T10), an API host treats every request as the system principal, which holds no role: protected operations answer 403. That is the safe default.
- The 60-second cache means a revoked permission may work for up to a minute; a grant through the service shows at once. TF-031 AC2 allows this.
- Every model change is two steps: change the entity, run `make ef-add`. The generated files under `Data/Migrations` are committed with the change.
