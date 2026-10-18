using System.Net.Http.Json;
using System.Text.Json;
using Azure.Storage.Blobs;
using Iron.Core.Identifiers;
using Iron.Files.Services;
using Iron.Fleet.Contracts;
using Iron.Fleet.Data;
using Iron.Fleet.Services;
using Iron.Fleet.Services.Steps;
using Iron.Security.Secrets;
using Iron.Tenancy.Contracts;
using Iron.Tenancy.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Iron.Fleet.Tests;

/// <summary>TF-002 against the real platform services: the steps, their records, resume, and the isolation they produce (TF-005).</summary>
[Collection(FleetCollection.Name)]
public class ProvisioningTests(FleetFixture fixture)
{
    [Fact]
    public async Task AJobRunsEveryStepActivatesTheTenantAndReportsTheInvitationLink()
    {
        var time = FleetFixture.Clock();
        var runtime = new RecordingRuntime();
        await using var provider = fixture.BuildServices(time, runtime);
        var slug = await RegisterAndRequestAsync(provider, "acme");

        Assert.True(await new ProvisioningWorkerHarness(provider).RunNextAsync());

        await using var scope = provider.CreateAsyncScope();
        var job = await scope.ServiceProvider.GetRequiredService<IProvisioningJobs>().LatestAsync(slug, TestContext.Current.CancellationToken);
        Assert.NotNull(job);
        Assert.Equal(ProvisioningStatus.Completed, job.Status);
        Assert.Equal(8, job.Steps.Count);
        Assert.All(job.Steps, step => Assert.Equal("completed", step.Status));
        Assert.All(job.Steps, step => Assert.NotNull(step.DurationMs));
        Assert.Equal([StepNames.Reserve, StepNames.Database, StepNames.Storage, StepNames.Secrets, StepNames.Migrate, StepNames.Seed, StepNames.Unit, StepNames.Verify], job.Steps.Select(s => s.Name));
        Assert.Contains($"https://{slug}.localhost/invitations/{RecordingRuntime.InvitationToken}", job.Output, StringComparison.Ordinal);

        var tenant = await scope.ServiceProvider.GetRequiredService<ITenantRegistry>().FindAsync(slug, TestContext.Current.CancellationToken);
        Assert.Equal(TenantStatus.Active, tenant!.Status);
        Assert.NotNull(tenant.ActivatedAt);

        // The image commands ran as the owner with the unit's environment; the unit itself was applied and checked.
        Assert.Equal(["migrate", "seed"], runtime.Commands.Select(c => c.Command));
        Assert.All(runtime.Commands, c => Assert.Contains("_owner;", c.Unit.Environment["ConnectionStrings__Database"], StringComparison.Ordinal));
        Assert.All(runtime.Commands, c => Assert.Equal($"tenants/{slug}", c.Unit.Environment["Secrets__Path"]));
        Assert.Equal("test", Assert.Single(runtime.Applied).ImageTag);
        Assert.Equal(SizeClasses.LimitsOf(SizeClass.Small), Assert.Single(runtime.Applied).Limits);
        Assert.True(runtime.ReadyChecks >= 1);
    }

