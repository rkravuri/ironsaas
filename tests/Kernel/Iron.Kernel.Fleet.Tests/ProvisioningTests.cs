using System.Net.Http.Json;
using System.Text.Json;
using Azure.Storage.Blobs;
using Iron.Kernel.Files.Services;
using Iron.Kernel.Fleet.Contracts;
using Iron.Kernel.Fleet.Data;
using Iron.Kernel.Fleet.Services;
using Iron.Kernel.Fleet.Services.Steps;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Iron.Kernel.Fleet.Tests;

/// <summary>TF-002 against the real platform services: the steps, their records, resume, and the isolation they produce (TF-005).</summary>
[Collection(FleetCollection.Name)]
public class ProvisioningTests(FleetFixture fixture)
{
    [Fact]
    [Proves("TF-002 AC1", "ADR-0018", "ADR-0048")]
    public async Task AJobRunsEveryStepActivatesTheTenantAndHandsTheInvitationOver()
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
        Assert.DoesNotContain(RecordingRuntime.InvitationToken, job.Output, StringComparison.Ordinal);
        var handover = await scope.ServiceProvider.GetRequiredService<IJobHandovers>().TakeAsync(job.Id, SystemPrincipal.SystemId, TestContext.Current.CancellationToken);
        Assert.Equal((HandoverState.Given, $"/invitations#{RecordingRuntime.InvitationToken}"), (handover.State, handover.Item?.LinkPath));

        var tenant = await scope.ServiceProvider.GetRequiredService<ITenantRegistry>().FindAsync(slug, TestContext.Current.CancellationToken);
        Assert.Equal(TenantStatus.Active, tenant!.Status);
        Assert.NotNull(tenant.ActivatedAt);

