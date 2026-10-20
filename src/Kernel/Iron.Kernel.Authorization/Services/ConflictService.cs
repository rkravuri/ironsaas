using System.Globalization;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Data.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Authorization.Services;

/// <summary>A rule as an administrator writes it.</summary>
public sealed record ConflictRuleInput(string Name, ConflictSide First, ConflictSide Second, string Mode, string? Reason, Guid? OwnerId);

/// <summary>A conflict a role's change would create for one person who holds the role, by an assignment or a delegation.</summary>
public sealed record RoleChangeConflict(Guid PrincipalId, Conflict Conflict);

/// <summary>
/// Segregation of duties (TF-036, ADR-0039): the tenant's conflict rules, the conflicts a grant or a delegation would
/// create, and the report of those that exist. A person holds both sides of a rule when some unit lies within the scope
/// of both (<see cref="ConflictDetector"/>); a delegation counts as the delegate holding the delegated duties' part of
/// the delegator's grants beside their own, though acting is never a union (ADR-0037), since one person could still do
/// one side as themselves and the other while acting.
/// </summary>
public sealed class ConflictService(
    AuthorizationDbContext context,
    PermissionCatalogue catalogue,
    DutyDeclarations duties,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal actor,
    TimeProvider time,
    IPrincipals? principals = null)
{
    public const string RecordType = "conflict_rule";

    public async Task<IReadOnlyList<ConflictRuleView>> RulesAsync(CancellationToken cancellationToken) =>
        [.. (await context.ConflictRules.AsNoTracking().OrderBy(r => r.Name).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(View)];

    public static ConflictRuleView View(ConflictRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new ConflictRuleView(rule.Id, rule.Name, rule.Mode, rule.Reason, new ConflictSide(rule.FirstKind, rule.FirstValue), new ConflictSide(rule.SecondKind, rule.SecondValue));
    }

    public async Task<Result<ConflictRule>> CreateAsync(ConflictRuleInput input, CancellationToken cancellationToken)
    {
        var refused = await CheckAsync(input, cancellationToken).ConfigureAwait(false);
        if (refused is not null)
        {
            return refused;
        }

        var rule = new ConflictRule(input.Name, input.First.Kind, Normalise(input.First), input.Second.Kind, Normalise(input.Second), input.Mode, input.Reason ?? string.Empty, input.OwnerId ?? actor.Id);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        context.ConflictRules.Add(rule);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(AuthorizationAuditEvents.SodRuleCreated, rule, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rule;
    }

    public async Task<Result<ConflictRule>> UpdateAsync(Guid id, ConflictRuleInput input, CancellationToken cancellationToken)
    {
        var refused = await CheckAsync(input, cancellationToken).ConfigureAwait(false);
        if (refused is not null)
        {
            return refused;
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var rule = await context.ConflictRules.SingleOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
        if (rule is null)
        {
            return RuleNotFound();
        }

        rule.Change(input.Name, input.First.Kind, Normalise(input.First), input.Second.Kind, Normalise(input.Second), input.Mode, input.Reason ?? string.Empty, input.OwnerId ?? rule.OwnerId);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(AuthorizationAuditEvents.SodRuleChanged, rule, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rule;
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var rule = await context.ConflictRules.SingleOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
        if (rule is null)
        {
            return RuleNotFound();
        }

        context.ConflictRules.Remove(rule);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(AuthorizationAuditEvents.SodRuleDeleted, rule, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>The conflicts granting this role at this scope would create for the person: those the new grant takes part in.</summary>
    public async Task<IReadOnlyList<Conflict>> ForGrantAsync(Guid principalId, Role role, RoleScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(scope);
        var rules = await RulesAsync(cancellationToken).ConfigureAwait(false);
        if (rules.Count == 0)
        {
            return [];
        }

        var held = (await HeldAsync([principalId], null, cancellationToken).ConfigureAwait(false)).GetValueOrDefault(principalId) ?? [];
        var paths = await PathsAsync(scope.UnitId is { } unit ? [unit] : [], cancellationToken).ConfigureAwait(false);
        var candidate = new HeldGrant(null, null, role.Id, role.Name, role.Permissions, role.Permissions, scope.Kind, scope.UnitId ?? scope.RecordId, scope.RecordType, scope.UnitId is { } id ? paths.GetValueOrDefault(id, []) : [], IsNew: true);
        return ConflictDetector.Find(rules, [.. held, candidate], onlyNew: true);
    }

    /// <summary>
    /// The conflicts a delegation would create for its delegate: the delegator's grants limited to the duties, as if the
    /// delegate held them beside their own rights and the delegations they already hold.
    /// </summary>
    public async Task<IReadOnlyList<Conflict>> ForDelegationAsync(Guid delegatorId, Guid delegateId, IReadOnlyCollection<string> dutyCodes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dutyCodes);
        var rules = await RulesAsync(cancellationToken).ConfigureAwait(false);
        if (rules.Count == 0)
        {
            return [];
        }

        var held = (await HeldAsync([delegateId], null, cancellationToken).ConfigureAwait(false)).GetValueOrDefault(delegateId) ?? [];
        var delegators = await OwnAsync([delegatorId], null, cancellationToken).ConfigureAwait(false);
        var handed = Delegated(delegators.GetValueOrDefault(delegatorId) ?? [], duties.PermissionsOf(dutyCodes), Guid.Empty, isNew: true);
        return ConflictDetector.Find(rules, [.. held, .. handed], onlyNew: true);
    }

    /// <summary>
    /// The conflicts changing roles' permissions would create (TF-036, ADR-0039): for everyone who holds one of the roles,
    /// by an assignment or through a delegation from someone who does, the conflicts found with the new permissions and
    /// not with the present ones. The changes are taken together, as an import applies them, so a pair that only two
    /// changes complete is found too.
    /// </summary>
    public async Task<IReadOnlyList<RoleChangeConflict>> ForRoleChangesAsync(IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var rules = await RulesAsync(cancellationToken).ConfigureAwait(false);
        if (rules.Count == 0 || changes.Count == 0)
        {
            return [];
        }

        var people = await HoldersAsync([.. changes.Keys], cancellationToken).ConfigureAwait(false);
        var before = await HeldAsync(people, null, cancellationToken).ConfigureAwait(false);
        var after = await HeldAsync(people, changes, cancellationToken).ConfigureAwait(false);
        var found = new List<RoleChangeConflict>();
        foreach (var person in people)
        {
            var existing = ConflictDetector.Find(rules, before.GetValueOrDefault(person) ?? [], onlyNew: false).Select(KeyOf).ToHashSet(StringComparer.Ordinal);
            found.AddRange(ConflictDetector.Find(rules, after.GetValueOrDefault(person) ?? [], onlyNew: false).Where(c => !existing.Contains(KeyOf(c))).Select(c => new RoleChangeConflict(person, c)));
        }

        return found;
    }

    /// <summary>
    /// The words of a refusal of a role's change by block rules: each rule by name, the changed roles it concerns, how
    /// many people would hold both of its sides, and its reason.
    /// </summary>
    public static string RoleChangeRefusal(IReadOnlyCollection<RoleChangeConflict> blocking, IReadOnlyCollection<Guid> changedRoles)
    {
        ArgumentNullException.ThrowIfNull(blocking);
        ArgumentNullException.ThrowIfNull(changedRoles);
        return string.Join(' ', blocking.GroupBy(c => c.Conflict.Rule.Id).Select(group =>
        {
            var rule = group.First().Conflict.Rule;
            var roles = group.SelectMany(c => new[] { c.Conflict.First, c.Conflict.Second })
                .Where(grant => changedRoles.Contains(grant.RoleId))
                .Select(grant => $"'{grant.RoleName}'")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);
            var count = group.Select(c => c.PrincipalId).Distinct().Count();
            var people = count == 1 ? "1 person" : $"{count.ToString(CultureInfo.InvariantCulture)} people";
            var reason = string.IsNullOrWhiteSpace(rule.Reason) ? string.Empty : $" {rule.Reason}";
            return $"The conflict rule '{rule.Name}' blocks this change to {string.Join(" and ", roles)}: {people} would hold both sides of it.{reason}";
        }));
    }

    /// <summary>
    /// The entries of everyone holding these roles, computed again in the caller's transaction after a change the rules
    /// allowed: the conflicts a warn rule lets through are listed at once, and those the change ended no longer are.
    /// </summary>
    public async Task RefreshHoldersAsync(IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken) =>
        await RefreshAsync(await HoldersAsync(roleIds, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The report, computed now for everyone (TF-036 AC3): every entry replaced, the run written and audited, in one
    /// transaction. The worker runs it daily; an administrator runs it on demand.
    /// </summary>
    public async Task<ConflictReportRun> RunReportAsync(string trigger, CancellationToken cancellationToken)
    {
        var rules = await RulesAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        await context.ConflictReportEntries.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<ConflictReportEntry>();
        if (rules.Count > 0)
        {
            var people = await context.RoleAssignments.AsNoTracking().Select(a => a.PrincipalId)
                .Union(context.Delegations.AsNoTracking().Where(d => d.EndsAt > now && (d.EndedAt == null || d.EndedAt > now)).Select(d => d.DelegateId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            entries = await EntriesAsync(rules, people, now, cancellationToken).ConfigureAwait(false);
            context.ConflictReportEntries.AddRange(entries);
        }

        var run = new ConflictReportRun(trigger, rules.Count, entries.Count);
        context.ConflictReportRuns.Add(run);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["trigger"] = trigger,
            ["rules"] = rules.Count.ToString(CultureInfo.InvariantCulture),
            ["conflicts"] = entries.Count.ToString(CultureInfo.InvariantCulture),
        };
        await audit.WriteAsync(new AuditEvent(AuthorizationAuditEvents.SodReportRun, AuditOutcome.Success, actor.Id, "conflict report", details, "conflict_report_run", run.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return run;
    }

    /// <summary>
    /// One person's entries, computed again after their rights changed, in the caller's transaction: a grant or a
    /// delegation a warn rule allowed is listed at once (TF-036 AC2), and a removed one no longer is.
    /// </summary>
    public Task RefreshPrincipalAsync(Guid principalId, CancellationToken cancellationToken) => RefreshAsync([principalId], cancellationToken);

    /// <summary>The words of a refusal by a block rule: the person, the rule by name, where, and the rule's reason (TF-036 AC1).</summary>
    public async Task<string> RefusalAsync(Conflict conflict, Guid principalId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        var principalName = principals is null ? null : (await principals.FindAsync([principalId], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(principalId)?.DisplayName;
        var where = conflict.UnitId is { } unitId
            ? $"at {await context.OrganisationUnits.AsNoTracking().Where(u => u.Id == unitId).Select(u => u.Name).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)}"
            : "across the whole organisation";
        var reason = string.IsNullOrWhiteSpace(conflict.Rule.Reason) ? string.Empty : $" {conflict.Rule.Reason}";
        return $"The conflict rule '{conflict.Rule.Name}' blocks this: {principalName ?? "this person"} would hold both sides of it {where}.{reason}";
    }

    /// <summary>These people's entries, replaced by what the rules find now, in the caller's transaction.</summary>
    private async Task RefreshAsync(IReadOnlyCollection<Guid> people, CancellationToken cancellationToken)
    {
        if (people.Count == 0)
        {
            return;
        }

        var rules = await RulesAsync(cancellationToken).ConfigureAwait(false);
        var ids = people.ToArray();
        await context.ConflictReportEntries.Where(e => ids.Contains(e.PrincipalId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (rules.Count == 0)
        {
            return;
        }

        context.ConflictReportEntries.AddRange(await EntriesAsync(rules, people, time.GetUtcNow(), cancellationToken).ConfigureAwait(false));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Everyone holding one of these roles: by an assignment, or through a delegation, active or to come, from someone who does.</summary>
    private async Task<List<Guid>> HoldersAsync(IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken)
    {
        var roles = roleIds.ToArray();
        var now = time.GetUtcNow();
        var holders = await context.RoleAssignments.AsNoTracking().Where(a => roles.Contains(a.RoleId)).Select(a => a.PrincipalId).Distinct().ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var delegates = await context.Delegations.AsNoTracking()
            .Where(d => holders.Contains(d.DelegatorId) && d.EndsAt > now && (d.EndedAt == null || d.EndedAt > now))
            .Select(d => d.DelegateId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. holders.Union(delegates)];
    }

    /// <summary>One key per conflict, the same before and after a change: the rule and the two grants.</summary>
    private static string KeyOf(Conflict conflict) => $"{conflict.Rule.Id:N}|{conflict.First.Key}|{conflict.Second.Key}";

    private async Task<List<ConflictReportEntry>> EntriesAsync(IReadOnlyList<ConflictRuleView> rules, IReadOnlyCollection<Guid> people, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var held = await HeldAsync(people, null, cancellationToken).ConfigureAwait(false);
        var found = held.SelectMany(p => ConflictDetector.Find(rules, p.Value, onlyNew: false).Select(c => (Principal: p.Key, Conflict: c))).ToList();
        if (found.Count == 0)
        {
            return [];
        }

        var byId = await context.ConflictRules.AsNoTracking().ToDictionaryAsync(r => r.Id, cancellationToken).ConfigureAwait(false);
        var unitIds = found.SelectMany(f => new[] { f.Conflict.UnitId, f.Conflict.First.UnitId, f.Conflict.Second.UnitId }).OfType<Guid>().Distinct().ToArray();
        var units = await context.OrganisationUnits.AsNoTracking().Where(u => unitIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, cancellationToken).ConfigureAwait(false);
        var names = principals is null
            ? new Dictionary<Guid, PrincipalSummary>()
            : await principals.FindAsync([.. found.Select(f => f.Principal).Distinct()], cancellationToken).ConfigureAwait(false);
        ConflictGrantFacts Facts(HeldGrant grant) => new(
            grant.AssignmentId,
            grant.DelegationId,
            grant.RoleName,
            Scopes.NameOf(grant.Kind),
            grant.UnitId,
            grant.UnitId is { } unit ? units.GetValueOrDefault(unit) : null,
            grant.DelegationId is null ? ConflictSources.Assignment : ConflictSources.Delegation);
        return [.. found.Select(f => new ConflictReportEntry(
            byId[f.Conflict.Rule.Id],
            f.Principal,
            names.GetValueOrDefault(f.Principal)?.DisplayName ?? f.Principal.ToString("D"),
            Facts(f.Conflict.First),
            Facts(f.Conflict.Second),
            f.Conflict.UnitId,
            f.Conflict.UnitId is { } unitId ? units.GetValueOrDefault(unitId) : null,
            now))];
    }

    /// <summary>
    /// Each person's own grants and the delegated grants of the delegations to them that are active or still to come;
    /// with <paramref name="changes"/>, as they would be with those roles' permissions replaced.
    /// </summary>
    private async Task<Dictionary<Guid, List<HeldGrant>>> HeldAsync(IReadOnlyCollection<Guid> people, IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>? changes, CancellationToken cancellationToken)
    {
        var own = await OwnAsync(people, changes, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var ids = people.ToArray();
        var delegations = await context.Delegations.AsNoTracking()
            .Where(d => ids.Contains(d.DelegateId) && d.EndsAt > now && (d.EndedAt == null || d.EndedAt > now))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var delegators = await OwnAsync([.. delegations.Select(d => d.DelegatorId).Distinct()], changes, cancellationToken).ConfigureAwait(false);
        var result = people.Distinct().ToDictionary(p => p, p => own.GetValueOrDefault(p) ?? []);
        foreach (var delegation in delegations)
        {
            result[delegation.DelegateId].AddRange(Delegated(delegators.GetValueOrDefault(delegation.DelegatorId) ?? [], duties.PermissionsOf(delegation.Duties), delegation.Id, isNew: false));
        }

        return result;
    }

    /// <summary>The assignments of these people with their roles (or the roles' changed permissions) and their units' paths, in two queries.</summary>
    private async Task<Dictionary<Guid, List<HeldGrant>>> OwnAsync(IReadOnlyCollection<Guid> people, IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>? changes, CancellationToken cancellationToken)
    {
        var ids = people.ToArray();
        var rows = await context.RoleAssignments.AsNoTracking()
            .Where(a => ids.Contains(a.PrincipalId))
            .Join(context.Roles.AsNoTracking(), a => a.RoleId, r => r.Id, (a, r) => new { a.Id, a.PrincipalId, a.ScopeKind, a.ScopeId, a.ScopeRecordType, RoleId = r.Id, r.Name, r.Permissions })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var unitIds = rows.Where(r => r.ScopeKind is Scopes.Unit or Scopes.UnitWithChildren).Select(r => r.ScopeId!.Value).Distinct().ToArray();
        var paths = await PathsAsync(unitIds, cancellationToken).ConfigureAwait(false);
        return rows.GroupBy(r => r.PrincipalId).ToDictionary(
            g => g.Key,
            g => g.Select(r =>
            {
                var kind = Scopes.KindOf(r.ScopeKind);
                var path = kind is RoleScopeKind.Unit or RoleScopeKind.UnitWithChildren ? paths.GetValueOrDefault(r.ScopeId!.Value, []) : [];
                IReadOnlyCollection<string> permissions = changes is not null && changes.TryGetValue(r.RoleId, out var changed) ? changed : r.Permissions;
                return new HeldGrant(r.Id, null, r.RoleId, r.Name, permissions, permissions, kind, r.ScopeId, r.ScopeRecordType, path, IsNew: false);
            }).ToList());
    }

    private async Task<Dictionary<Guid, IReadOnlyList<Guid>>> PathsAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken)
    {
        if (unitIds.Count == 0)
        {
            return [];
        }

        var ids = unitIds.ToArray();
        var rows = await context.OrganisationUnits.AsNoTracking().Where(u => ids.Contains(u.Id)).Select(u => new { u.Id, u.Path }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Id, r => OrganisationUnit.PathIds(r.Path));
    }

    /// <summary>A delegator's grants as a delegation hands them over: limited to the duties' permissions, or whole for <c>all</c>.</summary>
    private static IEnumerable<HeldGrant> Delegated(IEnumerable<HeldGrant> delegatorGrants, IReadOnlySet<string>? limit, Guid delegationId, bool isNew) =>
        delegatorGrants
            .Select(g => g with { DelegationId = delegationId, IsNew = isNew, Permissions = limit is null ? g.Permissions : [.. g.Permissions.Where(limit.Contains)] })
            .Where(g => g.Permissions.Count > 0);

    private async Task<Error?> CheckAsync(ConflictRuleInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > ConflictRule.NameMaxLength)
        {
            return Error.Validation(AuthorizationErrors.SodRuleInvalid, "A rule needs a name.", new FieldError("name", "required", $"Name the rule in at most {ConflictRule.NameMaxLength} characters."));
        }

        if (!ConflictModes.IsKnown(input.Mode))
        {
            return Error.Validation(AuthorizationErrors.SodRuleInvalid, "A rule blocks or warns.", new FieldError("mode", "unknown", "Choose block or warn."));
        }

        if ((input.Reason?.Length ?? 0) > ConflictRule.ReasonMaxLength)
        {
            return Error.Validation(AuthorizationErrors.SodRuleInvalid, "The reason is too long.", new FieldError("reason", "too_long", $"Use at most {ConflictRule.ReasonMaxLength} characters."));
        }

        foreach (var (side, field) in new[] { (input.First, "first"), (input.Second, "second") })
        {
            if (await SideErrorAsync(side, cancellationToken).ConfigureAwait(false) is { } words)
            {
                return Error.Validation(AuthorizationErrors.SodSideUnknown, words, new FieldError(field, "unknown", words));
            }
        }

        if (string.Equals(input.First.Kind, input.Second.Kind, StringComparison.Ordinal) && string.Equals(Normalise(input.First), Normalise(input.Second), StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation(AuthorizationErrors.SodSidesSame, "The two sides of a rule are the same.", new FieldError("second", "same", "Choose a different permission or role."));
        }

        if (input.OwnerId is { } owner && principals is not null && !(await principals.FindAsync([owner], cancellationToken).ConfigureAwait(false)).ContainsKey(owner))
        {
            return Error.Validation(AuthorizationErrors.PrincipalNotFound, "There is no such person to own the rule.", new FieldError("owner_id", "not_found", "Choose a person of this tenant."));
        }

        return null;
    }

    private async Task<string?> SideErrorAsync(ConflictSide? side, CancellationToken cancellationToken)
    {
        if (side is null || !ConflictSides.IsKnown(side.Kind) || string.IsNullOrWhiteSpace(side.Value))
        {
            return "Each side is a permission or a role.";
        }

        if (side.Kind == ConflictSides.Permission)
        {
            return catalogue.Contains(side.Value) ? null : $"There is no permission '{side.Value}'.";
        }

        return Guid.TryParse(side.Value, out var roleId) && await context.Roles.AnyAsync(r => r.Id == roleId, cancellationToken).ConfigureAwait(false)
            ? null
            : "There is no such role.";
    }

    private static string Normalise(ConflictSide side) => side.Kind == ConflictSides.Role && Guid.TryParse(side.Value, out var id) ? id.ToString("D") : side.Value.Trim();

    private static Error RuleNotFound() => Error.NotFound(AuthorizationErrors.SodRuleNotFound, "There is no conflict rule with this id.");

    private Task WriteAsync(string action, ConflictRule rule, CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = rule.Name,
            ["first"] = $"{rule.FirstKind}:{rule.FirstValue}",
            ["second"] = $"{rule.SecondKind}:{rule.SecondValue}",
            ["mode"] = rule.Mode,
            ["owner_id"] = rule.OwnerId.ToString("D"),
        };
        return audit.WriteAsync(new AuditEvent(action, AuditOutcome.Success, actor.Id, rule.Name, details, RecordType, rule.Id, rule.Reason), cancellationToken);
    }
}
