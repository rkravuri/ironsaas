namespace Iron.Kernel.Authorization.Contracts;

public enum ReviewStatus
{
    Open,
    Closed,
}

/// <summary>What a reviewer decides about one assignment: keep it, or end it now (TF-038 AC2).</summary>
public enum ReviewDecision
{
    Confirm,
    Revoke,
}

/// <summary>
/// A unit of a campaign whose reviewers are late: its items still open, how many days past the due date, and who the
/// reviewers are (the principals holding <c>authz.reviews.decide</c> at the unit; none when nobody does).
/// </summary>
public sealed record OverdueReview(
    Guid CampaignId,
    string CampaignName,
    Guid OwnerId,
    Guid UnitId,
    string UnitName,
    DateOnly DueOn,
    int DaysOverdue,
    int OpenItems,
    IReadOnlyList<Guid> Reviewers);

/// <summary>
/// Who is told about reviewers who are late (TF-038 AC3, ADR-0037). The kernel names the campaign's owner, since it knows
/// no managers; a business module that knows the reviewer's manager replaces it:
/// <c>services.Replace(ServiceDescriptor.Scoped&lt;IReviewEscalation, ManagerEscalation&gt;())</c>. Sending the message
/// is S4; until then the daily pass records the escalation with these targets.
/// </summary>
public interface IReviewEscalation
{
    /// <summary>The principals to tell; at least one, or the escalation names the campaign's owner.</summary>
    Task<IReadOnlyList<Guid>> TargetsAsync(OverdueReview review, CancellationToken cancellationToken);
}
