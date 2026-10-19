using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;

namespace Iron.Kernel.Authorization.Tests;

/// <summary>One PostgreSQL container for the test run, migrated and seeded with the module's own commands.</summary>
public sealed class AuthorizationFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:19rc1").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        await using var provider = BuildServices(new FakeTimeProvider(), new FixedPrincipal(SystemPrincipal.SystemId), new RecordingAuditWriter());
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["migrate"], TestContext.Current.CancellationToken));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>The services a host would have for this module, with a controllable clock, principal and audit writer.</summary>
    public ServiceProvider BuildServices(FakeTimeProvider time, ICurrentPrincipal principal, RecordingAuditWriter audit, string? connectionString = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(principal);
        services.AddSingleton<IAuditWriter>(audit);
        services.AddLogging();
        services.AddIronDatabase(connectionString ?? ConnectionString);
        services.AddModules(new ConfigurationBuilder().Build(), [new AuthorizationModule()]);
        return services.BuildServiceProvider();
    }
}

[CollectionDefinition(Name)]
public sealed class AuthorizationCollection : ICollectionFixture<AuthorizationFixture>
{
    public const string Name = "authorization";
}

public sealed class FixedPrincipal(Guid id) : ICurrentPrincipal
{
    public Guid Id => id;
}

/// <summary>A test principal taken from a request header; Identity replaces this with the session principal in T10.</summary>
public sealed class HeaderPrincipal(IHttpContextAccessor accessor) : ICurrentPrincipal
{
    public const string HeaderName = "X-Test-Principal";

    public Guid Id => Guid.TryParse(accessor.HttpContext?.Request.Headers[HeaderName].ToString(), out var id) ? id : Guid.Empty;
}

public sealed class RecordingAuditWriter : IAuditWriter
{
    public List<AuditEvent> Events { get; } = [];

    public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        Events.Add(auditEvent);
        return Task.CompletedTask;
    }
}
