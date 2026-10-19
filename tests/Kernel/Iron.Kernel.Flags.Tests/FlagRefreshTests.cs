using System.Diagnostics.Metrics;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Flags.Contracts;
using Iron.Kernel.Flags.Data;
using Iron.Kernel.Flags.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using OpenFeature.Constant;

namespace Iron.Kernel.Flags.Tests;

/// <summary>
/// ADR-0035 in a unit: the rules read every 30 seconds, last known rules before code defaults (a kill switch cannot switch
/// itself off in a store outage, nor after a restart during one), the code defaults with the alert only when nothing is
/// known, evaluation through OpenFeature, and a misconfigured provider stopping the host.
/// </summary>
[Collection(FlagsCollection.Name)]
public class FlagRefreshTests(FlagsFixture fixture)
{
    private static readonly string Imports = KillSwitches.Imports.Key;

    [Fact]
    public async Task TheUnitReadsBothPathsEveryThirtySecondsAndLearnsAChangeWithoutARestart()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = new ControlledSource { Rules = Rules.Set(1, []) };
        await using var provider = fixture.BuildServices(time, source, SystemPrincipal.Instance);
        var refresher = provider.GetRequiredService<FlagRefresher>();

        await refresher.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.False(await BooleanAsync(provider, Imports));

            // The operator turns the kill switch on: one write to platform/flags, read within thirty seconds.
            source.Rules = Rules.Set(2, [Rules.Global(Imports, true, reason: "A bad import file is going round.")]);
            time.Advance(TimeSpan.FromSeconds(29));
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.Equal(1, source.Reads);