    [Fact]
    public async Task TheUnitReachesOnlyItsOwnDatabaseStoragePathAndSecret()
    {
        var runtime = new RecordingRuntime();
        await using var provider = fixture.BuildServices(FleetFixture.Clock(), runtime);
        var acme = await RegisterAndRequestAsync(provider, "acme");
        var bravo = await RegisterAndRequestAsync(provider, "bravo");
        var harness = new ProvisioningWorkerHarness(provider);
        Assert.True(await harness.RunNextAsync());
        Assert.True(await harness.RunNextAsync());

        using var root = new OpenBaoSecretStore(fixture.StoreOptions());
        var acmeUnit = await root.ReadAsync($"tenants/{acme}/unit", TestContext.Current.CancellationToken);
        var acmeFleet = await root.ReadAsync($"tenants/{acme}/fleet", TestContext.Current.CancellationToken);
        Assert.NotNull(acmeUnit);
        Assert.NotNull(acmeFleet);
        Assert.Equal($"{acme}_app", acmeUnit["Database:AppRole"]);
        Assert.StartsWith("-----BEGIN EC PRIVATE KEY-----", acmeUnit["Tokens:SigningKeyPem"], StringComparison.Ordinal);

        // The app role connects to its own database and to nothing else (TF-005).
        await using (var own = new NpgsqlConnection(acmeUnit["ConnectionStrings:Database"]))
        {
            await own.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand("select current_database()", own);
            Assert.Equal(acme, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        var foreign = new NpgsqlConnectionStringBuilder(acmeUnit["ConnectionStrings:Database"]) { Database = bravo }.ToString();
        await using (var other = new NpgsqlConnection(foreign))
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => other.OpenAsync(TestContext.Current.CancellationToken));
            Assert.Equal("42501", exception.SqlState);
        }

        // The unit token reads its own path and nothing else (TF-073).
        using var unitStore = new OpenBaoSecretStore(fixture.StoreOptions(acmeFleet[SecretsStep.UnitTokenKey]));
        Assert.NotNull(await unitStore.ReadAsync($"tenants/{acme}/unit", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SecretStoreException>(() => unitStore.ReadAsync($"tenants/{bravo}/unit", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SecretStoreException>(() => unitStore.ReadAsync($"tenants/{acme}/database", TestContext.Current.CancellationToken));

        // The signed container URL is the unit's whole view of storage: the Files module's own class works through it.
        var sasUrl = acmeUnit["Storage:ContainerSasUrl"];
        Assert.Equal(acme, new BlobContainerClient(new Uri(sasUrl)).Name);
        var unitStorage = new BlobObjectStorage(new StorageOptions { ContainerSasUrl = sasUrl });
        Assert.Null(await unitStorage.CheckAsync(TestContext.Current.CancellationToken));
        await unitStorage.PutAsync("probe/hello.txt", new MemoryStream("hello"u8.ToArray()), "text/plain", TestContext.Current.CancellationToken);
        using (var stored = await unitStorage.GetAsync("probe/hello.txt", TestContext.Current.CancellationToken))
        {
            Assert.NotNull(stored);
            using var reader = new StreamReader(stored.Content);
            Assert.Equal("hello", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        }

        Assert.True(await unitStorage.DeleteAsync("probe/hello.txt", TestContext.Current.CancellationToken));
        var strangerStorage = new BlobObjectStorage(new StorageOptions { ContainerSasUrl = sasUrl.Replace($"/{acme}?", $"/{bravo}?", StringComparison.Ordinal) });
        Assert.NotNull(await strangerStorage.CheckAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFailedStepStopsTheJobAndARerunContinuesFromIt()
    {
        var time = FleetFixture.Clock();
        var runtime = new RecordingRuntime { FailNext = ("migrate", "relation \"authz.roles\" does not exist") };
        await using var provider = fixture.BuildServices(time, runtime);
        var slug = await RegisterAndRequestAsync(provider, "charlie");
        var harness = new ProvisioningWorkerHarness(provider);

        Assert.True(await harness.RunNextAsync());

        await using (var scope = provider.CreateAsyncScope())
        {
            var job = (await scope.ServiceProvider.GetRequiredService<IProvisioningJobs>().LatestAsync(slug, TestContext.Current.CancellationToken))!;
            Assert.Equal(ProvisioningStatus.Failed, job.Status);
            Assert.Equal(StepNames.Migrate, job.CurrentStep);
            Assert.Contains("authz.roles", job.Error, StringComparison.Ordinal);
            Assert.Equal(["completed", "completed", "completed", "completed", "failed", "pending", "pending", "pending"], job.Steps.Select(s => s.Status));
            Assert.Equal(TenantStatus.Provisioning, (await scope.ServiceProvider.GetRequiredService<ITenantRegistry>().FindAsync(slug, TestContext.Current.CancellationToken))!.Status);

            // Asking again reopens the same job instead of making a new one.
            var reopened = await scope.ServiceProvider.GetRequiredService<TenantRegistry>().RequestProvisioningAsync(slug, TestContext.Current.CancellationToken);
            Assert.True(reopened.IsSuccess, reopened.IsFailure ? reopened.Error.Message : null);
            Assert.Equal(job.Id, reopened.Value.Id);
            Assert.Equal(ProvisioningStatus.Requested, reopened.Value.Status);
        }

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await harness.RunNextAsync());

        await using (var scope = provider.CreateAsyncScope())
        {
            var job = (await scope.ServiceProvider.GetRequiredService<IProvisioningJobs>().LatestAsync(slug, TestContext.Current.CancellationToken))!;
            Assert.Equal(ProvisioningStatus.Completed, job.Status);
            Assert.All(job.Steps, step => Assert.Equal("completed", step.Status));

            // The database step ran once; migrate ran twice; the steps before the failure kept their first timing.
            Assert.Equal(["migrate", "migrate", "seed"], runtime.Commands.Select(c => c.Command));
            var database = job.Steps.Single(s => s.Name == StepNames.Database);
            Assert.True(database.StartedAt < job.Steps.Single(s => s.Name == StepNames.Migrate).StartedAt);
            Assert.Equal(TenantStatus.Active, (await scope.ServiceProvider.GetRequiredService<ITenantRegistry>().FindAsync(slug, TestContext.Current.CancellationToken))!.Status);
        }
    }

    [Fact]
    public async Task RunningAJobOfAnActiveTenantIsRefused()
    {
        var runtime = new RecordingRuntime();
        await using var provider = fixture.BuildServices(FleetFixture.Clock(), runtime);
        var slug = await RegisterAndRequestAsync(provider, "delta");
        await using var scope = provider.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<TenantRegistry>();
        var jobId = (await scope.ServiceProvider.GetRequiredService<IProvisioningJobs>().LatestAsync(slug, TestContext.Current.CancellationToken))!.Id;
        Assert.True((await registry.SetStatusAsync(slug, TenantStatus.Active, TestContext.Current.CancellationToken)).IsSuccess);

        var result = await scope.ServiceProvider.GetRequiredService<ProvisioningRunner>().RunAsync(jobId, TestContext.Current.CancellationToken);

        Assert.Equal(ProvisioningStatus.Failed, result.Status);
        Assert.Contains("active already", result.Error, StringComparison.Ordinal);
        Assert.Empty(runtime.Commands);
    }

    [Fact]
    public async Task DeprovisioningTakesTheUnitDownAndKeepsTheData()
    {
        var runtime = new RecordingRuntime();
        await using var provider = fixture.BuildServices(FleetFixture.Clock(), runtime);
        var slug = await RegisterAndRequestAsync(provider, "echo");
        var harness = new ProvisioningWorkerHarness(provider);
        Assert.True(await harness.RunNextAsync());

        await using (var scope = provider.CreateAsyncScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<TenantRegistry>();
            var requested = await registry.RequestDeprovisioningAsync(slug, TestContext.Current.CancellationToken);
            Assert.True(requested.IsSuccess, requested.IsFailure ? requested.Error.Message : null);
            Assert.Equal(JobKind.Deprovision, requested.Value.Kind);
        }

        Assert.True(await harness.RunNextAsync());

        await using (var scope = provider.CreateAsyncScope())
        {
            var job = (await scope.ServiceProvider.GetRequiredService<IProvisioningJobs>().LatestAsync(slug, TestContext.Current.CancellationToken))!;
            Assert.Equal(JobKind.Deprovision, job.Kind);
            Assert.Equal(ProvisioningStatus.Completed, job.Status);
            Assert.Equal([StepNames.Remove], job.Steps.Select(s => s.Name));
            Assert.Equal(slug, Assert.Single(runtime.Removed).Tenant.Slug);
            Assert.Equal(TenantStatus.Decommissioned, (await scope.ServiceProvider.GetRequiredService<ITenantRegistry>().FindAsync(slug, TestContext.Current.CancellationToken))!.Status);

            // The data is still there: the tenant database answers its owner.
            using var root = new OpenBaoSecretStore(fixture.StoreOptions());
            var unit = await root.ReadAsync($"tenants/{slug}/unit", TestContext.Current.CancellationToken);
            await using var connection = new NpgsqlConnection(unit!["ConnectionStrings:Database"]);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            // A tenant that is not Active cannot be de-provisioned again.
            var again = await scope.ServiceProvider.GetRequiredService<TenantRegistry>().RequestDeprovisioningAsync(slug, TestContext.Current.CancellationToken);
            Assert.Equal(TenancyErrors.NotProvisionable, again.Error.Code);
        }
    }

    [Fact]
    public async Task AnUpdateMigratesSeedsAndReappliesTheUnitOfAnActiveTenant()
    {
        var runtime = new RecordingRuntime();
        await using var provider = fixture.BuildServices(FleetFixture.Clock(), runtime);
        var slug = await RegisterAndRequestAsync(provider, "foxtrot");
        var harness = new ProvisioningWorkerHarness(provider);
        Assert.True(await harness.RunNextAsync());
        var commandsBefore = runtime.Commands.Count;

        await using (var scope = provider.CreateAsyncScope())
        {
            var requested = await scope.ServiceProvider.GetRequiredService<TenantRegistry>().RequestUpdateAsync(slug, TestContext.Current.CancellationToken);
            Assert.True(requested.IsSuccess, requested.IsFailure ? requested.Error.Message : null);
            Assert.Equal(JobKind.Update, requested.Value.Kind);
        }

        Assert.True(await harness.RunNextAsync());

        await using (var scope = provider.CreateAsyncScope())
        {
            var job = (await scope.ServiceProvider.GetRequiredService<IProvisioningJobs>().LatestAsync(slug, TestContext.Current.CancellationToken))!;
            Assert.Equal(ProvisioningStatus.Completed, job.Status);
            Assert.Equal(UpdateSteps, job.Steps.Select(s => s.Name));
            Assert.Equal(2, runtime.Applied.Count);
            Assert.Equal(commandsBefore + 2, runtime.Commands.Count); // migrate and seed ran again, idempotently
            Assert.Equal(TenantStatus.Active, (await scope.ServiceProvider.GetRequiredService<ITenantRegistry>().FindAsync(slug, TestContext.Current.CancellationToken))!.Status);
        }
    }

    [Fact]
    public async Task AnUpdateHealsAUnitWhoseSecretsWereDeletedFromTheStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new RecordingRuntime();
        await using var provider = fixture.BuildServices(FleetFixture.Clock(), runtime);
        var slug = await RegisterAndRequestAsync(provider, "golf");
        var harness = new ProvisioningWorkerHarness(provider);
        Assert.True(await harness.RunNextAsync());

        using var root = new OpenBaoSecretStore(fixture.StoreOptions());
        var before = (await root.ReadAsync($"tenants/{slug}/unit", ct))!;
        var tokenBefore = (await root.ReadAsync($"tenants/{slug}/fleet", ct))![SecretsStep.UnitTokenKey];

        // What a store that lost its data looks like to one tenant: every secret under tenants/<slug> is gone.
        var forgotten = await DeleteEverySecretOfAsync(slug, ct);
        Assert.Equal(["database", "fleet", "storage", "unit"], forgotten.Order(StringComparer.Ordinal));
        Assert.Null(await root.ReadAsync($"tenants/{slug}/unit", ct));

        await using (var scope = provider.CreateAsyncScope())
        {
            var requested = await scope.ServiceProvider.GetRequiredService<TenantRegistry>().RequestUpdateAsync(slug, ct);
            Assert.True(requested.IsSuccess, requested.IsFailure ? requested.Error.Message : null);
        }

        Assert.True(await harness.RunNextAsync());

        await using (var scope = provider.CreateAsyncScope())
        {
            var job = (await scope.ServiceProvider.GetRequiredService<IProvisioningJobs>().LatestAsync(slug, ct))!;
            Assert.Equal(ProvisioningStatus.Completed, job.Status);
            Assert.Equal(UpdateSteps, job.Steps.Select(s => s.Name));
        }

        // The unit secret is back, with a new app password that opens the tenant's database; the old one does not.
        var after = await root.ReadAsync($"tenants/{slug}/unit", ct);
        Assert.NotNull(after);
        Assert.NotEqual(before["ConnectionStrings:Database"], after["ConnectionStrings:Database"]);
        Assert.StartsWith("-----BEGIN EC PRIVATE KEY-----", after["Tokens:SigningKeyPem"], StringComparison.Ordinal);
        await using (var healed = new NpgsqlConnection(after["ConnectionStrings:Database"]))
        {
            await healed.OpenAsync(ct);
            await using var command = new NpgsqlCommand("select current_database()", healed);
            Assert.Equal(slug, await command.ExecuteScalarAsync(ct));
        }

        await using (var stale = new NpgsqlConnection(before["ConnectionStrings:Database"]))
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => stale.OpenAsync(ct));
            Assert.Equal("28P01", refused.SqlState);
        }

        // A new unit token reads the unit secret and nothing else, and the re-applied unit carries it.
        var tokenAfter = (await root.ReadAsync($"tenants/{slug}/fleet", ct))![SecretsStep.UnitTokenKey];
        Assert.NotEqual(tokenBefore, tokenAfter);
        using var unitStore = new OpenBaoSecretStore(fixture.StoreOptions(tokenAfter));
        Assert.NotNull(await unitStore.ReadAsync($"tenants/{slug}/unit", ct));
        await Assert.ThrowsAsync<SecretStoreException>(() => unitStore.ReadAsync($"tenants/{slug}/database", ct));
        await Assert.ThrowsAsync<SecretStoreException>(() => unitStore.ReadAsync($"tenants/{slug}/fleet", ct));
        Assert.Equal(tokenAfter, runtime.Applied[^1].Environment["Secrets__Token"]);

        // The container signature was issued again and opens the tenant's container.
        var storage = new BlobObjectStorage(new StorageOptions { ContainerSasUrl = after["Storage:ContainerSasUrl"] });
        Assert.Null(await storage.CheckAsync(ct));
    }

    /// <summary>The steps of an update, in order (ADR-0028): the three in-process steps heal, the rest re-apply the unit.</summary>
    private static readonly string[] UpdateSteps = [StepNames.Database, StepNames.Storage, StepNames.Secrets, StepNames.Migrate, StepNames.Seed, StepNames.Unit, StepNames.Verify];

    /// <summary>Deletes every secret under <c>tenants/&lt;slug&gt;</c>, every version, through OpenBao's metadata endpoint; returns their names.</summary>
    private async Task<List<string>> DeleteEverySecretOfAsync(string slug, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(fixture.OpenBaoAddress + "/") };
        http.DefaultRequestHeaders.Add("X-Vault-Token", FleetFixture.RootToken);
        using var list = new HttpRequestMessage(new HttpMethod("LIST"), new Uri($"v1/secret/metadata/tenants/{slug}", UriKind.Relative));
        using var listed = await http.SendAsync(list, cancellationToken);
        listed.EnsureSuccessStatusCode();
        var body = await listed.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var names = body.GetProperty("data").GetProperty("keys").EnumerateArray().Select(key => key.GetString()!).ToList();
        foreach (var name in names)
        {
            using var deleted = await http.DeleteAsync(new Uri($"v1/secret/metadata/tenants/{slug}/{name}", UriKind.Relative), cancellationToken);
            deleted.EnsureSuccessStatusCode();
        }

        return names;
    }

    private static async Task<string> RegisterAndRequestAsync(ServiceProvider provider, string prefix)
    {
        var slug = $"{prefix}-{Ids.New():N}"[..20];
        await using var scope = provider.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<TenantRegistry>();
        var created = await registry.CreateAsync(new NewTenant(slug, prefix, [], $"admin@{prefix}.test", "Admin", SizeClass.Small, null, null), TestContext.Current.CancellationToken);
        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Message : null);
        var requested = await registry.RequestProvisioningAsync(slug, TestContext.Current.CancellationToken);
        Assert.True(requested.IsSuccess, requested.IsFailure ? requested.Error.Message : null);
        return slug;
    }
}

/// <summary>Runs the worker's single-step loop without starting the hosted service.</summary>
public sealed class ProvisioningWorkerHarness(ServiceProvider provider)
{
    public async Task<bool> RunNextAsync()
    {
        var worker = new ProvisioningWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<FleetOptions>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ProvisioningWorker>>());
        return await worker.RunNextAsync(TestContext.Current.CancellationToken);
    }
}
