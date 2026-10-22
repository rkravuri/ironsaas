using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Data.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Authorization.Services;

/// <summary>
/// The assignments a single sign-on provider's group mapping gives (ADR-0048): checked against ADR-0031's rule 1 when the
/// mapping is saved, brought in step at every sign-in through the provider, each marked with its source so a sync never
/// touches a grant a person made.
/// </summary>
public sealed class ProvidedAssignments(
    AuthorizationDbContext context,
    AccessGrants grants,
    IPrincipals principals,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal actor,
    ConflictService segregation,
    ILogger<ProvidedAssignments> logger) : IProvidedAssignments
{
    public async Task<Result<IReadOnlyList<string>>> MissingToGrantAsync(Guid granterId, string roleCode, RoleScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var role = await context.Roles.AsNoTracking().SingleOrDefaultAsync(r => r.Code == roleCode, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return Error.Validation(AuthorizationErrors.RoleNotFound, $"There is no role with the code '{roleCode}'.", new FieldError("role_code", "not_found", "Choose an existing role."));
        }

        if (!role.IsFor(PrincipalKind.User))
        {
            return Error.Validation(AuthorizationErrors.AssignmentPrincipalType, $"'{role.Name}' is for service accounts; a single sign-on provider signs people in.", new FieldError("role_code", "principal_type", "Choose a role for people."));
        }

        var unit = await RoleAssignments.CheckUnitAsync(context, scope, cancellationToken).ConfigureAwait(false);
        if (unit is not null)
        {
            return unit;
        }

        var held = await grants.PermissionsCoveringAsync(granterId, scope, cancellationToken).ConfigureAwait(false);
        var missing = new List<string>();
        if (!held.Contains(AuthorizationPermissions.AssignmentsManage.Name))
        {
            missing.Add(AuthorizationPermissions.AssignmentsManage.Name);
        }

        missing.AddRange(role.Permissions.Where(permission => !held.Contains(permission)));
        return missing;
    }

    public async Task<Result<ProvidedAssignmentChanges>> SyncAsync(Guid principalId, string source, IReadOnlyList<ProvidedRole> wanted, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(wanted);
        if (principalId == Guid.Empty)
        {
            return Error.Validation(AuthorizationErrors.PrincipalRequired, "A principal is required.");
        }

        var codes = wanted.Select(w => w.RoleCode).Distinct(StringComparer.Ordinal).ToList();
        var roles = await context.Roles.AsNoTracking().Where(r => codes.Contains(r.Code)).ToDictionaryAsync(r => r.Code, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        var held = await context.RoleAssignments.Where(a => a.PrincipalId == principalId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var fromSource = held.Where(a => a.Source == source).ToList();
        var added = new List<string>();
        var removed = new List<string>();
        var skipped = new List<string>();
        var keep = new HashSet<Guid>();
        var additions = new List<(Role Role, RoleScope Scope)>();

        foreach (var want in wanted.DistinctBy(w => (w.RoleCode, w.Scope.ToString())))
        {
            if (!roles.TryGetValue(want.RoleCode, out var role) || !role.IsFor(PrincipalKind.User))
            {
                skipped.Add($"{want.RoleCode}@{want.Scope}: the role is gone or not for people");
                continue;
            }

            if (await RoleAssignments.CheckUnitAsync(context, want.Scope, cancellationToken).ConfigureAwait(false) is not null)
            {
                skipped.Add($"{want.RoleCode}@{want.Scope}: the unit is gone or deactivated");
                continue;
            }

            var kind = Scopes.NameOf(want.Scope.Kind);
            var scopeId = want.Scope.UnitId ?? want.Scope.RecordId;
            var existing = held.FirstOrDefault(a => a.RoleId == role.Id && a.ScopeKind == kind && a.ScopeId == scopeId && a.ScopeRecordType == want.Scope.RecordType);
            if (existing is not null)
            {
                // Held already, from this source or granted by a person: nothing to add, and this source's own stays.
                keep.Add(existing.Id);
                continue;
            }

            var conflicts = await segregation.ForGrantAsync(principalId, role, want.Scope, cancellationToken).ConfigureAwait(false);
            if (conflicts.FirstOrDefault(c => c.Rule.Mode == ConflictModes.Block) is { } blocking)
            {
                skipped.Add($"{want.RoleCode}@{want.Scope}: {await segregation.RefusalAsync(blocking, principalId, cancellationToken).ConfigureAwait(false)}");
                continue;
            }

            additions.Add((role, want.Scope));
        }

        var removals = fromSource.Where(a => !keep.Contains(a.Id)).ToList();
        if (additions.Count == 0 && removals.Count == 0)
        {
            return new ProvidedAssignmentChanges(added, removed, skipped);
        }

        await using (var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var (role, scope) in additions)
            {
                var assignment = new RoleAssignment(role.Id, principalId, scope, source);
                context.RoleAssignments.Add(assignment);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await WriteAsync(AuthorizationAuditEvents.RoleAssigned, role.Code, principalId, scope, source, assignment.Id, cancellationToken).ConfigureAwait(false);
                added.Add($"{role.Code}@{scope}");
            }

            var allRoles = await context.Roles.AsNoTracking().Where(r => removals.Select(a => a.RoleId).Contains(r.Id)).ToDictionaryAsync(r => r.Id, cancellationToken).ConfigureAwait(false);
            foreach (var assignment in removals)
            {
                var role = allRoles[assignment.RoleId];
                var scope = assignment.ToScope();
                if (await IsLastAdministratorAsync(assignment, role, cancellationToken).ConfigureAwait(false))
                {
                    skipped.Add($"{role.Code}@{scope}: the last active tenant administrator keeps the role");
                    continue;
                }

                context.RoleAssignments.Remove(assignment);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await WriteAsync(AuthorizationAuditEvents.RoleUnassigned, role.Code, principalId, scope, source, assignment.Id, cancellationToken).ConfigureAwait(false);
                removed.Add($"{role.Code}@{scope}");
            }

            await segregation.RefreshPrincipalAsync(principalId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        grants.Forget(principalId);
        if (skipped.Count > 0)
        {
            logger.LogWarning("Roles from {Source} not applied to principal {PrincipalId}: {Skipped}.", source, principalId, string.Join("; ", skipped));
        }

        return new ProvidedAssignmentChanges(added, removed, skipped);
    }

    public async Task<int> RemoveSourceAsync(string source, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var rows = await context.RoleAssignments.Where(a => a.Source == source).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return 0;
        }

        var roles = await context.Roles.AsNoTracking().Where(r => rows.Select(a => a.RoleId).Contains(r.Id)).ToDictionaryAsync(r => r.Id, cancellationToken).ConfigureAwait(false);
        var count = 0;
        await using (var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var assignment in rows)
            {
                var role = roles[assignment.RoleId];
                if (await IsLastAdministratorAsync(assignment, role, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                context.RoleAssignments.Remove(assignment);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await WriteAsync(AuthorizationAuditEvents.RoleUnassigned, role.Code, assignment.PrincipalId, assignment.ToScope(), source, assignment.Id, cancellationToken).ConfigureAwait(false);
                count++;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var principalId in rows.Select(a => a.PrincipalId).Distinct())
        {
            grants.Forget(principalId);
        }

        return count;
    }

    /// <summary>ADR-0031 rule 3 under its lock: the tenant-wide tenant administrator role of the last active holder stays.</summary>
    private async Task<bool> IsLastAdministratorAsync(RoleAssignment assignment, Role role, CancellationToken cancellationToken)
    {
        if (role.Code != SystemRoles.TenantAdministrator || assignment.ScopeKind != Scopes.Tenant)
        {
            return false;
        }

        await context.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock({AssignmentService.AdministratorsLockKey})", cancellationToken).ConfigureAwait(false);
        var others = await context.RoleAssignments.Where(a => a.RoleId == role.Id && a.ScopeKind == Scopes.Tenant && a.Id != assignment.Id && a.PrincipalId != assignment.PrincipalId).Select(a => a.PrincipalId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        var found = others.Count == 0 ? new Dictionary<Guid, PrincipalSummary>() : await principals.FindAsync(others, cancellationToken).ConfigureAwait(false);
        return !found.Values.Any(p => p.Status == PrincipalStatuses.Active);
    }

    private Task WriteAsync(string action, string roleCode, Guid principalId, RoleScope scope, string source, Guid assignmentId, CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["role"] = roleCode,
            ["principal_id"] = principalId.ToString(),
            ["scope"] = scope.ToString(),
            ["source"] = source,
        };
        return audit.WriteAsync(new AuditEvent(action, AuditOutcome.Success, actor.Id, roleCode, details, AssignmentService.RecordType, assignmentId), cancellationToken);
    }
}
