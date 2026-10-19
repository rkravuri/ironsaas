using Iron.Kernel.Authorization.Contracts;

namespace Iron.Kernel.Authorization.Services;

/// <summary>
/// The kernel's kinds (ADR-0029): a root, and units under it and under each other. No business meaning; a business
/// module replaces this service with its own kinds.
/// </summary>
public sealed class DefaultOrganisationKinds : IOrganisationKinds
{
    public IReadOnlyList<OrganisationKind> Kinds { get; } =
    [
        new(KernelOrganisationKinds.Root, "Root", [KernelOrganisationKinds.Unit]),
        new(KernelOrganisationKinds.Unit, "Unit", [KernelOrganisationKinds.Unit]),
    ];

    public string RootKind => KernelOrganisationKinds.Root;
}
