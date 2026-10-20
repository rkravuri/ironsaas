using System.Diagnostics.Metrics;
using System.Text.Json;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Settings.Contracts;
using Iron.Kernel.Settings.Data;
using Iron.Kernel.Settings.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Settings.Tests;

/// <summary>
/// ADR-0036 against a real database: a save and a reset with the history and the audit event (TF-058 AC1, AC2); a change
/// seen by another process within 30 seconds without a restart (TF-059 AC2); the last values through a failed refresh and
/// the alert after five minutes (TF-059 AC4); a unit two levels up (TF-057 AC1); and secret references that keep the
/// secret out of the database, read from the store when asked and kept five minutes at most (TF-057 AC3, TF-059 AC3).
/// </summary>
[Collection(SettingsCollection.Name)]
public class SettingsStoreTests(SettingsFixture fixture)
{
    [Fact]
    public async Task ASaveAndAResetWriteTheHistoryAndTheAuditWithBeforeAfterActorAndTime()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var ravi = Ids.New();
        await using var provider = fixture.BuildServices(time, actor: new FixedPrincipal(ravi));
        var unit = await SettingsFixture.UnitAsync(provider, "Audit branch");
        var place = SettingPlace.Unit(unit);

        await using (var scope = provider.CreateAsyncScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<SettingsWriter>();
            Assert.True((await writer.SetAsync(ProbeSettings.Batch.Key, place, Json("120"), ct)).IsSuccess);
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.True((await writer.SetAsync(ProbeSettings.Batch.Key, place, Json("200"), ct)).IsSuccess);
            // The same value again changes nothing and writes nothing.
            Assert.True((await writer.SetAsync(ProbeSettings.Batch.Key, place, Json("200"), ct)).IsSuccess);
            Assert.Equal(200, await scope.ServiceProvider.GetRequiredService<ISettings>().GetAsync(ProbeSettings.Batch, SettingContext.ForUnit(unit), ct));
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.True((await writer.ResetAsync(ProbeSettings.Batch.Key, place, ct)).IsSuccess);
            Assert.Equal(50, await scope.ServiceProvider.GetRequiredService<ISettings>().GetAsync(ProbeSettings.Batch, SettingContext.ForUnit(unit), ct));
        }

        await using var read = provider.CreateAsyncScope();
        var history = await read.ServiceProvider.GetRequiredService<SettingsDbContext>().History.AsNoTracking()
            .Where(c => c.Key == ProbeSettings.Batch.Key && c.TargetId == unit).OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(ct);
        Assert.Equal([("set", null, "120"), ("set", "120", "200"), ("reset", "200", null)], history.Select(c => (c.Kind, Compact(c.Before), Compact(c.After))));
        Assert.All(history, c => Assert.Equal(ravi, c.CreatedBy));

