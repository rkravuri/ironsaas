using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Visibility;
using Iron.Kernel.Visibility.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Visibility.Services;

/// <summary>
/// The tags as the record policies ask for them (ADR-0057): what a principal holds, retired tags included so that a
/// retirement never widens what they see; while the request's principal acts for someone, what the delegator holds, as
/// the grants are the delegator's (ADR-0037). One query each; the policy keeps the answer for its evaluation.
/// </summary>
public sealed class VisibilityTagsSource(VisibilityDbContext context, IActingFor? acting = null) : IVisibilityTags
{
    public async Task<IReadOnlyList<string>> HeldAsync(Guid principalId, CancellationToken cancellationToken)
    {
        var whose = acting is null ? principalId : await acting.DelegatorOfAsync(principalId, cancellationToken).ConfigureAwait(false) ?? principalId;
        return await (from holding in context.Holdings.AsNoTracking()
                      join tag in context.Tags.AsNoTracking() on holding.TagId equals tag.Id
                      where holding.PrincipalId == whose
                      orderby tag.Code
                      select tag.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, VisibilityTagInfo>> FindAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(codes);
        if (codes.Count == 0)
        {
            return new Dictionary<string, VisibilityTagInfo>(StringComparer.Ordinal);
        }

        var wanted = codes.ToArray();
        return await context.Tags.AsNoTracking().Where(t => wanted.Contains(t.Code))
            .ToDictionaryAsync(t => t.Code, t => new VisibilityTagInfo(t.Code, t.Name, t.Status == TagStatus.Active), StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VisibilityTagInfo>> ActiveAsync(CancellationToken cancellationToken) =>
        await context.Tags.AsNoTracking().Where(t => t.Status == TagStatus.Active).OrderBy(t => t.Code)
            .Select(t => new VisibilityTagInfo(t.Code, t.Name, true))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
