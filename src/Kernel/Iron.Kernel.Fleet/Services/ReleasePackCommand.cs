using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Iron.Kernel.Fleet.Services.Steps;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Fleet.Services;

/// <summary>
/// The fleet image's command <c>publish-validation-pack &lt;version&gt; &lt;file&gt;</c> (ADR-0052): puts a release's validation
/// pack into the platform's releases container under its version, where the next update of every unit finds it. On the
/// laptop <c>make validation-pack</c> runs it for the <c>development</c> pack; a deployment runs it with the pack the
/// release workflow built.
/// </summary>
public static class ReleasePackCommand
{
    public const string Name = "publish-validation-pack";

    public static bool IsCommand(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Length > 0 && string.Equals(args[0], Name, StringComparison.Ordinal);
    }

    public static async Task<int> RunAsync(IServiceProvider services, string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 3 || !ReleasePacks.IsVersion(args[1]) || !File.Exists(args[2]))
        {
            await Console.Error.WriteLineAsync($"usage: {Name} <version, such as 2026.10.23.57, or development> <the pack's zip file>").ConfigureAwait(false);
            return 2;
        }

        var options = services.GetRequiredService<FleetOptions>();
        var account = options.Storage.AccountConnectionString ?? throw new InvalidOperationException("Fleet:Storage:AccountConnectionString is required to publish validation packs.");
        var container = new BlobContainerClient(account, options.Storage.ReleasesContainer);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var file = File.OpenRead(args[2]);
        await container.GetBlobClient(ReleasePacks.ReleaseKeyOf(args[1])).UploadAsync(file, new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "application/zip" } }, cancellationToken).ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"Published the validation pack of version {args[1]} to {options.Storage.ReleasesContainer}/{ReleasePacks.ReleaseKeyOf(args[1])}; the next update of each unit places it.").ConfigureAwait(false);
        return 0;
    }
}
