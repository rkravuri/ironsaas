namespace Iron.Kernel.Data.Entities;

/// <summary>
/// Marks an entity of a business module that belongs to the whole tenant on purpose and has no owning organisation,
/// for example shared reference data. The owning-organisation check accepts it; everything else in a business
/// module must be a <see cref="BusinessRecord"/> (TF-220).
/// </summary>
public interface ITenantWide
{
}
