using System.Globalization;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Data.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Authorization.Services;

/// <summary>A delegation to create: from the delegator (null: the person asking) to the delegate, these duties, this period, this reason.</summary>
public sealed record DelegationInput(Guid? DelegatorId, Guid DelegateId, IReadOnlyList<string> Duties, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Reason);

/// <summary>A person on either side of a delegation, by name.</summary>
public sealed record DelegationPerson(Guid Id, string Name);

/// <summary>A duty as people read it.</summary>
public sealed record DutyView(string Code, string Name);

/// <summary>A delegation as its people and administrators see it: who, to whom, what, when, why, where it stands.</summary>
public sealed record DelegationView(
    Guid Id,
    DelegationPerson Delegator,
    DelegationPerson Delegate,
    IReadOnlyList<DutyView> Duties,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Reason,
    DelegationStatus Status,
    DateTimeOffset? EndedAt,
    string? EndReason,
    Guid CreatedBy,
    DateTimeOffset CreatedAt);

/// <summary>
/// Delegations of duties (TF-037, ADR-0037). A person delegates their own duties without a permission; an administrator
/// does it for someone with <c>authz.delegations.manage</c> at that person's home unit. Refused, each with its code: to
/// oneself; a service account on either side; the same delegator and a shared duty in overlapping periods; a cycle of any
/// length among delegations that overlap the period; and onward delegation, either while acting for someone or of a duty
/// the delegator holds by delegation in the period. Every change and its audit event are one transaction, taken under
/// one lock so two requests cannot pass each other's checks.
/// </summary>
public sealed class DelegationService(
    AuthorizationDbContext context,
    IPrincipals principals,
    DutyDeclarations duties,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal current,
    IPermissionEvaluator evaluator,
    AccessGrants grants,
    TimeProvider time,
    ConflictService segregation)
{
    public const string RecordType = "delegation";

    /// <summary>The key of the advisory lock every change of delegations takes.</summary>
    public const long DelegationsLockKey = 0x1A_0B_DE_37;

    public async Task<Result<DelegationView>> CreateAsync(DelegationInput input, string operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (current.ActingForId is not null)
        {
            return Error.Validation(AuthorizationErrors.DelegationOnward, "You are acting for someone else. Delegated rights cannot be delegated again: stop acting, then delegate your own duties.");
        }

        var delegatorId = input.DelegatorId ?? current.Id;
        if (delegatorId == input.DelegateId)
        {
            return Error.Validation(AuthorizationErrors.DelegationSelf, "A person cannot delegate duties to themselves. Choose someone else.", new FieldError("delegate_id", "self", "Choose someone else."));
        }

        var people = await principals.FindAsync([delegatorId, input.DelegateId], cancellationToken).ConfigureAwait(false);
        if (!people.TryGetValue(delegatorId, out var delegator) || !people.TryGetValue(input.DelegateId, out var delegateTo))
        {
            return Error.NotFound(AuthorizationErrors.PrincipalNotFound, "There is no such person in this tenant.");
        }

        if (delegatorId != current.Id && await RefusalAsync(delegator, operation, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        if (delegator.Type != PrincipalKind.User || delegateTo.Type != PrincipalKind.User)
        {
            return Error.Validation(AuthorizationErrors.DelegationServiceAccount, "Duties pass between people only: a service account neither delegates nor receives them.", new FieldError("delegate_id", "service_account", "Choose a person, not a service account."));
        }

        var codes = input.Duties.Distinct(StringComparer.Ordinal).ToList();
        var unknown = codes.Where(code => duties.Find(code) is null).ToList();
        if (codes.Count == 0 || unknown.Count > 0)
        {
            var words = codes.Count == 0 ? "Choose at least one duty." : $"There is no duty {string.Join(", ", unknown)}.";
            return Error.Validation(AuthorizationErrors.DelegationDutyUnknown, words, new FieldError("duties", "unknown", words));
        }

        if (codes.Contains(KernelDuties.AllCode, StringComparer.Ordinal))
        {
            codes = [KernelDuties.AllCode];
        }

        var now = time.GetUtcNow();
        if (input.EndsAt <= input.StartsAt || input.EndsAt <= now)
        {
            return Error.Validation(AuthorizationErrors.DelegationPeriod, "The period must end after it starts, and in the future.", new FieldError("ends_at", "period", "End after the start, and in the future."));
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock({DelegationsLockKey})", cancellationToken).ConfigureAwait(false);

        // Every delegation that counts at some moment of the new one's period: the rules look at these only.
        var overlapping = await context.Delegations
            .Where(d => d.StartsAt < input.EndsAt && input.StartsAt < d.EndsAt && (d.EndedAt == null || input.StartsAt < d.EndedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (overlapping.FirstOrDefault(d => d.DelegatorId == delegatorId && d.SharesDutyWith(codes)) is { } clash)
        {
            return Error.Conflict(AuthorizationErrors.DelegationOverlap, $"{delegator.DisplayName} already delegates {Words(clash.Duties)} to someone from {Day(clash.StartsAt)} to {Day(clash.Until)}. A duty goes to one person at a time: end that delegation or choose another period.");
        }

        if (overlapping.FirstOrDefault(d => d.DelegateId == delegatorId && d.SharesDutyWith(codes)) is { } held)
        {
            return Error.Conflict(AuthorizationErrors.DelegationOnward, $"{delegator.DisplayName} holds {Words(held.Duties)} by delegation in this period. Delegated rights cannot be delegated again.");
        }

        if (Reaches(overlapping, input.DelegateId, delegatorId))
        {
            return Error.Conflict(AuthorizationErrors.DelegationCycle, $"{delegateTo.DisplayName} already delegates, directly or through others, to {delegator.DisplayName} in this period. Delegations cannot go round in a circle.");
        }

        // Segregation of duties (TF-036, ADR-0039): the delegate's own rights and the delegated duties' rights as if held
        // together, by the same rules as a grant.
        var conflicts = await segregation.ForDelegationAsync(delegatorId, input.DelegateId, codes, cancellationToken).ConfigureAwait(false);
        if (conflicts.FirstOrDefault(c => c.Rule.Mode == ConflictModes.Block) is { } blocking)
        {
            return Error.Conflict(AuthorizationErrors.SodConflict, await segregation.RefusalAsync(blocking, input.DelegateId, cancellationToken).ConfigureAwait(false));
        }

        var delegation = new Delegation(delegatorId, input.DelegateId, codes, input.StartsAt, input.EndsAt, input.Reason);
        context.Delegations.Add(delegation);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEvent(AuthorizationAuditEvents.DelegationCreated, AuditOutcome.Success, current.Id, delegateTo.DisplayName, Details(delegation), RecordType, delegation.Id, delegation.Reason), cancellationToken).ConfigureAwait(false);
        if (conflicts.Count > 0)
        {
            await segregation.RefreshPrincipalAsync(input.DelegateId, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await DescribeAsync([delegation], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>Ends a delegation now: by its delegator, its delegate, or an administrator of the delegator.</summary>
    public async Task<Result<DelegationView>> EndAsync(Guid id, string? reason, string operation, CancellationToken cancellationToken)
    {
        var delegation = await context.Delegations.SingleOrDefaultAsync(d => d.Id == id, cancellationToken).ConfigureAwait(false);
        if (delegation is null)
        {
            return Error.NotFound(AuthorizationErrors.DelegationNotFound, "There is no delegation with this id.");
        }

        var now = time.GetUtcNow();
        if (delegation.StatusAt(now) is DelegationStatus.Ended or DelegationStatus.Expired)
        {
            return Error.Conflict(AuthorizationErrors.DelegationNotRunning, "This delegation has ended already.");
        }

        if (current.Id != delegation.DelegatorId && current.Id != delegation.DelegateId)
        {
            var delegator = (await principals.FindAsync([delegation.DelegatorId], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(delegation.DelegatorId);
            if (delegator is null)
            {
                return Error.NotFound(AuthorizationErrors.PrincipalNotFound, "There is no such person in this tenant.");
            }

            if (await RefusalAsync(delegator, operation, cancellationToken).ConfigureAwait(false) is { } refused)
            {
                return refused;
            }
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        delegation.End(now, current.Id, reason);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEvent(AuthorizationAuditEvents.DelegationEnded, AuditOutcome.Success, current.Id, null, Details(delegation), RecordType, delegation.Id, delegation.EndReason), cancellationToken).ConfigureAwait(false);
        await segregation.RefreshPrincipalAsync(delegation.DelegateId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await DescribeAsync([delegation], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <summary>The delegations a person gives and holds, newest first.</summary>
    public async Task<IReadOnlyList<DelegationView>> MineAsync(CancellationToken cancellationToken)
    {
        var me = current.Id;
        var rows = await context.Delegations.AsNoTracking()
            .Where(d => d.DelegatorId == me || d.DelegateId == me)
            .OrderByDescending(d => d.StartsAt)
            .Take(200)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return await DescribeAsync(rows, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The delegations an administrator may see, for a paged list: every one with <c>authz.delegations.manage</c>
    /// tenant-wide; otherwise those whose delegator's home unit is a unit where they hold it. Null when they hold it
    /// nowhere. Read straight from the grants: a row left out is not a denial, so nothing is audited for it.
    /// </summary>
    public async Task<IQueryable<Delegation>?> ManagedAsync(CancellationToken cancellationToken)
    {
        var access = await grants.AccessibleAsync(current.Id, AuthorizationPermissions.DelegationsManage, RecordType, cancellationToken).ConfigureAwait(false);
        if (access.IsEverything)
        {
            return context.Delegations.AsNoTracking();
        }

        if (access.Units.Count == 0)
        {
            return null;
        }

        var people = await principals.HomedAtAsync(access.Units, cancellationToken).ConfigureAwait(false);
        return context.Delegations.AsNoTracking().Where(d => people.Contains(d.DelegatorId));
    }

    public async Task<IReadOnlyList<DelegationView>> DescribeAsync(IReadOnlyList<Delegation> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var ids = rows.SelectMany(d => new[] { d.DelegatorId, d.DelegateId }).Distinct().ToList();
        var people = await principals.FindAsync(ids, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        DelegationPerson Person(Guid id) => new(id, people.GetValueOrDefault(id)?.DisplayName ?? id.ToString("D"));
        return [.. rows.Select(d => new DelegationView(
            d.Id,
            Person(d.DelegatorId),
            Person(d.DelegateId),
            [.. d.Duties.Select(code => new DutyView(code, duties.Find(code)?.Duty.Name ?? code))],
            d.StartsAt,
            d.EndsAt,
            d.Reason,
            d.StatusAt(now),
            d.EndedAt,
            d.EndReason,
            d.CreatedBy,
            d.CreatedAt))];
    }

    /// <summary>Whether a path of delegations leads from one person to another: adding the reverse edge would close a circle.</summary>
    private static bool Reaches(IReadOnlyList<Delegation> delegations, Guid from, Guid to)
    {
        var seen = new HashSet<Guid> { from };
        var queue = new Queue<Guid>([from]);
        while (queue.TryDequeue(out var person))
        {
            foreach (var next in delegations.Where(d => d.DelegatorId == person).Select(d => d.DelegateId))
            {
                if (next == to)
                {
                    return true;
                }

                if (seen.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return false;
    }

    /// <summary>The administrator's check at the delegator's home unit (the tenant for a person without one); the evaluator audits a denial.</summary>
    private async Task<Error?> RefusalAsync(PrincipalSummary delegator, string operation, CancellationToken cancellationToken)
    {
        var scope = delegator.HomeUnitId is { } unit ? AccessScope.Unit(unit) : AccessScope.Tenant;
        var decision = await evaluator.EvaluateAsync(new AccessRequest(current.Id, AuthorizationPermissions.DelegationsManage, operation, operation) { Scope = scope }, cancellationToken).ConfigureAwait(false);
        return decision.Allowed
            ? null
            : Error.Forbidden(PermissionErrors.PermissionDenied, $"Delegating for {delegator.DisplayName} needs {AuthorizationPermissions.DelegationsManage.Name} at their home unit. Ask them to delegate, or an administrator who holds it.");
    }

    private async Task<bool> HoldsAsync(AccessScope scope, CancellationToken cancellationToken) =>
        (await grants.PermissionsAtAsync(current.Id, scope, cancellationToken).ConfigureAwait(false)).Contains(AuthorizationPermissions.DelegationsManage.Name);

    private Dictionary<string, string> Details(Delegation delegation) => new(StringComparer.Ordinal)
    {
        ["delegator"] = delegation.DelegatorId.ToString("D"),
        ["delegate"] = delegation.DelegateId.ToString("D"),
        ["duties"] = string.Join(",", delegation.Duties),
        ["starts_at"] = delegation.StartsAt.ToString("o", CultureInfo.InvariantCulture),
        ["ends_at"] = delegation.EndsAt.ToString("o", CultureInfo.InvariantCulture),
    };

    private string Words(IEnumerable<string> codes) => string.Join(", ", codes.Select(code => duties.Find(code)?.Duty.Name ?? code));

    private static string Day(DateTimeOffset at) => at.ToString("d MMM yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
}

/// <summary>The active delegations, for Identity's session and <c>/me</c> (<see cref="IDelegations"/>).</summary>
public sealed class DelegationQueries(AuthorizationDbContext context, TimeProvider time) : IDelegations
{
    public async Task<ActiveDelegation?> ActiveAsync(Guid delegatorId, Guid delegateId, CancellationToken cancellationToken)
    {
        var all = await ActiveOfAsync(delegatorId, cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(d => d.DelegatorId == delegatorId && d.DelegateId == delegateId);
    }

    public async Task<IReadOnlyList<ActiveDelegation>> ActiveOfAsync(Guid principalId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var rows = await context.Delegations.AsNoTracking()
            .Where(d => (d.DelegatorId == principalId || d.DelegateId == principalId) && d.StartsAt <= now && d.EndsAt > now && (d.EndedAt == null || d.EndedAt > now))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rows
            .GroupBy(d => (d.DelegatorId, d.DelegateId))
            .Select(g => new ActiveDelegation(
                g.Key.DelegatorId,
                g.Key.DelegateId,
                g.Any(d => d.CoversAll) ? [KernelDuties.AllCode] : [.. g.SelectMany(d => d.Duties).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                g.Max(d => d.Until)))];
    }
}
