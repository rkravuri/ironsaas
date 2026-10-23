namespace Iron.Architecture.Tests;

/// <summary>
/// Operational logs are separate from the audit trail (TF-069, ADR-0050): the log store deletes lines after 30 days, and
/// nothing in the telemetry pipeline or its tools can reach the store that holds audit events.
/// </summary>
public class OperationalLogsTests
{
    private static readonly string Telemetry = Path.Combine(Repository.Root, "infra", "compose", "telemetry");

    [Fact]
    [Proves("TF-069 AC2", "ADR-0050")]
    public void TheLogStoreDeletesLinesAfterThirtyDays()
    {
        var loki = File.ReadAllLines(Path.Combine(Telemetry, "loki.yaml")).Select(line => line.Trim()).ToList();

        Assert.Contains("retention_period: 720h", loki);
        Assert.Contains("retention_enabled: true", loki);
    }

    [Fact]
    [Proves("TF-069 AC4", "ADR-0050")]
    public void TheLogToolsHaveNoWayToTheAuditStore()
    {
        // The tools a developer reads logs with: Grafana's data sources and the collector's exporters. None is a database.
        var sources = File.ReadAllLines(Path.Combine(Telemetry, "grafana", "provisioning", "datasources", "datasources.yaml"))
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("type:", StringComparison.Ordinal))
            .Select(line => line["type:".Length..].Trim())
            .ToList();
        Assert.Equal(["prometheus", "loki", "tempo"], sources);

        var collector = File.ReadAllText(Path.Combine(Telemetry, "otel-collector.yaml"));
        Assert.DoesNotContain("postgres", collector, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sqlquery", collector, StringComparison.OrdinalIgnoreCase);

        // The logging code itself references no module that holds audit events or opens a database.
        var telemetry = ProjectCatalog.Load(Repository.Root).Single(project => project.Name == "Iron.Kernel.Telemetry");
        Assert.DoesNotContain(telemetry.References, name => name.StartsWith("Iron.Kernel.Audit", StringComparison.Ordinal) || name == "Iron.Kernel.Data");
        var assembly = typeof(Iron.Kernel.Telemetry.Scrubbing.LinkScrubber).Assembly.GetReferencedAssemblies().Select(name => name.Name).ToList();
        Assert.DoesNotContain(assembly, name => name is "Iron.Kernel.Audit" or "Iron.Kernel.Audit.Contracts" or "Iron.Kernel.Data" or "Npgsql");
    }
}
