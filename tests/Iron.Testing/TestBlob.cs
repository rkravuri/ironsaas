using Testcontainers.Azurite;

namespace Iron.Testing;

/// <summary>
/// Blob storage for a test project: the run's one Azurite under make test (<see cref="SharedVariable"/>), or one of the
/// project's own when it runs alone. Projects share the account, so each names its containers apart from the others':
/// the fleet's are its tenants' slugs.
/// </summary>
public sealed class TestBlob : IAsyncDisposable
{
    public const string Image = "mcr.microsoft.com/azure-storage/azurite:3.37.0";

    /// <summary>The run's Azurite, as make test started it: the development account's blob endpoint.</summary>
    public const string SharedVariable = "IRON_TEST_BLOB";

    private readonly AzuriteContainer? _container;

    private TestBlob(AzuriteContainer? container, string connectionString)
    {
        _container = container;
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public static async Task<TestBlob> StartAsync(CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable(SharedVariable) is { Length: > 0 } endpoint)
        {
            return new TestBlob(null, $"DefaultEndpointsProtocol=http;AccountName={AzuriteBuilder.AccountName};AccountKey={AzuriteBuilder.AccountKey};BlobEndpoint={endpoint};");
        }

        // --skipApiVersionCheck as on the laptop platform: the SDK's newest API version runs ahead of the emulator (ADR-0006).
        var container = new AzuriteBuilder(Image).WithCommand("--skipApiVersionCheck", "--loose").Build();
        await container.StartAsync(cancellationToken).ConfigureAwait(false);
        return new TestBlob(container, container.GetConnectionString());
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }
}
