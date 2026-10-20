using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Tenancy.Data;

/// <summary>
/// Where a store path stands (ADR-0035): <c>platform/flags</c> or <c>tenants/&lt;slug&gt;/flags</c>, the revision of its rules
/// and the revision the store holds, behind until the publisher or the fleet's reconcile writes it.
/// </summary>
public sealed class FlagPublication : AuditedEntity, ITenantWide
{
    private FlagPublication()
    {
        Path = string.Empty;
    }

    public FlagPublication(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }

    public string Path { get; private set; }

    public int Revision { get; private set; }

    public int PublishedRevision { get; private set; }

    public int Touch() => ++Revision;

    public void Published(int revision) => PublishedRevision = Math.Max(PublishedRevision, revision);
}