        var events = await read.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .Where(e => e.ActorId == ravi && e.Target == ProbeSettings.Batch.Key).OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync(ct);
        Assert.Equal([SettingsAuditEvents.ValueChanged, SettingsAuditEvents.ValueChanged, SettingsAuditEvents.ValueReset], events.Select(e => e.EventType));
        var last = JsonSerializer.Deserialize<Dictionary<string, string>>(events[^1].Details!)!;
        Assert.Equal(("unit", unit.ToString("D"), "200", "none"), (last["scope"], last["unit"], last["before"], last["after"]));
    }

    [Fact]
    public async Task AChangeReachesAnotherProcessWithinThirtySecondsWithoutARestart()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = fixture.BuildServices(time);
        await using var worker = fixture.BuildServices(time);
        var unit = await SettingsFixture.UnitAsync(api, "Refresh branch");
        await StartAsync(worker);

        Assert.Equal(50, await BatchAsync(worker, unit));
        await using (var scope = api.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<SettingsWriter>().SetAsync(ProbeSettings.Batch.Key, SettingPlace.Unit(unit), Json("75"), ct)).IsSuccess);
        }

        // The process that saved sees it at once; the other within its 30-second refresh.
        Assert.Equal(75, await BatchAsync(api, unit));
        Assert.Equal(50, await BatchAsync(worker, unit));
        time.Advance(TimeSpan.FromSeconds(30));
        await WaitForAsync(async () => await BatchAsync(worker, unit) == 75);
        await worker.GetRequiredService<SettingsRefresher>().StopAsync(ct);
    }

    [Fact]
    public async Task AFailedRefreshKeepsTheLastValuesAndTheAlertComesAfterFiveMinutes()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var unit = Ids.New();
        var logs = new CapturingLogs();
        // A process whose database stops answering: a port nothing listens on, with a short timeout.
        await using var provider = fixture.BuildServices(time, logs: logs, connectionString: "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1");
        var cache = provider.GetRequiredService<SettingsCache>();
        cache.Replace(new SettingsSnapshot([new StoredValue(ProbeSettings.Batch.Key, SettingLevel.Unit, unit, Json("90"), Ids.New(), time.GetUtcNow())], time.GetUtcNow()));
        var refresher = provider.GetRequiredService<SettingsRefresher>();
        using var stale = new Gauge(SettingsMetrics.MeterName, "settings.stale");

        Assert.False(await refresher.RefreshOnceAsync(ct));
        Assert.Equal(90, await BatchAsync(provider, unit, withTree: false));
        Assert.DoesNotContain(logs.Lines, l => l.Level == LogLevel.Error && l.Category == typeof(SettingsRefresher).FullName);
        Assert.Equal(0, stale.Read());
        Assert.Equal(HealthStatus.Healthy, (await HealthAsync(provider)).Status);

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.False(await refresher.RefreshOnceAsync(ct));

        Assert.Equal(90, await BatchAsync(provider, unit, withTree: false));
        var alert = Assert.Single(logs.Lines, l => l.Level == LogLevel.Error && l.Category == typeof(SettingsRefresher).FullName);
        Assert.StartsWith("ALERT: the settings have not refreshed since", alert.Message, StringComparison.Ordinal);
        Assert.Equal(1, stale.Read());
        var health = await HealthAsync(provider);
        Assert.Equal(HealthStatus.Degraded, health.Status);
        Assert.Contains("the values read at", health.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUnitTwoLevelsUpAppliesToAPersonInTheUnitBelowItAndTheirOwnValueWins()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var people = new TestPrincipals();
        await using var provider = fixture.BuildServices(time, people);
        var region = await SettingsFixture.UnitAsync(provider, "Region");
        var branch = await SettingsFixture.UnitAsync(provider, "Branch", region);
        var desk = await SettingsFixture.UnitAsync(provider, "Desk", branch);
        var ada = people.Add("Ada", desk);
        var tenantWide = people.Add("Grace", null);

        await using var scope = provider.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<SettingsWriter>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettings>();
        Assert.True((await writer.SetAsync(ProbeSettings.Tone.Key, SettingPlace.Tenant, Json("\"warm\""), ct)).IsSuccess);
        Assert.True((await writer.SetAsync(ProbeSettings.Tone.Key, SettingPlace.Unit(region), Json("\"brief\""), ct)).IsSuccess);

        var resolved = await settings.ResolveAsync(ProbeSettings.Tone, SettingContext.ForPerson(ada), ct);
        Assert.Equal(("brief", SettingLevel.Unit, region), (resolved.Value.GetString(), resolved.Level, resolved.UnitId));
        Assert.Equal("warm", await settings.GetAsync(ProbeSettings.Tone, SettingContext.ForPerson(tenantWide), ct));
        Assert.Equal("brief", await settings.GetAsync(ProbeSettings.Tone, SettingContext.ForUnit(desk), ct));

        Assert.True((await writer.SetAsync(ProbeSettings.Tone.Key, SettingPlace.User(ada), Json("\"plain\""), ct)).IsSuccess);
        Assert.Equal("plain", await settings.GetAsync(ProbeSettings.Tone, SettingContext.ForPerson(ada), ct));
        // A record of the desk's still takes the unit's value: a person's own value is theirs alone.
        Assert.Equal("brief", await settings.GetAsync(ProbeSettings.Tone, SettingContext.ForUnit(desk), ct));

        Assert.True((await writer.ResetAsync(ProbeSettings.Tone.Key, SettingPlace.Unit(region), ct)).IsSuccess);
        Assert.Equal("warm", await settings.GetAsync(ProbeSettings.Tone, SettingContext.ForUnit(desk), ct));
        Assert.True((await writer.ResetAsync(ProbeSettings.Tone.Key, SettingPlace.Tenant, ct)).IsSuccess);
    }

    [Fact]
    public async Task APersonsSettingsFollowTheUnitTheyWorkForAndAnotherPersonsTheirHomeUnit()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var people = new TestPrincipals();
        var actor = new FixedPrincipal(Ids.New());
        await using var provider = fixture.BuildServices(time, people, actor);
        var region = await SettingsFixture.UnitAsync(provider, "Region");
        var branch = await SettingsFixture.UnitAsync(provider, "Branch", region);
        var subsidiary = await SettingsFixture.UnitAsync(provider, "Subsidiary");
        var ada = people.Add("Ada", branch);
        var grace = people.Add("Grace", branch);
        actor.Id = ada;

        await using var scope = provider.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<SettingsWriter>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettings>();
        var set = await writer.SetAsync(ProbeSettings.Tone.Key, SettingPlace.Unit(region), Json("\"brief\""), ct);
        Assert.True(set.IsSuccess, set.IsFailure ? set.Error.Message : null);
        set = await writer.SetAsync(ProbeSettings.Tone.Key, SettingPlace.Unit(subsidiary), Json("\"warm\""), ct);
        Assert.True(set.IsSuccess, set.IsFailure ? set.Error.Message : null);

        var atHome = await settings.GetAsync(ProbeSettings.Tone, SettingContext.ForPerson(ada), ct);
        actor.WorkingUnitId = subsidiary;
        var atWork = await settings.GetAsync(ProbeSettings.Tone, SettingContext.ForPerson(ada), ct);
        var colleague = await settings.GetAsync(ProbeSettings.Tone, SettingContext.ForPerson(grace), ct);

        Assert.Equal(("brief", "warm", "brief"), (atHome, atWork, colleague));
        Assert.True((await writer.ResetAsync(ProbeSettings.Tone.Key, SettingPlace.Unit(region), ct)).IsSuccess);
        Assert.True((await writer.ResetAsync(ProbeSettings.Tone.Key, SettingPlace.Unit(subsidiary), ct)).IsSuccess);
    }

    [Fact]
    public async Task ASecretReferenceStoresAPathTheSecretIsReadFromTheStoreAndKeptFiveMinutesAtMost()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new CountingStore();
        store.Put("tenants/echo/settings/mail", "first secret");
        await using var provider = fixture.BuildServices(time, store: store);

        await using var scope = provider.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<SettingsWriter>();
        var outside = await writer.SetAsync(ProbeSettings.MailPassword.Key, SettingPlace.Tenant, Json("\"tenants/acme/settings/mail\""), ct);
        Assert.Equal(("settings.value.invalid", "A path under tenants/echo/settings/, the part of the store this unit reads."), (outside.Error.Code, outside.Error.Fields.Single().Message));
        Assert.True((await writer.SetAsync(ProbeSettings.MailPassword.Key, SettingPlace.Tenant, Json("\"tenants/echo/settings/mail\""), ct)).IsSuccess);

        var secrets = scope.ServiceProvider.GetRequiredService<ISecretSettings>();
        Assert.Equal("first secret", await secrets.ReadSecretAsync(ProbeSettings.MailPassword, SettingContext.Tenant, ct));
        store.Put("tenants/echo/settings/mail", "rotated secret");
        Assert.Equal("first secret", await secrets.ReadSecretAsync(ProbeSettings.MailPassword, SettingContext.Tenant, ct));
        Assert.Equal(1, store.Reads);

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("rotated secret", await secrets.ReadSecretAsync(ProbeSettings.MailPassword, SettingContext.Tenant, ct));
        Assert.Equal(2, store.Reads);

        // The reference is all the settings hold: the service answers the path, and no row or event holds a secret.
        Assert.Equal("tenants/echo/settings/mail", (await scope.ServiceProvider.GetRequiredService<ISettings>().GetAsync(ProbeSettings.MailPassword, SettingContext.Tenant, ct))!.Path);
        var stored = await scope.ServiceProvider.GetRequiredService<SettingsDbContext>().Values.AsNoTracking().Where(v => v.Key == ProbeSettings.MailPassword.Key).Select(v => v.ValueJson).ToListAsync(ct);
        var history = await scope.ServiceProvider.GetRequiredService<SettingsDbContext>().History.AsNoTracking().Where(v => v.Key == ProbeSettings.MailPassword.Key).Select(v => v.After).ToListAsync(ct);
        var audited = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking().Where(e => e.Target == ProbeSettings.MailPassword.Key).Select(e => e.Details).ToListAsync(ct);
        Assert.All(stored.Concat(history).Concat(audited), text => Assert.DoesNotContain("secret", text ?? string.Empty, StringComparison.Ordinal));
        Assert.True((await writer.ResetAsync(ProbeSettings.MailPassword.Key, SettingPlace.Tenant, ct)).IsSuccess);
    }

    private static async Task StartAsync(IServiceProvider provider) => await provider.GetRequiredService<SettingsRefresher>().StartAsync(TestContext.Current.CancellationToken);

    private static async Task<int> BatchAsync(IServiceProvider provider, Guid unit, bool withTree = true)
    {
        await using var scope = provider.CreateAsyncScope();
        if (!withTree)
        {
            // Without a database the tree cannot be walked: ask for the unit's own value through the cache alone.
            var values = scope.ServiceProvider.GetRequiredService<SettingsCache>().Current!.ValuesOf(ProbeSettings.Batch.Key);
            return ProbeSettings.Batch.Read(SettingResolution.Resolve(ProbeSettings.Batch, values, null, [unit]).Value);
        }

        return await scope.ServiceProvider.GetRequiredService<ISettings>().GetAsync(ProbeSettings.Batch, SettingContext.ForUnit(unit), TestContext.Current.CancellationToken);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Fail("The other process did not see the change after its refresh.");
    }

    private static async Task<HealthReportEntry> HealthAsync(IServiceProvider provider)
    {
        var check = ActivatorUtilities.CreateInstance<SettingsHealthCheck>(provider);
        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
        return new HealthReportEntry(result.Status, result.Description, TimeSpan.Zero, result.Exception, result.Data);
    }

    private static string? Compact(string? json) => json is null ? null : JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Reads one observable gauge of a meter, as a metrics exporter would.</summary>
    private sealed class Gauge : IDisposable
    {
        private readonly MeterListener _listener = new();
        private int _value;

        public Gauge(string meter, string name)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == meter && instrument.Name == name)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<int>((_, value, _, _) => _value = value);
            _listener.Start();
        }

        public int Read()
        {
            _listener.RecordObservableInstruments();
            return _value;
        }

        public void Dispose() => _listener.Dispose();
    }
}
