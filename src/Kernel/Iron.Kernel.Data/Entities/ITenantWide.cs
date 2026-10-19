namespace Iron.Kernel.Data.Entities;

/// <summary>
/// Marks an entity that belongs to the whole tenant on purpose and has no owning organisation, for example shared
/// reference data, or kernel data such as the roles. The owning-organisation check accepts it; everything else in a
/// business module must be a <see cref="BusinessRecord"/> (TF-220). Belonging to the whole tenant, it has no record
/// policy, so it is also an <see cref="IOwnAccessRule"/> (ADR-0032).
/// </summary>
public interface ITenantWide : IOwnAccessRule
{
}
