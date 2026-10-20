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
/// Resolved once per request; with no active delegation left, the limit is empty and nothing is allowed.
/// </summary>
public sealed class ActingScope(AuthorizationDbContext context, ICurrentPrincipal current, DutyDeclarations duties, TimeProvider time)
{
    private bool _resolved;
    private ActingSubstitution? _substitution;

    public async Task<ActingSubstitution?> ForAsync(Guid principalId, CancellationToken cancellationToken)
    {
        if (current.ActingForId is not { } delegator || principalId != current.Id || principalId == Guid.Empty)
        {
            return null;
        }

        if (!_resolved)
        {
            var now = time.GetUtcNow();
            var delegated = await context.Delegations.AsNoTracking()
                .Where(d => d.DelegatorId == delegator && d.DelegateId == principalId && d.StartsAt <= now && d.EndsAt > now && (d.EndedAt == null || d.EndedAt > now))
                .Select(d => d.Duties)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var codes = delegated.SelectMany(d => d).ToList();
            _substitution = new ActingSubstitution(delegator, codes.Count == 0 ? new HashSet<string>(StringComparer.Ordinal) : duties.PermissionsOf(codes));
            _resolved = true;
        }

        return _substitution;
    }
}
