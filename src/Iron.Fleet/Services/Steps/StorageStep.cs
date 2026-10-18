using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Iron.Fleet.Contracts;
using Iron.Security.Secrets;

namespace Iron.Fleet.Services.Steps;

/// <summary>
/// Step 3: the tenant's blob container and a container-scoped signature (ADR-0006), kept in the store at
/// <c>tenants/&lt;slug&gt;/storage</c>. The signature is built for the endpoint the units see, not the fleet's.
/// An update runs it too (ADR-0028): a lost signature is issued again; one in place is kept.
/// </summary>
public sealed class StorageStep(ISecretStore secrets, TimeProvider time) : IProvisioningStep
{
    public const string ContainerSasUrlKey = "container_sas_url";

    public string Name => StepNames.Storage;

    public int Order => 3;

    public IReadOnlyCollection<JobKind> Kinds => [JobKind.Provision, JobKind.Update];

    public async Task<string?> RunAsync(ProvisioningContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var storage = context.Options.Storage;
        var account = storage.AccountConnectionString ?? throw new InvalidOperationException("Fleet:Storage:AccountConnectionString is required to create tenant containers.");

        var container = new BlobContainerClient(account, context.Tenant.StorageContainer);
        var created = await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false) is not null;

        var existing = await secrets.ReadAsync(context.StorageSecretPath, cancellationToken).ConfigureAwait(false);
        if (existing?.GetValueOrDefault(ContainerSasUrlKey) is null)
        {
            var signer = string.IsNullOrWhiteSpace(storage.UnitBlobEndpoint) ? container : new BlobContainerClient(ForUnits(account, storage.UnitBlobEndpoint), context.Tenant.StorageContainer);
            if (!signer.CanGenerateSasUri)
            {
                throw new InvalidOperationException("The storage account connection string has no key, so no container signature can be issued.");
            }

            var permissions = BlobContainerSasPermissions.Read | BlobContainerSasPermissions.Add | BlobContainerSasPermissions.Create | BlobContainerSasPermissions.Write | BlobContainerSasPermissions.Delete | BlobContainerSasPermissions.List;
            var sas = signer.GenerateSasUri(permissions, time.GetUtcNow().AddDays(storage.SasValidDays));
            await secrets.WriteAsync(context.StorageSecretPath, new Dictionary<string, string>(StringComparer.Ordinal) { [ContainerSasUrlKey] = sas.ToString() }, cancellationToken).ConfigureAwait(false);
        }

        return $"container {context.Tenant.StorageContainer} {(created ? "created" : "exists")}; signature valid {storage.SasValidDays} days";
    }

    /// <summary>The same account and key, with the blob endpoint the units see.</summary>
    private static string ForUnits(string accountConnectionString, string unitBlobEndpoint)
    {
        var parts = accountConnectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.StartsWith("BlobEndpoint=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        parts.Add("BlobEndpoint=" + unitBlobEndpoint.TrimEnd('/'));
        return string.Join(";", parts) + ";";
    }
}
