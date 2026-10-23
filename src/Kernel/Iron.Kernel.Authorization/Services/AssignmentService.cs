using System.Text.Json;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Data.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Authorization.Services;

/// <summary>A role scope as a held change keeps it.</summary>
public sealed record ScopePayload(RoleScopeKind Kind, Guid? UnitId, string? RecordType, Guid? RecordId)
{
    public static ScopePayload Of(RoleScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new(scope.Kind, scope.UnitId, scope.RecordType, scope.RecordId);
    }

    public RoleScope ToScope() => Kind switch
    {
        RoleScopeKind.Unit => RoleScope.Unit(UnitId!.Value),
        RoleScopeKind.UnitWithChildren => RoleScope.Unit(UnitId!.Value, includeChildren: true),
        RoleScopeKind.Record => RoleScope.Record(RecordType!, RecordId!.Value),
        _ => RoleScope.Tenant,
    };
}

/// <summary>What a held grant or removal needs to apply: the assignment (for a removal), the person, the role and where.</summary>
public sealed record AssignmentPayload(Guid? AssignmentId, Guid PrincipalId, Guid RoleId, ScopePayload Scope);

/// <summary>One unit on the way from the root to an assignment's unit, for people to see where it applies.</summary>
public sealed record UnitStep(Guid Id, string Name);

