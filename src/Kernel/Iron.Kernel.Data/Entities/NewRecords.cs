namespace Iron.Kernel.Data.Entities;

/// <summary>Where a new business record belongs when its creator does not say (TF-222 AC3, ADR-0038).</summary>
public static class NewRecords
{
    /// <summary>
    /// The unit the creator chose, else the unit they work for; null for a token or a job, which must name the unit.
    /// The save still refuses a unit that is not active (TF-223 AC2).
    /// </summary>
    public static Guid? OwningUnit(ICurrentPrincipal creator, Guid? chosen = null)
    {
        ArgumentNullException.ThrowIfNull(creator);
        return chosen ?? creator.WorkingUnitId;
    }
}
