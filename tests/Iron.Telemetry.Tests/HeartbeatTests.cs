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

        // Export happens when the activity stops; give it a moment, as the request test does.
        List<System.Diagnostics.Activity> beats = [];
        for (var attempt = 0; attempt < 40 && beats.Count == 0; attempt++)
        {
            beats = fixture.ExportedSpans.Where(s => s.OperationName == HeartbeatService.ActivityName).ToList();
            if (beats.Count == 0)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }

        Assert.NotEmpty(beats);
        Assert.All(beats, beat => Assert.Equal("acme", beat.GetTagItem("tenant.id")));
        Assert.All(beats, beat => Assert.Null(beat.Parent));
    }
}
