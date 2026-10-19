using System.Linq.Expressions;
using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Data.Policies;

/// <summary>
/// What one permission reaches in a list of records of a type (TF-033, ADR-0030), computed once per list: every record,
/// or the records of a set of units plus the records granted one by one. Iron.Kernel.Authorization implements it; the
/// record policies apply it first (ADR-0032).
/// </summary>
public interface IRecordAccess
{
    Task<RecordAccess> AccessibleAsync(Guid principalId, Permission permission, string recordType, CancellationToken cancellationToken);
}

/// <summary>The actions every record knows. A module adds its own in snake_case, for example <c>approve</c>.</summary>
public static class RecordActions
{
    public const string Read = "read";
    public const string Update = "update";
    public const string Delete = "delete";
}

/// <summary>What a policy answers for one record and one action (ADR-0032).</summary>
public enum PolicyDecision
{
    /// <summary>The subject may perform the action.</summary>
    Allowed,

    /// <summary>The subject may see the record but not perform the action: forbidden (403).</summary>
    NotAllowed,

    /// <summary>The subject may not even see the record: not found (404), as for an id that does not exist.</summary>
    NotVisible,

    /// <summary>
    /// The policy has nothing to say about this action. <c>All</c> and <c>Any</c> skip it; at the top it means not
    /// allowed, because what no rule allows is denied.
    /// </summary>
    NoRule,
}

/// <summary>
/// Who asks, and what each of their permissions reaches for one record type (ADR-0030). The reach of a permission is
/// asked once and kept for the rest of the evaluation, so a policy can ask for it as often as it likes.
/// </summary>
public sealed class PolicySubject
{
    private readonly Func<Permission, CancellationToken, Task<RecordAccess>> _reach;
    private readonly Dictionary<string, RecordAccess> _known = new(StringComparer.Ordinal);

    public PolicySubject(Guid principalId, Func<Permission, CancellationToken, Task<RecordAccess>> reach)
    {
        ArgumentNullException.ThrowIfNull(reach);
        PrincipalId = principalId;
        _reach = reach;
    }

    public Guid PrincipalId { get; }

    public async Task<RecordAccess> ReachAsync(Permission permission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(permission);
        if (!_known.TryGetValue(permission.Name, out var reach))
        {
            reach = await _reach(permission, cancellationToken).ConfigureAwait(false);
            _known[permission.Name] = reach;
        }

        return reach;
    }
}

/// <summary>
/// A module's rule for one record type (TF-035, ADR-0032). The kernel applies the scope rule for the registered read
/// permission first and joins this policy to it, so a policy narrows what the scope shows and never widens it. Build one
/// from <see cref="RecordRules"/> in a few lines, and register it with <c>AddRecordPolicy</c>.
/// </summary>
public interface IRecordPolicy<T>
    where T : BusinessRecord
{
    /// <summary>The records the subject may read, as a predicate the database runs.</summary>
    Task<Expression<Func<T, bool>>> ReadableAsync(PolicySubject subject, CancellationToken cancellationToken);

    /// <summary>What the subject may do with one record it can read: <c>read</c>, <c>update</c>, <c>delete</c> or a module's own action.</summary>
    Task<PolicyDecision> DecideAsync(T record, string action, PolicySubject subject, CancellationToken cancellationToken);
}

/// <summary>The error codes of a read by id through a policy (ADR-0032).</summary>
public static class RecordErrors
{
    /// <summary>No record with this id that the reader may see: the same answer for both, so a link reveals nothing.</summary>
    public const string NotFound = "data.record.not_found";

    /// <summary>The reader may see the record but not perform the action.</summary>
    public const string Forbidden = "data.record.forbidden";
}

/// <summary>One record type's policy as registered: the entity, the record type name record-scoped assignments use, and the permission that reads it.</summary>
public sealed record RecordPolicyRegistration(Type RecordType, string Name, Permission Read);
