using Iron.Kernel.Api.Context;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Files.Contracts;

namespace Iron.Kernel.Compliance.Services;

/// <summary>
/// The release this unit runs (ADR-0052): the version the release workflow stamped into its images (<c>Release:Version</c>,
/// the images' <c>Release__Version</c>), such as <c>2026.10.23.57</c>, or none for an image built on a laptop, which is a
/// development build.
/// </summary>
public sealed class ReleaseOptions
{
    public const string SectionName = "Release";
    public const string Development = "development";

    public string? Version { get; set; }

    public bool IsDevelopment => string.IsNullOrWhiteSpace(Version) || string.Equals(Version, Development, StringComparison.Ordinal);

    /// <summary>The version as the unit names it: the release's, or <c>development</c>.</summary>
    public string Name => IsDevelopment ? Development : Version!.Trim();
}

/// <summary>The validation pack of the installed version, as the unit finds it in its tenant's container.</summary>
public sealed record ValidationPackInfo(string Version, bool Development, bool Available, long? Bytes, string Key);

/// <summary>A pack opened for download.</summary>
public sealed record OpenedValidationPack(string Version, string FileName, byte[] Content);

/// <summary>
/// The validation pack of the version this tenant runs (TF-218 AC3, ADR-0052). At each update the fleet places the pack of
/// the version it installs in the tenant's own container, under <c>validation/&lt;version&gt;/</c>, so the unit serves it
/// without asking the control plane (ADR-0018). A development build serves the latest local pack, if one was placed.
/// Downloading it is audited.
/// </summary>
public sealed class ValidationPacks(ReleaseOptions release, IObjectStorage storage, IAuditWriter audit, ICurrentPrincipal current)
{
    public static string KeyOf(string version) => $"validation/{version}/validation-pack-{version}.zip";

    public async Task<ValidationPackInfo> InstalledAsync(CancellationToken cancellationToken)
    {
        var key = KeyOf(release.Name);
        using var stored = await GetAsync(key, cancellationToken).ConfigureAwait(false);
        return new ValidationPackInfo(release.Name, release.IsDevelopment, stored is not null, stored?.Length, key);
    }

    public async Task<Result<OpenedValidationPack>> OpenAsync(CancellationToken cancellationToken)
    {
        var key = KeyOf(release.Name);
        var stored = await GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return Error.NotFound(ComplianceErrors.ValidationPackMissing, release.IsDevelopment
                ? "This is a development build and no local validation pack was placed for it. Run make validation-pack, then make update-unit."
                : $"The validation pack of version {release.Name} is not in this tenant's storage yet. The next update places it; ask the operator.");
        }

        byte[] content;
        using (stored)
        await using (var buffer = new MemoryStream())
        {
            await stored.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            content = buffer.ToArray();
        }

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["version"] = release.Name,
            ["sha256"] = EvidencePackWriter.Sha256(content),
        };
        await audit.WriteAsync(new AuditEvent(ComplianceAuditEvents.ValidationPackDownloaded, AuditOutcome.Success, current.Id, $"Validation pack {release.Name}", details, "validation_pack"), cancellationToken).ConfigureAwait(false);
        return new OpenedValidationPack(release.Name, $"validation-pack-{release.Name}.zip", content);
    }

    /// <summary>The stored pack, or nothing when there is none or the host keeps no files.</summary>
    private async Task<StoredObject?> GetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await storage.GetAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return null; // a host without the Files module keeps no files
        }
    }
}

/// <summary>The <c>release</c> part of <c>/me/context</c>: the version this unit runs, for the help panel and the console.</summary>
public sealed record ContextRelease(string Version, bool Development);

public sealed class ReleaseContextPart(ReleaseOptions release) : IContextPart
{
    public const string PartName = "release";

    public string Name => PartName;

    public Task<object> BuildAsync(Guid principalId, CancellationToken cancellationToken) => Task.FromResult<object>(new ContextRelease(release.Name, release.IsDevelopment));
}
