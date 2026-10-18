using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Iron.Fleet.Services.Compose;

/// <summary>
/// The laptop runtime (ADR-0002, ADR-0019): every unit is a Compose project <c>tenant-&lt;slug&gt;</c> rendered from one
/// template into the fleet's units directory, applied with <c>docker compose up --wait</c>, routed by Traefik through its
/// labels, and checked through the ingress. The image commands run with <c>docker run</c> on the platform network.
/// </summary>
public sealed class ComposeUnitRuntime(IProcessRunner processes, HttpClient http, FleetOptions options, ILogger<ComposeUnitRuntime> logger) : IUnitRuntime
{
    public const string TemplateResource = "Iron.Fleet.Services.Compose.unit.compose.yaml";

    public static string ProjectName(string slug) => $"tenant-{slug}";

    public async Task<CommandResult> RunCommandAsync(UnitDefinition unit, IReadOnlyList<string> command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(command);
        var units = options.Units;
        var envFile = Path.Combine(Path.GetTempPath(), $"iron-{unit.Tenant.Slug}-{Guid.NewGuid():N}.env");
        await File.WriteAllTextAsync(envFile, EnvFile(unit.Environment), cancellationToken).ConfigureAwait(false);
        try
        {
            var arguments = new List<string> { "run", "--rm", "--network", units.PlatformNetwork, "--env-file", envFile, $"{units.ImageRepository}/iron-api:{unit.ImageTag}" };
            arguments.AddRange(command);
            logger.LogInformation("Unit {Slug}: docker run iron-api:{Tag} {Command}", unit.Tenant.Slug, unit.ImageTag, string.Join(' ', command));
            var result = await processes.RunAsync("docker", arguments, null, TimeSpan.FromSeconds(units.CommandTimeoutSeconds), cancellationToken).ConfigureAwait(false);
            return new CommandResult(result.ExitCode, result.Output);
        }
        finally
        {
            File.Delete(envFile);
        }
    }

    public async Task ApplyAsync(UnitDefinition unit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var directory = Render(unit);
        var result = await ComposeAsync(unit, directory, ["up", "--detach", "--wait", "--remove-orphans"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker compose up failed with {result.ExitCode}: {Tail(result.Output)}");
        }

        logger.LogInformation("Unit {Slug}: compose project {Project} is up.", unit.Tenant.Slug, ProjectName(unit.Tenant.Slug));
    }

    public async Task<string?> CheckReadyAsync(UnitDefinition unit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var hostname = unit.Tenant.Hostnames[0];
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.Units.IngressUrl.TrimEnd('/') + "/health/ready"));
            request.Headers.Host = hostname;
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return $"{hostname} answered {(int)response.StatusCode} on /health/ready: {Tail(body)}";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return $"{hostname} did not answer through the ingress: {exception.Message}";
        }
    }

    public async Task RemoveAsync(UnitDefinition unit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var directory = Render(unit);
        var result = await ComposeAsync(unit, directory, ["down", "--remove-orphans"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker compose down failed with {result.ExitCode}: {Tail(result.Output)}");
        }

        logger.LogInformation("Unit {Slug}: compose project {Project} is down; data kept.", unit.Tenant.Slug, ProjectName(unit.Tenant.Slug));
    }

    /// <summary>Writes the project directory: the template as is, and the .env with this unit's values. Returns the directory.</summary>
    public string Render(UnitDefinition unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var directory = Path.Combine(options.Units.ComposeDirectory, unit.Tenant.Slug);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "compose.yaml"), Template());
        File.WriteAllText(Path.Combine(directory, ".env"), EnvFile(Variables(unit)));
        return directory;
    }

    /// <summary>The variables of the template for this unit: the image, the limits, the hostnames and the unit's environment.</summary>
    public IReadOnlyDictionary<string, string> Variables(UnitDefinition unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var units = options.Units;
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TENANT"] = unit.Tenant.Slug,
            ["IMAGE_REPOSITORY"] = units.ImageRepository,
            ["IMAGE_TAG"] = unit.ImageTag,
            ["PLATFORM_NETWORK"] = units.PlatformNetwork,
            ["HOSTNAMES"] = string.Join(',', unit.Tenant.Hostnames),
            ["HOST_RULE"] = string.Join(" || ", unit.Tenant.Hostnames.Select(h => $"Host(`{h}`)")),
            ["API_CPUS"] = unit.Limits.ApiCpus.ToString("0.##", CultureInfo.InvariantCulture),
            ["API_MEMORY_MB"] = unit.Limits.ApiMemoryMb.ToString(CultureInfo.InvariantCulture),
            ["WORKER_CPUS"] = unit.Limits.WorkerCpus.ToString("0.##", CultureInfo.InvariantCulture),
            ["WORKER_MEMORY_MB"] = unit.Limits.WorkerMemoryMb.ToString(CultureInfo.InvariantCulture),
            ["SECRETS_ADDRESS"] = unit.Environment.GetValueOrDefault("Secrets__Address", string.Empty),
            ["SECRETS_TOKEN"] = unit.Environment.GetValueOrDefault("Secrets__Token", string.Empty),
            ["SECRETS_MOUNT"] = unit.Environment.GetValueOrDefault("Secrets__Mount", "secret"),
            ["SECRETS_PATH"] = unit.Environment.GetValueOrDefault("Secrets__Path", string.Empty),
            ["OTLP_ENDPOINT"] = unit.Environment.GetValueOrDefault("OTEL_EXPORTER_OTLP_ENDPOINT", units.OtlpEndpoint),
        };
        return variables;
    }

    public static string Template()
    {
        using var stream = typeof(ComposeUnitRuntime).Assembly.GetManifestResourceStream(TemplateResource)
            ?? throw new InvalidOperationException($"The unit template '{TemplateResource}' is not embedded in the fleet assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>KEY=VALUE lines. Values must not contain line breaks; a value with one is refused rather than corrupted.</summary>
    public static string EnvFile(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var builder = new StringBuilder();
        foreach (var (key, value) in values.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            if (value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The value of {key} has a line break and cannot go into an env file.");
            }

            builder.Append(key).Append('=').Append(value).Append('\n');
        }

        return builder.ToString();
    }

    private async Task<ProcessResult> ComposeAsync(UnitDefinition unit, string directory, IReadOnlyList<string> verb, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "compose", "--project-name", ProjectName(unit.Tenant.Slug), "--file", Path.Combine(directory, "compose.yaml"), "--env-file", Path.Combine(directory, ".env") };
        arguments.AddRange(verb);
        logger.LogInformation("Unit {Slug}: docker {Arguments}", unit.Tenant.Slug, string.Join(' ', arguments));
        return await processes.RunAsync("docker", arguments, directory, TimeSpan.FromSeconds(options.Units.ComposeTimeoutSeconds), cancellationToken).ConfigureAwait(false);
    }

    private static string Tail(string output) => output.Length > 1500 ? output[^1500..] : output;
}
