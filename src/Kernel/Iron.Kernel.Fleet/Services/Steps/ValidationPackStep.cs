using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Iron.Kernel.Fleet.Contracts;
using Iron.Kernel.Security.Secrets;

namespace Iron.Kernel.Fleet.Services.Steps;

/// <summary>
/// Where releases' validation packs are kept and where a tenant's unit finds the one of its version (TF-218, ADR-0052).
/// The release's pack goes to the platform's <c>releases</c> container under its version; at each update the fleet copies
/// the pack of the version it installs into the tenant's own container, where the unit's Compliance module reads it.
/// </summary>
public static partial class ReleasePacks
{
    public const string Development = "development";

    /// <summary>The pack's name in the releases container: <c>2026.10.23.57/validation-pack-2026.10.23.57.zip</c>.</summary>
    public static string ReleaseKeyOf(string version) => $"{version}/validation-pack-{version}.zip";

    /// <summary>The pack's name in a tenant's container, as the unit reads it (Compliance's <c>ValidationPacks.KeyOf</c>).</summary>
    public static string TenantKeyOf(string version) => $"validation/{version}/validation-pack-{version}.zip";

    /// <summary>A release version (<c>2026.10.23.57</c>) or <c>development</c>, as the image's <c>version</c> command prints it.</summary>
    public static bool IsVersion(string? text) => text is not null && (text == Development || VersionPattern().IsMatch(text));

    [GeneratedRegex(@"^\d{4}\.\d{2}\.\d{2}\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}

/// <summary>
/// Step 9 of a provisioning or an update (TF-218 AC3, ADR-0052): the version the unit's image says it is, and the
/// validation pack of exactly that version copied from the releases container into the tenant's own, so the unit serves
/// it without asking the control plane (ADR-0018). A release whose pack is not published yet leaves the unit saying so;
/// it never stops an update, which must heal a unit whatever else is missing.
/// </summary>
public sealed class ValidationPackStep(IUnitRuntime runtime, ISecretStore secrets, SecretStoreOptions fleetStore) : IProvisioningStep
{
    public string Name => StepNames.ValidationPack;

    public int Order => 9;

    public IReadOnlyCollection<JobKind> Kinds => [JobKind.Provision, JobKind.Update];

    public async Task<string?> RunAsync(ProvisioningContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var environment = await UnitEnvironment.ForUnitAsync(context, secrets, fleetStore, cancellationToken).ConfigureAwait(false);
        var said = await runtime.RunCommandAsync(context.ToUnit(environment), ["version"], cancellationToken).ConfigureAwait(false);
        var version = said.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (!said.Succeeded || !ReleasePacks.IsVersion(version))
        {
            return "the unit's image did not say its version, so no validation pack was placed";
        }

        var account = context.Options.Storage.AccountConnectionString ?? throw new InvalidOperationException("Fleet:Storage:AccountConnectionString is required to place validation packs.");
        var releases = new BlobContainerClient(account, context.Options.Storage.ReleasesContainer).GetBlobClient(ReleasePacks.ReleaseKeyOf(version!));
        try
        {
            var pack = await releases.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
            var target = new BlobContainerClient(account, context.Tenant.StorageContainer).GetBlobClient(ReleasePacks.TenantKeyOf(version!));
            await target.UploadAsync(pack.Value.Content, new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "application/zip" } }, cancellationToken).ConfigureAwait(false);
            return $"validation pack of version {version} placed ({pack.Value.Content.ToMemory().Length} bytes)";
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return $"no validation pack of version {version} in the releases container yet: the unit says so until an update places it";
        }
    }
}
