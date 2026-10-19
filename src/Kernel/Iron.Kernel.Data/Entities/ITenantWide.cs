namespace Iron.Kernel.Data.Entities;

/// <summary>
/// Marks an entity that belongs to the whole tenant on purpose and has no owning organisation: kernel data a list pages
/// directly (roles, principals, audit events), or shared reference data of a business module. The owning-organisation
/// check accepts it; everything else in a business module must be a <see cref="BusinessRecord"/> (TF-220). Only such an
/// entity pages without a record policy; a business record is never one, so its raw query cannot be paged (ADR-0032).
/// </summary>
public interface ITenantWide
{
}
