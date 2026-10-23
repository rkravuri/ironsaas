using System.Security.Cryptography;
using System.Text.Json;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Changes;
using Iron.Kernel.Compliance;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Iron.Kernel.Settings;
using Iron.Kernel.Settings.Services;
using Iron.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Iron.Kernel.Identity.Tests;

/// <summary>
/// One PostgreSQL container for the test run with Audit, Authorization, Identity and Settings migrated and seeded through the
/// unit's own commands: the first administrator exists in Invited status with the tenant-administrator role.
/// Argon2id runs with small parameters here so the tests stay fast; the shape of the hash is the same.
/// </summary>
public sealed class IdentityFixture : IAsyncLifetime
{
    public const string AdministratorLoginId = "admin@acme.test";
    public const string AdministratorDisplayName = "Acme Administrator";
    public const string SmokeClientId = "smoke-client";
    public const string SmokeClientSecret = "a smoke client secret of some length";

    private TestPostgres? _postgres;

    public string ConnectionString => Postgres.ConnectionString;

    /// <summary>The project's own database on the run's server, or on a server of its own when it runs alone (tests/Iron.Testing).</summary>
    private TestPostgres Postgres => _postgres ?? throw new InvalidOperationException("The fixture is not initialised.");

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
        ["Identity:FactorKey"] = FactorKey,
    };

    /// <summary>One factor key for the whole run, as the unit's secret holds one (ADR-0047).</summary>
    public static string FactorKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

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
        _postgres = await TestPostgres.StartAsync("identity", TestContext.Current.CancellationToken);
        await using var provider = BuildServices(new FakeTimeProvider());
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["migrate"], TestContext.Current.CancellationToken));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));
        await RequireMfaForAsync(provider, MfaRequirement.Nobody);
    }

    /// <summary>
    /// Sets the tenant's MFA requirement (ADR-0047). The shared database requires a second factor of nobody, so the tests of
    /// passwords, sessions and users sign in with a password as before; the tests of second factors use databases of their
    /// own (<see cref="CreateTenantDatabaseAsync"/> with <c>mfa: true</c>), where the tenant's default applies.
    /// </summary>
    public static async Task RequireMfaForAsync(IServiceProvider provider, string requiredFor)
    {
        await using var scope = provider.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<SettingsWriter>();
        var set = await writer.SetAsync(IdentitySettings.MfaRequiredFor.Key, SettingPlace.Tenant, JsonSerializer.SerializeToElement(requiredFor), TestContext.Current.CancellationToken);
        Assert.True(set.IsSuccess, set.IsFailure ? set.Error.ToString() : null);
    }

    public async ValueTask DisposeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    /// <summary>
    /// Another tenant's database in the same container, migrated and seeded: a person there is no one here. With
    /// <paramref name="mfa"/> the tenant keeps the default MFA policy (ADR-0047); without it, like the shared one, it
    /// requires a second factor of nobody.
    /// </summary>
    public async Task<string> CreateTenantDatabaseAsync(string name, bool mfa = false)
    {
        var connectionString = await Postgres.CreateDatabaseAsync(name, TestContext.Current.CancellationToken);
        await using var provider = BuildServices(new FakeTimeProvider(), connectionString: connectionString);
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["migrate"], TestContext.Current.CancellationToken));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));
        if (!mfa)
        {
            await RequireMfaForAsync(provider, MfaRequirement.Nobody);
        }

        return connectionString;
    }

    public ServiceProvider BuildServices(FakeTimeProvider time, string? administratorLoginId = null, Action<IServiceCollection>? configure = null, IReadOnlyDictionary<string, string?>? settings = null, string? connectionString = null)
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
        services.AddIronDatabase(connectionString ?? ConnectionString);
        services.AddModules(configuration, [new AuditModule(), new AuthorizationModule(), new IdentityModule(), new SettingsModule(), new ComplianceModule(), new ChangesModule()]);
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
    public static async Task<Principal> CreateActiveUserAsync(IServiceProvider scope, string loginId, string password, string? displayName = null)
    {
        var context = scope.GetRequiredService<IdentityDbContext>();
        var hasher = scope.GetRequiredService<IPasswordHasher>();
        var user = new Principal(loginId, PrincipalType.User, "Test User", loginId);
        user.AcceptInvitation(hasher.Hash(password), displayName);
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
