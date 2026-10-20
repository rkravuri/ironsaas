using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Authorization.Data;

/// <summary>
/// An access review campaign (TF-038, ADR-0037): a name, the units it covers, a due date, the person who started it, and
/// whether it is open. Starting it takes the snapshot of its items; closing it ends the deciding. The campaign itself is
/// tenant-wide: who reads which of its items is the reviewers' rule, applied by <c>ReviewService</c>.
/// </summary>
public sealed class AccessReview : AuditedEntity, IOwnAccessRule
{
    public const int NameMaxLength = 200;

    private AccessReview()
    {
        Name = string.Empty;
    }

    public AccessReview(string name, DateOnly dueOn, Guid ownerId)
        : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim()[..Math.Min(name.Trim().Length, NameMaxLength)];
        DueOn = dueOn;
        OwnerId = ownerId;
        Status = ReviewStatus.Open;
    }

    public string Name { get; private set; }

    /// <summary>The last day to decide, as a date: an item still open on the day after it is overdue.</summary>
    public DateOnly DueOn { get; private set; }

    /// <summary>The person who started it, whom the kernel's escalation names (ADR-0037).</summary>
    public Guid OwnerId { get; private set; }

    public ReviewStatus Status { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public Guid? ClosedBy { get; private set; }

    public bool IsOpen => Status == ReviewStatus.Open;

    /// <summary>Whole days past the due date on this day; zero or less while it is not due.</summary>
    public int DaysOverdue(DateOnly today) => today.DayNumber - DueOn.DayNumber;

    public void Close(DateTimeOffset now, Guid by)
    {
        if (!IsOpen)
        {
            return;
        }

        Status = ReviewStatus.Closed;
        ClosedAt = now;
        ClosedBy = by;
    }
}

/// <summary>One unit a campaign covers, alone or with every unit below it. Written when the campaign starts.</summary>
public sealed class AccessReviewUnit : AppendOnlyEntity
{
    private AccessReviewUnit()
    {
    }

    public AccessReviewUnit(Guid reviewId, Guid unitId, bool includesChildren)
    {
        ReviewId = reviewId;
        UnitId = unitId;
        IncludesChildren = includesChildren;
    }

    public Guid ReviewId { get; private set; }

    public Guid UnitId { get; private set; }

    public bool IncludesChildren { get; private set; }
}

/// <summary>
/// One role assignment as it stood when the campaign started (TF-038): the principal, the role, the scope and the unit
/// with its path, by id and by name, so the reviewer reads what was reviewed even after a rename or a removal. A
/// tenant-wide assignment sits at the root unit; it is in a campaign only when the campaign covers the root.
/// </summary>
public sealed class AccessReviewItem : AppendOnlyEntity, IOwnAccessRule
{
    public const string RecordType = "access_review_item";

    private AccessReviewItem()
    {
        PrincipalName = string.Empty;
        RoleCode = string.Empty;
        RoleName = string.Empty;
        ScopeKind = Scopes.Tenant;
        UnitName = string.Empty;
        UnitPathIds = [];
        UnitPathNames = [];
    }

    public AccessReviewItem(Guid reviewId, RoleAssignment assignment, string principalName, Role role, IReadOnlyList<Guid> unitPathIds, IReadOnlyList<string> unitPathNames)
        : this()
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(unitPathIds);
        ArgumentNullException.ThrowIfNull(unitPathNames);
        if (unitPathIds.Count == 0 || unitPathIds.Count != unitPathNames.Count)
        {
            throw new ArgumentException("An item sits at a unit: its path names every unit from the root down to it.", nameof(unitPathIds));
        }

        ReviewId = reviewId;
        AssignmentId = assignment.Id;
        PrincipalId = assignment.PrincipalId;
        PrincipalName = principalName;
        RoleId = role.Id;
        RoleCode = role.Code;
        RoleName = role.Name;
        ScopeKind = assignment.ScopeKind;
        UnitId = unitPathIds[^1];
        UnitName = unitPathNames[^1];
        UnitPathIds = [.. unitPathIds];
        UnitPathNames = [.. unitPathNames];
    }

    public Guid ReviewId { get; private set; }

    /// <summary>The assignment reviewed; it may have been removed since, by the review or otherwise.</summary>
    public Guid AssignmentId { get; private set; }

    public Guid PrincipalId { get; private set; }

    public string PrincipalName { get; private set; }

    public Guid RoleId { get; private set; }

    public string RoleCode { get; private set; }

    public string RoleName { get; private set; }

    /// <summary>The stored scope name: <c>tenant</c>, <c>unit</c> or <c>unit_with_children</c>.</summary>
    public string ScopeKind { get; private set; }

    /// <summary>The unit the item belongs to, where its reviewers hold <c>authz.reviews.decide</c>.</summary>
    public Guid UnitId { get; private set; }

    public string UnitName { get; private set; }

    public List<Guid> UnitPathIds { get; private set; }

    public List<string> UnitPathNames { get; private set; }
}

/// <summary>
/// A reviewer's decision on one item (TF-038 AC2), written once: confirm keeps the assignment, revoke removed it in the
/// same transaction. <see cref="AssignmentRemoved"/> is false for a revoke of an assignment that was gone already.
/// </summary>
public sealed class AccessReviewDecision : AppendOnlyEntity
{
    public const int CommentMaxLength = 500;

    private AccessReviewDecision()
    {
    }

    public AccessReviewDecision(Guid reviewId, Guid itemId, ReviewDecision decision, string? comment, bool assignmentRemoved)
    {
        ReviewId = reviewId;
        ItemId = itemId;
        Decision = decision;
        Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()[..Math.Min(comment.Trim().Length, CommentMaxLength)];
        AssignmentRemoved = assignmentRemoved;
    }

    public Guid ReviewId { get; private set; }

    public Guid ItemId { get; private set; }

    public ReviewDecision Decision { get; private set; }

    public string? Comment { get; private set; }

    public bool AssignmentRemoved { get; private set; }
}

/// <summary>
/// The record that a unit's reviewers were 7 days late and who is told (TF-038 AC3): once per campaign and unit, with the
/// reviewers and the targets the escalation named. Sending the message is S4; until then this row and its audit event
/// are the report.
/// </summary>
public sealed class AccessReviewEscalation : AppendOnlyEntity
{
    private AccessReviewEscalation()
    {
        Reviewers = [];
        Targets = [];
    }

    public AccessReviewEscalation(Guid reviewId, Guid unitId, int openItems, int daysOverdue, IEnumerable<Guid> reviewers, IEnumerable<Guid> targets)
        : this()
    {
        ArgumentNullException.ThrowIfNull(reviewers);
        ArgumentNullException.ThrowIfNull(targets);
        ReviewId = reviewId;
        UnitId = unitId;
        OpenItems = openItems;
        DaysOverdue = daysOverdue;
        Reviewers = [.. reviewers.Distinct()];
        Targets = [.. targets.Distinct()];
    }

    public Guid ReviewId { get; private set; }

    public Guid UnitId { get; private set; }

    public int OpenItems { get; private set; }

    public int DaysOverdue { get; private set; }

    public List<Guid> Reviewers { get; private set; }

    public List<Guid> Targets { get; private set; }
}
