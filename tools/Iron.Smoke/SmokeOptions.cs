namespace Iron.Smoke;

/// <summary>Everything from the environment, with the laptop's defaults. The Makefile sets the two secrets from infra/compose/.env.</summary>
public sealed record SmokeOptions(
    Uri ControlUrl,
    string OperatorClientId,
    string OperatorSecret,
    string ReaderClientId,
    string? ReaderSecret,
    string Tenant,
    string? AdministratorPassword,
    string BaseDomain,
    Uri IngressUrl,
    Uri StoreAddress,
    string StoreToken,
    Uri? TempoUrl,
    string ReportPath,
    TimeSpan ProvisionTimeout)
{
    public string Hostname => $"{Tenant}.{BaseDomain}";

    public string AdministratorLoginId => $"admin@{Hostname}";

    public static SmokeOptions FromEnvironment()
    {
        static string Get(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
        static string Require(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"{name} is required.");

        var tempo = Get("SMOKE_TEMPO_URL", "http://localhost:3200");
        return new SmokeOptions(
            new Uri(Get("SMOKE_CONTROL_URL", "http://localhost:5200")),
            Get("SMOKE_OPERATOR_CLIENT_ID", "operator"),
            Require("SMOKE_OPERATOR_SECRET"),
            Get("SMOKE_READER_CLIENT_ID", "smoke-reader"),
            Environment.GetEnvironmentVariable("SMOKE_READER_SECRET") is { Length: > 0 } reader ? reader : null,
            Get("SMOKE_TENANT", "smoke"),
            Environment.GetEnvironmentVariable("SMOKE_ADMIN_PASSWORD") is { Length: > 0 } adminPassword ? adminPassword : null,
            Get("SMOKE_BASE_DOMAIN", "localhost"),
            new Uri(Get("SMOKE_INGRESS_URL", "http://localhost")),
            new Uri(Get("SMOKE_STORE_ADDRESS", "http://localhost:8200")),
            Require("SMOKE_STORE_TOKEN"),
            string.Equals(tempo, "skip", StringComparison.OrdinalIgnoreCase) ? null : new Uri(tempo),
            Get("SMOKE_REPORT", "smoke-report.json"),
            TimeSpan.FromSeconds(int.Parse(Get("SMOKE_PROVISION_TIMEOUT_SECONDS", "900"), System.Globalization.CultureInfo.InvariantCulture)));
    }
}
