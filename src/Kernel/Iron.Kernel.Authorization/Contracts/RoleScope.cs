namespace Iron.Kernel.Authorization.Contracts;

public enum RoleScopeKind
{
    Tenant,
    Unit,
    UnitWithChildren,
    Record,
}

/// <summary>
/// Where a role assignment applies (TF-033, ADR-0030): the whole tenant, one organisation unit with or without its
/// children, or one record. Access to a unit other than one's home unit is an assignment with a unit scope.
/// </summary>
public sealed record RoleScope
{
    public const int RecordTypeMaxLength = 100;

    private RoleScope(RoleScopeKind kind, Guid? unitId, string? recordType, Guid? recordId)
    {
        Kind = kind;
        UnitId = unitId;
        RecordType = recordType;
        RecordId = recordId;
    }

    public RoleScopeKind Kind { get; }

    public Guid? UnitId { get; }

    public string? RecordType { get; }

    public Guid? RecordId { get; }

    public static RoleScope Tenant { get; } = new(RoleScopeKind.Tenant, null, null, null);

    public static RoleScope Unit(Guid unitId, bool includeChildren = false)
    {
        if (unitId == Guid.Empty)
        {
            throw new ArgumentException("The unit id is empty.", nameof(unitId));
        }

        return new RoleScope(includeChildren ? RoleScopeKind.UnitWithChildren : RoleScopeKind.Unit, unitId, null, null);
    }

    public static RoleScope Record(string recordType, Guid recordId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordType);
        if (recordType.Length > RecordTypeMaxLength)
        {
            throw new ArgumentException($"A record type has at most {RecordTypeMaxLength} characters.", nameof(recordType));
        }

        if (recordId == Guid.Empty)
        {
            throw new ArgumentException("The record id is empty.", nameof(recordId));
        }

        return new RoleScope(RoleScopeKind.Record, null, recordType, recordId);
    }

    public override string ToString() => Kind switch
    {
        RoleScopeKind.Unit => $"unit:{UnitId:N}",
        RoleScopeKind.UnitWithChildren => $"unit_with_children:{UnitId:N}",
        RoleScopeKind.Record => $"record:{RecordType}:{RecordId:N}",
        _ => "tenant",
    };
}
