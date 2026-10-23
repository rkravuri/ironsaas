using Iron.Kernel.Fleet.Contracts;
using Iron.Kernel.Tenancy.Contracts;

namespace Iron.Kernel.Fleet.Services.Steps;

/// <summary>
/// What every step gets: the tenant, the fleet settings, and the secret paths where earlier steps left what later steps
/// need. Nothing generated lives only in memory, so a job can resume from a failed step in another process.
/// </summary>
public sealed class ProvisioningContext(TenantRecord tenant, FleetOptions options, JobRequest? request = null, Guid? jobId = null)
{
    /// <summary>The job the steps run for; its handover place is named after it (ADR-0046).</summary>
    public Guid JobId { get; } = jobId ?? Guid.Empty;

    public TenantRecord Tenant { get; } = tenant;

    public FleetOptions Options { get; } = options;

    /// <summary>What the job was asked for beyond its kind: a recovery job's administrator, reason and operator.</summary>
    public JobRequest? Request { get; } = request;

    public string Slug => Tenant.Slug;

    public string OwnerRole => $"{Slug}_owner";

    public string AppRole => $"{Slug}_app";

    /// <summary>The audit verifier's role (ADR-0050): it connects and reads the chain's tables, nothing else.</summary>
    public string AuditReaderRole => $"{Slug}_audit";

    /// <summary>The unit's own client in the control plane (ADR-0050), which sends its audit anchors.</summary>
    public string ControlClientId => $"unit-{Slug}";

    public string DatabaseSecretPath => $"tenants/{Slug}/database";

    public string StorageSecretPath => $"tenants/{Slug}/storage";

    public string FleetSecretPath => $"tenants/{Slug}/fleet";

    public string UnitSecretPath => $"tenants/{Slug}/unit";

    /// <summary>Where the control plane keeps the tenant's effective entitlements for its unit (ADR-0034).</summary>
    public string EntitlementsSecretPath => $"tenants/{Slug}/entitlements";

    /// <summary>Where the control plane keeps the tenant's flag rules (ADR-0035).</summary>
    public string FlagsSecretPath => $"tenants/{Slug}/flags";

    /// <summary>The global and region flag rules every unit reads (ADR-0035): one write for the platform.</summary>
    public const string PlatformFlagsPath = "platform/flags";

    /// <summary>The platform's documents every person accepts, which every unit reads (ADR-0049).</summary>
    public const string PlatformDocumentsPath = "platform/documents";

    /// <summary>Where the secrets of the tenant's secret-reference settings live (ADR-0036): the unit reads everything below it.</summary>
    public string SettingsSecretPaths => $"tenants/{Slug}/settings/*";

    /// <summary>The client secrets of the tenant's single sign-on providers, one path each, which the unit itself writes (ADR-0048).</summary>
    public string SsoSecretPaths => $"tenants/{Slug}/sso/*";

    public string UnitPolicy => $"tenant-{Slug}";

    /// <summary>The policy of the short-lived tokens that may write the tenant's handover places, and nothing else.</summary>
    public string HandoverPolicy => $"tenant-{Slug}-handover";

    /// <summary>The job's one-time place (ADR-0046): an image command writes the link there, the control plane takes it once.</summary>
    public string HandoverPath => HandoverPlaces.PathOf(Slug, JobId);

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
    public const string Recover = "recover";
    public const string Impersonate = "impersonate";
    public const string BreakGlassOpen = "break-glass-open";
    public const string BreakGlassClose = "break-glass-close";
    public const string ValidationPack = "validation-pack";
}
