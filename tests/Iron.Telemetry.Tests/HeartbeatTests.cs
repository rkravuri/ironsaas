using Iron.Telemetry.Heartbeat;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Telemetry.Tests;

[Collection(TelemetryCollection.Name)]
public class HeartbeatTests(TelemetryFixture fixture)
{
    [Fact]
    public async Task TheHeartbeatSpanCarriesTheTenant()
    {
        var time = new FakeTimeProvider();
        var options = new TelemetryOptions { ServiceName = "iron-worker-tests", TenantId = "acme" };
        using var service = new HeartbeatService(NullLogger<HeartbeatService>.Instance, options, new HeartbeatOptions { Interval = TimeSpan.FromSeconds(10) }, time);

        await service.StartAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(10));
        await service.StopAsync(TestContext.Current.CancellationToken);

        var beats = fixture.ExportedSpans.Where(s => s.OperationName == HeartbeatService.ActivityName).ToList();
        Assert.NotEmpty(beats);
        Assert.All(beats, beat => Assert.Equal("acme", beat.GetTagItem("tenant.id")));
    }
}
