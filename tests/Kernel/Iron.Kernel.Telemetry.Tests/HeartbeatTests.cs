using System.Diagnostics;
using Iron.Kernel.Telemetry.Heartbeat;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Iron.Kernel.Telemetry.Tests;

/// <summary>
/// The heartbeat with a tracer provider of its own, subscribed to Iron.* as AddIronTelemetry subscribes. Since .NET 10,
/// BackgroundService runs ExecuteAsync on the thread pool, so the first beat lands a moment after StartAsync returns;
/// the tests wait for it instead of racing it.
/// </summary>
public class HeartbeatTests
{
    [Fact]
    public async Task TheHeartbeatSpanCarriesTheTenantAndIsARootSpan()
    {
        var exported = new List<Activity>();
        using var tracing = Sdk.CreateTracerProviderBuilder().AddSource("Iron.*").AddInMemoryExporter(exported).Build();
        var time = new FakeTimeProvider();
        var options = new TelemetryOptions { ServiceName = "iron-worker-tests", TenantId = "acme" };
        using var service = new HeartbeatService(NullLogger<HeartbeatService>.Instance, options, new HeartbeatOptions { Interval = TimeSpan.FromSeconds(10) }, time);

        await service.StartAsync(TestContext.Current.CancellationToken);
        var beats = await WaitForBeatsAsync(exported, 1);
        time.Advance(TimeSpan.FromSeconds(10));
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(beats);
        Assert.All(beats, beat => Assert.Equal("acme", beat.GetTagItem("tenant.id")));
        Assert.All(beats, beat => Assert.Null(beat.Parent));
    }

    [Fact]
    public async Task AHeartbeatDoesNotJoinAnAmbientActivity()
    {
        var exported = new List<Activity>();
        using var tracing = Sdk.CreateTracerProviderBuilder().AddSource("Iron.*").AddInMemoryExporter(exported).Build();
        using var ambient = new Activity("something.else").Start();
        using var service = new HeartbeatService(NullLogger<HeartbeatService>.Instance, new TelemetryOptions { TenantId = "acme" }, new HeartbeatOptions { Interval = TimeSpan.FromHours(1) }, new FakeTimeProvider());

        await service.StartAsync(TestContext.Current.CancellationToken);
        var beat = Assert.Single(await WaitForBeatsAsync(exported, 1));
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Null(beat.Parent);
        Assert.NotEqual(ambient.TraceId, beat.TraceId);
        Assert.Same(ambient, Activity.Current);
    }

    private static async Task<List<Activity>> WaitForBeatsAsync(List<Activity> exported, int count)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            List<Activity> beats;
            lock (exported)
            {
                beats = exported.Where(s => s.OperationName == HeartbeatService.ActivityName).ToList();
            }

            if (beats.Count >= count)
            {
                return beats;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return [];
    }
}
