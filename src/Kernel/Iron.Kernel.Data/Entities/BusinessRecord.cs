namespace Iron.Kernel.Data.Entities;

/// <summary>Who sees a record beyond its owning unit (TF-220, TF-222, ADR-0038).</summary>
public enum SharingMode
{
    /// <summary>Its owning unit, and whoever's scope covers that unit.</summary>
    Private = 0,

    /// <summary>As private, and also the units listed on the record (<c>kernel_data.record_shares</c>).</summary>
    Shared = 1,

    /// <summary>Every unit of the tenant: anyone who may read the record type anywhere.</summary>
    Tenant = 2,
}

/// <summary>
/// Base of every business and master record (TF-220, decision D-13): it belongs to one organisation unit of the tenant
/// and has a sharing mode. Lists and reads by id go through the record type's policy, which follows the mode in the
/// query (ADR-0032, ADR-0038). The mode changes only through <c>IRecordSharing</c>, which asks the policy and audits.
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

    public SharingMode SharingMode { get; private set; } = SharingMode.Private;

    /// <summary>
    /// The codes of the visibility tags the record carries, in order (TF-241, ADR-0057): none, and everyone who passes
    /// roles and units sees it; otherwise a reader who holds tags sees it when they share one. A column of the module's
    /// own table, so a list's tag filter is a condition on the row, and the field history records every change.
    /// </summary>
    public IReadOnlyList<string> VisibilityTags { get; private set; } = [];

    /// <summary>Through <c>IRecordSharing</c> only, which asks the policy, keeps the listed units and audits the change.</summary>
    internal void ChangeSharing(SharingMode mode) => SharingMode = mode;

    /// <summary>
    /// Through <c>IRecordTags</c>, which asks the policy and holds rule 5, or from the record's master when it is created
    /// (rule 6): a module never sets tags itself.
    /// </summary>
    internal void ChangeVisibilityTags(IEnumerable<string> codes) => VisibilityTags = codes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static Guid RequireOrganisation(Guid owningOrganisationId)
    {
        if (owningOrganisationId == Guid.Empty)
        {
            throw new ArgumentException("A business record needs an owning organisation.", nameof(owningOrganisationId));
        }

        return owningOrganisationId;
    }
}
