using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.History;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Data.Visibility;
using Iron.Kernel.History;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Visibility.Contracts;
using Iron.Kernel.Visibility.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Visibility.Services;

/// <summary>
/// The tags a person or a service account holds (TF-241 rule 3, ADR-0057). The endpoint checks
/// <c>visibility.assignments.manage</c> at the principal's home unit; this service holds the escalation rule of A2: a
/// person gives or takes away only tags they hold, unless they hold none, and a retired tag is given to nobody. A change
/// is one audit event on the principal's own record, with the tags before and after, so their History shows it; and a
/// line of their security activity.
/// </summary>
public sealed class HoldingService(
    VisibilityDbContext context,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal current,
    IVisibilityTags tags,
    ISecurityActivity? activity = null) : IVisibilityTagHolders
{
    /// <summary>The tags the principal holds, retired ones included, in code order.</summary>
    public async Task<IReadOnlyList<VisibilityTag>> TagsOfAsync(Guid principalId, CancellationToken cancellationToken) =>
        await (from holding in context.Holdings.AsNoTracking()
               join tag in context.Tags.AsNoTracking() on holding.TagId equals tag.Id
               where holding.PrincipalId == principalId
               orderby tag.Code
               select tag).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// What the request's principal may give: the active tags they hold, or every active tag when they hold none. A tag
    /// they cannot give they cannot take away either.
    /// </summary>
    public async Task<IReadOnlyList<VisibilityTag>> GivableAsync(CancellationToken cancellationToken)
    {
        var held = await tags.HeldAsync(current.Id, cancellationToken).ConfigureAwait(false);
        return await context.Tags.AsNoTracking().Where(t => t.Status == TagStatus.Active && (held.Count == 0 || held.Contains(t.Code))).OrderBy(t => t.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gives the principal exactly these tags; the refusal of the escalation rule names the first tag it refuses.</summary>
    public async Task<Result<IReadOnlyList<VisibilityTag>>> SetAsync(Guid principalId, string principalName, IReadOnlyList<string> codes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(codes);
        var wanted = codes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(RecordTags.Normalise).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var before = await TagsOfAsync(principalId, cancellationToken).ConfigureAwait(false);

        // In code order as C# compares, not as the database's collation does: the history's text is the same either way.
        var beforeCodes = before.Select(t => t.Code).Order(StringComparer.Ordinal).ToList();
        var added = wanted.Where(c => !beforeCodes.Contains(c)).ToList();
        var removed = before.Where(t => !wanted.Contains(t.Code)).ToList();
        if (added.Count == 0 && removed.Count == 0)
        {
            return Result.Success(before);
        }

        var known = await context.Tags.AsNoTracking().Where(t => added.Contains(t.Code)).ToDictionaryAsync(t => t.Code, cancellationToken).ConfigureAwait(false);
        if (added.FirstOrDefault(c => !known.ContainsKey(c)) is { } unknown)
        {
            return Error.Validation(VisibilityErrors.TagNotFound, $"There is no visibility tag {unknown}.", new FieldError("codes", "unknown", $"{unknown} is not a tag of this tenant."));
        }

        if (added.FirstOrDefault(c => !known[c].IsActive) is { } retired)
        {
            return Error.Validation(VisibilityErrors.TagRetired, $"The tag {retired} is retired, so it can be given to nobody.", new FieldError("codes", "retired", $"{retired} is retired."));
        }

        // The escalation rule: whoever holds tags gives and takes away only those; whoever holds none, any.
        var held = await tags.HeldAsync(current.Id, cancellationToken).ConfigureAwait(false);
        if (held.Count > 0)
        {
            if (added.FirstOrDefault(c => !held.Contains(c)) is { } notHeld)
            {
                return Error.Forbidden(VisibilityErrors.TagNotHeld, $"You do not hold the tag {notHeld}, so you cannot give it to anyone.");
            }

            if (removed.FirstOrDefault(t => !held.Contains(t.Code)) is { } notYours)
            {
                return Error.Forbidden(VisibilityErrors.TagNotHeld, $"You do not hold the tag {notYours.Code}, so you cannot take it away from anyone.");
            }
        }

        await using (var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            var removedIds = removed.Select(t => t.Id).ToArray();
            context.Holdings.RemoveRange(await context.Holdings.Where(h => h.PrincipalId == principalId && removedIds.Contains(h.TagId)).ToListAsync(cancellationToken).ConfigureAwait(false));
            context.Holdings.AddRange(added.Select(c => new TagHolding(principalId, known[c].Id)));
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await ChangedAsync(principalId, principalName, beforeCodes, wanted, null, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return Result.Success(await TagsOfAsync(principalId, cancellationToken).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<TagHoldingInfo>> HoldingsAsync(IReadOnlyCollection<Guid>? principalIds, CancellationToken cancellationToken)
    {
        var ids = principalIds?.ToArray();
        return await (from holding in context.Holdings.AsNoTracking()
                      join tag in context.Tags.AsNoTracking() on holding.TagId equals tag.Id
                      where ids == null || ids.Contains(holding.PrincipalId)
                      orderby tag.Code
                      select new TagHoldingInfo(holding.Id, holding.PrincipalId, tag.Id, tag.Code, tag.Name, tag.Status == TagStatus.Active))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> RemoveForReviewAsync(Guid holdingId, Guid reviewId, CancellationToken cancellationToken)
    {
        var holding = await context.Holdings.SingleOrDefaultAsync(h => h.Id == holdingId, cancellationToken).ConfigureAwait(false);
        if (holding is null)
        {
            return false;
        }

        var before = (await TagsOfAsync(holding.PrincipalId, cancellationToken).ConfigureAwait(false)).Select(t => t.Code).Order(StringComparer.Ordinal).ToList();
        var code = await context.Tags.AsNoTracking().Where(t => t.Id == holding.TagId).Select(t => t.Code).SingleAsync(cancellationToken).ConfigureAwait(false);
        context.Holdings.Remove(holding);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await ChangedAsync(holding.PrincipalId, null, before, [.. before.Where(c => c != code)], reviewId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>The change on the principal's record (ADR-0051), the field as a record's tags write it, and their security activity.</summary>
    private async Task ChangedAsync(Guid principalId, string? principalName, IReadOnlyList<string> before, IReadOnlyList<string> after, Guid? reviewId, CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["added"] = string.Join(',', after.Except(before)),
            ["removed"] = string.Join(',', before.Except(after)),
        };
        if (reviewId is { } review)
        {
            details["access_review_id"] = review.ToString("D");
        }

        var field = new FieldChange(VisibilityFields.Tags, ChangeHistoryInterceptor.Text(before.ToArray()), ChangeHistoryInterceptor.Text(after.ToArray()));
        await audit.WriteAsync(new AuditEvent(VisibilityAuditEvents.HolderTagsChanged, AuditOutcome.Success, current.Id, principalName, details, IdentityRecordTypes.Principal, principalId, Changes: [field]), cancellationToken).ConfigureAwait(false);
        if (activity is not null)
        {
            await activity.NoteAsync(principalId, SecurityEventKinds.VisibilityTagsChanged, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>The field a principal's and a record's tags are recorded under in their history, the record column's name.</summary>
public static class VisibilityFields
{
    public const string Tags = "visibility_tags";
}
