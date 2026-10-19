namespace Iron.Kernel.Data.Entities;

/// <summary>
/// Whether organisation units may own records. The data library declares it and Authorization, which keeps the tree,
/// implements it, so the library never references a module (ADR-0029). Without an implementation the owning
/// organisation is not checked on save.
/// </summary>
public interface IOrganisationUnitCheck
{
    /// <summary>The ids among these that are not active units of this tenant: unknown or deactivated.</summary>
    Task<IReadOnlyList<Guid>> NotActiveAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken);
}

/// <summary>A business record was saved with an owning organisation that is not an active unit (TF-034, A1).</summary>
public sealed class OwningOrganisationException(IReadOnlyList<Guid> unitIds)
    : InvalidOperationException($"A business record must be owned by an active organisation unit; {string.Join(", ", unitIds)} is not one. Check the unit before saving.")
{
    public IReadOnlyList<Guid> UnitIds { get; } = unitIds;
}
