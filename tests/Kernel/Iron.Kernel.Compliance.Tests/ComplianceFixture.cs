using System.Text.Json;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Events;
using Iron.Kernel.Settings;
using Iron.Kernel.Settings.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Compliance.Tests;

/// <summary>
/// A unit's database for the run with Audit, Authorization, Settings and Compliance migrated and seeded, and the sample
/// module's documents beside them: a business record, tracked, that its module lets people sign, as a business module will.
/// </summary>
public sealed class ComplianceFixture : IAsyncLifetime
{
    private TestPostgres? _postgres;

    public string ConnectionString => Postgres.ConnectionString;

    private TestPostgres Postgres => _postgres ?? throw new InvalidOperationException("The fixture is not initialised.");

    public async ValueTask InitializeAsync()
    {
        _postgres = await TestPostgres.StartAsync("compliance", TestContext.Current.CancellationToken);
        await using var provider = BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow), new StandInSigner());
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["migrate"], TestContext.Current.CancellationToken));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SampleDocumentsDbContext>().GetService<IRelationalDatabaseCreator>().CreateTablesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    /// <summary>The services of a unit with these modules, the clock the test moves, the signer it stands in for, and an actor.</summary>
    public ServiceProvider BuildServices(FakeTimeProvider time, StandInSigner signer, Guid? actor = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(new TenantContext(TenantId.Parse("echo"), ["echo.localhost"]));
        services.AddLogging();
        services.AddIronDatabase(ConnectionString);
        services.AddModules(new ConfigurationBuilder().Build(), [new AuditModule(), new EventsModule(), new AuthorizationModule(), new SettingsModule(), new ComplianceModule()]);

        // The sample module: no migrations, its tables created as they are, as the data tests' sample.
        services.AddIronDbContext<SampleDocumentsDbContext>(options => options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ISignableRecords, SampleDocuments>());
        services.Replace(ServiceDescriptor.Singleton<ICurrentPrincipal>(new Actor(actor ?? signer.SignerId)));
        services.Replace(ServiceDescriptor.Singleton<ISignerAuthentication>(signer));
        return services.BuildServiceProvider();
    }

    /// <summary>Sets the tenant's signing policy for one document type.</summary>
    public static async Task PolicyAsync(IServiceProvider provider, string recordType, object policy)
    {
        await using var scope = provider.CreateAsyncScope();
        var value = JsonSerializer.SerializeToElement(new Dictionary<string, object> { [recordType] = policy });
        var set = await scope.ServiceProvider.GetRequiredService<SettingsWriter>().SetAsync(ComplianceSettings.Policies.Key, SettingPlace.Tenant, value, TestContext.Current.CancellationToken);
        Assert.True(set.IsSuccess, set.IsFailure ? set.Error.ToString() : null);
    }

    private sealed class Actor(Guid id) : ICurrentPrincipal
    {
        public Guid Id => id;
    }
}

[CollectionDefinition(Name)]
public sealed class ComplianceCollection : ICollectionFixture<ComplianceFixture>
{
    public const string Name = "compliance";
}

/// <summary>
/// Stands in for Identity's proof of the signer (its rules are tested in Identity's tests): the same person each time,
/// signed in with a password; or a refusal the test sets.
/// </summary>
public sealed class StandInSigner(string fullName = "Ada Lovelace", string loginId = "ada@echo.test") : ISignerAuthentication
{
    public Guid SignerId { get; } = Ids.New();

    public Guid SessionId { get; } = Ids.New();

    /// <summary>When set, every proof is refused with it.</summary>
    public Error? Refusal { get; set; }

    public int Proofs { get; private set; }

    public Task<Result<SignerChallenge>> ChallengeAsync(CancellationToken cancellationToken) =>
        Task.FromResult<Result<SignerChallenge>>(Refusal is { } refused ? refused : new SignerChallenge(Proofs == 0, Proofs == 0, true, false, [], null));

    public Task<Result<SignerIdentity>> AuthenticateAsync(SigningCredentials credentials, CancellationToken cancellationToken)
    {
        if (Refusal is { } refused)
        {
            return Task.FromResult<Result<SignerIdentity>>(refused);
        }

        Proofs++;
        return Task.FromResult<Result<SignerIdentity>>(new SignerIdentity(SignerId, fullName, loginId, SignatureMethods.Password, SessionId));
    }

    public Task<Result<SignerIdentity>> IdentifyAsync(CancellationToken cancellationToken) =>
        Task.FromResult<Result<SignerIdentity>>(Refusal is { } refused ? refused : new SignerIdentity(SignerId, fullName, loginId, SignatureMethods.Session, SessionId));
}
