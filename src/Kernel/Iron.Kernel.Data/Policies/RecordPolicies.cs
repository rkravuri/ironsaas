using System.Text.RegularExpressions;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Contexts;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Sharing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Data.Policies;

/// <summary>
/// The one way to list business records and to open one by id (TF-035, ADR-0032): the scope rule for the record
/// type's read permission first, then the module's policy, both in the query.
/// </summary>
public interface IRecordPolicies
{
    /// <summary>The policy of a record type for one principal.</summary>
    PolicyEvaluation<T> For<T>(Guid principalId)
        where T : BusinessRecord;

    /// <summary>The records of the query the signed-in principal may read.</summary>
    Task<PolicyQuery<T>> ApplyAsync<T>(IQueryable<T> source, CancellationToken cancellationToken)
        where T : BusinessRecord;

    /// <summary>One record for an action by the signed-in principal: not found when they may not see it, forbidden when they may see it but not act.</summary>
    Task<Result<T>> FindAsync<T>(Guid id, string action, CancellationToken cancellationToken)
        where T : BusinessRecord;
}

/// <summary>A record type's policy bound to one principal: apply it to a query, or open one record for an action.</summary>
public sealed class PolicyEvaluation<T>
    where T : BusinessRecord
{
    private readonly RecordPolicyRegistration _registration;
    private readonly IRecordPolicy<T> _policy;
    private readonly Func<DbSet<T>> _set;
    private readonly ILogger _logger;
    private readonly IUnitReach? _units;
    private readonly IRecordAudit? _audit;

    internal PolicyEvaluation(RecordPolicyRegistration registration, IRecordPolicy<T> policy, PolicySubject subject, Func<DbSet<T>> set, ILogger logger, IUnitReach? units, IRecordAudit? audit)
    {
        _registration = registration;
        _policy = policy;
        Subject = subject;
        _set = set;
        _logger = logger;
        _units = units;
        _audit = audit;
    }

    public PolicySubject Subject { get; }

    /// <summary>Whether the principal may take an action on a record already read: for a page to offer the action or not. Nothing is audited.</summary>
    public async Task<bool> AllowsAsync(T record, string action, CancellationToken cancellationToken) =>
        await _policy.DecideAsync(record, action, Subject, cancellationToken).ConfigureAwait(false) == PolicyDecision.Allowed;

    /// <summary>
    /// A list: the scope rule (ADR-0030) for the read permission, following the record's sharing mode, then the policy's
    /// filter, joined with AND, so a policy narrows and never widens; then the reader's working unit and the units below
    /// it, unless they see all their units (ADR-0038). Each page it reads is audited when it holds private records of
    /// units outside the reader's home unit.
    /// </summary>
    public async Task<PolicyQuery<T>> ApplyAsync(IQueryable<T> source, CancellationToken cancellationToken)
    {
        var query = await ReadableAsync(source, cancellationToken).ConfigureAwait(false);
        var working = _units is null ? null : await _units.WorkingUnitsAsync(Subject.PrincipalId, cancellationToken).ConfigureAwait(false);
        if (working is not null)
        {
            var reach = await Subject.ReachAsync(_registration.Read, cancellationToken).ConfigureAwait(false);
            query = query.Where(Subject.Visibility.Within<T>(working, reach));
        }

        return new PolicyQuery<T>(query, ListedAsync);
    }

    /// <summary>
    /// The record for the action, tracked by the module's context so the caller can change it and save. Not found when
    /// it does not exist or the subject may not read it; forbidden when it may read it but the policy refuses the action
    /// or has no rule for it. The working unit does not narrow a read by id: a link works from any unit the reader may
    /// see. A read of a private record from outside the reader's home unit is audited, and so is every refusal.
    /// </summary>
    public async Task<Result<T>> FindAsync(Guid id, string action, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        var readable = await ReadableAsync(_set(), cancellationToken).ConfigureAwait(false);
        var record = await readable.SingleOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            await RefusedAsync(id, action, RecordAnswers.NotFound, cancellationToken).ConfigureAwait(false);
            return NotFound();
        }

        if (!string.Equals(action, RecordActions.Read, StringComparison.Ordinal))
        {
            var decision = await _policy.DecideAsync(record, action, Subject, cancellationToken).ConfigureAwait(false);
            if (decision != PolicyDecision.Allowed)
            {
                _logger.LogWarning("The {RecordType} policy refused {Action} on {RecordId} to principal {PrincipalId}: {Decision}.", _registration.Name, action, id, Subject.PrincipalId, decision);
                var notVisible = decision == PolicyDecision.NotVisible;
                await RefusedAsync(id, action, notVisible ? RecordAnswers.NotFound : RecordAnswers.Forbidden, cancellationToken).ConfigureAwait(false);
                return notVisible
                    ? NotFound()
                    : Error.Forbidden(RecordErrors.Forbidden, $"You may see this record but not {action.Replace('_', ' ')} it.");
            }
        }

        await ReadByIdAsync(record, action, cancellationToken).ConfigureAwait(false);
        return record;
    }

    /// <summary>The scope rule following the sharing mode, then the policy: what the reader may read, before the working unit.</summary>
    private async Task<IQueryable<T>> ReadableAsync(IQueryable<T> source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var reach = await Subject.ReachAsync(_registration.Read, cancellationToken).ConfigureAwait(false);
        var readable = await _policy.ReadableAsync(Subject, cancellationToken).ConfigureAwait(false);
        return source.Where(Subject.Visibility.Predicate<T>(reach)).Where(readable);
    }

    /// <summary>A record read by id from outside the reader's home unit: audited with what allowed it, unless it is the tenant's.</summary>
    private async Task ReadByIdAsync(T record, string action, CancellationToken cancellationToken)
    {
        if (_audit is null || _units is null || record.SharingMode == SharingMode.Tenant)
        {
            return;
        }

        var home = await _units.HomeUnitsAsync(Subject.PrincipalId, cancellationToken).ConfigureAwait(false);
        if (home.Contains(record.OwningOrganisationId))
        {
            return;
        }

        var reach = await Subject.ReachAsync(_registration.Read, cancellationToken).ConfigureAwait(false);
        var allowedBy = await Subject.Visibility.ReasonAsync(record, reach, cancellationToken).ConfigureAwait(false) ?? VisibleBy.Scope;
        await _audit.ReadAsync(new RecordReadEvent(Subject.PrincipalId, _registration.Name, record.Id, record.OwningOrganisationId, action, allowedBy), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A refused read of a record that exists, audited as the reader was answered; an id of no record is nobody's to audit.</summary>
    private async Task RefusedAsync(Guid id, string action, string answer, CancellationToken cancellationToken)
    {
        if (_audit is null)
        {
            return;
        }

        var owner = await _set().AsNoTracking().Where(r => r.Id == id).Select(r => (Guid?)r.OwningOrganisationId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (owner is { } unit)
        {
            await _audit.RefusedAsync(new RecordRefusedEvent(Subject.PrincipalId, _registration.Name, id, unit, action, answer), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One event for a page that holds private records of units outside the reader's home unit, not one per row.</summary>
    private async Task ListedAsync(IReadOnlyList<T> items, CancellationToken cancellationToken)
    {
        if (_audit is null || _units is null || items.All(r => r.SharingMode == SharingMode.Tenant))
        {
            return;
        }

        var home = await _units.HomeUnitsAsync(Subject.PrincipalId, cancellationToken).ConfigureAwait(false);
        var outside = items.Where(r => r.SharingMode != SharingMode.Tenant && !home.Contains(r.OwningOrganisationId)).Select(r => r.Id).ToList();
        if (outside.Count > 0)
        {
            await _audit.ListedAsync(new RecordsListedEvent(Subject.PrincipalId, _registration.Name, outside), cancellationToken).ConfigureAwait(false);
        }
    }

    private static Error NotFound() => Error.NotFound(RecordErrors.NotFound, "There is no record with this id that you may see.");
}

/// <summary>The engine behind <see cref="IRecordPolicies"/>, registered by <c>AddIronDatabase</c>.</summary>
public sealed class RecordPolicies(
    IServiceProvider services,
    IRecordAccess access,
    ICurrentPrincipal principal,
    IEnumerable<RecordPolicyRegistration> registrations,
    IEnumerable<RegisteredContext> contexts,
    ILogger<RecordPolicies> logger,
    IUnitReach? units = null,
    IRecordAudit? audit = null) : IRecordPolicies
{
    public PolicyEvaluation<T> For<T>(Guid principalId)
        where T : BusinessRecord
    {
        var registration = registrations.SingleOrDefault(r => r.RecordType == typeof(T))
            ?? throw new InvalidOperationException($"No record policy is registered for {typeof(T).Name}. Register one in the module's AddServices with AddRecordPolicy (ADR-0032).");
        var policy = services.GetRequiredService<IRecordPolicy<T>>();

        // The shares come from the record's own module context, which maps the kernel's table, so a list joins them.
        var visibility = new RecordVisibility(registration.Name, ContextOf<T>().Set<RecordShare>());
        var subject = new PolicySubject(principalId, (permission, ct) => access.AccessibleAsync(principalId, permission, registration.Name, ct), visibility);
        return new PolicyEvaluation<T>(registration, policy, subject, SetOf<T>, logger, units, audit);
    }

    /// <summary>The module context that maps a business record type.</summary>
    private DbContext ContextOf<T>()
        where T : class => ModuleContexts.Of<T>(services, contexts);

    public Task<PolicyQuery<T>> ApplyAsync<T>(IQueryable<T> source, CancellationToken cancellationToken)
        where T : BusinessRecord => For<T>(principal.Id).ApplyAsync(source, cancellationToken);

    public Task<Result<T>> FindAsync<T>(Guid id, string action, CancellationToken cancellationToken)
        where T : BusinessRecord => For<T>(principal.Id).FindAsync(id, action, cancellationToken);

    private DbSet<T> SetOf<T>()
        where T : class => ContextOf<T>().Set<T>();
}

public static partial class RecordPolicyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the policy of a business record type (ADR-0032): the record type name that record-scoped assignments
    /// use (snake_case, its code), what people call these records (plural, as a list heading names them: "Purchase
    /// orders"), the permission that reads it, and the policy, built from <see cref="RecordRules"/>. One per type.
    /// </summary>
    public static IServiceCollection AddRecordPolicy<T>(this IServiceCollection services, string recordType, string displayName, Permission read, IRecordPolicy<T> policy)
        where T : BusinessRecord
    {
        ArgumentNullException.ThrowIfNull(policy);
        Register<T>(services, recordType, displayName, read);
        services.AddSingleton(policy);
        return services;
    }

    /// <summary>The same with a policy class of the module's own, resolved per request so it may use the module's services.</summary>
    public static IServiceCollection AddRecordPolicy<T, TPolicy>(this IServiceCollection services, string recordType, string displayName, Permission read)
        where T : BusinessRecord
        where TPolicy : class, IRecordPolicy<T>
    {
        Register<T>(services, recordType, displayName, read);
        services.AddScoped<IRecordPolicy<T>, TPolicy>();
        return services;
    }

    private static void Register<T>(IServiceCollection services, string recordType, string displayName, Permission read)
        where T : BusinessRecord
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(read);
        if (recordType is null || !RecordTypeName().IsMatch(recordType))
        {
            throw new ArgumentException($"'{recordType}' is not a record type name. Use snake_case of at most 100 characters, for example purchase_order.", nameof(recordType));
        }

        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 100)
        {
            throw new ArgumentException($"{typeof(T).Name} needs a display name of 1 to 100 characters: what people call these records, for example Purchase orders.", nameof(displayName));
        }

        if (services.Any(d => d.ServiceType == typeof(RecordPolicyRegistration) && d.ImplementationInstance is RecordPolicyRegistration r && r.RecordType == typeof(T)))
        {
            throw new InvalidOperationException($"{typeof(T).Name} has a record policy already; a record type has one policy (ADR-0032).");
        }

        services.AddSingleton(new RecordPolicyRegistration(typeof(T), recordType, read, displayName.Trim()));
        services.TryAddScoped<IRecordPolicies, RecordPolicies>();
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,99}$")]
    private static partial Regex RecordTypeName();
}
