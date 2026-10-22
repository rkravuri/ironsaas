using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Tenancy.Data;

/// <summary>Where a break-glass access stands (TF-026, ADR-0049).</summary>
public enum BreakGlassStatus
{
    /// <summary>One operator asked; a second, different one must approve within 30 minutes.</summary>
    Requested,

    /// <summary>Not approved within 30 minutes (TF-026 AC2).</summary>
    Expired,

    /// <summary>Approved: the unit's account exists or is being made, until the time limit.</summary>
    Active,

    /// <summary>The time limit passed, or an operator ended it: the unit's account is being closed.</summary>
    Ending,

    /// <summary>Closed, with its review of every action (TF-026 AC3).</summary>
    Ended,

    /// <summary>The unit could not open the account; nothing was granted.</summary>
    Failed,
}

/// <summary>
/// An operator's emergency access to a tenant (TF-026, ADR-0049): who asked, why, under which ticket, for how long; who
/// approved; the unit's account; and, once it ended, the review: every action of the account from the tenant's audit
/// trail. Until S5's task engine, this record is the review the operators read.
/// </summary>
public sealed class BreakGlassAccess : AuditedEntity, ITenantWide
{
    public const int ReasonMaxLength = 500;
    public const int TicketMaxLength = 100;
    public const int NameMaxLength = 254;
    public const int DefaultMinutes = 60;
    public const int LongestMinutes = 240;

    /// <summary>How long a request waits for its second operator (TF-026 AC2).</summary>
    public static readonly TimeSpan ApprovalWindow = TimeSpan.FromMinutes(30);

    private BreakGlassAccess()
    {
        TenantSlug = string.Empty;
        Reason = string.Empty;
        Ticket = string.Empty;
        RequestedByName = string.Empty;
        AccountLoginId = string.Empty;
    }

    public BreakGlassAccess(string tenantSlug, string reason, string ticket, int minutes, Guid requestedById, string requestedByName, DateTimeOffset now)
        : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantSlug);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket);
        ArgumentOutOfRangeException.ThrowIfLessThan(minutes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minutes, LongestMinutes);
        TenantSlug = tenantSlug;
        Reason = Cut(reason.Trim(), ReasonMaxLength);
        Ticket = Cut(ticket.Trim(), TicketMaxLength);
        Minutes = minutes;
        RequestedById = requestedById;
        RequestedByName = Cut(requestedByName, NameMaxLength);
        AccountLoginId = Iron.Kernel.Identity.Contracts.BreakGlassAccountNames.LoginIdOf(Id);
        RequestedAt = now;
        Status = BreakGlassStatus.Requested;
    }

    public string TenantSlug { get; private set; }

    public string Reason { get; private set; }

    public string Ticket { get; private set; }

    public int Minutes { get; private set; }

    public BreakGlassStatus Status { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public Guid RequestedById { get; private set; }

    public string RequestedByName { get; private set; }

    public Guid? ApprovedById { get; private set; }

    public string? ApprovedByName { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    /// <summary>When the access ends by itself; the unit's account stops then too.</summary>
    public DateTimeOffset? AccessUntil { get; private set; }

    /// <summary>The account the unit makes for the access, by a rule both planes know.</summary>
    public string AccountLoginId { get; private set; }

    public Guid? OpenJobId { get; private set; }

    public Guid? CloseJobId { get; private set; }

    /// <summary>Why it ended: its time limit, or the operator who ended it early.</summary>
    public string? EndReason { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>The review (TF-026 AC3): every action of the account, as JSON, from the tenant's audit trail.</summary>
    public string? Review { get; private set; }

    public int? ReviewActions { get; private set; }

    public DateTimeOffset ApprovalDeadline => RequestedAt + ApprovalWindow;

    public void Approve(Guid approverId, string approverName, DateTimeOffset now, Guid openJobId)
    {
        Status = BreakGlassStatus.Active;
        ApprovedById = approverId;
        ApprovedByName = Cut(approverName, NameMaxLength);
        ApprovedAt = now;
        AccessUntil = now.AddMinutes(Minutes);
        OpenJobId = openJobId;
    }

    public void Expire() => Status = BreakGlassStatus.Expired;

    public void Fail(string reason)
    {
        Status = BreakGlassStatus.Failed;
        EndReason = Cut(reason, ReasonMaxLength);
    }

    /// <summary>The account must close: at its time limit, or ended early. The close job follows when the unit is free.</summary>
    public void Close(string reason)
    {
        if (Status != BreakGlassStatus.Active)
        {
            return;
        }

        Status = BreakGlassStatus.Ending;
        EndReason = Cut(reason, ReasonMaxLength);
    }

    public void Closing(Guid? closeJobId) => CloseJobId = closeJobId;

    public void Reviewed(string review, int actions, DateTimeOffset now)
    {
        Status = BreakGlassStatus.Ended;
        Review = review;
        ReviewActions = actions;
        EndedAt = now;
    }

    private static string Cut(string value, int length) => value.Length > length ? value[..length] : value;
}

/// <summary>
/// One version of one of the platform's documents (TF-028, ADR-0049): the terms of service or the privacy notice. Written
/// once; a change is a new version, which every unit asks everyone to accept within a minute of its publication.
/// </summary>
public sealed class PlatformDocument : AuditedEntity, ITenantWide
{
    public const int KeyMaxLength = 64;
    public const int TitleMaxLength = 200;
    public const int BodyMaxLength = 100_000;

    private PlatformDocument()
    {
        Key = string.Empty;
        Title = string.Empty;
        Body = string.Empty;
    }

    public PlatformDocument(string key, int number, string title, string body, DateTimeOffset publishedAt)
        : this()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        Key = key;
        Number = number;
        Title = title.Trim();
        Body = body;
        PublishedAt = publishedAt;
    }

    public string Key { get; private set; }

    /// <summary>The version's number, from 1.</summary>
    public int Number { get; private set; }

    public string Title { get; private set; }

    public string Body { get; private set; }

    public DateTimeOffset PublishedAt { get; private set; }
}
