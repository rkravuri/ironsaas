using Iron.Kernel.Fleet.Contracts;
using Iron.Kernel.Tenancy.Contracts;

namespace Iron.Kernel.Fleet.Services.Steps;

/// <summary>Step 1: the tenant is registered, in Provisioning, with at least one hostname. Hostname uniqueness is the registry's constraint.</summary>
public sealed class ReserveStep : IProvisioningStep
{
    public string Name => StepNames.Reserve;

    public int Order => 1;

    public IReadOnlyCollection<JobKind> Kinds => [JobKind.Provision];

    public Task<string?> RunAsync(ProvisioningContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var tenant = context.Tenant;
        if (tenant.Status != TenantStatus.Provisioning)
        {
            throw new InvalidOperationException($"Tenant '{tenant.Slug}' is {tenant.Status.ToString().ToLowerInvariant()}; only a tenant in Provisioning can be provisioned.");
        }

        if (tenant.Hostnames.Count == 0)
        {
            throw new InvalidOperationException($"Tenant '{tenant.Slug}' has no hostname.");
        }

        return Task.FromResult<string?>($"{tenant.Slug}: {string.Join(", ", tenant.Hostnames)}; size {tenant.SizeClass.ToString().ToLowerInvariant()}; database {tenant.DatabaseName}; container {tenant.StorageContainer}");
    }
}
