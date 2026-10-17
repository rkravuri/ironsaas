namespace Iron.Core.Principals;

/// <summary>The principal that acts when no person does: jobs, provisioning, seeds. One fixed identifier in every tenant.</summary>
public sealed class SystemPrincipal : ICurrentPrincipal
{
    public static readonly Guid SystemId = new("00000000-0000-0000-0000-000000000001");

    public static readonly SystemPrincipal Instance = new();

    private SystemPrincipal()
    {
    }

    public Guid Id => SystemId;
}
