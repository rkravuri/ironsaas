namespace Iron.Kernel.Samples.Contracts;

/// <summary>
/// The sample module's permissions (ADR-0057). The module runs only in units the operator marks for testing, so these
/// exist only there: in every other unit no role can hold them and no page asks for them.
/// </summary>
public static class SamplesPermissions
{
    public static readonly Permission RecordsRead = new("samples.records.read", "View sample records", Risk.Low);

    public static readonly Permission RecordsManage = new("samples.records.manage", "Create, change, share and tag sample records", Risk.Low);

    public static IReadOnlyList<Permission> All => [RecordsRead, RecordsManage];
}

public static class SamplesErrors
{
    public const string UnitRequired = "samples.record.unit_required";
}

/// <summary>Where a unit's configuration switches the sample module on: the fleet writes it into a marked tenant's unit secret.</summary>
public static class SamplesConfiguration
{
    public const string EnabledKey = "Samples:Enabled";
}
