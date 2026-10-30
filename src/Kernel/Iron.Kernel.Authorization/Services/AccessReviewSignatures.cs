using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Compliance.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Authorization.Services;

/// <summary>
/// Access review decisions as signable records (TF-063, ADR-0051): a confirm is signed "approved", a revoke "rejected",
/// when the tenant's policy for <c>access_review_decision</c> requires it (not by default). A decision is written once, so
/// its snapshot is the decision and the item it decided, as they were taken when the campaign started; nothing in it
/// changes later.
/// </summary>
public sealed class AccessReviewSignatures(AuthorizationDbContext context, AccessGrants grants, ICurrentPrincipal current) : ISignableRecords
{
    public const string RecordType = "access_review_decision";

    public static SignableType Type { get; } = new(
        RecordType,
        "Access review decision",
        [SignatureMeanings.Approved, SignatureMeanings.Rejected],
        new SigningPolicy(false, [SignatureMeanings.Approved, SignatureMeanings.Rejected], true));

    public IReadOnlyList<SignableType> Types => [Type];

    /// <summary>The meaning a decision's signature has: approved for a confirm, rejected for a revoke.</summary>
    public static string MeaningOf(ReviewDecision decision) => decision == ReviewDecision.Confirm ? SignatureMeanings.Approved : SignatureMeanings.Rejected;

    public static RecordSnapshot Snapshot(AccessReviewDecision decision, AccessReviewItem item)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(item);

        // A tag holding's decision has a form of its own; a role assignment's keeps the form its signatures were made in.
        if (item.IsTagHolding)
        {
            return RecordSnapshot.Of(RecordType, decision.Id, 1, $"Access review decision: {item.PrincipalName}, the tag {item.TagCode}", new
            {
                ReviewId = decision.ReviewId,
                ItemId = item.Id,
                HoldingId = item.AssignmentId,
                PrincipalId = item.PrincipalId,
                PrincipalName = item.PrincipalName,
                TagCode = item.TagCode,
                TagName = item.TagName,
                UnitId = item.UnitId,
                UnitName = item.UnitName,
                Decision = decision.Decision == ReviewDecision.Confirm ? "confirm" : "revoke",
                decision.Comment,
                decision.AssignmentRemoved,
            });
        }

        return RecordSnapshot.Of(RecordType, decision.Id, 1, $"Access review decision: {item.PrincipalName}, {item.RoleName}", new
        {
            ReviewId = decision.ReviewId,
            ItemId = item.Id,
            AssignmentId = item.AssignmentId,
            PrincipalId = item.PrincipalId,
            PrincipalName = item.PrincipalName,
            RoleCode = item.RoleCode,
            RoleName = item.RoleName,
            UnitId = item.UnitId,
            UnitName = item.UnitName,
            Decision = decision.Decision == ReviewDecision.Confirm ? "confirm" : "revoke",
            decision.Comment,
            decision.AssignmentRemoved,
        });
    }

    public async Task<RecordSnapshot?> CurrentAsync(string recordType, Guid recordId, CancellationToken cancellationToken)
    {
        if (!string.Equals(recordType, RecordType, StringComparison.Ordinal))
        {
            return null;
        }

        var decision = await context.AccessReviewDecisions.AsNoTracking().SingleOrDefaultAsync(d => d.Id == recordId, cancellationToken).ConfigureAwait(false);
        var item = decision is null ? null : await context.AccessReviewItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == decision.ItemId, cancellationToken).ConfigureAwait(false);
        return decision is null || item is null ? null : Snapshot(decision, item);
    }

    /// <summary>Those who may see access reviews read their decisions' signatures: managers, reviewers, and a campaign's owner.</summary>
    public async Task<bool> MayReadAsync(string recordType, Guid recordId, CancellationToken cancellationToken)
    {
        var held = await grants.OfAsync(current.Id, cancellationToken).ConfigureAwait(false);
        if (held.TenantWide.Contains(AuthorizationPermissions.ReviewsManage.Name) || held.Anywhere.Contains(AuthorizationPermissions.ReviewsDecide.Name))
        {
            return true;
        }

        var owner = current.ActingForId ?? current.Id;
        return await context.AccessReviewDecisions.AsNoTracking()
            .Where(d => d.Id == recordId)
            .AnyAsync(d => context.AccessReviews.Any(r => r.Id == d.ReviewId && r.OwnerId == owner), cancellationToken)
            .ConfigureAwait(false);
    }
}
