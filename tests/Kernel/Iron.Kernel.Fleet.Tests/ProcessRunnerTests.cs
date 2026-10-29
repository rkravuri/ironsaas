using Iron.Kernel.Fleet.Services.Compose;

namespace Iron.Kernel.Fleet.Tests;

/// <summary>
/// The fleet's child processes (ADR-0055, V0): the Docker CLI and its Compose plugin start with the fleet's environment
/// but none of its telemetry settings, so neither exports over gRPC to the collector.
/// </summary>
public sealed class ProcessRunnerTests
{
    [Fact]
    [Proves("ADR-0055")]
    public async Task AChildProcessGetsNoneOfTheFleetsTelemetrySettingsAndTheRestOfItsEnvironment()
    {
        // Names no OpenTelemetry library reads, so setting them in this process changes nothing that runs beside the test;
        // the rule goes by prefix, and the second half proves the prefix covers the names the binaries read.
        var run = Guid.NewGuid().ToString("N").ToUpperInvariant();
        string[] telemetry = [$"OTEL_IRON_PROBE_{run}", $"DOCKER_CLI_OTEL_IRON_PROBE_{run}"];
        var kept = $"IRON_PROBE_{run}";
        string[] probes = [.. telemetry, kept];
        foreach (var name in probes)
        {
            Environment.SetEnvironmentVariable(name, "http://collector.test:4317");
        }

        try
        {
            var result = await new ProcessRunner().RunAsync("env", [], null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            var names = result.Output.Split('\n').Select(line => line.Split('=', 2)[0]).ToHashSet(StringComparer.Ordinal);
            Assert.Contains(kept, names);
            Assert.DoesNotContain(names, ProcessRunner.IsTelemetrySetting);
        }
        finally
        {
            foreach (var name in probes)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        // What the docker and docker-compose binaries of the fleet image name (strings, docker:29.9.2-cli), and the
        // SDK's own settings, are telemetry settings; what Docker needs to reach the daemon is not.
        Assert.All(
            ["OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", "DOCKER_CLI_OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_SERVICE_NAME", "OTEL_RESOURCE_ATTRIBUTES"],
            name => Assert.True(ProcessRunner.IsTelemetrySetting(name), name));
        Assert.All(["DOCKER_HOST", "DOCKER_CONFIG", "PATH", "HOME", "COMPOSE_PROJECT_NAME"], name => Assert.False(ProcessRunner.IsTelemetrySetting(name), name));
    }
}
