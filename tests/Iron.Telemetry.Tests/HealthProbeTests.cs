using Iron.Telemetry.Health;

namespace Iron.Telemetry.Tests;

[Collection(TelemetryCollection.Name)]
public class HealthProbeTests(TelemetryFixture fixture)
{
    [Fact]
    public void RecognisesTheHealthCommandOnly()
    {
        Assert.True(HealthProbe.IsCommand(["health"]));
        Assert.True(HealthProbe.IsCommand(["health", "http://localhost:5100/health/ready"]));
        Assert.False(HealthProbe.IsCommand([]));
        Assert.False(HealthProbe.IsCommand(["migrate"]));
    }

    [Fact]
    public async Task ExitsZeroWhenTheProbeAnswersOk()
    {
        Assert.Equal(0, await HealthProbe.RunAsync(["health", "/health/live"], fixture.Client));
    }

    [Fact]
    public async Task ExitsOneWhenTheProbeIsNotReadyOrUnreachable()
    {
        Assert.Equal(1, await HealthProbe.RunAsync(["health", "/health/ready"], fixture.Client));
        Assert.Equal(1, await HealthProbe.RunAsync(["health", "/no/such/probe"], fixture.Client));
    }
}
