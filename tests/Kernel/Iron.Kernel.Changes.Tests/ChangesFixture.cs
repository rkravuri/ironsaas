using System.Collections.Concurrent;
using System.Text.Json;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Changes.Contracts;
using Iron.Kernel.Compliance;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.History;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Settings;
using Iron.Kernel.Settings.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Changes.Tests;

/// <summary>
/// A unit's database for the run with Audit, Authorization, Settings, Compliance and Changes migrated and seeded, and the
/// settings of Identity declared beside them, as a unit has them. Tests share it in one collection, one after another,
/// and each sets the four-eyes areas it needs first.
/// </summary>
public sealed class ChangesFixture : IAsyncLifetime
{
    private TestPostgres? _postgres;

    public string ConnectionString => Postgres.ConnectionString;

    public TestPrincipals Principals { get; } = new();

    private TestPostgres Postgres => _postgres ?? throw new InvalidOperationException("The fixture is not initialised.");

    public async ValueTask InitializeAsync()
    {
        _postgres = await TestPostgres.StartAsync("changes", TestContext.Current.CancellationToken);
        await using var provider = BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow), new TestActor(), new StandInSigner());
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

    /// <summary>The services of a unit with these modules, on the clock the test moves, acting as the actor the test switches.</summary>
    public ServiceProvider BuildServices(FakeTimeProvider time, TestActor actor, StandInSigner signer, SwitchableRecentAuthentication? recent = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(new TenantContext(TenantId.Parse("echo"), ["echo.localhost"]));
        services.AddLogging();
        services.AddIronDatabase(ConnectionString);
        services.AddModules(new ConfigurationBuilder().Build(), [new AuditModule(), new AuthorizationModule(), new IdentitySettingsModule(), new SettingsModule(), new ComplianceModule(), new ChangesModule()]);
        services.AddSingleton<IPrincipals>(Principals);
        services.Replace(ServiceDescriptor.Singleton<ICurrentPrincipal>(actor));
        services.Replace(ServiceDescriptor.Singleton<ISignerAuthentication>(signer));
        services.Replace(ServiceDescriptor.Singleton<IRecentAuthentication>(recent ?? new SwitchableRecentAuthentication()));
        return services.BuildServiceProvider();
    }

    /// <summary>A person with the tenant administrator role, as the seed's first administrator has it.</summary>
    public async Task<Guid> AdministratorAsync(IServiceProvider provider, string name)
    {
        var id = Principals.Add(name: name);
        await using var scope = provider.CreateAsyncScope();
        var assigned = await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(id, SystemRoles.TenantAdministrator, TestContext.Current.CancellationToken);
        Assert.True(assigned.IsSuccess, assigned.IsFailure ? assigned.Error.ToString() : null);
        return id;
    }

    /// <summary>Sets the tenant's four-eyes areas, and the areas signed, as the system: no second administrator for a setup.</summary>
    public static async Task FourEyesAsync(IServiceProvider provider, TestActor actor, string[] areas, string[]? signed = null)
    {
        var acting = actor.Id;
        actor.Id = SystemPrincipal.SystemId;
        try
        {
            await using var scope = provider.CreateAsyncScope();
            var writer = scope.ServiceProvider.GetRequiredService<SettingsWriter>();
            var set = await writer.SetAsync(ChangesSettings.Areas.Key, SettingPlace.Tenant, JsonSerializer.SerializeToElement(areas), TestContext.Current.CancellationToken);
            Assert.True(set.IsSuccess, set.IsFailure ? set.Error.ToString() : null);
            set = await writer.SetAsync(ChangesSettings.SignedAreas.Key, SettingPlace.Tenant, JsonSerializer.SerializeToElement(signed ?? []), TestContext.Current.CancellationToken);
            Assert.True(set.IsSuccess, set.IsFailure ? set.Error.ToString() : null);

            // A unit's worker refreshes the cache at start; this provider's cache is new, and a value saved again unchanged refreshes nothing.
            await scope.ServiceProvider.GetRequiredService<SettingsRefresher>().RefreshOnceAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            actor.Id = acting;
        }
    }

    /// <summary>Identity's settings, declared as the Identity module declares them in a unit, without the rest of Identity.</summary>
    private sealed class IdentitySettingsModule : IModule
    {
        public string Name => "identity";

        public IReadOnlyList<Permission> Permissions => [];

        public IReadOnlyList<SettingDefinition> Settings => IdentitySettings.All;

        public void AddServices(IServiceCollection services, IConfiguration configuration)
        {
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ChangesCollection : ICollectionFixture<ChangesFixture>
{
    public const string Name = "changes";
}

/// <summary>Who acts now: the test switches between the requester, the approver, and someone signed in as another, acting for one, or break-glass.</summary>
public sealed class TestActor : ICurrentPrincipal
{
    public Guid Id { get; set; } = SystemPrincipal.SystemId;

    public Guid? ActingForId { get; set; }

    public Guid? ImpersonatorId { get; set; }

    public string? ImpersonatorName { get; set; }

    public bool IsBreakGlass { get; set; }

    /// <summary>Acts as this person, plainly: no impersonator, nobody acted for, not break-glass.</summary>
    public TestActor As(Guid id)
    {
        Id = id;
        ActingForId = null;
        ImpersonatorId = null;
        ImpersonatorName = null;
        IsBreakGlass = false;
        return this;
    }
}

/// <summary>A step-up the test can refuse: by default the person confirmed it is them a moment ago.</summary>
public sealed class SwitchableRecentAuthentication : IRecentAuthentication
{
    public bool Stale { get; set; }

    public Result Require(TimeSpan maxAge) => Stale ? RecentAuthentication.Required(maxAge) : Result.Success();
}

/// <summary>Stands in for Identity's proof of the signer: whoever acts, by password, unless the test refuses.</summary>
public sealed class StandInSigner(TestActor? actor = null) : ISignerAuthentication
{
    public Error? Refusal { get; set; }

    public Task<Result<SignerChallenge>> ChallengeAsync(CancellationToken cancellationToken) =>
        Task.FromResult<Result<SignerChallenge>>(Refusal is { } refused ? refused : new SignerChallenge(false, false, true, false, [], null));

    public Task<Result<SignerIdentity>> AuthenticateAsync(SigningCredentials credentials, CancellationToken cancellationToken) =>
        Task.FromResult<Result<SignerIdentity>>(Refusal is { } refused ? refused : new SignerIdentity(actor?.Id ?? Guid.Empty, "Approving Administrator", "approver@echo.test", SignatureMethods.Password, null));

    public Task<Result<SignerIdentity>> IdentifyAsync(CancellationToken cancellationToken) => AuthenticateAsync(SigningCredentials.None, cancellationToken);
}

/// <summary>The principals Identity would answer for; a test adds the people it needs.</summary>
public sealed class TestPrincipals : IPrincipals
{
    private readonly ConcurrentDictionary<Guid, PrincipalSummary> _principals = new();

    public Guid Add(PrincipalKind kind = PrincipalKind.User, Guid? homeUnit = null, string? name = null)
    {
        var id = Ids.New();
        _principals[id] = new PrincipalSummary(id, kind, name ?? $"Person {id:N}"[..14], homeUnit, PrincipalStatuses.Active);
        return id;
    }

    public Task<IReadOnlyDictionary<Guid, PrincipalSummary>> FindAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, PrincipalSummary> found = ids.Where(_principals.ContainsKey).Distinct().ToDictionary(id => id, id => _principals[id]);
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<Guid>> HomedAtAsync(IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([.. _principals.Values.Where(p => p.HomeUnitId is { } unit && unitIds.Contains(unit)).Select(p => p.Id)]);
}
