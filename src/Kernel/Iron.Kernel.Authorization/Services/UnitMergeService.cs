using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Sharing;
using Iron.Kernel.Data.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Authorization.Services;

/// <summary>A merge, previewed or done: the unit that goes, the unit that stays, and what moves from one to the other.</summary>
public sealed record UnitMergeOutcome(OrganisationUnit From, OrganisationUnit Into, IReadOnlyList<UnitMergePart> Parts);

/// <summary>
/// Merging one unit into another (TF-223 AC1, ADR-0038). The preview reads what would move, by module: the business
/// records the unit owns, the people homed there, the assignments scoped to it, its settings (dropped where the
/// surviving unit has its own), the shares that list it and its child units. The merge moves all of it in one
/// transaction, under the tree's lock, with one audit event that lists everything, and leaves the unit inactive and
/// merged. The root does not merge, a unit does not merge into itself or a unit below it, and the surviving unit is active.
/// </summary>
public sealed class UnitMergeService(
    AuthorizationDbContext context,
    OrganisationUnitService units,
    IEnumerable<IUnitRecordOwners> recordOwners,
    IEnumerable<IUnitMergeParticipant> participants,
    IRecordShares shares,
    IPrincipals principals,
    AccessGrants grants,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal actor)
{
    private static readonly string[] PartOrder = [UnitMergeParts.Records, UnitMergeParts.People, UnitMergeParts.Assignments, UnitMergeParts.Settings, UnitMergeParts.Shares, UnitMergeParts.Children];

    public async Task<Result<UnitMergeOutcome>> PreviewAsync(Guid fromId, Guid intoId, CancellationToken cancellationToken)
    {
        var checkedMerge = await CheckAsync(fromId, intoId, cancellationToken).ConfigureAwait(false);
        if (checkedMerge.IsFailure)
        {
            return checkedMerge.Error;
        }

        var (from, into, children) = checkedMerge.Value;
        var parts = new List<UnitMergePart>();
        foreach (var owner in recordOwners)
        {
            parts.AddRange((await owner.OwnedByAsync(from.Id, cancellationToken).ConfigureAwait(false)).Select(owned => Records(owner.Module, owned)));
        }

        foreach (var participant in participants)
        {
            parts.AddRange(await participant.PreviewAsync(from.Id, into.Id, cancellationToken).ConfigureAwait(false));
        }

        var (moving, dropping) = await AssignmentsAsync(from.Id, into.Id, tracked: false, cancellationToken).ConfigureAwait(false);
        parts.Add(await AssignmentsPartAsync(moving, dropping, cancellationToken).ConfigureAwait(false));
        parts.Add(Shares(await shares.OfUnitAsync(from.Id, cancellationToken).ConfigureAwait(false)));
        parts.Add(Children(children));
        return new UnitMergeOutcome(from, into, Ordered(parts));
    }

    public async Task<Result<UnitMergeOutcome>> MergeAsync(Guid fromId, Guid intoId, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        await units.LockTreeAsync(cancellationToken).ConfigureAwait(false);

        // Checked again under the lock: the tree may have changed since the preview.
        var checkedMerge = await CheckAsync(fromId, intoId, cancellationToken).ConfigureAwait(false);
        if (checkedMerge.IsFailure)
        {
            return checkedMerge.Error;
        }

        var (_, into, children) = checkedMerge.Value;
        var from = await context.OrganisationUnits.SingleAsync(u => u.Id == fromId, cancellationToken).ConfigureAwait(false);
        var parts = new List<UnitMergePart>();
        foreach (var owner in recordOwners)
        {
            parts.AddRange((await owner.MoveAsync(from.Id, into.Id, cancellationToken).ConfigureAwait(false)).Select(moved => Records(owner.Module, moved)));
        }

        foreach (var participant in participants)
        {
            parts.AddRange(await participant.ApplyAsync(from.Id, into.Id, cancellationToken).ConfigureAwait(false));
        }

        var (moving, dropping) = await AssignmentsAsync(from.Id, into.Id, tracked: true, cancellationToken).ConfigureAwait(false);
        parts.Add(await AssignmentsPartAsync(moving, dropping, cancellationToken).ConfigureAwait(false));
        foreach (var assignment in moving)
        {
            assignment.MoveToUnit(into.Id);
        }

        context.RoleAssignments.RemoveRange(dropping);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        parts.Add(Shares(await shares.MoveAsync(from.Id, into.Id, cancellationToken).ConfigureAwait(false)));

        // Each child moves as a move does, audited as one, with its subtree, its roles and its settings.
        foreach (var child in children)
        {
            var moved = await units.MoveAsync(child.Id, into.Id, cancellationToken).ConfigureAwait(false);
            if (moved.IsFailure)
            {
                return moved.Error;
            }
        }

        parts.Add(Children(children));
        from.MergeInto(into);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var ordered = Ordered(parts);
        await audit.WriteAsync(new AuditEvent(AuthorizationAuditEvents.UnitMerged, AuditOutcome.Success, actor.Id, from.Code, Details(from, into, ordered), OrganisationUnitService.RecordType, from.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        foreach (var participant in participants)
        {
            await participant.MergedAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var principalId in moving.Concat(dropping).Select(a => a.PrincipalId).Distinct())
        {
            grants.Forget(principalId); // the moved roles hold at the surviving unit on the next request
        }

        return new UnitMergeOutcome(from, into, ordered);
    }

    private async Task<Result<(OrganisationUnit From, OrganisationUnit Into, IReadOnlyList<OrganisationUnit> Children)>> CheckAsync(Guid fromId, Guid intoId, CancellationToken cancellationToken)
    {
        var from = await context.OrganisationUnits.AsNoTracking().SingleOrDefaultAsync(u => u.Id == fromId, cancellationToken).ConfigureAwait(false);
        if (from is null)
        {
            return Error.NotFound(AuthorizationErrors.UnitNotFound, "There is no organisation unit with this id.");
        }

        var into = await context.OrganisationUnits.AsNoTracking().SingleOrDefaultAsync(u => u.Id == intoId, cancellationToken).ConfigureAwait(false);
        if (into is null)
        {
            return Error.Validation(AuthorizationErrors.UnitNotFound, "The unit to merge into does not exist.", new FieldError("into_id", "not_found", "Choose an existing unit."));
        }

        if (from.IsRoot)
        {
            return Error.Validation(AuthorizationErrors.UnitIsRoot, "The root unit holds the whole tree; it cannot merge into another unit.");
        }

        if (from.MergedIntoId is not null)
        {
            return Error.Conflict(AuthorizationErrors.UnitMerged, $"'{from.Name}' was merged already.");
        }

        if (from.Contains(into))
        {
            return Error.Validation(AuthorizationErrors.UnitMergeIntoOwn, $"'{from.Name}' cannot merge into itself or one of its own units.", new FieldError("into_id", "own_unit", "Choose a unit outside the one that goes."));
        }

        if (!into.IsActive)
        {
            return Error.Validation(AuthorizationErrors.UnitInactive, $"'{into.Name}' is deactivated; nothing can merge into it.", new FieldError("into_id", "inactive", "Choose an active unit."));
        }

        var children = await context.OrganisationUnits.AsNoTracking().Where(u => u.ParentId == from.Id).OrderBy(u => u.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var child in children)
        {
            if (units.AllowedUnder(into, child.Kind, "into_id") is { } refused)
            {
                return refused;
            }
        }

        if (children.Count > 0)
        {
            var below = from.Path;
            var deepest = await context.OrganisationUnits.Where(u => u.Path.StartsWith(below) && u.Id != from.Id).MaxAsync(u => u.Depth, cancellationToken).ConfigureAwait(false);
            if (deepest + into.Depth - from.Depth > OrganisationUnit.MaxDepth)
            {
                return OrganisationUnitService.TooDeep("into_id");
            }
        }

        return (from, into, children);
    }

    /// <summary>The assignments scoped to the unit that goes: those that move, and those the person already holds at the unit that stays.</summary>
    private async Task<(List<RoleAssignment> Moving, List<RoleAssignment> Dropping)> AssignmentsAsync(Guid fromId, Guid intoId, bool tracked, CancellationToken cancellationToken)
    {
        var query = tracked ? context.RoleAssignments : context.RoleAssignments.AsNoTracking();
        var atFrom = await query.Where(a => a.ScopeId == fromId && (a.ScopeKind == Scopes.Unit || a.ScopeKind == Scopes.UnitWithChildren)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var atInto = await context.RoleAssignments.AsNoTracking()
            .Where(a => a.ScopeId == intoId && (a.ScopeKind == Scopes.Unit || a.ScopeKind == Scopes.UnitWithChildren))
            .Select(a => new { a.PrincipalId, a.RoleId, a.ScopeKind })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var held = atInto.Select(a => (a.PrincipalId, a.RoleId, a.ScopeKind)).ToHashSet();
        var dropping = atFrom.Where(a => held.Contains((a.PrincipalId, a.RoleId, a.ScopeKind))).ToList();
        return ([.. atFrom.Except(dropping)], dropping);
    }

    private async Task<UnitMergePart> AssignmentsPartAsync(IReadOnlyList<RoleAssignment> moving, IReadOnlyList<RoleAssignment> dropping, CancellationToken cancellationToken)
    {
        var all = moving.Concat(dropping).ToList();
        var names = await principals.FindAsync([.. all.Select(a => a.PrincipalId).Distinct()], cancellationToken).ConfigureAwait(false);
        var roleIds = all.Select(a => a.RoleId).Distinct().ToArray();
        var roles = await context.Roles.AsNoTracking().Where(r => roleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, cancellationToken).ConfigureAwait(false);
        string Words(RoleAssignment a) => $"{names.GetValueOrDefault(a.PrincipalId)?.DisplayName ?? a.PrincipalId.ToString("D")} ({roles.GetValueOrDefault(a.RoleId, string.Empty)})";
        return new UnitMergePart(UnitMergeParts.Assignments, "authorization", null, null, [.. moving.Select(Words).Order(StringComparer.CurrentCulture)], [.. moving.Select(a => a.Id.ToString("D"))], [.. dropping.Select(Words).Order(StringComparer.CurrentCulture)]);
    }

    private static UnitMergePart Records(string module, UnitRecords records) =>
        new(UnitMergeParts.Records, module, records.RecordType, records.DisplayName, [], [.. records.RecordIds.Select(id => id.ToString("D"))], []);

    private static UnitMergePart Shares(IReadOnlyList<RecordShare> listed) =>
        new(UnitMergeParts.Shares, "data", null, null, [.. listed.Select(s => s.RecordType).Distinct().Order(StringComparer.Ordinal)], [.. listed.Select(s => $"{s.RecordType}/{s.RecordId:D}")], []);

    private static UnitMergePart Children(IReadOnlyList<OrganisationUnit> children) =>
        new(UnitMergeParts.Children, "authorization", null, null, [.. children.Select(c => c.Name)], [.. children.Select(c => c.Id.ToString("D"))], []);

    private static List<UnitMergePart> Ordered(IEnumerable<UnitMergePart> parts) =>
        [.. parts.OrderBy(p => Array.IndexOf(PartOrder, p.Code) is var index and >= 0 ? index : PartOrder.Length).ThenBy(p => p.Module, StringComparer.Ordinal).ThenBy(p => p.RecordType, StringComparer.Ordinal)];

    /// <summary>Everything the merge moved, by id (keys for settings), and what it dropped: the audit event lists it all (ADR-0013).</summary>
    private static Dictionary<string, string> Details(OrganisationUnit from, OrganisationUnit into, IReadOnlyList<UnitMergePart> parts)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = from.Name,
            ["into_unit_id"] = into.Id.ToString("D"),
            ["into_name"] = into.Name,
        };
        foreach (var part in parts)
        {
            var key = part.RecordType is null ? part.Code : $"{part.Code}:{part.Module}:{part.RecordType}";
            details[key] = string.Join(',', part.Ids);
            if (part.Conflicts.Count > 0)
            {
                details[$"{key}_dropped"] = string.Join(',', part.Conflicts);
            }
        }

        return details;
    }
}
