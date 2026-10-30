using Iron.Kernel.History;

namespace Iron.Kernel.Visibility.Contracts;

/// <summary>
/// The Visibility module's permissions (TF-241, ADR-0057). Tags are the tenant's, so defining them is tenant-wide;
/// giving them to people is checked at the person's home unit, as managing the person is.
/// </summary>
public static class VisibilityPermissions
{
    public static readonly Permission TagsRead = new("visibility.tags.read", "View the visibility tags and who holds them", Risk.Low);

    /// <summary>Defining what a person's tags can narrow to: high, as a role's definition is.</summary>
    public static readonly Permission TagsManage = new("visibility.tags.manage", "Create, rename, retire and reactivate visibility tags", Risk.High);

    /// <summary>Giving a person tags changes which records they see; the escalation rule limits it to tags the giver holds.</summary>
    public static readonly Permission AssignmentsManage = new("visibility.assignments.manage", "Give people and service accounts visibility tags, and take them away", Risk.High);

    public static IReadOnlyList<Permission> All => [TagsRead, TagsManage, AssignmentsManage];
}

/// <summary>The Visibility module's error codes; a record's own tags answer the data library's (<c>data.visibility.*</c>).</summary>
public static class VisibilityErrors
{
    public const string TagNotFound = "visibility.tag.not_found";

    /// <summary>A code is capitals, digits, hyphens and underscores, at most 32, starting with a capital or a digit.</summary>
    public const string TagCodeInvalid = "visibility.tag.code_invalid";

    /// <summary>A code is the tag's for good: the records that carry it keep it, so it is never used again.</summary>
    public const string TagCodeTaken = "visibility.tag.code_taken";

    /// <summary>A retired tag stays where it is, and is given to nobody new.</summary>
    public const string TagRetired = "visibility.tag.retired";

    public const string TagActive = "visibility.tag.active";

    public const string PrincipalNotFound = "visibility.principal.not_found";

    /// <summary>The escalation rule: a person gives, or takes away, only tags they hold, unless they hold none.</summary>
    public const string TagNotHeld = "visibility.holding.not_held";
}

/// <summary>The Visibility module's audit events (ADR-0013), each with its words (ADR-0051).</summary>
public static class VisibilityAuditEvents
{
    public const string TagCreated = "visibility.tag_created";
    public const string TagChanged = "visibility.tag_changed";
    public const string TagRetired = "visibility.tag_retired";
    public const string TagReactivated = "visibility.tag_reactivated";

    /// <summary>A person's or a service account's tags changed: before and after, on their own record's history.</summary>
    public const string HolderTagsChanged = "visibility.holder_tags_changed";

    public static IReadOnlyList<AuditEventType> Types { get; } =
    [
        new AuditEventType(TagCreated, "Visibility tag created").AsAdministrative(),
        new AuditEventType(TagChanged, "Visibility tag changed").AsAdministrative(),
        new AuditEventType(TagRetired, "Visibility tag retired").AsAdministrative(),
        new AuditEventType(TagReactivated, "Visibility tag reactivated").AsAdministrative(),
        new AuditEventType(HolderTagsChanged, "Visibility tags of a person changed").AsAdministrative(),
    ];
}

/// <summary>One tag a principal holds, as an access review reads it: the holding is what a revoke removes.</summary>
public sealed record TagHoldingInfo(Guid HoldingId, Guid PrincipalId, Guid TagId, string Code, string Name, bool Active);

/// <summary>
/// Who holds which visibility tag, for access reviews (TF-241 AC7, TF-038, ADR-0057): every item of a review lists the
/// tags its person holds, and a review of tag holders confirms or removes them. Visibility implements it.
/// </summary>
public interface IVisibilityTagHolders
{
    /// <summary>The holdings of these principals, every principal's when none are named, in code order.</summary>
    Task<IReadOnlyList<TagHoldingInfo>> HoldingsAsync(IReadOnlyCollection<Guid>? principalIds, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a holding because a reviewer revoked it (TF-038 AC2), in the caller's transaction, audited with the review;
    /// false when it was gone already. The reviewer's right is the review's, so the escalation rule does not apply.
    /// </summary>
    Task<bool> RemoveForReviewAsync(Guid holdingId, Guid reviewId, CancellationToken cancellationToken);
}
