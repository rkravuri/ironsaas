using Iron.Kernel.Tenancy.Contracts;

namespace Iron.Kernel.Fleet.Services;

/// <summary>What one tenant unit is made of: enough to render a Compose project (laptop) or a Kustomize overlay (Azure) (ADR-0002).</summary>
public sealed record UnitDefinition(
    TenantRecord Tenant,
    string ImageTag,
    UnitLimits Limits,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>What a command run in the unit's image returned.</summary>
public sealed record CommandResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// The runtime that runs tenant units: Docker Compose on the laptop (T14b), Kubernetes later. The provisioning steps
/// talk to units only through this, so the steps are the same everywhere.
/// </summary>
public interface IUnitRuntime
{
    /// <summary>Runs a command of the API image (<c>migrate</c>, <c>seed</c>) with the given environment and waits for it.</summary>
    Task<CommandResult> RunCommandAsync(UnitDefinition unit, IReadOnlyList<string> command, CancellationToken cancellationToken);

    /// <summary>Starts or updates the unit's containers and routes.</summary>
    Task ApplyAsync(UnitDefinition unit, CancellationToken cancellationToken);

    /// <summary>Null when the unit answers its readiness probe through the ingress, otherwise what is wrong.</summary>
    Task<string?> CheckReadyAsync(UnitDefinition unit, CancellationToken cancellationToken);

    /// <summary>Stops and removes the unit's containers and routes; data stays (TF-210 de-provision in S1).</summary>
    Task RemoveAsync(UnitDefinition unit, CancellationToken cancellationToken);
}

/// <summary>The runtime of a fleet without a configured runtime: every unit operation fails with a clear message.</summary>
public sealed class NoUnitRuntime : IUnitRuntime
{
    private const string Message = "No unit runtime is configured: set Fleet:Units:Runtime to compose (ADR-0019).";

    public Task<CommandResult> RunCommandAsync(UnitDefinition unit, IReadOnlyList<string> command, CancellationToken cancellationToken) => throw new InvalidOperationException(Message);

    public Task ApplyAsync(UnitDefinition unit, CancellationToken cancellationToken) => throw new InvalidOperationException(Message);

    public Task<string?> CheckReadyAsync(UnitDefinition unit, CancellationToken cancellationToken) => throw new InvalidOperationException(Message);

    public Task RemoveAsync(UnitDefinition unit, CancellationToken cancellationToken) => throw new InvalidOperationException(Message);
}
