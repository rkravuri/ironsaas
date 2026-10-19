using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Fleet.Services;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.Azurite;
using Testcontainers.PostgreSql;

namespace Iron.Kernel.Fleet.Tests;

/// <summary>
/// The platform as the fleet sees it: PostgreSQL (the control plane database and the tenant databases to come),
/// Azurite and OpenBao. The unit runtime is a recording fake (Compose arrives in T14b).
/// </summary>
public sealed class FleetFixture : IAsyncLifetime
{
    public const string RootToken = "fleet-test-root-token";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:19rc1").Build();
    private readonly AzuriteContainer _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:3.37.0").WithCommand("--skipApiVersionCheck", "--loose").Build();
    private readonly IContainer _openBao = new ContainerBuilder("openbao/openbao:2.7.1")
        .WithCommand("server", "-dev", "-dev-root-token-id=" + RootToken, "-dev-listen-address=0.0.0.0:8200")
        .WithEnvironment("BAO_ADDR", "http://127.0.0.1:8200")
        .WithCreateParameterModifier(parameters =>
        {
            parameters.HostConfig ??= new Docker.DotNet.Models.HostConfig();
            parameters.HostConfig.CapAdd = ["IPC_LOCK"];
        })
        .WithPortBinding(8200, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(8200).ForPath("/v1/sys/health")))
        .Build();

    /// <summary>The control plane's own database (the container's default one).</summary>
    public string ControlConnectionString => _postgres.GetConnectionString();

    public string StorageConnectionString => _azurite.GetConnectionString();

    public string OpenBaoAddress => $"http://{_openBao.Hostname}:{_openBao.GetMappedPublicPort(8200)}";

    public SecretStoreOptions StoreOptions(string token = RootToken, string path = "tenants") => new() { Address = OpenBaoAddress, Token = token, Path = path };

    public FleetOptions FleetOptions => new()
    {
        Database = new FleetOptions.FleetDatabaseOptions
        {
            AdminConnectionString = ControlConnectionString,
            UnitHost = _postgres.Hostname,
            UnitPort = _postgres.GetMappedPublicPort(5432),
        },
        Storage = new FleetOptions.FleetStorageOptions { AccountConnectionString = StorageConnectionString, UnitBlobEndpoint = null, SasValidDays = 7 },
        Secrets = new FleetOptions.FleetSecretsOptions { UnitAddress = OpenBaoAddress, UnitTokenValidDays = 7 },
        Units = new FleetOptions.FleetUnitsOptions { ImageTag = "test", ReadyTimeoutSeconds = 5 },
        PollSeconds = 1,
    };

    /// <summary>A controllable clock that starts now: the storage step signs container access with it, and Azurite checks the signature against the real time.</summary>
    public static FakeTimeProvider Clock() => new(TimeProvider.System.GetUtcNow());

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(TestContext.Current.CancellationToken), _azurite.StartAsync(TestContext.Current.CancellationToken), _openBao.StartAsync(TestContext.Current.CancellationToken));
        await using var provider = BuildServices(Clock(), new RecordingRuntime());
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["migrate"], TestContext.Current.CancellationToken));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        await _openBao.DisposeAsync();
        await _azurite.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    /// <summary>The control plane's services as the fleet host has them, with the store configured and the runtime replaced.</summary>
    public ServiceProvider BuildServices(FakeTimeProvider time, IUnitRuntime runtime, Action<IServiceCollection>? configure = null)
    {
        var options = FleetOptions;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Secrets:Address"] = OpenBaoAddress,
            ["Secrets:Token"] = RootToken,
            ["Secrets:Path"] = "control",
            ["Fleet:Database:AdminConnectionString"] = options.Database.AdminConnectionString,
            ["Fleet:Database:UnitHost"] = options.Database.UnitHost,
            ["Fleet:Database:UnitPort"] = options.Database.UnitPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Fleet:Storage:AccountConnectionString"] = options.Storage.AccountConnectionString,
            ["Fleet:Storage:SasValidDays"] = "7",
            ["Fleet:Secrets:UnitAddress"] = options.Secrets.UnitAddress,
            ["Fleet:Secrets:UnitTokenValidDays"] = "7",
            ["Fleet:Units:ImageTag"] = "test",
            ["Fleet:Units:ReadyTimeoutSeconds"] = "5",
            ["Fleet:PollSeconds"] = "1",
        }).Build();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<ICurrentPrincipal>(SystemPrincipal.Instance);
        services.AddSingleton<IUnitRuntime>(runtime);
        services.AddLogging();
        services.AddIronDatabase(ControlConnectionString);
        services.AddIronSecretStore(configuration);
        services.AddModules(configuration, [new AuditModule(), new AuthorizationModule(), new FleetModule(), new TenancyModule()]);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}

[CollectionDefinition(Name)]
public sealed class FleetCollection : ICollectionFixture<FleetFixture>
{
    public const string Name = "fleet";
}

/// <summary>Records what the steps asked of the runtime and answers like a unit that works, unless told to fail.</summary>
public sealed class RecordingRuntime : IUnitRuntime
{
    public const string InvitationToken = "TEST-INVITATION-TOKEN";

    public List<(string Command, UnitDefinition Unit)> Commands { get; } = [];

    public List<UnitDefinition> Applied { get; } = [];

    public List<UnitDefinition> Removed { get; } = [];

    public int ReadyChecks { get; private set; }

    /// <summary>The next command of this name fails once with this message.</summary>
    public (string Command, string Message)? FailNext { get; set; }

    public Task<CommandResult> RunCommandAsync(UnitDefinition unit, IReadOnlyList<string> command, CancellationToken cancellationToken)
    {
        var name = command[0];
        Commands.Add((name, unit));
        if (FailNext is { } failure && failure.Command == name)
        {
            FailNext = null;
            return Task.FromResult(new CommandResult(1, failure.Message));
        }

        var output = name == "seed" ? $"Created the first administrator {unit.Tenant.AdministratorLoginId} in Invited status. Invitation token: {InvitationToken}\n" : $"{name} done\n";
        return Task.FromResult(new CommandResult(0, output));
    }

    public Task ApplyAsync(UnitDefinition unit, CancellationToken cancellationToken)
    {
        Applied.Add(unit);
        return Task.CompletedTask;
    }

    public Task<string?> CheckReadyAsync(UnitDefinition unit, CancellationToken cancellationToken)
    {
        ReadyChecks++;
        return Task.FromResult<string?>(null);
    }

    public Task RemoveAsync(UnitDefinition unit, CancellationToken cancellationToken)
    {
        Removed.Add(unit);
        return Task.CompletedTask;
    }
}
