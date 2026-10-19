using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Flags.Data;

/// <summary>
/// The last rules the store gave, from both paths, and when it last confirmed them (ADR-0035): a unit that restarts while
/// the store is down evaluates its flags from these, never from the code defaults, so a kill switch stays on. One row,
/// keyed <c>current</c>; a path the store had nothing at is kept as null.
/// </summary>
public sealed class LastKnownFlagRules : AuditedEntity, ITenantWide
{
    public const string CurrentKey = "current";

    private LastKnownFlagRules()
    {
        Key = CurrentKey;
    }

    public LastKnownFlagRules(string? platformDocument, string? tenantDocument, DateTimeOffset confirmedAt)
    {
        Key = CurrentKey;
        Confirm(platformDocument, tenantDocument, confirmedAt);
    }

    public string Key { get; private set; }

    /// <summary><c>platform/flags</c> as the store held it (jsonb), or null when it held nothing.</summary>
    public string? PlatformDocument { get; private set; }

    /// <summary><c>tenants/&lt;slug&gt;/flags</c> as the store held it (jsonb), or null when it held nothing.</summary>
    public string? TenantDocument { get; private set; }

    public DateTimeOffset ConfirmedAt { get; private set; }

    public void Confirm(string? platformDocument, string? tenantDocument, DateTimeOffset confirmedAt)
    {
        PlatformDocument = platformDocument;
        TenantDocument = tenantDocument;
        ConfirmedAt = confirmedAt;
    }
}
