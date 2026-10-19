using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Lists;
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

    internal PolicyEvaluation(RecordPolicyRegistration registration, IRecordPolicy<T> policy, PolicySubject subject, Func<DbSet<T>> set, ILogger logger)
    {
        _registration = registration;
        _policy = policy;
        Subject = subject;
        _set = set;
        _logger = logger;
    }

    public PolicySubject Subject { get; }

    /// <summary>The scope rule (ADR-0030) for the read permission, then the policy's filter, joined with AND: a policy narrows, never widens.</summary>
    public async Task<PolicyQuery<T>> ApplyAsync(IQueryable<T> source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var reach = await Subject.ReachAsync(_registration.Read, cancellationToken).ConfigureAwait(false);
        var readable = await _policy.ReadableAsync(Subject, cancellationToken).ConfigureAwait(false);
        return new PolicyQuery<T>(source.WhereAccessible(reach).Where(readable));
    }

    /// <summary>
    /// The record for the action, tracked by the module's context so the caller can change it and save. Not found when
    /// it does not exist or the subject may not read it; forbidden when it may read it but the policy refuses the action
    /// or has no rule for it.
    /// </summary>
    public async Task<Result<T>> FindAsync(Guid id, string action, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        var readable = await ApplyAsync(_set(), cancellationToken).ConfigureAwait(false);
        var record = await readable.Query.SingleOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound();
        }

        if (string.Equals(action, RecordActions.Read, StringComparison.Ordinal))
        {
            return record;
        }

        var decision = await _policy.DecideAsync(record, action, Subject, cancellationToken).ConfigureAwait(false);
        if (decision == PolicyDecision.Allowed)
        {
            return record;
        }

        _logger.LogWarning("The {RecordType} policy refused {Action} on {RecordId} to principal {PrincipalId}: {Decision}.", _registration.Name, action, id, Subject.PrincipalId, decision);
        return decision == PolicyDecision.NotVisible
            ? NotFound()
            : Error.Forbidden(RecordErrors.Forbidden, $"You may see this record but not {action.Replace('_', ' ')} it.");
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
    ILogger<RecordPolicies> logger) : IRecordPolicies
{
    // A record type is mapped by one module context for the life of the process; found once, then remembered.
    private static readonly ConcurrentDictionary<Type, Type> ContextOfRecord = new();

    public PolicyEvaluation<T> For<T>(Guid principalId)
        where T : BusinessRecord
    {
        var registration = registrations.SingleOrDefault(r => r.RecordType == typeof(T))
            ?? throw new InvalidOperationException($"No record policy is registered for {typeof(T).Name}. Register one in the module's AddServices with AddRecordPolicy (ADR-0032).");
        var policy = services.GetRequiredService<IRecordPolicy<T>>();
        var subject = new PolicySubject(principalId, (permission, ct) => access.AccessibleAsync(principalId, permission, registration.Name, ct));
        return new PolicyEvaluation<T>(registration, policy, subject, SetOf<T>, logger);
    }

    public Task<PolicyQuery<T>> ApplyAsync<T>(IQueryable<T> source, CancellationToken cancellationToken)
        where T : BusinessRecord => For<T>(principal.Id).ApplyAsync(source, cancellationToken);

    public Task<Result<T>> FindAsync<T>(Guid id, string action, CancellationToken cancellationToken)
        where T : BusinessRecord => For<T>(principal.Id).FindAsync(id, action, cancellationToken);

    private DbSet<T> SetOf<T>()
        where T : class
    {
        var contextType = ContextOfRecord.GetOrAdd(typeof(T), _ =>
            contexts.Select(c => c.ContextType).FirstOrDefault(type => ((DbContext)services.GetRequiredService(type)).Model.FindEntityType(typeof(T)) is not null)
            ?? throw new InvalidOperationException($"No module context maps {typeof(T).Name}; register the module's context with AddIronDbContext."));
        return ((DbContext)services.GetRequiredService(contextType)).Set<T>();
    }
}

public static partial class RecordPolicyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the policy of a business record type (ADR-0032): the record type name that record-scoped assignments
    /// use (snake_case), the permission that reads it, and the policy, built from <see cref="RecordRules"/>. One per type.
    /// </summary>
    public static IServiceCollection AddRecordPolicy<T>(this IServiceCollection services, string recordType, Permission read, IRecordPolicy<T> policy)
        where T : BusinessRecord
    {
        ArgumentNullException.ThrowIfNull(policy);
        Register<T>(services, recordType, read);
        services.AddSingleton(policy);
        return services;
    }

    /// <summary>The same with a policy class of the module's own, resolved per request so it may use the module's services.</summary>
    public static IServiceCollection AddRecordPolicy<T, TPolicy>(this IServiceCollection services, string recordType, Permission read)
        where T : BusinessRecord
        where TPolicy : class, IRecordPolicy<T>
    {
        Register<T>(services, recordType, read);
        services.AddScoped<IRecordPolicy<T>, TPolicy>();
        return services;
    }

    private static void Register<T>(IServiceCollection services, string recordType, Permission read)
        where T : BusinessRecord
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(read);
        if (recordType is null || !RecordTypeName().IsMatch(recordType))
        {
            throw new ArgumentException($"'{recordType}' is not a record type name. Use snake_case of at most 100 characters, for example purchase_order.", nameof(recordType));
        }

        if (services.Any(d => d.ServiceType == typeof(RecordPolicyRegistration) && d.ImplementationInstance is RecordPolicyRegistration r && r.RecordType == typeof(T)))
        {
            throw new InvalidOperationException($"{typeof(T).Name} has a record policy already; a record type has one policy (ADR-0032).");
        }

        services.AddSingleton(new RecordPolicyRegistration(typeof(T), recordType, read));
        services.TryAddScoped<IRecordPolicies, RecordPolicies>();
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,99}$")]
    private static partial Regex RecordTypeName();
}
