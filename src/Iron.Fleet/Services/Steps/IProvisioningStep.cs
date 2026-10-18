using Iron.Fleet.Contracts;
using Iron.Tenancy.Contracts;

namespace Iron.Fleet.Services.Steps;

/// <summary>
/// What every step gets: the tenant, the fleet settings, and the secret paths where earlier steps left what later steps
/// need. Nothing generated lives only in memory, so a job can resume from a failed step in another process.
/// </summary>
public sealed class ProvisioningContext(TenantRecord tenant, FleetOptions options)
{
    public TenantRecord Tenant { get; } = tenant;

    public FleetOptions Options { get; } = options;

    public string Slug => Tenant.Slug;

    public string OwnerRole => $"{Slug}_owner";

    public string AppRole => $"{Slug}_app";

    public string DatabaseSecretPath => $"tenants/{Slug}/database";

    public string StorageSecretPath => $"tenants/{Slug}/storage";

    public string FleetSecretPath => $"tenants/{Slug}/fleet";

    public string UnitSecretPath => $"tenants/{Slug}/unit";

    public string UnitPolicy => $"tenant-{Slug}";

    public string ConnectionString(string role, string password) =>
        $"Host={Options.Database.UnitHost};Port={Options.Database.UnitPort};Database={Tenant.DatabaseName};Username={role};Password={password}";
}

/// <summary>One of the provisioning steps (TF-002). Steps are idempotent: running one again after a failure is safe.</summary>
public interface IProvisioningStep
{
    string Name { get; }

    int Order { get; }

    /// <summary>The kinds of job this step belongs to.</summary>
    IReadOnlyCollection<JobKind> Kinds { get; }

    /// <summary>Does the work and returns what the step reports (shown on the job), or null.</summary>
    Task<string?> RunAsync(ProvisioningContext context, CancellationToken cancellationToken);
}

public static class StepNames
{
    public const string Reserve = "reserve";
    public const string Database = "database";
    public const string Storage = "storage";
    public const string Secrets = "secrets";
    public const string Migrate = "migrate";
    public const string Seed = "seed";
    public const string Unit = "unit";
    public const string Verify = "verify";
    public const string Remove = "remove";
}
