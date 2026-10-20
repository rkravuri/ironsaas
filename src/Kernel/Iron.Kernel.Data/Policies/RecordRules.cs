using System.Linq.Expressions;
using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Data.Policies;

/// <summary>
/// The kernel's building blocks for record policies (TF-035, ADR-0032): unit visibility, the owner while a condition
/// holds, and the two combinators. Supplier and approver rules belong to the Products layer and the workflow slice.
/// </summary>
public static class RecordRules
{
    /// <summary>
    /// The owning unit within the reader's reach for the action's permission (ADR-0030): <paramref name="read"/> for
    /// reading, and one permission per other action it governs. An action it does not name has no rule here.
    /// </summary>
    public static IRecordPolicy<T> UnitVisibility<T>(Permission read, params (string Action, Permission Permission)[] actions)
        where T : BusinessRecord
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(actions);
        return new UnitVisibilityPolicy<T>(read, actions.ToDictionary(a => a.Action, a => a.Permission, StringComparer.Ordinal));
    }

    /// <summary>
    /// The creator may perform these actions while the module's condition holds, for example "update while a draft";
    /// "draft" is the module's word, so the module supplies the condition. Reading is not narrowed, and other actions
    /// have no rule here.
    /// </summary>
    public static IRecordPolicy<T> OwnerWhile<T>(Expression<Func<T, bool>> condition, params string[] actions)
        where T : BusinessRecord
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(actions);
        if (actions.Length == 0)
        {
            throw new ArgumentException("Name the actions the owner may perform.", nameof(actions));
        }

        return new OwnerWhilePolicy<T>(condition.Compile(), actions.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>Every policy must agree: the readable records of all of them, and the strictest answer for an action.</summary>
    public static IRecordPolicy<T> All<T>(params IRecordPolicy<T>[] policies)
        where T : BusinessRecord => new CombinedPolicy<T>(Required(policies), every: true);

    /// <summary>One policy is enough: the readable records of any of them, and the most generous answer for an action.</summary>
    public static IRecordPolicy<T> Any<T>(params IRecordPolicy<T>[] policies)
        where T : BusinessRecord => new CombinedPolicy<T>(Required(policies), every: false);

    private static IRecordPolicy<T>[] Required<T>(IRecordPolicy<T>[] policies)
        where T : BusinessRecord
    {
        ArgumentNullException.ThrowIfNull(policies);
        return policies.Length == 0 ? throw new ArgumentException("Give at least one policy.", nameof(policies)) : policies;
    }

    private sealed class UnitVisibilityPolicy<T>(Permission read, IReadOnlyDictionary<string, Permission> actions) : IRecordPolicy<T>
        where T : BusinessRecord
    {
        // Reading follows the record's sharing mode (ADR-0038); every other action needs its permission over the record's
        // own unit, because sharing a record lets other units see it, not change it.
        public async Task<Expression<Func<T, bool>>> ReadableAsync(PolicySubject subject, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(subject);
            return subject.Visibility.Predicate<T>(await subject.ReachAsync(read, cancellationToken).ConfigureAwait(false));
        }

        public async Task<PolicyDecision> DecideAsync(T record, string action, PolicySubject subject, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(subject);
            var visible = await subject.Visibility.ReasonAsync(record, await subject.ReachAsync(read, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false) is not null;
            if (!visible)
            {
                return PolicyDecision.NotVisible;
            }

            if (string.Equals(action, RecordActions.Read, StringComparison.Ordinal))
            {
                return PolicyDecision.Allowed;
            }

            if (!actions.TryGetValue(action, out var permission))
            {
                return PolicyDecision.NoRule;
            }

            var reach = await subject.ReachAsync(permission, cancellationToken).ConfigureAwait(false);
            return reach.Allows(record.OwningOrganisationId, record.Id) ? PolicyDecision.Allowed : PolicyDecision.NotAllowed;
        }
    }

    private sealed class OwnerWhilePolicy<T>(Func<T, bool> condition, IReadOnlySet<string> actions) : IRecordPolicy<T>
        where T : BusinessRecord
    {
        public Task<Expression<Func<T, bool>>> ReadableAsync(PolicySubject subject, CancellationToken cancellationToken) =>
            Task.FromResult<Expression<Func<T, bool>>>(_ => true);

        public Task<PolicyDecision> DecideAsync(T record, string action, PolicySubject subject, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(subject);
            if (!actions.Contains(action))
            {
                return Task.FromResult(PolicyDecision.NoRule);
            }

            var allowed = record.CreatedBy == subject.PrincipalId && condition(record);
            return Task.FromResult(allowed ? PolicyDecision.Allowed : PolicyDecision.NotAllowed);
        }
    }

    private sealed class CombinedPolicy<T>(IRecordPolicy<T>[] policies, bool every) : IRecordPolicy<T>
        where T : BusinessRecord
    {
        public async Task<Expression<Func<T, bool>>> ReadableAsync(PolicySubject subject, CancellationToken cancellationToken)
        {
            var parts = new List<Expression<Func<T, bool>>>(policies.Length);
            foreach (var policy in policies)
            {
                parts.Add(await policy.ReadableAsync(subject, cancellationToken).ConfigureAwait(false));
            }

            return PolicyExpressions.Join(parts, every);
        }

        public async Task<PolicyDecision> DecideAsync(T record, string action, PolicySubject subject, CancellationToken cancellationToken)
        {
            var answers = new List<PolicyDecision>(policies.Length);
            foreach (var policy in policies)
            {
                answers.Add(await policy.DecideAsync(record, action, subject, cancellationToken).ConfigureAwait(false));
            }

            // All: the strictest answer wins. Any: the most generous. No rule counts only when no block has a rule.
            var ranked = every
                ? new[] { PolicyDecision.NotVisible, PolicyDecision.NotAllowed, PolicyDecision.Allowed }
                : new[] { PolicyDecision.Allowed, PolicyDecision.NotAllowed, PolicyDecision.NotVisible };
            foreach (var decision in ranked)
            {
                if (answers.Contains(decision))
                {
                    return decision;
                }
            }

            return PolicyDecision.NoRule;
        }
    }
}

/// <summary>Joins predicates over the same record type into one, so the database runs them as one condition.</summary>
internal static class PolicyExpressions
{
    public static Expression<Func<T, bool>> Join<T>(IReadOnlyList<Expression<Func<T, bool>>> parts, bool every)
    {
        var record = Expression.Parameter(typeof(T), "record");
        Expression? body = null;
        foreach (var part in parts)
        {
            var rebound = new Rebind(part.Parameters[0], record).Visit(part.Body);
            body = body is null ? rebound : every ? Expression.AndAlso(body, rebound) : Expression.OrElse(body, rebound);
        }

        return Expression.Lambda<Func<T, bool>>(body ?? Expression.Constant(every), record);
    }

    private sealed class Rebind(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
