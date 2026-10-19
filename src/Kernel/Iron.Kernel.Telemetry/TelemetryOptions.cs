namespace Iron.Kernel.Telemetry;

/// <summary>Settings of the telemetry pipeline. Bound from the <c>Telemetry</c> configuration section; hosts override in code.</summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>The <c>service.name</c> resource attribute: <c>iron-api</c>, <c>iron-worker</c>, <c>iron-control-plane</c>, <c>iron-fleet</c>.</summary>
    public string ServiceName { get; set; } = "iron";

    /// <summary>The <c>tenant.id</c> resource attribute on every span, metric and log of the unit (TF-003, TF-130). <c>control</c> for the control plane.</summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Export to the collector over OTLP. The endpoint comes from the standard OTEL_EXPORTER_OTLP_ENDPOINT variable
    /// (default localhost:4317, which is the laptop collector). Tests switch this off.
    /// </summary>
    public bool ExportToCollector { get; set; } = true;

    /// <summary>Whether requests to /health/* produce traces. On during S1; probes are filtered when the fleet runs many units (S6).</summary>
    public bool TraceHealthEndpoints { get; set; } = true;
}
