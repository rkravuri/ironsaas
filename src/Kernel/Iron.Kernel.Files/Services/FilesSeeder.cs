using Iron.Kernel.Data.Seeding;
using Iron.Kernel.Files.Contracts;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Files.Services;

/// <summary>Makes sure the tenant's container exists. The fleet creates it when it creates the unit (T14); the seed does it too, so the laptop needs no extra step.</summary>
public sealed class FilesSeeder(IObjectStorage storage, ILogger<FilesSeeder> logger) : ISeeder
{
    public int Order => 300;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        await storage.EnsureContainerAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("The tenant's blob container is in place.");
    }
}
