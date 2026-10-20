using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Fleet;
using Iron.Kernel.Security.Secrets;
using Iron.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Tenancy.Tests;

/// <summary>The control plane's database for the test run: Audit, Authorization, Fleet and Tenancy migrated and seeded.</summary>
public sealed class TenancyFixture : IAsyncLifetime
{
    private TestPostgres? _postgres;

    public string ConnectionString => Postgres.ConnectionString;

    /// <summary>The project's own database on the run's server, or on a server of its own when it runs alone (tests/Iron.Testing).</summary>
    private TestPostgres Postgres => _postgres ?? throw new InvalidOperationException("The fixture is not initialised.");

    public async ValueTask InitializeAsync()
    {
        _postgres = await TestPostgres.StartAsync("tenancy", TestContext.Current.CancellationToken);
        await using var provider = BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["migrate"], TestContext.Current.CancellationToken));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    public ServiceProvider BuildServices(FakeTimeProvider time, ICurrentPrincipal principal, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(principal);
        services.AddLogging();
        services.AddIronDatabase(ConnectionString);
        // No store: the entitlements stay behind in the store's copy, as when the store refuses a write (ADR-0034).
        services.AddIronSecretStore(new ConfigurationBuilder().Build());
        services.AddModules(new ConfigurationBuilder().Build(), [new AuditModule(), new AuthorizationModule(), new FleetModule(), new TenancyModule()]);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}

[CollectionDefinition(Name)]
public sealed class TenancyCollection : ICollectionFixture<TenancyFixture>
{
    public const string Name = "tenancy";
}

public sealed class FixedPrincipal(Guid id) : ICurrentPrincipal
{
    public Guid Id => id;
}

/// <summary>A test principal taken from a request header; Identity provides the real one.</summary>
public sealed class HeaderPrincipal(IHttpContextAccessor accessor) : ICurrentPrincipal
{
    public const string HeaderName = "X-Test-Principal";

    public Guid Id => Guid.TryParse(accessor.HttpContext?.Request.Headers[HeaderName].ToString(), out var id) ? id : Guid.Empty;
}
