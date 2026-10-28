using Iron.Kernel.Audit.Data;
using Iron.Kernel.Fleet.Services;
using Iron.Kernel.Fleet.Services.Steps;
using Iron.Kernel.Security.Keys;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Iron.Kernel.Fleet.Tests;

/// <summary>
/// Key rotation in the control plane and the fleet (TF-073, TF-075, ADR-0054), on the real store and database: a key
/// officer's rotation carried out, its re-encryption reported, its old version retired, each step audited with who asked
/// and why; the schedule; a signing key's overlap; a database password rotated in the store, then the database; and the
/// control plane's API and fleet each with a token of its own.
/// </summary>
[Collection(FleetCollection.Name)]
public class KeyRotationTests(FleetFixture fixture)
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-075 AC1", "TF-075 AC3", "ADR-0054")]
    public async Task AKeyOfficersRotationIsCarriedOutMovedAndRetiredAndEveryStepAuditedWithTheOfficerAndTheReason()
    {
        var time = FleetFixture.Clock();
        var officer = new Principal(Ids.New());
        var slug = await ProvisionAsync(time, "kilo");
        const string Reason = "The quarterly key drill";

        KeyRotationInfo requested;
        await using (var provider = fixture.BuildServices(time, new RecordingRuntime(), services => services.AddSingleton<ICurrentPrincipal>(officer)))
        await using (var scope = provider.CreateAsyncScope())
        {
            var asked = await scope.ServiceProvider.GetRequiredService<KeyRotationService>().RequestAsync(slug, TenantKeyNames.Index, Reason, Cancel);
            Assert.True(asked.IsSuccess, asked.IsFailure ? asked.Error.Message : null);
            requested = asked.Value;
            var again = await scope.ServiceProvider.GetRequiredService<KeyRotationService>().RequestAsync(slug, TenantKeyNames.Index, Reason, Cancel);
            Assert.Equal(TenancyErrors.KeyRotationInProgress, again.Error.Code);
        }

        await RunKeyWorkerAsync(time);
        var rotated = await RotationAsync(time, slug, requested.Id);

        // The unit reports every value moved, as its own client; the fleet then retires the old version.
        var unitClient = new Principal(Ids.New());
        await using (var provider = fixture.BuildServices(time, new RecordingRuntime(), services => services.AddSingleton<ICurrentPrincipal>(unitClient)))
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ITenantAuditChains>().BindUnitClientAsync(slug, unitClient.Id, Cancel);
            var reported = await scope.ServiceProvider.GetRequiredService<KeyRotationService>().ProgressAsync(requested.Id, 12, 12, finished: true, Cancel);
            Assert.True(reported.IsSuccess, reported.IsFailure ? reported.Error.Message : null);
        }

        await RunKeyWorkerAsync(time);
        var retired = await RotationAsync(time, slug, requested.Id);
        using var keys = new TransitKeyService(fixture.StoreOptions(), new KeyOptions());
        var versions = await keys.DescribeAsync($"tenant-{slug}-index", Cancel);

        Assert.Equal((KeyRotationStates.Rotated, 1, 2), (rotated.State, rotated.FromVersion, rotated.ToVersion));
        Assert.Equal((KeyRotationStates.Retired, 12L), (retired.State, retired.Done));
        Assert.Equal((2, 2), (versions!.Latest, versions.Minimum));
        await using var auditProvider = fixture.BuildServices(time, new RecordingRuntime());
        await using var auditScope = auditProvider.CreateAsyncScope();
        var events = await auditScope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking().Where(e => e.RecordId == requested.Id || (e.EventType == TenancyAuditEvents.KeyRotationRequested && e.Target == slug)).OrderBy(e => e.CreatedAt).ToListAsync(Cancel);
        Assert.Equal([TenancyAuditEvents.KeyRotationRequested, TenancyAuditEvents.KeyRotated, TenancyAuditEvents.KeyRetired], events.Select(e => e.EventType));
        Assert.All(events, e => Assert.Equal(Reason, e.Reason));
        Assert.Equal(officer.Id, events[0].ActorId);
        Assert.All(events, e => Assert.Contains(officer.Id.ToString("D"), e.Details, StringComparison.Ordinal));
    }

    [Fact]
    [Proves("TF-075 AC2", "ADR-0054")]
    public async Task AnOldSigningVersionIsRetiredOnlyOnceEveryTokenItSignedHasExpired()
    {
        var time = FleetFixture.Clock();
        var slug = await ProvisionAsync(time, "lima");
        var rotation = await RequestAsync(time, slug, TenantKeyNames.Signing);

        await RunKeyWorkerAsync(time);
        time.Advance(TimeSpan.FromMinutes(20));
        await RunKeyWorkerAsync(time);
        var withinOverlap = await RotationAsync(time, slug, rotation.Id);
        time.Advance(TimeSpan.FromMinutes(11));
        await RunKeyWorkerAsync(time);
        var afterOverlap = await RotationAsync(time, slug, rotation.Id);

        Assert.Equal(KeyRotationStates.Rotated, withinOverlap.State);
        Assert.Equal(KeyRotationStates.Retired, afterOverlap.State);
    }

    [Fact]
    [Proves("TF-073 AC2", "ADR-0054")]
    public async Task ADatabasePasswordIsRotatedInTheStoreThenInTheDatabaseAndTheOldOneStopsWorking()
    {
        var time = FleetFixture.Clock();
        var slug = await ProvisionAsync(time, "mike");
        using var root = new OpenBaoSecretStore(fixture.StoreOptions());
        var before = (await root.ReadAsync($"tenants/{slug}/unit", Cancel))!["ConnectionStrings:Database"];
        var rotation = await RequestAsync(time, slug, TenantKeyNames.Database);

        await RunKeyWorkerAsync(time);
        var after = (await root.ReadAsync($"tenants/{slug}/unit", Cancel))!["ConnectionStrings:Database"];
        var database = (await root.ReadAsync($"tenants/{slug}/database", Cancel))!;
        var done = await RotationAsync(time, slug, rotation.Id);

        Assert.NotEqual(before, after);
        Assert.Equal("2", database[DatabasePasswordRotation.VersionKey]);
        Assert.Equal((KeyRotationStates.Retired, 1, 2), (done.State, done.FromVersion, done.ToVersion));
        await using (var fresh = new NpgsqlConnection(after))
        {
            await fresh.OpenAsync(Cancel);
        }

        await using var stale = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(before) { Pooling = false }.ConnectionString);
        var refused = await Assert.ThrowsAsync<PostgresException>(() => stale.OpenAsync(Cancel));
        Assert.Equal("28P01", refused.SqlState);
    }

    [Fact]
    [Proves("ADR-0054")]
    public async Task TheScheduleRotatesTheDatabasePasswordQuarterlyAndTheKeysYearly()
    {
        var time = FleetFixture.Clock();
        var slug = await ProvisionAsync(time, "november");

        time.Advance(TimeSpan.FromDays(91));
        await RunKeyWorkerAsync(time);
        var quarter = await RotationsAsync(time, slug);
        time.Advance(TimeSpan.FromDays(275));
        await RunKeyWorkerAsync(time);
        var year = await RotationsAsync(time, slug);

        Assert.Equal([TenantKeyNames.Database], quarter.Select(r => r.Key));
        Assert.All(quarter, r => Assert.Equal((KeyRotationTriggers.Schedule, KeyRotationService.ScheduleName), (r.Trigger, r.RequestedBy)));
        Assert.Equal([TenantKeyNames.Data, TenantKeyNames.Index, TenantKeyNames.Signing], year.Where(r => r.Key != TenantKeyNames.Database).Select(r => r.Key).Order(StringComparer.Ordinal));
        Assert.All(year.Where(r => r.Key != TenantKeyNames.Database), r => Assert.Equal(KeyRotationStates.Rotated, r.State));
    }

    [Fact]
    [Proves("ADR-0054")]
    public async Task TheControlPlanesApiTokenCannotReadTheFleetsSecretNorMakeTokensAndTheFleetsCannotReadTheTokens()
    {
        var options = fixture.StoreOptions(path: "control");
        using var root = new OpenBaoSecretStore(options);
        using var keys = new TransitKeyService(options, new KeyOptions());
        await root.WriteAsync(ControlPlaneNames.UnitSecretPath, new Dictionary<string, string>(StringComparer.Ordinal) { ["ConnectionStrings:Database"] = "Host=postgres;Username=control_app;Password=api" }, Cancel);
        await root.WriteAsync(ControlPlaneNames.FleetSecretPath, new Dictionary<string, string>(StringComparer.Ordinal) { ["Fleet:Database:AdminConnectionString"] = "Host=postgres;Username=postgres;Password=superuser" }, Cancel);
        await root.WriteAsync(ControlPlaneNames.MigrateSecretPath, new Dictionary<string, string>(StringComparer.Ordinal) { ["ConnectionStrings:Database"] = "Host=postgres;Username=control_owner;Password=owner" }, Cancel);

        var tokens = await new ControlPlaneAccess(root, root, keys, options).PrepareAsync(Cancel);
        var again = await new ControlPlaneAccess(root, root, keys, options).PrepareAsync(Cancel);
        using var api = new OpenBaoSecretStore(fixture.StoreOptions(tokens.ApiToken, "control"));
        var fleetOptions = fixture.StoreOptions(tokens.FleetToken, "fleet");
        fleetOptions.TokenRole = ControlPlaneNames.TokenRole;
        using var fleet = new OpenBaoSecretStore(fleetOptions);
        using var apiKeys = new TransitKeyService(fixture.StoreOptions(tokens.ApiToken), new KeyOptions { Prefix = ControlPlaneNames.KeyPrefix });

        Assert.Equal(tokens, again);
        Assert.NotNull(await api.ReadAsync(ControlPlaneNames.UnitSecretPath, Cancel));
        await Assert.ThrowsAsync<SecretStoreException>(() => api.ReadAsync(ControlPlaneNames.FleetSecretPath, Cancel));
        await Assert.ThrowsAsync<SecretStoreException>(() => api.ReadAsync(ControlPlaneNames.MigrateSecretPath, Cancel));
        await Assert.ThrowsAsync<SecretStoreException>(() => api.ReadAsync(ControlPlaneNames.TokensSecretPath, Cancel));
        await Assert.ThrowsAsync<SecretStoreException>(() => api.ReadAsync("tenants/acme/unit", Cancel));
        await Assert.ThrowsAsync<SecretStoreException>(() => api.CreateTokenAsync(["tenant-acme"], "escalation", TimeSpan.FromMinutes(1), Cancel));
        await api.WriteAsync("tenants/acme/entitlements", new Dictionary<string, string>(StringComparer.Ordinal) { ["document"] = "{}" }, Cancel);
        var sealedValue = await apiKeys.EncryptAsync(KeyPurpose.Data, [1, 2, 3], Cancel);
        Assert.Equal([1, 2, 3], await apiKeys.DecryptAsync(KeyPurpose.Data, sealedValue, Cancel));

        Assert.NotNull(await fleet.ReadAsync(ControlPlaneNames.FleetSecretPath, Cancel));
        Assert.NotNull(await fleet.ReadAsync(ControlPlaneNames.MigrateSecretPath, Cancel));
        await Assert.ThrowsAsync<SecretStoreException>(() => fleet.ReadAsync(ControlPlaneNames.TokensSecretPath, Cancel));
        await fleet.CreateReadPolicyAsync("tenant-acme-probe", ["tenants/acme/unit"], Cancel);
        Assert.False(string.IsNullOrEmpty(await fleet.CreateTokenAsync(["tenant-acme-probe"], "unit-acme", TimeSpan.FromMinutes(1), Cancel)));
        await Assert.ThrowsAsync<SecretStoreException>(() => fleet.CreateTokenAsync([ControlPlaneNames.ApiPolicy], "escalation", TimeSpan.FromMinutes(1), Cancel));
    }

    private async Task<string> ProvisionAsync(FakeTimeProvider time, string prefix)
    {
        await using var provider = fixture.BuildServices(time, new RecordingRuntime());
        var slug = await ProvisioningTests.RegisterAndRequestAsync(provider, prefix);
        Assert.True(await new ProvisioningWorkerHarness(provider).RunNextAsync());
        return slug;
    }

    private async Task<KeyRotationInfo> RequestAsync(FakeTimeProvider time, string slug, string key)
    {
        await using var provider = fixture.BuildServices(time, new RecordingRuntime(), services => services.AddSingleton<ICurrentPrincipal>(new Principal(Ids.New())));
        await using var scope = provider.CreateAsyncScope();
        var asked = await scope.ServiceProvider.GetRequiredService<KeyRotationService>().RequestAsync(slug, key, "A test of the rotation", Cancel);
        Assert.True(asked.IsSuccess, asked.IsFailure ? asked.Error.Message : null);
        return asked.Value;
    }

    private async Task RunKeyWorkerAsync(FakeTimeProvider time)
    {
        await using var provider = fixture.BuildServices(time, new RecordingRuntime());
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<KeyRotationRunner>().RunOnceAsync(Cancel);
    }

    private async Task<KeyRotationInfo> RotationAsync(FakeTimeProvider time, string slug, Guid id)
    {
        await using var provider = fixture.BuildServices(time, new RecordingRuntime());
        await using var scope = provider.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<KeyRotationService>().FindAsync(slug, id, Cancel))!;
    }

    private async Task<IReadOnlyList<KeyRotationInfo>> RotationsAsync(FakeTimeProvider time, string slug)
    {
        await using var provider = fixture.BuildServices(time, new RecordingRuntime());
        await using var scope = provider.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<KeyRotationService>().StatusAsync(slug, Cancel)).Rotations;
    }

    private sealed record Principal(Guid Id) : ICurrentPrincipal;
}
