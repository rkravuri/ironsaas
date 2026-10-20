using Iron.Kernel.Api.Modules;
using Iron.Kernel.Authorization.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Authorization.Services;

/// <summary>Whose grants a principal acts with: the delegator's, limited to these permissions (null: all the delegator holds).</summary>
public sealed record ActingSubstitution(Guid DelegatorId, IReadOnlySet<string>? Limit);

/// <summary>
/// Acting for someone, as the evaluator sees it (TF-037, ADR-0037). When the request's principal has chosen to act for a
/// delegator, every check of that principal uses the delegator's grants as they are now, at the delegator's scopes,
/// limited to the permissions of the duties delegated and active at this moment; never the two people's rights mixed.
/// Resolved once per request; with no active delegation left, the limit is empty and nothing is allowed. An explanation
/// (ADR-0039) assumes the acting of the person it explains, and the same resolution follows.
/// </summary>
public sealed class ActingScope(AuthorizationDbContext context, ICurrentPrincipal current, DutyDeclarations duties, TimeProvider time)
{
    private readonly Dictionary<(Guid Principal, Guid Delegator), ActingSubstitution> _resolved = [];
    private (Guid Principal, Guid Delegator)? _assumed;

    /// <summary>
    /// For an explanation (ADR-0039): in this request, the principal acts for the delegator, as their own session would
    /// after choosing to. Their grants, lists and record policies follow; the requester's own checks do not change.
    /// </summary>
    public void Assume(Guid principalId, Guid delegatorId) => _assumed = (principalId, delegatorId);

    public async Task<ActingSubstitution?> ForAsync(Guid principalId, CancellationToken cancellationToken)
    {
        Guid delegator;
        if (_assumed is { } assumed && assumed.Principal == principalId)
        {
            delegator = assumed.Delegator;
        }
        else if (current.ActingForId is { } chosen && principalId == current.Id && principalId != Guid.Empty)
        {
            delegator = chosen;
        }
        else
        {
            return null;
        }

        if (!_resolved.TryGetValue((principalId, delegator), out var substitution))
        {
            var now = time.GetUtcNow();
            var delegated = await context.Delegations.AsNoTracking()
                .Where(d => d.DelegatorId == delegator && d.DelegateId == principalId && d.StartsAt <= now && d.EndsAt > now && (d.EndedAt == null || d.EndedAt > now))
                .Select(d => d.Duties)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var codes = delegated.SelectMany(d => d).ToList();
            substitution = new ActingSubstitution(delegator, codes.Count == 0 ? new HashSet<string>(StringComparer.Ordinal) : duties.PermissionsOf(codes));
            _resolved[(principalId, delegator)] = substitution;
        }

        return substitution;
    }
}
