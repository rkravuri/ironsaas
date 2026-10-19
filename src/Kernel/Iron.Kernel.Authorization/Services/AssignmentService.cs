using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Data.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Authorization.Services;

/// <summary>One unit on the way from the root to an assignment's unit, for people to see where it applies.</summary>
public sealed record UnitStep(Guid Id, string Name);

/// <summary>An assignment as a reader sees it: who, which role, where, and the unit's path from the root.</summary>
public sealed record AssignmentView(
    Guid Id,
    Guid PrincipalId,
    string? PrincipalName,
    Guid RoleId,
    string RoleCode,
    string RoleName,
    RoleScopeKind ScopeKind,
    Guid? UnitId,
    string? RecordType,
    Guid? RecordId,
    IReadOnlyList<UnitStep> UnitPath,
    DateTimeOffset CreatedAt);

/// <summary>
/// Role assignments made by people through the API (TF-033, ADR-0031): listed where the reader holds
/// <c>authz.assignments.read</c>, granted and removed where the granter holds <c>authz.assignments.manage</c>. A grant
/// needs every permission of the role at its scope; the last tenant-wide tenant administrator stays. Each change and
/// its audit event are one transaction, and the principal's cached grants are cleared at once.
/// </summary>
public sealed class AssignmentService(
    AuthorizationDbContext context,
    AccessGrants grants,
    IPrincipals principals,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal actor,
    ILogger<AssignmentService> logger)
{
    public const string RecordType = "role_assignment";

    /// <summary>The key of the advisory lock a removal of a tenant administrator takes, so two removals cannot leave none.</summary>
    public const long AdministratorsLockKey = 0x1A_0B_AD_31;

    /// <summary>
    /// The assignments the reader may see (ADR-0031): every one for a tenant-wide reader; otherwise those at a unit
    /// where the reader holds <c>authz.assignments.read</c>. Tenant and record assignments need a tenant-wide reader.
    /// </summary>
    public async Task<IQueryable<RoleAssignment>> VisibleAsync(CancellationToken cancellationToken)
    {
        var access = await grants.AccessibleAsync(actor.Id, AuthorizationPermissions.AssignmentsRead, RecordType, cancellationToken).ConfigureAwait(false);
        return context.RoleAssignments.AsNoTracking().WhereAccessible(
            access,
            assignment => assignment.ScopeKind == Scopes.Unit || assignment.ScopeKind == Scopes.UnitWithChildren ? assignment.ScopeId : null,
            assignment => assignment.Id);
    }

    /// <summary>The assignments with their role, principal and unit path: a few queries for the whole page, whatever its length.</summary>
    public async Task<IReadOnlyList<AssignmentView>> DescribeAsync(IReadOnlyList<RoleAssignment> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return [];
        }

        var roleIds = rows.Select(r => r.RoleId).Distinct().ToArray();
        var roles = await context.Roles.AsNoTracking().Where(r => roleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, cancellationToken).ConfigureAwait(false);
        var unitIds = rows.Where(r => r.ScopeKind is Scopes.Unit or Scopes.UnitWithChildren).Select(r => r.ScopeId!.Value).Distinct().ToArray();
        var paths = await context.OrganisationUnits.AsNoTracking().Where(u => unitIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => OrganisationUnit.PathIds(u.Path), cancellationToken).ConfigureAwait(false);
        var stepIds = paths.Values.SelectMany(path => path).Distinct().ToArray();
        var names = await context.OrganisationUnits.AsNoTracking().Where(u => stepIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, cancellationToken).ConfigureAwait(false);
        var people = await principals.FindAsync(rows.Select(r => r.PrincipalId).Distinct().ToArray(), cancellationToken).ConfigureAwait(false);

        return rows.Select(row =>
        {
            var role = roles[row.RoleId];
            var scope = row.ToScope();
            IReadOnlyList<UnitStep> path = scope.UnitId is { } unit && paths.TryGetValue(unit, out var ids)
                ? ids.Select(id => new UnitStep(id, names.GetValueOrDefault(id, string.Empty))).ToList()
                : [];
            return new AssignmentView(
                row.Id,
                row.PrincipalId,
                people.TryGetValue(row.PrincipalId, out var person) ? person.DisplayName : null,
                role.Id,
                role.Code,
                role.Name,
                scope.Kind,
                scope.UnitId,
                scope.RecordType,
                scope.RecordId,
                path,
                row.CreatedAt);
        }).ToList();
    }

    /// <summary>
    /// Grants a role at a scope (ADR-0031 rule 1): the principal exists and is of the role's type, the unit is active,
    /// and the granter holds <c>authz.assignments.manage</c> and every permission of the role over the whole scope.
    /// Granting what is granted already answers the existing assignment.
    /// </summary>
    public async Task<Result<AssignmentView>> GrantAsync(Guid principalId, Guid roleId, RoleScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var found = await principals.FindAsync([principalId], cancellationToken).ConfigureAwait(false);
        if (!found.TryGetValue(principalId, out var principal))
        {
            return Error.Validation(AuthorizationErrors.PrincipalNotFound, "There is no user or service account with this id.", new FieldError("principal_id", "not_found", "Choose an existing user or service account."));
        }

        var role = await context.Roles.AsNoTracking().SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return Error.Validation(AuthorizationErrors.RoleNotFound, "There is no role with this id.", new FieldError("role_id", "not_found", "Choose an existing role."));
        }

        if (!role.IsFor(principal.Type))
        {
            var meantFor = role.PrincipalType == PrincipalKind.Service ? "service accounts" : "people";
            return Error.Validation(AuthorizationErrors.AssignmentPrincipalType, $"'{role.Name}' is for {meantFor}, and {principal.DisplayName} is not one.", new FieldError("role_id", "principal_type", $"Choose a role for {(principal.Type == PrincipalKind.Service ? "service accounts" : "people")}."));
        }

        var unit = await RoleAssignments.CheckUnitAsync(context, scope, cancellationToken).ConfigureAwait(false);
        if (unit is not null)
        {
            return unit;
        }

        var refusal = await RefuseAsync(scope, role, granting: true, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        var kind = Scopes.NameOf(scope.Kind);
        var scopeId = scope.UnitId ?? scope.RecordId;
        var existing = await context.RoleAssignments.AsNoTracking()
            .SingleOrDefaultAsync(a => a.PrincipalId == principalId && a.RoleId == role.Id && a.ScopeKind == kind && a.ScopeId == scopeId && a.ScopeRecordType == scope.RecordType, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return (await DescribeAsync([existing], cancellationToken).ConfigureAwait(false))[0];
        }

        var assignment = new RoleAssignment(role.Id, principalId, scope);
        await using (var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            context.RoleAssignments.Add(assignment);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await WriteAsync(AuthorizationAuditEvents.RoleAssigned, AuditOutcome.Success, assignment.Id, role, principalId, scope, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        grants.Forget(principalId); // the grant shows on the principal's next request, not after the cache window
        return (await DescribeAsync([assignment], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>
    /// Removes an assignment where the remover holds <c>authz.assignments.manage</c> over its scope. The last
    /// tenant-wide assignment of the tenant administrator role stays (ADR-0031 rule 3).
    /// </summary>
    public async Task<Result> RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        var assignment = await context.RoleAssignments.SingleOrDefaultAsync(a => a.Id == id, cancellationToken).ConfigureAwait(false);
        if (assignment is null)
        {
            return Error.NotFound(AuthorizationErrors.AssignmentNotFound, "There is no role assignment with this id.");
        }

        var scope = assignment.ToScope();
        var role = await context.Roles.AsNoTracking().SingleAsync(r => r.Id == assignment.RoleId, cancellationToken).ConfigureAwait(false);
        var refusal = await RefuseAsync(scope, role, granting: false, cancellationToken).ConfigureAwait(false);
        if (refusal is not null)
        {
            return refusal;
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        if (role.Code == SystemRoles.TenantAdministrator && scope.Kind == RoleScopeKind.Tenant)
        {
            await context.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock({AdministratorsLockKey})", cancellationToken).ConfigureAwait(false);
            var others = await context.RoleAssignments.CountAsync(a => a.RoleId == role.Id && a.ScopeKind == Scopes.Tenant && a.Id != assignment.Id, cancellationToken).ConfigureAwait(false);
            if (others == 0)
            {
                return Error.Conflict(AuthorizationErrors.AssignmentLastAdministrator, "This is the last tenant administrator for the whole tenant. Make someone else tenant administrator first, then remove this assignment.");
            }
        }

        context.RoleAssignments.Remove(assignment);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(AuthorizationAuditEvents.RoleUnassigned, AuditOutcome.Success, assignment.Id, role, assignment.PrincipalId, scope, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        grants.Forget(assignment.PrincipalId);
        return Result.Success();
    }

    /// <summary>
    /// ADR-0031 rule 1: the actor holds <c>authz.assignments.manage</c> over the whole scope and, to grant, every
    /// permission of the role there. A refusal is audited as a denial and names what is missing.
    /// </summary>
    private async Task<Error?> RefuseAsync(RoleScope scope, Role role, bool granting, CancellationToken cancellationToken)
    {
        var held = await grants.PermissionsCoveringAsync(actor.Id, scope, cancellationToken).ConfigureAwait(false);
        if (!held.Contains(AuthorizationPermissions.AssignmentsManage.Name))
        {
            await DenyAsync(PermissionEvaluator.DenialAction, role, scope, AccessDecision.NotGranted, [AuthorizationPermissions.AssignmentsManage.Name], cancellationToken).ConfigureAwait(false);
            return Error.Forbidden(PermissionErrors.PermissionDenied, $"You do not hold {AuthorizationPermissions.AssignmentsManage.Name} at {Describe(scope)}, so you cannot {(granting ? "grant" : "remove")} roles there.");
        }

        var missing = granting ? role.Permissions.Where(p => !held.Contains(p)).ToList() : [];
        if (missing.Count == 0)
        {
            return null;
        }

        await DenyAsync(AuthorizationAuditEvents.RoleAssigned, role, scope, "escalation", missing, cancellationToken).ConfigureAwait(false);
        return Error.Forbidden(AuthorizationErrors.AssignmentEscalation, $"You can grant a role only where you hold all its permissions. At {Describe(scope)} you do not hold: {string.Join(", ", missing)}.");
    }

    private async Task DenyAsync(string action, Role role, RoleScope scope, string reason, IReadOnlyList<string> missing, CancellationToken cancellationToken)
    {
        AuthorizationMetrics.Denials.Add(1, new KeyValuePair<string, object?>("reason", reason));
        try
        {
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["role"] = role.Code,
                ["scope"] = scope.ToString(),
                ["reason"] = reason,
                ["missing"] = string.Join(',', missing),
            };
            await audit.WriteAsync(new AuditEvent(action, AuditOutcome.Denied, actor.Id, role.Code, details, RecordType), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "The denial audit event for principal {PrincipalId} on role {Role} at {Scope} could not be written.", actor.Id, role.Code, scope);
        }
    }

    private Task WriteAsync(string action, string outcome, Guid assignmentId, Role role, Guid principalId, RoleScope scope, CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["role"] = role.Code,
            ["principal_id"] = principalId.ToString(),
            ["scope"] = scope.ToString(),
            ["permissions"] = string.Join(',', role.Permissions),
        };
        return audit.WriteAsync(new AuditEvent(action, outcome, actor.Id, role.Code, details, RecordType, assignmentId), cancellationToken);
    }

    private static string Describe(RoleScope scope) => scope.Kind switch
    {
        RoleScopeKind.Unit => "this unit",
        RoleScopeKind.UnitWithChildren => "this unit and the units below it",
        RoleScopeKind.Record => "this record",
        _ => "the whole tenant",
    };
}
