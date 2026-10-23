using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Fleet.Services.Steps;

namespace Iron.Kernel.Fleet.Services;

/// <summary>
/// The fleet image's command <c>publish-validation-pack &lt;version&gt; &lt;file&gt;</c> (ADR-0052): puts a release's validation
/// pack into the platform's releases container under its version, where the next update of every unit finds it. On the
/// laptop <c>make validation-pack</c> runs it for the <c>development</c> pack; a deployment runs it with the pack the
/// release workflow built. A unit command, so the host runs it as it runs the database commands (ADR-0011).
/// </summary>
public sealed class ReleasePackCommand(FleetOptions options) : IUnitCommand
{
    public string Name => DatabaseCommands.PublishValidationPack;

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count != 2 || !ReleasePacks.IsVersion(args[0]) || !File.Exists(args[1]))
        {
            await Console.Error.WriteLineAsync($"usage: {Name} <version, such as 2026.10.23.57, or development> <the pack's zip file>").ConfigureAwait(false);
            return 2;
        }

        var (version, path) = (args[0], args[1]);
        var account = options.Storage.AccountConnectionString ?? throw new InvalidOperationException("Fleet:Storage:AccountConnectionString is required to publish validation packs.");
        var container = new BlobContainerClient(account, options.Storage.ReleasesContainer);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var file = File.OpenRead(path);
        await container.GetBlobClient(ReleasePacks.ReleaseKeyOf(version)).UploadAsync(file, new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "application/zip" } }, cancellationToken).ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Published the validation pack of version {version} to {options.Storage.ReleasesContainer}/{ReleasePacks.ReleaseKeyOf(version)}; the next update of each unit places it.").ConfigureAwait(false);
        return 0;
    }
}