            time.Advance(TimeSpan.FromSeconds(1));
            await Until(() => source.Reads >= 2);
            await Until(() => BooleanAsync(provider, Imports).GetAwaiter().GetResult());
        }
        finally
        {
            await refresher.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task LastKnownRulesStandThroughAnOutageOfDaysAndARestartDuringIt()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = new ControlledSource { Rules = Rules.Set(3, [Rules.Global(Imports, true, reason: "Imports wait for a fix.")], 4, [Rules.Tenant(ExampleFlags.Tone.Key, "echo", "warm")]) };
        await using (var running = fixture.BuildServices(time, source, SystemPrincipal.Instance))
        {
            Assert.True(await running.GetRequiredService<FlagRefresher>().RefreshOnceAsync(TestContext.Current.CancellationToken));
            source.Fails = true;
            time.Advance(TimeSpan.FromDays(3));
            Assert.False(await running.GetRequiredService<FlagRefresher>().RefreshOnceAsync(TestContext.Current.CancellationToken));
            Assert.True(await BooleanAsync(running, Imports));
            var degraded = await ReadinessAsync(running);
            Assert.Equal(HealthStatus.Degraded, degraded.Status);
            Assert.Contains("stand until it does", degraded.Description, StringComparison.Ordinal);
        }

        // A new process while the store is still down: the last known rules, not the code defaults.
        var logs = new CapturingLogs();
        await using var restarted = fixture.BuildServices(time, source, SystemPrincipal.Instance, logs);
        var refresher = restarted.GetRequiredService<FlagRefresher>();
        await refresher.StartAsync(TestContext.Current.CancellationToken);
        await refresher.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(await BooleanAsync(restarted, Imports));
        Assert.Equal("warm", await StringAsync(restarted, ExampleFlags.Tone.Key));
        Assert.DoesNotContain(logs.Lines, line => line.Level == LogLevel.Error);
    }

    [Fact]
    public async Task OnlyWhenNothingIsKnownDoTheCodeDefaultsApplyAndTheAlertIsRaised()
    {
        await ForgetAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logs = new CapturingLogs();
        var source = new ControlledSource { Fails = true };
        await using var provider = fixture.BuildServices(time, source, SystemPrincipal.Instance, logs);
        using var unknown = new Gauge(FlagsMetrics.MeterName, "flags.unknown");
        var refresher = provider.GetRequiredService<FlagRefresher>();

        await refresher.StartAsync(TestContext.Current.CancellationToken);
        await refresher.StopAsync(TestContext.Current.CancellationToken);

        Assert.False(await BooleanAsync(provider, Imports));
        Assert.Equal("plain", await StringAsync(provider, ExampleFlags.Tone.Key));
        await using (var scope = provider.CreateAsyncScope())
        {
            var client = await scope.ServiceProvider.GetRequiredService<IFlagClients>().CurrentAsync(TestContext.Current.CancellationToken);
            Assert.Equal(Reason.Unknown, (await client.GetBooleanDetailsAsync(Imports, false, null, null, TestContext.Current.CancellationToken)).Reason);
        }

        Assert.Contains(logs.Lines, line => line.Level == LogLevel.Error && line.Message.StartsWith("ALERT: no flag rules are known", StringComparison.Ordinal));
        Assert.Equal(1, unknown.Read());
        var readiness = await ReadinessAsync(provider);
        Assert.Equal(HealthStatus.Degraded, readiness.Status);
        Assert.Contains("every flag has its code default", readiness.Description, StringComparison.Ordinal);

        // The store answers: the rules are known, nothing alerts, and an empty store is an answer too.
        source.Fails = false;
        Assert.True(await refresher.RefreshOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, unknown.Read());
        Assert.Equal(HealthStatus.Healthy, (await ReadinessAsync(provider)).Status);
    }

    [Fact]
    public async Task ProductCodeEvaluatesThroughOpenFeatureWithItsTypesAndAnUndeclaredFlagGetsTheCallersDefault()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = new ControlledSource { Rules = Rules.Set(5, [Rules.Global(ExampleFlags.Tone.Key, "warm"), Rules.Global(ExampleFlags.Release.Key, true)]) };
        await using var provider = fixture.BuildServices(time, source, SystemPrincipal.Instance);
        Assert.True(await provider.GetRequiredService<FlagRefresher>().RefreshOnceAsync(TestContext.Current.CancellationToken));

        await using var scope = provider.CreateAsyncScope();
        var client = await scope.ServiceProvider.GetRequiredService<IFlagClients>().CurrentAsync(TestContext.Current.CancellationToken);
        var tone = await client.GetStringDetailsAsync(ExampleFlags.Tone.Key, "plain", null, null, TestContext.Current.CancellationToken);
        var mismatch = await client.GetIntegerDetailsAsync(ExampleFlags.Tone.Key, 7, null, null, TestContext.Current.CancellationToken);
        var missing = await client.GetBooleanDetailsAsync("sample.not_declared", true, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(("warm", Reason.TargetingMatch), (tone.Value, tone.Reason));
        Assert.Equal((7, ErrorType.TypeMismatch), (mismatch.Value, mismatch.ErrorType));
        Assert.Equal((true, ErrorType.FlagNotFound), (missing.Value, missing.ErrorType));
        Assert.Equal("iron", provider.GetRequiredService<OpenFeatureBinding>().Provider.GetMetadata()?.Name);
    }

    [Theory]
    [InlineData("Flags:Provider", "unleash", "Flags:Provider is 'unleash', and this build has no such flag provider. The providers are: iron.")]
    [InlineData("Flags:Source", "store", "Flags:Source is 'store', but no secret store is configured")]
    [InlineData("Flags:Source", "database", "Flags:Source is 'database'. Use 'store' or 'configuration'.")]
    public void AMisconfiguredProviderStopsTheHostAtStartWithAClearMessage(string key, string value, string message)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
        var services = new ServiceCollection();

        var refused = Assert.Throws<InvalidOperationException>(() => services.AddModules(configuration, [new FlagsModule()]));

        Assert.StartsWith(message, refused.Message, StringComparison.Ordinal);
    }

    private async Task ForgetAsync()
    {
        await using var provider = fixture.BuildServices(new FakeTimeProvider(), new ControlledSource(), SystemPrincipal.Instance);
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<FlagsDbContext>().LastKnown.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<bool> BooleanAsync(IServiceProvider provider, string key)
    {
        await using var scope = provider.CreateAsyncScope();
        var client = await scope.ServiceProvider.GetRequiredService<IFlagClients>().CurrentAsync(TestContext.Current.CancellationToken);
        return await client.GetBooleanValueAsync(key, false, null, null, TestContext.Current.CancellationToken);
    }

    private static async Task<string> StringAsync(IServiceProvider provider, string key)
    {
        await using var scope = provider.CreateAsyncScope();
        var client = await scope.ServiceProvider.GetRequiredService<IFlagClients>().CurrentAsync(TestContext.Current.CancellationToken);
        return await client.GetStringValueAsync(key, "none", null, null, TestContext.Current.CancellationToken);
    }

    private static async Task<HealthReportEntry> ReadinessAsync(IServiceProvider provider)
    {
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(check => check.Name == "flags", TestContext.Current.CancellationToken);
        return report.Entries["flags"];
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    /// <summary>Reads an observable gauge as an exporter would.</summary>
    private sealed class Gauge : IDisposable
    {
        private readonly MeterListener _listener = new();
        private int _value = -1;

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
