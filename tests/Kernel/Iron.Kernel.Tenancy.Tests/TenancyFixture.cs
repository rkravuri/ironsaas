using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Fleet;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;

namespace Iron.Kernel.Tenancy.Tests;

/// <summary>The control plane's database for the test run: Audit, Authorization, Fleet and Tenancy migrated and seeded.</summary>
public sealed class TenancyFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:19rc1").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        await using var provider = BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["migrate"], TestContext.Current.CancellationToken));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public ServiceProvider BuildServices(FakeTimeProvider time, ICurrentPrincipal principal)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(principal);
        services.AddLogging();
        services.AddIronDatabase(ConnectionString);
        services.AddModules(new ConfigurationBuilder().Build(), [new AuditModule(), new AuthorizationModule(), new FleetModule(), new TenancyModule()]);
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
