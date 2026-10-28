using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Tenancy.Data;

/// <summary>
/// A security incident the operator declared (TF-079 AC2, ADR-0056): its severity (1 the most severe, to 4), its lead
/// and the people in its other roles, a summary, the tenants affected, its timeline, its close, and the post-incident
/// review that is due within 10 days of the close and escalated when it is not there.
/// </summary>
public sealed class Incident : AuditedEntity, ITenantWide
{
    public const int NameMaxLength = 200;
    public const int SummaryMaxLength = 1000;
    public const int TextMaxLength = 4000;

    /// <summary>The review is due this long after the close (TF-079 AC2).</summary>
    public static readonly TimeSpan ReviewWithin = TimeSpan.FromDays(10);

    public const string Open = "open";
    public const string Closed = "closed";

    private Incident()
    {
        Summary = string.Empty;
        Lead = string.Empty;
        Status = Open;
    }

    public Incident(int severity, string summary, string lead, string? communications, string? scribe, DateTimeOffset declaredAt, Guid declaredById)
        : this()
    {
        Severity = severity;
        Summary = summary;
        Lead = lead;
        Communications = communications;
        Scribe = scribe;
        DeclaredAt = declaredAt;
        DeclaredById = declaredById;
    }

    public int Severity { get; private set; }

    public string Summary { get; private set; }

    /// <summary>The incident lead, who decides; the runbook's roles.</summary>
    public string Lead { get; private set; }

    /// <summary>Who speaks for the incident, inside and to the tenants.</summary>
    public string? Communications { get; private set; }

    /// <summary>Who keeps the timeline.</summary>
    public string? Scribe { get; private set; }

    public string Status { get; private set; }

    public DateTimeOffset DeclaredAt { get; private set; }

    public Guid DeclaredById { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public string? CloseSummary { get; private set; }

    public DateTimeOffset? ReviewDueAt { get; private set; }

    public DateTimeOffset? ReviewRecordedAt { get; private set; }

    public Guid? ReviewRecordedById { get; private set; }

    public string? WhatHappened { get; private set; }

    public string? Why { get; private set; }

    public string? WhatChanges { get; private set; }

    /// <summary>When the missing review was escalated, once.</summary>
    public DateTimeOffset? ReviewEscalatedAt { get; private set; }

    public void Close(DateTimeOffset at, string summary)
    {
        Status = Closed;
        ClosedAt = at;
        CloseSummary = summary;
        ReviewDueAt = at + ReviewWithin;
    }

    public void Review(DateTimeOffset at, Guid by, string whatHappened, string why, string whatChanges)
    {
        ReviewRecordedAt = at;
        ReviewRecordedById = by;
        WhatHappened = whatHappened;
        Why = why;
        WhatChanges = whatChanges;
    }

    public void ReviewEscalated(DateTimeOffset at) => ReviewEscalatedAt = at;
}

/// <summary>
/// A tenant an incident affects (ADR-0056): whether its personal data is affected and since when that is known, so the
/// 72-hour deadline to tell it runs from that moment, and the notice sent to it.
/// </summary>
public sealed class IncidentTenant : AuditedEntity, ITenantWide
{
    /// <summary>A tenant must be told of a breach of personal data within this long of its becoming known.</summary>
    public static readonly TimeSpan NoticeWithin = TimeSpan.FromHours(72);

    private IncidentTenant()
    {
        TenantSlug = string.Empty;
    }

    public IncidentTenant(Guid incidentId, string tenantSlug, bool personalData, DateTimeOffset? knownAt)
        : this()
    {
        IncidentId = incidentId;
        TenantSlug = tenantSlug;
        Affect(personalData, knownAt);
    }

    public Guid IncidentId { get; private set; }

    public string TenantSlug { get; private set; }

    public bool PersonalData { get; private set; }

    /// <summary>When it became known that this tenant's personal data was affected, as the operator reports it.</summary>
    public DateTimeOffset? KnownAt { get; private set; }

    public DateTimeOffset? NoticeDueAt => PersonalData && KnownAt is { } known ? known + NoticeWithin : null;

    public DateTimeOffset? NotifiedAt { get; private set; }

    public Guid? NotifiedById { get; private set; }

    public string? Notice { get; private set; }

    public DateTimeOffset? NoticeEscalatedAt { get; private set; }

    public void Affect(bool personalData, DateTimeOffset? knownAt)
    {
        PersonalData = personalData;
        KnownAt = personalData ? knownAt : null;
    }

    public void Notified(DateTimeOffset at, Guid by, string notice)
    {
        NotifiedAt ??= at;
        NotifiedById ??= by;
        Notice = notice;
    }

    public void NoticeEscalated(DateTimeOffset at) => NoticeEscalatedAt = at;
}

/// <summary>One entry of an incident's timeline (ADR-0056): what was done, found or said, by whom, when; written once.</summary>
public sealed class IncidentEntry : AppendOnlyEntity, ITenantWide
{
    public const string Declared = "declared";
    public const string Note = "note";
    public const string Communication = "communication";
    public const string TenantAffected = "tenant";
    public const string NoticeSent = "notice";
    public const string ClosedKind = "closed";
    public const string ReviewKind = "review";
    public const string Escalated = "escalated";

    private IncidentEntry()
    {
        Kind = string.Empty;
        Text = string.Empty;
        ByName = string.Empty;
    }

    public IncidentEntry(Guid incidentId, DateTimeOffset at, Guid byId, string byName, string kind, string text, string? tenantSlug = null)
        : this()
    {
        IncidentId = incidentId;
        At = at;
        ById = byId;
        ByName = byName;
        Kind = kind;
        Text = text;
        TenantSlug = tenantSlug;
    }

    public Guid IncidentId { get; private set; }

    public DateTimeOffset At { get; private set; }

    public Guid ById { get; private set; }

    public string ByName { get; private set; }

    public string Kind { get; private set; }

    public string Text { get; private set; }

    public string? TenantSlug { get; private set; }
}
