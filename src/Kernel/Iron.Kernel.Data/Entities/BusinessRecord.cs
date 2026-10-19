namespace Iron.Kernel.Data.Entities;

/// <summary>How a record is visible across the organisations of a tenant (TF-220).</summary>
public enum SharingMode
{
    /// <summary>Only the owning organisation sees it.</summary>
    Private = 0,

    /// <summary>The owning organisation and the organisations it is shared with see it (sharing lists arrive in S2).</summary>
    Shared = 1,

    /// <summary>Every organisation of the tenant sees it.</summary>
    Global = 2,
}

/// <summary>
/// Base of every business and master record (TF-220, decision D-13): it belongs to one organisation unit of the tenant
/// and has a sharing mode. The data layer filters lists and reads by the user's organisation access from S2 on.
/// </summary>
public abstract class BusinessRecord : AuditedEntity
{
    /// <summary>For Entity Framework only. Code uses the constructors that take the owning organisation.</summary>
    protected BusinessRecord()
    {
    }

    protected BusinessRecord(Guid owningOrganisationId)
    {
        OwningOrganisationId = RequireOrganisation(owningOrganisationId);
    }

    protected BusinessRecord(Guid id, Guid owningOrganisationId)
        : base(id)
    {
        OwningOrganisationId = RequireOrganisation(owningOrganisationId);
    }

    public Guid OwningOrganisationId { get; private set; }

    public SharingMode SharingMode { get; protected set; } = SharingMode.Private;

    private static Guid RequireOrganisation(Guid owningOrganisationId)
    {
        if (owningOrganisationId == Guid.Empty)
        {
            throw new ArgumentException("A business record needs an owning organisation.", nameof(owningOrganisationId));
        }

        return owningOrganisationId;
    }
}