/// <summary>
/// An assignment as a reader sees it: who, which role, where, the unit's path from the root, and its source when no person
/// granted it (a single sign-on provider's mapping, ADR-0048).
/// </summary>
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
    DateTimeOffset CreatedAt,
    string? Source = null);

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
    ConflictService segregation,
    PermissionCatalogue catalogue,
    IRecentAuthentication recent,
    ILogger<AssignmentService> logger,
    IFourEyes? fourEyes = null)
{
    public const string RecordType = "role_assignment";

    /// <summary>The kinds of change a grant or a removal becomes while it waits for a second administrator (TF-068, ADR-0052).</summary>
    public const string GrantKind = "authz.assignment.grant";

    public const string RemoveKind = "authz.assignment.remove";

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

    /// <summary>
    /// The reader's own assignments, oldest first, for My profile (D4, ADR-0033). Open to every signed-in principal
    /// without <c>authz.assignments.read</c>: it answers only what the reader holds, which /me already implies.
    /// </summary>
    public async Task<IReadOnlyList<AssignmentView>> MineAsync(CancellationToken cancellationToken)
    {
        var rows = await context.RoleAssignments.AsNoTracking()
            .Where(assignment => assignment.PrincipalId == actor.Id)
            .OrderBy(assignment => assignment.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return await DescribeAsync(rows, cancellationToken).ConfigureAwait(false);
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
                row.CreatedAt,
                row.Source);
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

        // Segregation of duties (TF-036): a block rule refuses the grant and names itself; a warn rule lets it through,
        // and the person's conflicts are listed in the report at once.
        var conflicts = await segregation.ForGrantAsync(principalId, role, scope, cancellationToken).ConfigureAwait(false);
        if (conflicts.FirstOrDefault(c => c.Rule.Mode == ConflictModes.Block) is { } blocking)
        {
            return Error.Conflict(AuthorizationErrors.SodConflict, await segregation.RefusalAsync(blocking, principalId, cancellationToken).ConfigureAwait(false));
        }

        // Granting a role that holds a high-risk or critical permission is itself a high-risk action (TF-248): it needs a
        // recent authentication, asked last, so a grant that would be refused anyway never asks for a password.
        if (role.Permissions.Any(name => catalogue.Find(name)?.Risk is Risk.High or Risk.Critical) && recent.Require() is { IsFailure: true } stale)
        {
            return stale.Error;
        }

        if (await HeldAsync(GrantKind, principalId, principal.DisplayName, role, scope, ChangeJson.Nothing, State(principal.DisplayName, role, scope), new AssignmentPayload(null, principalId, role.Id, ScopePayload.Of(scope)), cancellationToken).ConfigureAwait(false) is { } held)
        {
            return held;
        }

        var assignment = new RoleAssignment(role.Id, principalId, scope);
        await using (var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            context.RoleAssignments.Add(assignment);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await WriteAsync(AuthorizationAuditEvents.RoleAssigned, AuditOutcome.Success, assignment.Id, role, principalId, scope, cancellationToken).ConfigureAwait(false);
            if (conflicts.Count > 0)
            {
                await segregation.RefreshPrincipalAsync(principalId, cancellationToken).ConfigureAwait(false);
            }

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

        var name = await NameAsync(assignment.PrincipalId, cancellationToken).ConfigureAwait(false);
        if (await HeldAsync(RemoveKind, assignment.PrincipalId, name, role, scope, State(name, role, scope), ChangeJson.Nothing, new AssignmentPayload(assignment.Id, assignment.PrincipalId, role.Id, ScopePayload.Of(scope)), cancellationToken).ConfigureAwait(false) is { } held)
        {
            return held;
        }

        return await RemoveCheckedAsync(assignment, role, scope, reviewId: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The state of the assignment a held change grants or removes, now (ADR-0052): the person, the role and where, or
    /// nothing when no such assignment exists. Compared with the change's before on approval.
    /// </summary>
    public async Task<JsonElement> StateAsync(HeldChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var payload = Payload(change);
        var scope = payload.Scope.ToScope();
        var kind = Scopes.NameOf(scope.Kind);
        var scopeId = scope.UnitId ?? scope.RecordId;
        var existing = await context.RoleAssignments.AsNoTracking()
            .SingleOrDefaultAsync(a => a.PrincipalId == payload.PrincipalId && a.RoleId == payload.RoleId && a.ScopeKind == kind && a.ScopeId == scopeId && a.ScopeRecordType == scope.RecordType, cancellationToken)
            .ConfigureAwait(false);
        var role = existing is null ? null : await context.Roles.AsNoTracking().SingleOrDefaultAsync(r => r.Id == payload.RoleId, cancellationToken).ConfigureAwait(false);
        return existing is null || role is null ? ChangeJson.Nothing : State(await NameAsync(payload.PrincipalId, cancellationToken).ConfigureAwait(false), role, scope);
    }

    /// <summary>Applies an approved grant or removal through the same path a direct one takes, as the approver (ADR-0052).</summary>
    public async Task<Result> ApplyAsync(HeldChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var payload = Payload(change);
        if (change.Kind == GrantKind)
        {
            var granted = await GrantAsync(payload.PrincipalId, payload.RoleId, payload.Scope.ToScope(), cancellationToken).ConfigureAwait(false);
            return granted.IsFailure ? granted.Error : Result.Success();
        }

        return await RemoveAsync(payload.AssignmentId!.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A grant or a removal waits for a second administrator when the tenant lists roles for four-eyes (TF-068 AC1).</summary>
    private async Task<Error?> HeldAsync(string kind, Guid principalId, string principalName, Role role, RoleScope scope, JsonElement before, JsonElement after, AssignmentPayload payload, CancellationToken cancellationToken)
    {
        if (fourEyes is null || !await fourEyes.AppliesAsync(ConfigurationAreas.Roles, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var title = kind == GrantKind ? $"Grant {role.Name} to {principalName} at {Describe(scope)}" : $"Remove {role.Name} from {principalName} at {Describe(scope)}";
        var where = scope.UnitId is { } unit ? AccessScope.Unit(unit) : AccessScope.Tenant;
        var proposal = new ChangeProposal(ConfigurationAreas.Roles, kind, $"assignment:{principalId:N}:{role.Id:N}:{scope}", title, before, after, ChangeJson.Of(payload), AuthorizationPermissions.AssignmentsManage.Name, where);
        return await fourEyes.HoldAsync(proposal, cancellationToken).ConfigureAwait(false);
    }

    private static JsonElement State(string principalName, Role role, RoleScope scope) =>
        ChangeJson.Of(new { Person = principalName, Role = role.Code, RoleName = role.Name, Scope = scope.ToString(), Permissions = Role.Normalise(role.Permissions) });

    private async Task<string> NameAsync(Guid principalId, CancellationToken cancellationToken)
    {
        var found = await principals.FindAsync([principalId], cancellationToken).ConfigureAwait(false);
        return found.TryGetValue(principalId, out var person) ? person.DisplayName : principalId.ToString("D");
    }

    private static AssignmentPayload Payload(HeldChange change) =>
        change.Payload.Deserialize<AssignmentPayload>(ChangeJson.Options) ?? throw new InvalidOperationException($"The change {change.Id} carries no assignment.");

    /// <summary>
    /// The removal an access review's revoke makes (TF-038 AC2): the reviewer's right is <c>authz.reviews.decide</c> at the
    /// item's unit, which the review checked, so <c>authz.assignments.manage</c> is not asked; everything else is the same
    /// path, the last-administrator guard included. Joins the caller's transaction, which records the decision with it.
    /// </summary>
    internal async Task<Result> RemoveForReviewAsync(Guid id, Guid reviewId, CancellationToken cancellationToken)
    {
        var assignment = await context.RoleAssignments.SingleOrDefaultAsync(a => a.Id == id, cancellationToken).ConfigureAwait(false);
        if (assignment is null)
        {
            return Error.NotFound(AuthorizationErrors.AssignmentNotFound, "There is no role assignment with this id.");
        }

        var role = await context.Roles.AsNoTracking().SingleAsync(r => r.Id == assignment.RoleId, cancellationToken).ConfigureAwait(false);
        return await RemoveCheckedAsync(assignment, role, assignment.ToScope(), reviewId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result> RemoveCheckedAsync(RoleAssignment assignment, Role role, RoleScope scope, Guid? reviewId, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        if (role.Code == SystemRoles.TenantAdministrator && scope.Kind == RoleScopeKind.Tenant)
        {
            await context.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock({AdministratorsLockKey})", cancellationToken).ConfigureAwait(false);
            // Only an active administrator counts: a deactivated one keeps the role but cannot sign in to use it (T2).
            var others = await context.RoleAssignments.Where(a => a.RoleId == role.Id && a.ScopeKind == Scopes.Tenant && a.Id != assignment.Id && a.PrincipalId != assignment.PrincipalId).Select(a => a.PrincipalId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
            var found = others.Count == 0 ? new Dictionary<Guid, PrincipalSummary>() : await principals.FindAsync(others, cancellationToken).ConfigureAwait(false);
            if (!found.Values.Any(p => p.Status == PrincipalStatuses.Active))
            {
                return Error.Conflict(AuthorizationErrors.AssignmentLastAdministrator, "This is the last tenant administrator for the whole tenant. Make someone else tenant administrator first, then remove this assignment.");
            }
        }

        context.RoleAssignments.Remove(assignment);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(AuthorizationAuditEvents.RoleUnassigned, AuditOutcome.Success, assignment.Id, role, assignment.PrincipalId, scope, cancellationToken, reviewId).ConfigureAwait(false);
        await segregation.RefreshPrincipalAsync(assignment.PrincipalId, cancellationToken).ConfigureAwait(false);
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

    private Task WriteAsync(string action, string outcome, Guid assignmentId, Role role, Guid principalId, RoleScope scope, CancellationToken cancellationToken, Guid? reviewId = null)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["role"] = role.Code,
            ["principal_id"] = principalId.ToString(),
            ["scope"] = scope.ToString(),
            ["permissions"] = string.Join(',', role.Permissions),
        };
        if (reviewId is { } review)
        {
            details["access_review_id"] = review.ToString("D");
        }
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
