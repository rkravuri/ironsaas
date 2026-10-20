namespace Iron.Kernel.Authorization.Contracts;

/// <summary>A kind of organisation unit: its code, its name, and the kinds it may hold as children.</summary>
public sealed record OrganisationKind(string Code, string Name, IReadOnlyList<string> ChildKinds);

/// <summary>
/// The kinds of organisation unit (ADR-0029, extension point). The tree knows no business meaning: the kernel offers
/// <c>root</c> and <c>unit</c>, and <c>Iron.Business.Organisations</c> replaces this service with legal entity, business
/// unit, department and team (<c>services.Replace(ServiceDescriptor.Singleton&lt;IOrganisationKinds, ...&gt;())</c>). A
/// purchasing group is not a kind: it serves several units and owns no records, so it is a business master of its own
/// (the Business PRD, section 5.1).
/// </summary>
public interface IOrganisationKinds
{
    /// <summary>Every kind, the root's first.</summary>
    IReadOnlyList<OrganisationKind> Kinds { get; }

    /// <summary>The kind the seed gives the root unit. No unit may hold it as a child.</summary>
    string RootKind { get; }
}

/// <summary>The kinds the kernel offers until a business module supplies its own.</summary>
public static class KernelOrganisationKinds
{
    public const string Root = "root";
    public const string Unit = "unit";
}

public enum UnitStatus
{
    Active,
    Inactive,
}

/// <summary>What other modules see of a unit. No business fields: those belong to the business module that owns the kinds.</summary>
public sealed record OrganisationUnitInfo(Guid Id, string Name, string Code, string Kind, Guid? ParentId, int Depth, UnitStatus Status);

/// <summary>What other modules may ask of the organisation tree (TF-034): Identity gives principals their home unit.</summary>
public interface IOrganisationUnits
{
    /// <summary>The root unit, or null before the seed has created it.</summary>
    Task<OrganisationUnitInfo?> RootAsync(CancellationToken cancellationToken);

    Task<OrganisationUnitInfo?> FindAsync(Guid unitId, CancellationToken cancellationToken);

    /// <summary>The ids from the root down to the unit, the unit last; empty for a unit that does not exist. One read of the materialised path.</summary>
    Task<IReadOnlyList<Guid>> PathAsync(Guid unitId, CancellationToken cancellationToken);
}
