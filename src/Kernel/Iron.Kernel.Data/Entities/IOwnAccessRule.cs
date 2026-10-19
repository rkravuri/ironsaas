namespace Iron.Kernel.Data.Entities;

/// <summary>
/// Marks an entity that is not a business record and whose module applies its own access rule: the principals, read by
/// their home unit; the role assignments, read by their scope; and every <see cref="ITenantWide"/> entity. A list of such
/// an entity pages directly with <c>ToPageAsync</c>, and the module's endpoint is where its rule lives. A business record
/// never carries it: it is listed and read through its record policy (ADR-0032).
/// </summary>
public interface IOwnAccessRule
{
}
