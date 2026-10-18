namespace Iron.Files.Services;

/// <summary>Bound from the <c>Storage</c> configuration section; in a unit the values come from its secret (TF-073).</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>The storage account connection string (Azurite or Azure). Used with <see cref="Container"/>.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>The tenant's container name: the tenant id.</summary>
    public string? Container { get; set; }

    /// <summary>Alternatively, a container URL with a shared access signature scoped to the tenant's container (ADR-0006).</summary>
    public string? ContainerSasUrl { get; set; }

    /// <summary>The largest upload accepted (TF-161 defaults; per tenant and record type in S2).</summary>
    public int MaxUploadMegabytes { get; set; } = 50;

    public long MaxUploadBytes => MaxUploadMegabytes * 1024L * 1024L;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ContainerSasUrl) || (!string.IsNullOrWhiteSpace(ConnectionString) && !string.IsNullOrWhiteSpace(Container));
}
