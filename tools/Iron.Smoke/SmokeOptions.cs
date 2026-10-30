namespace Iron.Smoke;

/// <summary>Everything from the environment, with the laptop's defaults. The Makefile sets the two secrets from infra/compose/.env.</summary>
public sealed record SmokeOptions(
    Uri ControlUrl,
    string OperatorClientId,
    string OperatorSecret,
    string ReaderClientId,
    string? ReaderSecret,
    string Tenant,
    string BaseDomain,
    Uri IngressUrl,
    Uri StoreAddress,
    string StoreToken,
    Uri? TempoUrl,
    Uri? AlertmanagerUrl,
    string ReportPath,
    TimeSpan ProvisionTimeout,
    bool SampleRecords = false,
    IReadOnlyList<string>? Suites = null)
{
    /// <summary>The suites a tenant the smoke registers starts with (TF-244), each under its only plan; none unless SMOKE_SUITES names them.</summary>
    public IReadOnlyList<string> NewTenantSuites => Suites ?? [];

    public string Hostname => $"{Tenant}.{BaseDomain}";

    public string AdministratorLoginId => $"admin@{Hostname}";

    public static SmokeOptions FromEnvironment()
    {
        static string Get(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
        static string Require(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"{name} is required.");

        var tempo = Get("SMOKE_TEMPO_URL", "http://localhost:3200");
        var alertmanager = Get("SMOKE_ALERTMANAGER_URL", "http://localhost:9093");
        return new SmokeOptions(
            new Uri(Get("SMOKE_CONTROL_URL", "http://localhost:5200")),
            Get("SMOKE_OPERATOR_CLIENT_ID", "operator"),
            Require("SMOKE_OPERATOR_SECRET"),
            Get("SMOKE_READER_CLIENT_ID", "smoke-reader"),
            Environment.GetEnvironmentVariable("SMOKE_READER_SECRET") is { Length: > 0 } reader ? reader : null,
            Get("SMOKE_TENANT", "smoke"),
            Get("SMOKE_BASE_DOMAIN", "localhost"),
            new Uri(Get("SMOKE_INGRESS_URL", "http://localhost")),
            new Uri(Get("SMOKE_STORE_ADDRESS", "http://localhost:8200")),
            Require("SMOKE_STORE_TOKEN"),
            string.Equals(tempo, "skip", StringComparison.OrdinalIgnoreCase) ? null : new Uri(tempo),
            string.Equals(alertmanager, "skip", StringComparison.OrdinalIgnoreCase) ? null : new Uri(alertmanager),
            Get("SMOKE_REPORT", "smoke-report.json"),
            TimeSpan.FromSeconds(int.Parse(Get("SMOKE_PROVISION_TIMEOUT_SECONDS", "900"), System.Globalization.CultureInfo.InvariantCulture)),

            // A tenant the smoke registers runs the sample module when this says so, as the release's does (ADR-0057).
            Get("SMOKE_SAMPLE_RECORDS", "0") == "1",

            // And the suites this names, as the release's tenant has the eProcurement Suite (ADR-0059).
            [.. Get("SMOKE_SUITES", string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]);
    }
}