        // The image commands ran as the owner with the unit's environment; the unit itself was applied and checked.
        Assert.Equal(["migrate", "seed"], runtime.Commands.Select(c => c.Command));
        Assert.All(runtime.Commands, c => Assert.Contains("_owner;", c.Unit.Environment["ConnectionStrings__Database"], StringComparison.Ordinal));
        Assert.All(runtime.Commands, c => Assert.Equal($"tenants/{slug}", c.Unit.Environment["Secrets__Path"]));
        var seed = runtime.Commands.Single(c => c.Command == "seed").Unit.Environment;
        Assert.Equal(tenant.DisplayName, seed["Seed__RootUnit__Name"]);
        Assert.Equal(slug, seed["Seed__RootUnit__Code"]);
        Assert.Equal("test", Assert.Single(runtime.Applied).ImageTag);
        Assert.Equal(SizeClasses.LimitsOf(SizeClass.Small), Assert.Single(runtime.Applied).Limits);
        Assert.True(runtime.ReadyChecks >= 1);
    }

    [Fact]
    [Proves("TF-210 AC1", "TF-210 AC2", "ADR-0018")]
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

        // It manages its own single sign-on providers' client secrets, and writes nothing else (ADR-0048).
        var probe = new Dictionary<string, string>(StringComparer.Ordinal) { ["client_secret"] = "probe" };
        var ssoPath = $"tenants/{acme}/sso/{Guid.NewGuid():N}";
        await unitStore.WriteAsync(ssoPath, probe, TestContext.Current.CancellationToken);
        Assert.Equal("probe", (await unitStore.ReadAsync(ssoPath, TestContext.Current.CancellationToken))!["client_secret"]);
        await unitStore.DeleteAsync(ssoPath, TestContext.Current.CancellationToken);
        Assert.Null(await unitStore.ReadAsync(ssoPath, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SecretStoreException>(() => unitStore.WriteAsync($"tenants/{bravo}/sso/{Guid.NewGuid():N}", probe, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SecretStoreException>(() => unitStore.WriteAsync($"tenants/{acme}/unit", probe, TestContext.Current.CancellationToken));

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
    [Proves("TF-002 AC2", "ADR-0018")]
    public async Task AFailedStepStopsTheJobAndARerunContinuesFromIt()
    {
        var time = FleetFixture.Clock();
        var runtime = new RecordingRuntime { FailNext = ("migrate", "relation \"kernel_authz.roles\" does not exist") };
        await using var provider = fixture.BuildServices(time, runtime);
        var slug = await RegisterAndRequestAsync(provider, "charlie");
        var harness = new ProvisioningWorkerHarness(provider);

        Assert.True(await harness.RunNextAsync());

        await using (var scope = provider.CreateAsyncScope())
        {
            var job = (await scope.ServiceProvider.GetRequiredService<IProvisioningJobs>().LatestAsync(slug, TestContext.Current.CancellationToken))!;
            Assert.Equal(ProvisioningStatus.Failed, job.Status);
            Assert.Equal(StepNames.Migrate, job.CurrentStep);
            Assert.Contains("kernel_authz.roles", job.Error, StringComparison.Ordinal);
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
    [Proves("TF-002 AC3")]
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
    [Proves("TF-210 AC3", "ADR-0019")]
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
    [Proves("ADR-0028")]
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
    [Proves("ADR-0028", "ADR-0034", "ADR-0035")]
    public async Task AnUpdateHealsAUnitWhoseSecretsWereDeletedFromTheStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new RecordingRuntime();
        await using var provider = fixture.BuildServices(FleetFixture.Clock(), runtime);
        var slug = await RegisterAndRequestAsync(provider, "golf");
        var harness = new ProvisioningWorkerHarness(provider);
        Assert.True(await harness.RunNextAsync());
        await using (var given = provider.CreateAsyncScope())
        {
            // The operator takes the invitation, which deletes its place, as after any provisioning (ADR-0046).
            var job = (await given.ServiceProvider.GetRequiredService<IProvisioningJobs>().LatestAsync(slug, ct))!;
            Assert.Equal(HandoverState.Given, (await given.ServiceProvider.GetRequiredService<IJobHandovers>().TakeAsync(job.Id, SystemPrincipal.SystemId, ct)).State);
        }

        using var root = new OpenBaoSecretStore(fixture.StoreOptions());
        var before = (await root.ReadAsync($"tenants/{slug}/unit", ct))!;
        var tokenBefore = (await root.ReadAsync($"tenants/{slug}/fleet", ct))![SecretsStep.UnitTokenKey];

        // What a store that lost its data looks like to one tenant: every secret under tenants/<slug> is gone.
        var forgotten = await DeleteEverySecretOfAsync(slug, ct);
        Assert.Equal(["database", "entitlements", "flags", "fleet", "storage", "unit"], forgotten.Order(StringComparer.Ordinal));
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

        // A new unit token reads the unit secret and the entitlements, written again (ADR-0034), and nothing else; the re-applied unit carries it.
        var tokenAfter = (await root.ReadAsync($"tenants/{slug}/fleet", ct))![SecretsStep.UnitTokenKey];
        Assert.NotEqual(tokenBefore, tokenAfter);
        using var unitStore = new OpenBaoSecretStore(fixture.StoreOptions(tokenAfter));
        Assert.NotNull(await unitStore.ReadAsync($"tenants/{slug}/unit", ct));
        Assert.NotNull(await unitStore.ReadAsync($"tenants/{slug}/entitlements", ct));
        Assert.NotNull(await unitStore.ReadAsync($"tenants/{slug}/flags", ct));
        await Assert.ThrowsAsync<SecretStoreException>(() => unitStore.ReadAsync($"tenants/{slug}/database", ct));
        await Assert.ThrowsAsync<SecretStoreException>(() => unitStore.ReadAsync($"tenants/{slug}/fleet", ct));
        Assert.Equal(tokenAfter, runtime.Applied[^1].Environment["Secrets__Token"]);

        // The container signature was issued again and opens the tenant's container.
        var storage = new BlobObjectStorage(new StorageOptions { ContainerSasUrl = after["Storage:ContainerSasUrl"] });
        Assert.Null(await storage.CheckAsync(ct));
    }

    [Fact]
    [Proves("TF-017 AC1", "ADR-0046")]
    public async Task ARecoveryJobRunsTheUnitsCommandWithTheOperatorsRequestAndAnswersTheLink()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new RecordingRuntime();
        await using var provider = fixture.BuildServices(FleetFixture.Clock(), runtime);
        var slug = await RegisterAndRequestAsync(provider, "golf");
        var harness = new ProvisioningWorkerHarness(provider);
        Assert.True(await harness.RunNextAsync());
        const string Reason = "The administrator lost their password (ticket 4711)";

        await using (var scope = provider.CreateAsyncScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<TenantRegistry>();
            Assert.Equal(TenancyErrors.RecoveryReasonRequired, (await registry.RequestAdministratorRecoveryAsync(slug, null, " ", "Operator One", ct)).Error.Code);
            var requested = await registry.RequestAdministratorRecoveryAsync(slug, null, Reason, "Operator One", ct);
            Assert.True(requested.IsSuccess, requested.IsFailure ? requested.Error.Message : null);
            Assert.Equal((JobKind.Recover, $"admin@golf.test"), (requested.Value.Kind, requested.Value.Request!.Subject));
        }

        Assert.True(await harness.RunNextAsync());
        ProvisioningJobInfo completed;
        await using (var scope = provider.CreateAsyncScope())
        {
            completed = (await scope.ServiceProvider.GetRequiredService<IProvisioningJobs>().LatestAsync(slug, ct))!;
            runtime.FailNext = ("recover-admin", "No recovery link was issued (identity.recovery.not_administrator)");
            Assert.True((await scope.ServiceProvider.GetRequiredService<TenantRegistry>().RequestAdministratorRecoveryAsync(slug, "someone@golf.test", Reason, "Operator One", ct)).IsSuccess);
        }

        Assert.True(await harness.RunNextAsync());
        await using var check = provider.CreateAsyncScope();
        var refused = (await check.ServiceProvider.GetRequiredService<IProvisioningJobs>().LatestAsync(slug, ct))!;
        var command = runtime.Commands.Where(c => c.Command == "recover-admin").Select(c => c.Unit.Environment).First();
        var tenant = (await check.ServiceProvider.GetRequiredService<ITenantRegistry>().FindAsync(slug, ct))!;

        Assert.Equal(ProvisioningStatus.Completed, completed.Status);
        Assert.Equal([StepNames.Recover], completed.Steps.Select(s => s.Name));
        Assert.Equal("recovery link for admin@golf.test handed over: it waits in the store for the operator who asked, once, for 15 minutes", completed.Output);
        Assert.Equal(("admin@golf.test", "Operator One", Reason), (command["Recovery__LoginId"], command["Recovery__Operator"], command["Recovery__Reason"]));
        Assert.Equal(ProvisioningStatus.Failed, refused.Status);
        Assert.Contains("not_administrator", refused.Error, StringComparison.Ordinal);
        Assert.Equal(TenantStatus.Active, tenant.Status); // a recovery changes a person, never the tenant
    }

    [Fact]
    [Proves("ADR-0047")]
    public async Task ARecoveryJobAsksTheUnitToResetTheAdministratorsFactorsOnlyWhenTheOperatorSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new RecordingRuntime();
        await using var provider = fixture.BuildServices(FleetFixture.Clock(), runtime);
        var slug = await RegisterAndRequestAsync(provider, "hotel");
        var harness = new ProvisioningWorkerHarness(provider);
        Assert.True(await harness.RunNextAsync());
        const string Reason = "Lost password and phone (ticket 4712)";

        await using (var scope = provider.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<TenantRegistry>().RequestAdministratorRecoveryAsync(slug, null, Reason, "Operator One", ct)).IsSuccess);
        }

        Assert.True(await harness.RunNextAsync());
        await using (var scope = provider.CreateAsyncScope())
        {
            var requested = await scope.ServiceProvider.GetRequiredService<TenantRegistry>().RequestAdministratorRecoveryAsync(slug, null, Reason, "Operator One", ct, resetFactors: true);
            Assert.True(requested.Value.Request!.ResetFactors);
        }

        Assert.True(await harness.RunNextAsync());
        await using var check = provider.CreateAsyncScope();
        var completed = (await check.ServiceProvider.GetRequiredService<IProvisioningJobs>().LatestAsync(slug, ct))!;
        var commands = runtime.Commands.Where(c => c.Command == "recover-admin").Select(c => c.Unit.Environment).ToList();
        var audit = await check.ServiceProvider.GetRequiredService<Iron.Kernel.Audit.Data.AuditDbContext>().Events.AsNoTracking()
            .Where(e => e.EventType == TenancyAuditEvents.AdministratorRecoveryRequested && e.Target == slug)
            .Select(e => e.Details!)
            .ToListAsync(ct);

        Assert.Equal(2, commands.Count);
        Assert.False(commands[0].ContainsKey("Recovery__ResetFactors"));
        Assert.Equal("true", commands[1]["Recovery__ResetFactors"]);
        Assert.EndsWith("; the administrator's second factors were reset", completed.Output, StringComparison.Ordinal);
        Assert.Equal(["false", "true"], audit.Select(details => JsonDocument.Parse(details).RootElement.GetProperty("reset_factors").GetString()).Order(StringComparer.Ordinal));
    }

    [Fact]
    [Proves("ADR-0047")]
    public async Task TheUnitsFactorKeyIsMadeOnceAtProvisioningAndKeptByAnUpdate()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new RecordingRuntime();
        await using var provider = fixture.BuildServices(FleetFixture.Clock(), runtime);
        var slug = await RegisterAndRequestAsync(provider, "india");
        var harness = new ProvisioningWorkerHarness(provider);
        Assert.True(await harness.RunNextAsync());
        using var root = new OpenBaoSecretStore(fixture.StoreOptions());
        var first = (await root.ReadAsync($"tenants/{slug}/unit", ct))![SecretsStep.FactorKeyKey];

        await using (var scope = provider.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<TenantRegistry>().RequestUpdateAsync(slug, ct)).IsSuccess);
        }

        Assert.True(await harness.RunNextAsync());
        var after = (await root.ReadAsync($"tenants/{slug}/unit", ct))![SecretsStep.FactorKeyKey];

        Assert.Equal(32, Convert.FromBase64String(first).Length);
        Assert.Equal(first, after); // a new key would leave every authenticator app's secret unreadable
    }

    [Fact]
    [Proves("ADR-0046")]
    public async Task ALinkFromTheOperatorsPathIsHandedOverOnceToWhoAskedAndIsInNoOutputAndNoLogLine()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new RecordingRuntime();
        var logs = new CapturingLogs();
        await using var provider = fixture.BuildServices(FleetFixture.Clock(), runtime, services => services.AddLogging(logging => logging.AddProvider(logs)));
        var harness = new ProvisioningWorkerHarness(provider);

        // Jobs are the fleet's, not a test's: another test's waiting job would otherwise run in this one's place.
        while (await harness.RunNextAsync())
        {
        }

        logs.Lines.Clear();
        var slug = await RegisterAndRequestAsync(provider, "india");
        Assert.True(await harness.RunNextAsync());
        await using (var scope = provider.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<TenantRegistry>().RequestAdministratorRecoveryAsync(slug, null, "Lost password (ticket 4711)", "Operator One", ct)).IsSuccess);
        }

        Assert.True(await harness.RunNextAsync());

        await using var check = provider.CreateAsyncScope();
        var context = check.ServiceProvider.GetRequiredService<FleetDbContext>();
        var jobs = await context.ProvisioningJobs.AsNoTracking().Where(j => j.TenantSlug == slug).OrderBy(j => j.RequestedAt).ThenBy(j => j.Id).ToListAsync(ct);
        var steps = await context.ProvisioningSteps.AsNoTracking().Where(s => jobs.Select(j => j.Id).Contains(s.JobId)).ToListAsync(ct);
        var handovers = check.ServiceProvider.GetRequiredService<IJobHandovers>();
        var someoneElse = await handovers.TakeAsync(jobs[1].Id, Ids.New(), ct);
        var asked = await handovers.TakeAsync(jobs[1].Id, SystemPrincipal.SystemId, ct);
        var again = await handovers.TakeAsync(jobs[1].Id, SystemPrincipal.SystemId, ct);
        var invitation = await handovers.TakeAsync(jobs[0].Id, SystemPrincipal.SystemId, ct);
        using var root = new OpenBaoSecretStore(fixture.StoreOptions());
        var left = await root.ReadAsync(HandoverPlaces.PathOf(slug, jobs[1].Id), ct);

        // Neither code is in a job's output, a step's output or error, or any line the fleet logged.
        var written = jobs.Select(j => $"{j.Output} {j.Error}").Concat(steps.Select(s => $"{s.Output} {s.Error}")).Concat(logs.Lines).ToList();
        Assert.NotEmpty(logs.Lines);
        foreach (var code in new[] { RecordingRuntime.InvitationToken, RecordingRuntime.RecoveryToken })
        {
            Assert.DoesNotContain(written, line => line.Contains(code, StringComparison.Ordinal));
        }

        Assert.DoesNotContain(written, line => line.Contains("/recovery#", StringComparison.Ordinal) || line.Contains("/invitations#", StringComparison.Ordinal));
        Assert.Equal(HandoverState.NotYours, someoneElse.State);
        Assert.Equal((HandoverState.Given, "recovery", $"/recovery#{RecordingRuntime.RecoveryToken}"), (asked.State, asked.Item?.Kind, asked.Item?.LinkPath));
        Assert.Equal(HandoverState.Taken, again.State);
        Assert.Equal(HandoverState.Given, invitation.State);
        Assert.Null(left); // the place is deleted as it is given
        Assert.Equal(4, runtime.Refused); // each command's token could not read back, and the unit's token could not write
    }

    /// <summary>The steps of an update, in order (ADR-0028): the three in-process steps heal, the rest re-apply the unit.</summary>
    private static readonly string[] UpdateSteps = [StepNames.Database, StepNames.Storage, StepNames.Secrets, StepNames.Migrate, StepNames.Seed, StepNames.Unit, StepNames.Verify];

    /// <summary>Deletes every secret under <c>tenants/&lt;slug&gt;</c>, every version, through OpenBao's metadata endpoint; returns their names.</summary>
    private async Task<List<string>> DeleteEverySecretOfAsync(string slug, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(fixture.OpenBaoAddress + "/") };
        http.DefaultRequestHeaders.Add("X-Vault-Token", fixture.RootToken);
        using var list = new HttpRequestMessage(new HttpMethod("LIST"), new Uri($"v1/{fixture.Mount}/metadata/tenants/{slug}", UriKind.Relative));
        using var listed = await http.SendAsync(list, cancellationToken);
        listed.EnsureSuccessStatusCode();
        var body = await listed.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var names = body.GetProperty("data").GetProperty("keys").EnumerateArray().Select(key => key.GetString()!).ToList();
        foreach (var name in names)
        {
            using var deleted = await http.DeleteAsync(new Uri($"v1/{fixture.Mount}/metadata/tenants/{slug}/{name}", UriKind.Relative), cancellationToken);
            deleted.EnsureSuccessStatusCode();
        }

        return names;
    }

    [Fact]
    [Proves("ADR-0050")]
    public async Task TheVerifiersRoleConnectsWithAPasswordTheUnitNeverHolds()
    {
        var runtime = new RecordingRuntime();
        await using var provider = fixture.BuildServices(FleetFixture.Clock(), runtime);
        var acme = await RegisterAndRequestAsync(provider, "audit");

        // Jobs are the fleet's, not a test's: run every one waiting, this tenant's among them, and leave none behind.
        var harness = new ProvisioningWorkerHarness(provider);
        while (await harness.RunNextAsync())
        {
        }

        using var root = new OpenBaoSecretStore(fixture.StoreOptions());
        var database = await root.ReadAsync($"tenants/{acme}/database", TestContext.Current.CancellationToken);
        var unit = await root.ReadAsync($"tenants/{acme}/unit", TestContext.Current.CancellationToken);
        Assert.NotNull(database);
        Assert.NotNull(unit);
        var password = database[DatabaseStep.AuditPasswordKey];
        Assert.Equal($"{acme}_audit", unit["Database:AuditReaderRole"]);
        Assert.DoesNotContain(unit.Values, value => value.Contains(password, StringComparison.Ordinal));

        // The fleet reaches the tenant's database with it; migrate then lets it read the chain and nothing else.
        var reader = new NpgsqlConnectionStringBuilder(unit["ConnectionStrings:Database"]) { Username = $"{acme}_audit", Password = password }.ToString();
        await using var connection = new NpgsqlConnection(reader);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("select current_user", connection);
        Assert.Equal($"{acme}_audit", await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
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
