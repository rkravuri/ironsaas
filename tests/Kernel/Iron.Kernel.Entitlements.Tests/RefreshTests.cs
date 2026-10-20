using System.Diagnostics.Metrics;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Entitlements.Contracts;
using Iron.Kernel.Entitlements.Data;
using Iron.Kernel.Entitlements.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Entitlements.Tests;

/// <summary>
/// TF-043: the unit reads its entitlements on a short interval, keeps the last known answer for a restart, and after
/// 24 hours without an answer treats everything licensed as disabled and raises the alert (log, metric, readiness).
/// </summary>
[Collection(EntitlementsCollection.Name)]
public class RefreshTests(EntitlementsFixture fixture)
{
    private static readonly string RoleTransfer = AuthorizationFeatures.RoleTransfer.Code;

    [Fact]
    [Proves("ADR-0034")]
    public async Task TheUnitReadsTheStoreEveryThirtySecondsAndLearnsAChangeWithoutARestart()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = new ControlledSource { Document = Documents.With(1, features: [Documents.Feature(RoleTransfer)]) };
        await using var provider = fixture.BuildServices(time, source, SystemPrincipal.Instance);
        var refresher = provider.GetRequiredService<EntitlementRefresher>();

        await refresher.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(1, source.Reads);
            Assert.Equal(EntitlementStatus.Enabled, Feature(provider, RoleTransfer).Status);

            // The plan changed in the control plane: the next read, thirty seconds on, brings it.
            source.Document = Documents.With(2);
            time.Advance(TimeSpan.FromSeconds(29));
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.Equal(1, source.Reads);

