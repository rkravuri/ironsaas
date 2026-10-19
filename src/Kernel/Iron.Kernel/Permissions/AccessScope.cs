namespace Iron.Kernel;

public enum AccessScopeKind
{
    Tenant,
    Unit,
    Record,
}

/// <summary>
/// Where an access happens (TF-033, ADR-0030): the whole tenant, one organisation unit, or one record with the unit
/// that owns it. A permission check carries one, so a role assigned to a unit applies to that unit's records and not to
/// others. Where an assignment applies is a different question, answered by the assignment's own scope.
/// </summary>
public sealed record AccessScope
{
    private AccessScope(AccessScopeKind kind, Guid? unitId, string? recordType, Guid? recordId)
    {
        Kind = kind;
        UnitId = unitId;
        RecordType = recordType;
        RecordId = recordId;
    }

    public AccessScopeKind Kind { get; }

    /// <summary>The unit for a unit scope, the owning unit for a record scope; null for the tenant.</summary>
    public Guid? UnitId { get; }

    public string? RecordType { get; }

    public Guid? RecordId { get; }

    /// <summary>No unit and no record: only tenant-wide assignments apply.</summary>
    public static AccessScope Tenant { get; } = new(AccessScopeKind.Tenant, null, null, null);

    public static AccessScope Unit(Guid unitId)
    {
        if (unitId == Guid.Empty)
        {
            throw new ArgumentException("The unit id is empty.", nameof(unitId));
        }

        return new AccessScope(AccessScopeKind.Unit, unitId, null, null);
    }

    /// <summary>One record of a type, with the unit that owns it (<c>BusinessRecord.OwningOrganisationId</c>).</summary>
    public static AccessScope Record(string recordType, Guid recordId, Guid owningUnitId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordType);
        if (recordId == Guid.Empty)
        {
            throw new ArgumentException("The record id is empty.", nameof(recordId));
        }

        if (owningUnitId == Guid.Empty)
        {
            throw new ArgumentException("The owning unit id is empty.", nameof(owningUnitId));
        }

        return new AccessScope(AccessScopeKind.Record, owningUnitId, recordType, recordId);
    }

    /// <summary>For logs and audit details: <c>tenant</c>, <c>unit:&lt;id&gt;</c>, <c>record:&lt;type&gt;:&lt;id&gt;</c>.</summary>
    public override string ToString() => Kind switch
    {
        AccessScopeKind.Unit => $"unit:{UnitId:N}",
        AccessScopeKind.Record => $"record:{RecordType}:{RecordId:N}",
        _ => "tenant",
    };
}
