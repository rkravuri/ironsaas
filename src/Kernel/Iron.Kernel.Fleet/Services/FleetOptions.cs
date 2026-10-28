using Iron.Kernel.Tenancy.Contracts;

namespace Iron.Kernel.Fleet.Services;

/// <summary>
/// Bound from the <c>Fleet</c> configuration section of the fleet worker. Two views of every platform service: the one
/// the fleet uses from where it runs, and the one the tenant units see from their network (ADR-0018).
/// </summary>
public sealed class FleetOptions
{
    public const string SectionName = "Fleet";

    public FleetDatabaseOptions Database { get; set; } = new();

    public FleetStorageOptions Storage { get; set; } = new();

    public FleetSecretsOptions Secrets { get; set; } = new();

    public FleetUnitsOptions Units { get; set; } = new();

    public FleetControlPlaneOptions ControlPlane { get; set; } = new();

    /// <summary>The audit chains' daily verification (TF-061, ADR-0050).</summary>
    public FleetAuditOptions Audit { get; set; } = new();

    /// <summary>The key worker (ADR-0054).</summary>
    public FleetKeysOptions Keys { get; set; } = new();

    /// <summary>How often the worker looks for Requested jobs.</summary>
    public int PollSeconds { get; set; } = 5;

    public sealed class FleetDatabaseOptions
    {
        /// <summary>A platform superuser connection, used to create roles and databases.</summary>
        public string? AdminConnectionString { get; set; }

        /// <summary>The PostgreSQL host and port as the units see them.</summary>
        public string UnitHost { get; set; } = "postgres";

        public int UnitPort { get; set; } = 5432;
    }

    public sealed class FleetStorageOptions
    {
        /// <summary>The storage account connection string, used to create containers and sign access.</summary>
        public string? AccountConnectionString { get; set; }

        /// <summary>The blob endpoint as the units see it, for the signed container URL: <c>http://blob:10000/foundation</c>.</summary>
        public string? UnitBlobEndpoint { get; set; }

        /// <summary>How long a unit's container signature is valid.</summary>
        public int SasValidDays { get; set; } = 365;

        /// <summary>The platform container that keeps each release's validation pack under its version (ADR-0052).</summary>
        public string ReleasesContainer { get; set; } = "releases";
    }

    public sealed class FleetControlPlaneOptions
    {
        /// <summary>The control plane's API as the units see it, for their audit anchors (ADR-0050): <c>http://control-api:8080</c>.</summary>
        public string UnitAddress { get; set; } = "http://control-api:8080";
    }

    public sealed class FleetKeysOptions
    {
        /// <summary>How often the key worker looks for rotations to carry out and old versions to retire.</summary>
        public int PollSeconds { get; set; } = 5;
    }

    public sealed class FleetAuditOptions
    {
        /// <summary>How often the fleet asks what verification is due; a tenant's own is due once a day.</summary>
        public int PollSeconds { get; set; } = 60;
    }

    public sealed class FleetSecretsOptions
    {
        /// <summary>The secret store address as the units see it: <c>http://openbao:8200</c>.</summary>
        public string? UnitAddress { get; set; }

        /// <summary>How long a unit token lives.</summary>
        public int UnitTokenValidDays { get; set; } = 365;
    }

    public sealed class FleetUnitsOptions
    {
        public const string ComposeRuntime = "compose";

        /// <summary><c>compose</c> for the laptop runtime (ADR-0019); anything else means no runtime.</summary>
        public string Runtime { get; set; } = "none";

        /// <summary>The image tag the units run: <c>dev</c> on the laptop, the build number in the pipeline.</summary>
        public string ImageTag { get; set; } = "dev";

        /// <summary>The images' repository prefix: <c>foundation</c> for <c>foundation/iron-api</c>.</summary>
        public string ImageRepository { get; set; } = "foundation";

        /// <summary>The Compose network the platform services are on; units join it to reach them.</summary>
        public string PlatformNetwork { get; set; } = "foundation";

        /// <summary>Where the fleet writes one directory per unit with its compose.yaml and .env.</summary>
        public string ComposeDirectory { get; set; } = "/var/lib/foundation/units";

        /// <summary>The ingress as the fleet reaches it, for readiness checks with the tenant's Host header: <c>http://traefik</c> inside the network, <c>http://localhost</c> from the laptop.</summary>
        public string IngressUrl { get; set; } = "http://traefik";

        /// <summary>The telemetry collector as the units see it.</summary>
        public string OtlpEndpoint { get; set; } = "http://otel-collector:4317";

        /// <summary>How long verify waits for a new unit to answer its readiness probe.</summary>
        public int ReadyTimeoutSeconds { get; set; } = 180;

        /// <summary>How long an image command (migrate, seed) may take.</summary>
        public int CommandTimeoutSeconds { get; set; } = 300;

        /// <summary>How long <c>docker compose up --wait</c> or <c>down</c> may take.</summary>
        public int ComposeTimeoutSeconds { get; set; } = 300;
    }
}

/// <summary>The container limits of a size class (TF-210, TF-007): the same image, different room.</summary>
public sealed record UnitLimits(double ApiCpus, int ApiMemoryMb, double WorkerCpus, int WorkerMemoryMb, double ShellCpus = 0.25, int ShellMemoryMb = 64);

public static class SizeClasses
{
    public static UnitLimits LimitsOf(SizeClass sizeClass) => sizeClass switch
    {
        SizeClass.Small => new UnitLimits(0.5, 512, 0.5, 512),
        SizeClass.Medium => new UnitLimits(1.0, 1024, 1.0, 1024, 0.5, 128),
        SizeClass.Large => new UnitLimits(2.0, 2048, 2.0, 2048, 0.5, 128),
        _ => throw new ArgumentOutOfRangeException(nameof(sizeClass), sizeClass, "Unknown size class."),
    };
}
