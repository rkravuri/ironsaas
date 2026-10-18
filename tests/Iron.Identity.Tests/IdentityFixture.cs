using System.Security.Cryptography;
using Iron.Api.Modules;
using Iron.Audit;
using Iron.Authorization;
using Iron.Core.Tenancy;
using Iron.Data;
using Iron.Data.Commands;
using Iron.Identity.Data;
using Iron.Identity.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;

namespace Iron.Identity.Tests;

/// <summary>
/// One PostgreSQL container for the test run with Audit, Authorization and Identity migrated and seeded through the
/// unit's own commands: the first administrator exists in Invited status with the tenant-administrator role.
/// Argon2id runs with small parameters here so the tests stay fast; the shape of the hash is the same.
/// </summary>
public sealed class IdentityFixture : IAsyncLifetime
{
    public const string AdministratorLoginId = "admin@acme.test";
    public const string AdministratorDisplayName = "Acme Administrator";
    public const string SmokeClientId = "smoke-client";
    public const string SmokeClientSecret = "a smoke client secret of some length";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:19rc1").Build();

    public string ConnectionString => _container.GetConnectionString();

    public static IReadOnlyDictionary<string, string?> Configuration => new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["Identity:Argon2:MemoryKb"] = "8192",
        ["Identity:Argon2:Iterations"] = "1",
        ["Identity:Argon2:Parallelism"] = "1",
        ["Seed:Administrator:LoginId"] = AdministratorLoginId,
        ["Seed:Administrator:DisplayName"] = AdministratorDisplayName,
        ["Seed:ServiceAccounts:0:ClientId"] = SmokeClientId,
        ["Seed:ServiceAccounts:0:Secret"] = SmokeClientSecret,
        ["Tokens:SigningKeyPem"] = SigningKeyPem,
    };

    /// <summary>One key for the whole run, so tokens issued by one service provider validate in another.</summary>
    public static string SigningKeyPem { get; } = CreateSigningKeyPem();

    public static readonly TenantContext Tenant = new(TenantId.Parse("acme"), ["acme.localhost"]);

    private static string CreateSigningKeyPem()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return key.ExportECPrivateKeyPem();
    }

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        await using var provider = BuildServices(new FakeTimeProvider());
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["migrate"], TestContext.Current.CancellationToken));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public ServiceProvider BuildServices(FakeTimeProvider time, string? administratorLoginId = null, Action<IServiceCollection>? configure = null, IReadOnlyDictionary<string, string?>? settings = null)
    {
        var values = new Dictionary<string, string?>(Configuration, StringComparer.Ordinal);
        if (administratorLoginId is not null)
        {
            values["Seed:Administrator:LoginId"] = administratorLoginId;
        }

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(Tenant);
        services.AddLogging();
        services.AddIronDatabase(ConnectionString);
        services.AddModules(configuration, [new AuditModule(), new AuthorizationModule(), new IdentityModule()]);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>A fresh invitation for the administrator (the seeder's token lives only in its log line).</summary>
    public static async Task<string> ReinviteAdministratorAsync(IServiceProvider scope)
    {
        var context = scope.GetRequiredService<IdentityDbContext>();
        var administrator = await context.Principals.SingleAsync(p => p.LoginId == AdministratorLoginId, TestContext.Current.CancellationToken);
        return await scope.GetRequiredService<InvitationService>().InviteAsync(administrator, TestContext.Current.CancellationToken);
    }

    /// <summary>A new principal in Invited status without a credential.</summary>
    public static async Task<Principal> CreateInvitedUserAsync(IServiceProvider scope, string loginId)
    {
        var context = scope.GetRequiredService<IdentityDbContext>();
        var user = new Principal(loginId, PrincipalType.User, "Invited User", loginId);
        context.Principals.Add(user);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user;
    }

    /// <summary>A new Active user with a known password, ready to log in.</summary>
    public static async Task<Principal> CreateActiveUserAsync(IServiceProvider scope, string loginId, string password)
    {
        var context = scope.GetRequiredService<IdentityDbContext>();
        var hasher = scope.GetRequiredService<IPasswordHasher>();
        var user = new Principal(loginId, PrincipalType.User, "Test User", loginId);
        user.AcceptInvitation(hasher.Hash(password), null);
        context.Principals.Add(user);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user;
    }
}

[CollectionDefinition(Name)]
public sealed class IdentityCollection : ICollectionFixture<IdentityFixture>
{
    public const string Name = "identity";
}
