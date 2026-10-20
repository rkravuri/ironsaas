using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Data.Lists;
using Iron.Kernel.Data.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Authorization.Services;

/// <summary>
/// The organisation tree (TF-034, ADR-0030): read it in tree order, create a unit under another, rename, move and
/// deactivate. Every write takes the tree's advisory lock and writes its audit event in the same transaction
/// (ADR-0013), so two moves at the same moment cannot pass each other's cycle check.
/// </summary>
public sealed class OrganisationUnitService(
    AuthorizationDbContext context,
    IOrganisationKinds kinds,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal actor,
    TimeProvider time,
    ILogger<OrganisationUnitService> logger) : IOrganisationUnits
{
    public const string RecordType = "organisation_unit";

    /// <summary>The key of the transaction-scoped advisory lock every tree write takes; one per tenant database.</summary>
    public const long TreeLockKey = 0x1A_0B_7E_E5;

    /// <summary>Every unit, depth first from the root, siblings by name. A tenant's tree is small next to its records (ADR-0030).</summary>
    public Task<IReadOnlyList<OrganisationUnit>> TreeAsync(CancellationToken cancellationToken) => TreeAsync(RecordAccess.Everything, cancellationToken);

    /// <summary>The units a reach covers (ADR-0031), in tree order; a unit whose parent is outside the reach starts a branch.</summary>
    public async Task<IReadOnlyList<OrganisationUnit>> TreeAsync(RecordAccess access, CancellationToken cancellationToken)
    {
        var units = await context.OrganisationUnits.AsNoTracking().WhereAccessible(access, u => (Guid?)u.Id, u => u.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return InTreeOrder(units);
    }

    public static IReadOnlyList<OrganisationUnit> InTreeOrder(IReadOnlyCollection<OrganisationUnit> units)
    {
        ArgumentNullException.ThrowIfNull(units);
        var children = units.Where(u => u.ParentId is not null).ToLookup(u => u.ParentId!.Value);
        var ordered = new List<OrganisationUnit>(units.Count);
        var present = units.Select(u => u.Id).ToHashSet();
        var tops = units.Where(u => u.ParentId is not { } parent || !present.Contains(parent));
        var stack = new Stack<OrganisationUnit>(tops.OrderByDescending(u => u.Path.Length).ThenByDescending(u => u.Name, StringComparer.OrdinalIgnoreCase));
        while (stack.Count > 0)
        {
            var unit = stack.Pop();
            ordered.Add(unit);
            foreach (var child in children[unit.Id].OrderByDescending(u => u.Name, StringComparer.OrdinalIgnoreCase).ThenByDescending(u => u.Code, StringComparer.Ordinal))
            {
                stack.Push(child);
            }
        }

        return ordered;
    }

    public async Task<OrganisationUnit?> FindEntityAsync(Guid unitId, CancellationToken cancellationToken) =>
        await context.OrganisationUnits.AsNoTracking().SingleOrDefaultAsync(u => u.Id == unitId, cancellationToken).ConfigureAwait(false);

    public async Task<OrganisationUnitInfo?> RootAsync(CancellationToken cancellationToken)
    {
        var root = await context.OrganisationUnits.AsNoTracking().SingleOrDefaultAsync(u => u.ParentId == null, cancellationToken).ConfigureAwait(false);
        return root is null ? null : Info(root);
    }

    public async Task<OrganisationUnitInfo?> FindAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var unit = await FindEntityAsync(unitId, cancellationToken).ConfigureAwait(false);
        return unit is null ? null : Info(unit);
    }

    public async Task<IReadOnlyList<Guid>> PathAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var path = await context.OrganisationUnits.AsNoTracking().Where(u => u.Id == unitId).Select(u => u.Path).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return path is null
            ? []
            : [.. path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(segment => Guid.ParseExact(segment, "N"))];
    }

    public static OrganisationUnitInfo Info(OrganisationUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return new OrganisationUnitInfo(unit.Id, unit.Name, unit.Code, unit.Kind, unit.ParentId, unit.Depth, unit.Status);
    }

    /// <summary>The seed's root (ADR-0030): created once with the provider's root kind; an existing root is left as it is.</summary>
    public async Task<OrganisationUnit> EnsureRootAsync(string name, string code, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        await LockTreeAsync(cancellationToken).ConfigureAwait(false);
        var existing = await context.OrganisationUnits.SingleOrDefaultAsync(u => u.ParentId == null, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        var root = OrganisationUnit.Root(name, code, kinds.RootKind);
        context.OrganisationUnits.Add(root);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(AuthorizationAuditEvents.UnitCreated, root, Details(("name", root.Name), ("code", root.Code), ("kind", root.Kind)), "seed", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Created the root unit {Code} ({UnitId}) of kind {Kind}.", root.Code, root.Id, root.Kind);
        return root;
    }

    public async Task<Result<OrganisationUnit>> CreateAsync(Guid parentId, string name, string code, string kind, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        await LockTreeAsync(cancellationToken).ConfigureAwait(false);
        var parent = await context.OrganisationUnits.AsNoTracking().SingleOrDefaultAsync(u => u.Id == parentId, cancellationToken).ConfigureAwait(false);
        if (parent is null)
        {
            return NotFound();
        }

        if (!parent.IsActive)
        {
            return Error.Validation(AuthorizationErrors.UnitInactive, $"'{parent.Name}' is deactivated; a unit cannot be created under it.");
        }

        var kindCheck = AllowedUnder(parent, kind);
        if (kindCheck is not null)
        {
            return kindCheck;
        }

        if (parent.Depth + 1 > OrganisationUnit.MaxDepth)
        {
            return TooDeep();
        }

        var normalised = OrganisationUnit.NormaliseCode(code);
        if (await context.OrganisationUnits.AnyAsync(u => u.Code == normalised, cancellationToken).ConfigureAwait(false))
        {
            return CodeTaken(normalised);
        }

        var unit = OrganisationUnit.ChildOf(parent, name, normalised, kind);
        context.OrganisationUnits.Add(unit);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(AuthorizationAuditEvents.UnitCreated, unit, Details(("name", unit.Name), ("code", unit.Code), ("kind", unit.Kind), ("parent_id", parent.Id.ToString())), null, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return unit;
    }

    public async Task<Result<OrganisationUnit>> RenameAsync(Guid unitId, string name, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        await LockTreeAsync(cancellationToken).ConfigureAwait(false);
        var unit = await context.OrganisationUnits.SingleOrDefaultAsync(u => u.Id == unitId, cancellationToken).ConfigureAwait(false);
        if (unit is null)
        {
            return NotFound();
        }

        var previous = unit.Name;
        unit.Rename(name);
        if (string.Equals(previous, unit.Name, StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return unit;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(AuthorizationAuditEvents.UnitRenamed, unit, Details(("from", previous), ("to", unit.Name)), null, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return unit;
    }

    /// <summary>
    /// Moves a unit and its subtree under another unit (ADR-0030): the root does not move, a unit cannot move under itself
    /// or a descendant (a cycle), the new parent must be active and allow the unit's kind, and the deepest unit of the
    /// subtree must stay within the depth limit. The unit's row changes, then its descendants' paths in one statement.
    /// </summary>
    public async Task<Result<OrganisationUnit>> MoveAsync(Guid unitId, Guid newParentId, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        await LockTreeAsync(cancellationToken).ConfigureAwait(false);
        var unit = await context.OrganisationUnits.SingleOrDefaultAsync(u => u.Id == unitId, cancellationToken).ConfigureAwait(false);
        if (unit is null)
        {
            return NotFound();
        }

        if (unit.IsRoot)
        {
            return Error.Validation(AuthorizationErrors.UnitIsRoot, "The root unit stays where it is.");
        }

        var parent = await context.OrganisationUnits.AsNoTracking().SingleOrDefaultAsync(u => u.Id == newParentId, cancellationToken).ConfigureAwait(false);
        if (parent is null)
        {
            return Error.Validation(AuthorizationErrors.UnitNotFound, "The unit to move under does not exist.", new FieldError("parent_id", "not_found", "Choose an existing unit."));
        }

        if (unit.ParentId == parent.Id)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return unit;
        }

        if (unit.Contains(parent))
        {
            return Error.Validation(AuthorizationErrors.UnitCycle, $"'{unit.Name}' cannot move under itself or one of its own units.", new FieldError("parent_id", "cycle", "Choose a unit outside the one you move."));
        }

        if (!parent.IsActive)
        {
            return Error.Validation(AuthorizationErrors.UnitInactive, $"'{parent.Name}' is deactivated; a unit cannot move under it.", new FieldError("parent_id", "inactive", "Choose an active unit."));
        }

        var kindCheck = AllowedUnder(parent, unit.Kind, "parent_id");
        if (kindCheck is not null)
        {
            return kindCheck;
        }

        var oldPath = unit.Path;
        var deepest = await context.OrganisationUnits.Where(u => u.Path.StartsWith(oldPath)).MaxAsync(u => u.Depth, cancellationToken).ConfigureAwait(false);
        var delta = parent.Depth + 1 - unit.Depth;
        if (deepest + delta > OrganisationUnit.MaxDepth)
        {
            return TooDeep("parent_id");
        }

        var previousParent = unit.ParentId;
        unit.MoveUnder(parent);
        var newPath = unit.Path;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var oldLength = oldPath.Length;
        var now = time.GetUtcNow();
        var by = actor.Id;
        await context.OrganisationUnits
            .Where(u => u.Path.StartsWith(oldPath) && u.Id != unitId)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(u => u.Path, u => newPath + u.Path.Substring(oldLength))
                    .SetProperty(u => u.Depth, u => u.Depth + delta)
                    .SetProperty(u => u.UpdatedAt, now)
                    .SetProperty(u => u.UpdatedBy, by)
                    .SetProperty(u => u.Version, u => u.Version + 1),
                cancellationToken)
            .ConfigureAwait(false);

        await WriteAsync(AuthorizationAuditEvents.UnitMoved, unit, Details(("from_parent_id", previousParent?.ToString() ?? string.Empty), ("to_parent_id", parent.Id.ToString())), null, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return unit;
    }

    /// <summary>
    /// Makes a deactivated unit active again (TF-223): its records, people, roles and settings never left it. Not under a
    /// deactivated parent, and never a merged unit, whose contents are in the unit it was merged into.
    /// </summary>
    public async Task<Result<OrganisationUnit>> ReactivateAsync(Guid unitId, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        await LockTreeAsync(cancellationToken).ConfigureAwait(false);
        var unit = await context.OrganisationUnits.SingleOrDefaultAsync(u => u.Id == unitId, cancellationToken).ConfigureAwait(false);
        if (unit is null)
        {
            return NotFound();
        }

        if (unit.IsActive)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return unit;
        }

        if (unit.MergedIntoId is { } mergedInto)
        {
            var into = await context.OrganisationUnits.AsNoTracking().Where(u => u.Id == mergedInto).Select(u => u.Name).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return Error.Conflict(AuthorizationErrors.UnitMerged, $"'{unit.Name}' was merged into '{into}', which holds what it held. Create a new unit if you need one.");
        }

        var parent = await context.OrganisationUnits.AsNoTracking().SingleAsync(u => u.Id == unit.ParentId, cancellationToken).ConfigureAwait(false);
        if (!parent.IsActive)
        {
            return Error.Conflict(AuthorizationErrors.UnitParentInactive, $"'{parent.Name}', the unit above it, is deactivated. Reactivate it first.");
        }

        unit.Reactivate();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(AuthorizationAuditEvents.UnitReactivated, unit, Details(("name", unit.Name)), null, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return unit;
    }

    /// <summary>Deactivates a unit (TF-034 AC2): never the root, and only when none of its children is active.</summary>
    public async Task<Result<OrganisationUnit>> DeactivateAsync(Guid unitId, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        await LockTreeAsync(cancellationToken).ConfigureAwait(false);
        var unit = await context.OrganisationUnits.SingleOrDefaultAsync(u => u.Id == unitId, cancellationToken).ConfigureAwait(false);
        if (unit is null)
        {
            return NotFound();
        }

        if (unit.IsRoot)
        {
            return Error.Validation(AuthorizationErrors.UnitIsRoot, "The root unit cannot be deactivated.");
        }

        if (!unit.IsActive)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return unit;
        }

        var activeChildren = await context.OrganisationUnits.CountAsync(u => u.ParentId == unitId && u.Status == UnitStatus.Active, cancellationToken).ConfigureAwait(false);
        if (activeChildren > 0)
        {
            return Error.Conflict(AuthorizationErrors.UnitHasActiveChildren, $"'{unit.Name}' has {activeChildren} active unit(s) under it. Move or deactivate them first.");
        }

        unit.Deactivate();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(AuthorizationAuditEvents.UnitDeactivated, unit, Details(("name", unit.Name)), null, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return unit;
    }

    internal Error? AllowedUnder(OrganisationUnit parent, string kind, string field = "kind")
    {
        var known = kinds.Kinds.FirstOrDefault(k => string.Equals(k.Code, kind, StringComparison.Ordinal));
        if (known is null)
        {
            return Error.Validation(AuthorizationErrors.UnitKindUnknown, $"'{kind}' is not a kind of organisation unit.", new FieldError("kind", "unknown", "Choose one of the kinds offered."));
        }

        var parentKind = kinds.Kinds.FirstOrDefault(k => string.Equals(k.Code, parent.Kind, StringComparison.Ordinal));
        if (parentKind is null || !parentKind.ChildKinds.Contains(kind, StringComparer.Ordinal))
        {
            var allowed = parentKind is null || parentKind.ChildKinds.Count == 0 ? "none" : string.Join(", ", parentKind.ChildKinds);
            return Error.Validation(
                AuthorizationErrors.UnitKindNotAllowed,
                $"A {parentKind?.Name ?? parent.Kind} cannot hold a {known.Name}. It can hold: {allowed}.",
                new FieldError(field, "kind_not_allowed", $"A {parentKind?.Name ?? parent.Kind} cannot hold a {known.Name}."));
        }

        return null;
    }

    private static Error NotFound() => Error.NotFound(AuthorizationErrors.UnitNotFound, "There is no organisation unit with this id.");

    internal static Error TooDeep(string field = "parent_id") =>
        Error.Validation(AuthorizationErrors.UnitTooDeep, $"A unit sits at most {OrganisationUnit.MaxDepth} levels below the root.", new FieldError(field, "too_deep", $"The tree is at most {OrganisationUnit.MaxDepth} levels deep."));

    private static Error CodeTaken(string code) =>
        new(ErrorKind.Conflict, AuthorizationErrors.UnitCodeTaken, $"Another unit has the code '{code}'.", [new FieldError("code", "taken", "Another unit has this code. Choose another.")]);

    private static Dictionary<string, string> Details(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private Task WriteAsync(string action, OrganisationUnit unit, Dictionary<string, string> details, string? reason, CancellationToken cancellationToken) =>
        audit.WriteAsync(new AuditEvent(action, AuditOutcome.Success, actor.Id, unit.Code, details, RecordType, unit.Id, reason), cancellationToken);

    internal Task LockTreeAsync(CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock({TreeLockKey})", cancellationToken);
}
