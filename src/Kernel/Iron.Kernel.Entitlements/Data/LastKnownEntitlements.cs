using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Entitlements.Data;

/// <summary>
/// The last answer the store gave, and when it was last confirmed (TF-043 AC3): a unit that restarts while the store is
/// down still knows its entitlements until the answer is 24 hours old. One row, keyed <c>current</c>.
/// </summary>
public sealed class LastKnownEntitlements : AuditedEntity, ITenantWide
{
    public const string CurrentKey = "current";

    private LastKnownEntitlements()
    {
        Key = CurrentKey;
        Document = "{}";
    }

    public LastKnownEntitlements(string document, int revision, DateTimeOffset confirmedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document);
        Key = CurrentKey;
        Document = document;
        Revision = revision;
        ConfirmedAt = confirmedAt;
    }

    public string Key { get; private set; }

    /// <summary>The document as the store held it (jsonb).</summary>
    public string Document { get; private set; }

    public int Revision { get; private set; }

    /// <summary>When the store last answered with this document.</summary>
    public DateTimeOffset ConfirmedAt { get; private set; }

    public void Confirm(string document, int revision, DateTimeOffset confirmedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document);
        Document = document;
        Revision = revision;
        ConfirmedAt = confirmedAt;
    }
}