            time.Advance(TimeSpan.FromSeconds(1));
            await Until(() => source.Reads >= 2);
            await Until(() => Feature(provider, RoleTransfer).Status == EntitlementStatus.Disabled);
            Assert.Equal(new Entitlement(EntitlementStatus.Disabled, EntitlementReasons.NotLicensed), Feature(provider, RoleTransfer));
        }
        finally
        {
            await refresher.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    [Proves("TF-043 AC3", "ADR-0034")]
    public async Task TheLastKnownAnswerStandsFor24HoursThenEverythingLicensedIsDisabledAndTheAlertIsRaised()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = new ControlledSource { Document = Documents.With(3, [Documents.Fixture()], [Documents.Feature(RoleTransfer)]) };
        var logs = new CapturingLogs();
        await using var provider = fixture.BuildServices(time, source, SystemPrincipal.Instance, logs);
        var refresher = provider.GetRequiredService<EntitlementRefresher>();
        using var stale = new StaleGauge();

        Assert.True(await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HealthStatus.Healthy, (await Readiness(provider)).Status);

        source.Fails = true;
        time.Advance(TimeSpan.FromHours(23));
        Assert.False(await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(EntitlementStatus.Enabled, Feature(provider, RoleTransfer).Status);
        Assert.Equal(EntitlementStatus.Enabled, Module(provider, FixtureModule.Licence.Code).Status);
        var degraded = await Readiness(provider);
        Assert.Equal(HealthStatus.Degraded, degraded.Status);
        Assert.Contains("has not answered since", degraded.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Lines, line => line.Level == LogLevel.Error);
        Assert.Equal(0, stale.Read());

        time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        Assert.False(await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new Entitlement(EntitlementStatus.Disabled, EntitlementReasons.Unknown), Feature(provider, RoleTransfer));
        Assert.Equal(new Entitlement(EntitlementStatus.Disabled, EntitlementReasons.Unknown), Module(provider, FixtureModule.Licence.Code));
        var unknown = await Readiness(provider);
        Assert.Equal(HealthStatus.Degraded, unknown.Status);
        Assert.Contains("The entitlements are unknown", unknown.Description, StringComparison.Ordinal);
        Assert.Contains(logs.Lines, line => line.Level == LogLevel.Error && line.Message.StartsWith("ALERT: the entitlements are unknown", StringComparison.Ordinal));
        Assert.Equal(1, stale.Read());

        // The store answers again: the alert ends and the answer is fresh.
        source.Fails = false;
        Assert.True(await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(EntitlementStatus.Enabled, Feature(provider, RoleTransfer).Status);
        Assert.Equal(HealthStatus.Healthy, (await Readiness(provider)).Status);
    }

    [Fact]
    [Proves("ADR-0034")]
    public async Task ARestartWhileTheStoreIsDownKnowsTheLastAnswerUntilItIs24HoursOld()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var before = new ControlledSource { Document = Documents.With(5, features: [Documents.Feature(RoleTransfer)]) };
        await using (var running = fixture.BuildServices(time, before, SystemPrincipal.Instance))
        {
            Assert.True(await running.GetRequiredService<EntitlementRefresher>().RefreshOnceAsync(TestContext.Current.CancellationToken));
        }

        // A new process: no answer in memory, and the store does not answer.
        time.Advance(TimeSpan.FromHours(2));
        var down = new ControlledSource { Fails = true };
        await using var restarted = fixture.BuildServices(time, down, SystemPrincipal.Instance);
        var refresher = restarted.GetRequiredService<EntitlementRefresher>();
        await refresher.StartAsync(TestContext.Current.CancellationToken);
        await refresher.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, down.Reads);
        Assert.Equal(new Entitlement(EntitlementStatus.Enabled, EntitlementReasons.Licensed), Feature(restarted, RoleTransfer));
        Assert.Equal(5, restarted.GetRequiredService<EntitlementCache>().Current!.Document.Revision);

        time.Advance(TimeSpan.FromHours(22) + TimeSpan.FromMinutes(1));
        Assert.Equal(EntitlementReasons.Unknown, Feature(restarted, RoleTransfer).Reason);
    }

    [Fact]
    [Proves("ADR-0034")]
    public async Task NothingPublishedMeansNothingLicensedAndRaisesNoAlert()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var provider = fixture.BuildServices(time, new ControlledSource { Document = null }, SystemPrincipal.Instance);

        Assert.True(await provider.GetRequiredService<EntitlementRefresher>().RefreshOnceAsync(TestContext.Current.CancellationToken));

        Assert.Equal(new Entitlement(EntitlementStatus.Disabled, EntitlementReasons.NotLicensed), Feature(provider, RoleTransfer));
        Assert.Equal(HealthStatus.Healthy, (await Readiness(provider)).Status);
    }

    private static Entitlement Feature(IServiceProvider provider, string code) => Ask(provider, entitlements => entitlements.Feature(code));

    private static Entitlement Module(IServiceProvider provider, string code) => Ask(provider, entitlements => entitlements.Module(code));

    /// <summary>The evaluator is scoped; its scope holds a unit of work, which only disposes asynchronously.</summary>
    private static Entitlement Ask(IServiceProvider provider, Func<IEntitlements, Entitlement> question)
    {
        var scope = provider.CreateAsyncScope();
        try
        {
            return question(scope.ServiceProvider.GetRequiredService<IEntitlements>());
        }
        finally
        {
            scope.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static async Task<HealthReportEntry> Readiness(IServiceProvider provider)
    {
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(check => check.Name == "entitlements", TestContext.Current.CancellationToken);
        return report.Entries["entitlements"];
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    /// <summary>Reads the <c>entitlements.stale</c> gauge as an exporter would.</summary>
    private sealed class StaleGauge : IDisposable
    {
        private readonly MeterListener _listener = new();
        private int _value = -1;

        public StaleGauge()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == EntitlementsMetrics.MeterName && instrument.Name == "entitlements.stale")
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

    [Fact]
    [Proves("ADR-0034")]
    public async Task TwoWritersAtOnceKeepOneLastKnownRowAndNeitherFails()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var round = 1; round <= 3; round++)
        {
            await using (var forget = fixture.BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow), new ControlledSource(), SystemPrincipal.Instance))
            await using (var scope = forget.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<EntitlementsDbContext>().LastKnown.ExecuteDeleteAsync(ct);
            }

            var logs = new CapturingLogs();
            var source = new ControlledSource { Document = Documents.With(70 + round) };
            await using var api = fixture.BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow), source, SystemPrincipal.Instance, logs);
            await using var worker = fixture.BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow), source, SystemPrincipal.Instance, logs);
            EntitlementRefresher[] writers = [api.GetRequiredService<EntitlementRefresher>(), worker.GetRequiredService<EntitlementRefresher>()];

            // A unit's API and worker on its first start: both read the store and both keep the answer, at the same moment.
            var read = await Task.WhenAll(writers.Select(writer => Task.Run(() => writer.RefreshOnceAsync(ct), ct)));

            Assert.All(read, Assert.True);
            Assert.DoesNotContain(logs.Lines, line => line.Level >= LogLevel.Error || line.Message.Contains("could not be kept", StringComparison.Ordinal));
            await using var check = api.CreateAsyncScope();
            var row = Assert.Single(await check.ServiceProvider.GetRequiredService<EntitlementsDbContext>().LastKnown.AsNoTracking().ToListAsync(ct));
            Assert.Equal(70 + round, row.Revision);
        }
    }
}
