using System.Collections.Concurrent;
using System.Globalization;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit;
using Iron.Kernel.Authorization;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Fleet.Services;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Tenancy;
using Iron.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Fleet.Tests;

/// <summary>
/// The platform as the fleet sees it: PostgreSQL (the control plane database and the tenant databases to come),
/// Azurite and OpenBao. The unit runtime is a recording fake (Compose arrives in T14b).
/// </summary>
public sealed class FleetFixture : IAsyncLifetime
{
    private TestPostgres? _postgres;
    private TestBlob? _blob;
    private TestOpenBao? _openBao;

    /// <summary>The control plane's own database; the database step makes the tenants' beside it on the same server.</summary>
    public string ControlConnectionString => Postgres.ConnectionString;

    public string StorageConnectionString => (_blob ?? throw new InvalidOperationException("The fixture is not initialised.")).ConnectionString;

    public string OpenBaoAddress => OpenBao.Address;

    /// <summary>The store's root token: the fleet's own, as the control plane's is on the laptop.</summary>
    public string RootToken => OpenBao.RootToken;

    /// <summary>The fleet tests' own KV mount in the store.</summary>
    public string Mount => OpenBao.Mount;

    public SecretStoreOptions StoreOptions(string? token = null, string path = "tenants") => new() { Address = OpenBaoAddress, Token = token ?? RootToken, Mount = OpenBao.Mount, Path = path };

    private TestPostgres Postgres => _postgres ?? throw new InvalidOperationException("The fixture is not initialised.");

    private TestOpenBao OpenBao => _openBao ?? throw new InvalidOperationException("The fixture is not initialised.");

    public FleetOptions FleetOptions => new()
    {
        Database = new FleetOptions.FleetDatabaseOptions
        {
            AdminConnectionString = ControlConnectionString,
            UnitHost = Postgres.Host,
            UnitPort = Postgres.Port,
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
        _postgres = await TestPostgres.StartAsync("fleet", TestContext.Current.CancellationToken);
        _blob = await TestBlob.StartAsync(TestContext.Current.CancellationToken);
        _openBao = await TestOpenBao.StartAsync("fleet", TestContext.Current.CancellationToken);
        await using var provider = BuildServices(Clock(), new RecordingRuntime());
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["migrate"], TestContext.Current.CancellationToken));
        Assert.Equal(0, await DatabaseCommands.RunAsync(provider, ["seed"], TestContext.Current.CancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        if (_openBao is not null)
        {
            await _openBao.DisposeAsync();
        }

        if (_blob is not null)
        {
            await _blob.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    /// <summary>The control plane's services as the fleet host has them, with the store configured and the runtime replaced.</summary>
    public ServiceProvider BuildServices(FakeTimeProvider time, IUnitRuntime runtime, Action<IServiceCollection>? configure = null)
    {
        var options = FleetOptions;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Secrets:Address"] = OpenBaoAddress,
            ["Secrets:Token"] = RootToken,
            ["Secrets:Mount"] = OpenBao.Mount,
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

/// <summary>
/// Records what the steps asked of the runtime and answers like a unit that works, unless told to fail. Its seed and
/// recover-admin hand their link over as a unit's do (ADR-0046): written once to the job's place with the token the fleet
/// gave, a line that says so and holds no link. It also tries what the token must not do, read back, and the unit's own
/// token what it must not do, write there; <see cref="Refused"/> counts the refusals.
/// </summary>
public sealed class RecordingRuntime : IUnitRuntime
{
    /// <summary>Codes of the real shape, 32 random bytes in base64url, so the scrubbing rule would know them.</summary>
    public const string InvitationToken = "test-invitation-token-abcdefghijklmnopqrstu";
    public const string RecoveryToken = "test-recovery-token-abcdefghijklmnopqrstuvwx";

    public int Refused { get; private set; }

    public List<(string Command, UnitDefinition Unit)> Commands { get; } = [];

    public List<UnitDefinition> Applied { get; } = [];

    public List<UnitDefinition> Removed { get; } = [];

    public int ReadyChecks { get; private set; }

    /// <summary>The next command of this name fails once with this message.</summary>
    public (string Command, string Message)? FailNext { get; set; }

    public async Task<CommandResult> RunCommandAsync(UnitDefinition unit, IReadOnlyList<string> command, CancellationToken cancellationToken)
    {
        var name = command[0];
        Commands.Add((name, unit));
        if (FailNext is { } failure && failure.Command == name)
        {
            FailNext = null;
            return new CommandResult(1, failure.Message);
        }

        switch (name)
        {
            case "seed":
                await HandOverAsync(unit, "invitation", unit.Tenant.AdministratorLoginId, $"/invitations#{InvitationToken}", DateTimeOffset.UtcNow.AddDays(3), cancellationToken);
                return new CommandResult(0, $"The administrator {unit.Tenant.AdministratorLoginId} was created in Invited status. Handed over an invitation to the operator who asked.\n");
            case "recover-admin":
                await HandOverAsync(unit, "recovery", unit.Environment["Recovery__LoginId"], $"/recovery#{RecoveryToken}", DateTimeOffset.UtcNow.AddMinutes(15), cancellationToken);
                return new CommandResult(0, $"Handed over a recovery link for {unit.Environment["Recovery__LoginId"]} to the operator who asked.\n");
            default:
                return new CommandResult(0, $"{name} done\n");
        }
    }

    private async Task HandOverAsync(UnitDefinition unit, string kind, string subject, string linkPath, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var environment = unit.Environment;
        var path = environment["Handover__Path"];
        using var place = new OpenBaoSecretStore(new SecretStoreOptions { Address = environment["Secrets__Address"], Token = environment["Handover__Token"], Mount = environment["Secrets__Mount"] });
        await place.WriteOnceAsync(
            path,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["kind"] = kind, ["subject"] = subject, ["link_path"] = linkPath, ["expires_at"] = expiresAt.ToString("O", CultureInfo.InvariantCulture) },
            expiresAt - DateTimeOffset.UtcNow,
            cancellationToken);

        // The handover token writes and nothing more: it cannot read the place back; the unit's own token cannot write it.
        try
        {
            await place.ReadAsync(path, cancellationToken);
        }
        catch (SecretStoreException)
        {
            Refused++;
        }

        using var unitStore = new OpenBaoSecretStore(new SecretStoreOptions { Address = environment["Secrets__Address"], Token = environment["Secrets__Token"], Mount = environment["Secrets__Mount"] });
        try
        {
            await unitStore.WriteAsync(path, new Dictionary<string, string>(StringComparer.Ordinal) { ["kind"] = "forged" }, cancellationToken);
        }
        catch (SecretStoreException)
        {
            Refused++;
        }
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

/// <summary>Every log line the services under test wrote, as the providers would format them.</summary>
public sealed class CapturingLogs : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLogs owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Lines.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}
