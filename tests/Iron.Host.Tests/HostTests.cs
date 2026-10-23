using System.Net;
using System.Text.Json;
using Iron.Kernel.Api.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Iron.Host.Tests;

/// <summary>
/// Every host starts with its real module list, which proves the start-up permission check passes (TF-030), stays
/// bound to its tenant (TF-003), fails closed when the database is not there (TF-031 AC4), and keeps the anonymous
/// allow-list fixed. The database settings point nowhere: no test here needs a database.
/// </summary>
public class HostTests
{
    private const string CatalogueFile = "src/Kernel/Iron.Kernel.Tenancy/Services/licensing-catalogue.json";

    static HostTests()
    {
        Environment.SetEnvironmentVariable("Tenant__Id", "acme");
        Environment.SetEnvironmentVariable("Tenant__Hostnames__0", "acme.localhost");
        Environment.SetEnvironmentVariable("ConnectionStrings__Database", "Host=localhost;Port=1;Database=nowhere;Username=nobody;Password=nothing;Timeout=1");
        Environment.SetEnvironmentVariable("Telemetry__ExportToCollector", "false");
        Environment.SetEnvironmentVariable("Heartbeat__Interval", "01:00:00");
        Environment.SetEnvironmentVariable("Storage__ConnectionString", "UseDevelopmentStorage=true");
        Environment.SetEnvironmentVariable("Storage__Container", "acme");
        Environment.SetEnvironmentVariable("Fleet__PollSeconds", "3600");
    }

    [Fact]
    [Proves("ADR-0011", "ADR-0012")]
    public async Task TheApiHostStartsBoundToItsTenantAndServesNothingProtectedToNobody()
    {
        await using var factory = Production<Iron.Host.Api.ApiHost>();
        using var client = factory.CreateClient();

        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        using var foreign = await Send(client, "/api/v1/roles", "bravo.localhost");
        Assert.Equal(HttpStatusCode.MisdirectedRequest, foreign.StatusCode);

        // No cookie: the request runs as nobody, and the permission filter answers before any module code runs.
        using var roles = await Send(client, "/api/v1/roles", "acme.localhost");
        Assert.Equal(HttpStatusCode.Unauthorized, roles.StatusCode);
        using var problem = JsonDocument.Parse(await roles.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PermissionErrors.NotAuthenticated, problem.RootElement.GetProperty("error_code").GetString());
    }

    [Fact]
    [Proves("ADR-0011", "ADR-0012")]
    public async Task TheControlPlaneHostStartsBoundToTheControlTenant()
    {
        await using var factory = Production<Iron.Host.ControlPlane.ControlPlaneHost>();
        using var client = factory.CreateClient();

        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        using var roles = await Send(client, "/api/v1/roles", "acme.localhost");
        Assert.Equal(HttpStatusCode.Unauthorized, roles.StatusCode);
    }

    [Fact]
    [Proves("ADR-0010", "ADR-0011")]
    public async Task TheWorkerHostsStartAndServeOnlyHealth()
    {
        await using var worker = Production<Iron.Host.Worker.WorkerHost>();
        await using var fleet = Production<Iron.Host.Fleet.FleetHost>();

        using var workerClient = worker.CreateClient();
        using var fleetClient = fleet.CreateClient();
        using var workerLive = await workerClient.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        using var fleetLive = await fleetClient.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, workerLive.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fleetLive.StatusCode);

        using var noRoles = await Send(workerClient, "/api/v1/roles", "acme.localhost");
        Assert.Equal(HttpStatusCode.NotFound, noRoles.StatusCode);
    }

    [Fact]
    [Proves("ADR-0034", "ADR-0035")]
    public void TheControlPlaneCarriesTheLicensingCatalogueOfTheUnitsModules()
    {
        // ADR-0034: one source. The control plane checks plans and flag rules against the file make catalogue writes from
        // these modules; the file keeps the flags it marks retired (ADR-0035).
        var file = Iron.Kernel.Tenancy.Services.UnitCatalogue.Load().Catalogue;
        var code = Iron.Kernel.Api.Modules.LicensingCatalogue.From(Iron.Host.Api.UnitModules.All());
        Assert.Empty(Iron.Kernel.Api.Modules.LicensingCatalogue.FlagLifecycleProblems(file, code));
        var expected = code.WithRetiredFrom(file).ToJson();
        var carried = file.ToJson();

        Assert.True(string.Equals(expected, carried, StringComparison.Ordinal), $"The control plane's licensing catalogue is not the units' one. Run make catalogue and commit {CatalogueFile}.");
        Assert.Contains("authorization.role_transfer", carried, StringComparison.Ordinal);
    }

    [Fact]
    [Proves("TF-218", "ADR-0052")]
    public async Task TheApiImageSaysItsVersionAndItsControlledConfigurationWithoutAHost()
    {
        var writer = new StringWriter();
        var original = Console.Out;
        Console.SetOut(writer);
        try
        {
            Assert.Equal(0, await Iron.Host.Api.UnitModules.WriteReleaseAsync(["version"]));
            Assert.Equal(0, await Iron.Host.Api.UnitModules.WriteReleaseAsync(["configuration"]));
        }
        finally
        {
            Console.SetOut(original);
        }

        var lines = writer.ToString().Split('\n', 2);
        using var configuration = JsonDocument.Parse(lines[1]);
        var root = configuration.RootElement;
        var catalogue = await File.ReadAllBytesAsync(Path.Combine(RepositoryRoot(), CatalogueFile), TestContext.Current.CancellationToken);

        Assert.Equal("development", lines[0].Trim());
        Assert.True(Iron.Host.Api.UnitModules.IsReleaseCommand(["configuration"]) && !Iron.Host.Api.UnitModules.IsReleaseCommand(["configuration", "extra"]));
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(catalogue)), root.GetProperty("catalogue_sha256").GetString());
        var seeds = root.GetProperty("seeds").EnumerateArray().Select(s => s.GetString()!).ToList();
        Assert.Contains(seeds, s => s.StartsWith("tenant-administrator:", StringComparison.Ordinal) && s.Contains("compliance.evidence.export", StringComparison.Ordinal));
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Concat(seeds.Select(s => s + "\n"))))), root.GetProperty("seeds_sha256").GetString());
    }

    [Fact]
    [Proves("ADR-0036")]
    public void TheShellsTypedSettingKeysAreTheUnitsSettings()
    {
        // ADR-0036: useSetting takes only the keys this file declares, so it is held to the code as the catalogue is.
        var expected = Iron.Kernel.Api.Modules.ShellSettingKeys.ToTypeScript(Iron.Kernel.Api.Modules.LicensingCatalogue.From(Iron.Host.Api.UnitModules.All()));
        var written = File.ReadAllText(Path.Combine(RepositoryRoot(), Iron.Kernel.Api.Modules.ShellSettingKeys.RelativePath));

        Assert.True(string.Equals(expected, written, StringComparison.Ordinal), $"The shell's setting keys are not the units' settings. Run make catalogue and commit {Iron.Kernel.Api.Modules.ShellSettingKeys.RelativePath}.");
        Assert.Contains("\"identity.session.idle_minutes\": number;", written, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Foundation.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Foundation.slnx was not found above the test's folder.");
    }

    /// <summary>
    /// TF-030 AC3: the anonymous list is health, login, invitation acceptance, the password policy and the recovery link
    /// (T2: both are for a person without a session), the token endpoint and the public keys, the sign-in's second step
    /// (T3), the sign-in through a single sign-on provider (T4), the one-time link of the operator's path (T5), and nothing
    /// else. Adding to it is a reviewed change.
    /// </summary>
    [Fact]
    [Proves("TF-030 AC3", "ADR-0014", "ADR-0048", "ADR-0049")]
    public async Task TheAnonymousAllowListIsFixedOutsideDevelopment()
    {
        await using var api = Production<Iron.Host.Api.ApiHost>();
        await using var controlPlane = Production<Iron.Host.ControlPlane.ControlPlaneHost>();
        await using var worker = Production<Iron.Host.Worker.WorkerHost>();
        await using var fleet = Production<Iron.Host.Fleet.FleetHost>();

        string[] health = ["ANY /health/live", "ANY /health/ready"];
        // The sign-in's second step, enrolment and the passkey sign-in are anonymous: they hold their state in the sign-in cookie (ADR-0047).
        // So does a sign-in through a provider, and the address to sign out there needs no session either (ADR-0048).
        string[] serving =
        [
            "ANY /health/live", "ANY /health/ready", "GET /.well-known/jwks.json", "GET /auth/password-policy", "GET /auth/sign-in-options",
            "GET /auth/sso/{id:guid}/sign-out", "POST /auth/enrolment/authenticator-app", "POST /auth/enrolment/authenticator-app/confirm", "POST /auth/enrolment/passkey", "POST /auth/enrolment/passkey/options",
            "POST /auth/invitations/accept", "POST /auth/login", "POST /auth/passkey", "POST /auth/passkey/options", "POST /auth/recovery",
            "POST /auth/second-factor", "POST /auth/second-factor/passkey-options", "POST /auth/sign-in-link", "POST /auth/sso/callback", "POST /auth/sso/discover",
            "POST /auth/sso/start", "POST /connect/token",
        ];
        Assert.Equal(serving, Anonymous(api.Services));
        Assert.Equal(serving, Anonymous(controlPlane.Services));
        Assert.Equal(health, Anonymous(worker.Services));
        Assert.Equal(health, Anonymous(fleet.Services));
    }

    [Fact]
    [Proves("ADR-0009", "ADR-0012")]
    public async Task InDevelopmentOnlyTheApiBrowserJoinsTheAnonymousList()
    {
        await using var api = new WebApplicationFactory<Iron.Host.Api.ApiHost>().WithWebHostBuilder(host => host.UseEnvironment(Environments.Development));
        using var client = api.CreateClient();

        var anonymous = Anonymous(api.Services);

        Assert.Contains("ANY /health/live", anonymous);
        Assert.Contains("ANY /health/ready", anonymous);
        Assert.All(
            anonymous.Where(e => !e.Contains("/health/", StringComparison.Ordinal) && !e.Contains(" /auth/", StringComparison.Ordinal) && !e.Contains("/connect/", StringComparison.Ordinal) && !e.Contains("/.well-known/", StringComparison.Ordinal)),
            e => Assert.True(e.Contains("/openapi", StringComparison.Ordinal) || e.Contains("/scalar", StringComparison.Ordinal), e));
    }

    [Fact]
    [Proves("TF-100 AC3", "ADR-0009")]
    public async Task EveryOperationOfTheOpenApiDocumentsSaysWhatItNeedsAndCarriesItsSchemas()
    {
        await using var api = new WebApplicationFactory<Iron.Host.Api.ApiHost>().WithWebHostBuilder(host => host.UseEnvironment(Environments.Development));
        await using var controlPlane = new WebApplicationFactory<Iron.Host.ControlPlane.ControlPlaneHost>().WithWebHostBuilder(host => host.UseEnvironment(Environments.Development));

        var problems = (await DocumentProblemsAsync(api)).Select(problem => $"api: {problem}")
            .Concat((await DocumentProblemsAsync(controlPlane)).Select(problem => $"control plane: {problem}"))
            .ToList();

        Assert.True(problems.Count == 0, $"{problems.Count} operation(s) of the OpenAPI documents fall short of TF-100 AC3:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    /// <summary>
    /// The times a request body may carry, each a period or a day a person chooses, never when something happened: the
    /// server stamps every record's own times from its clock (TF-065 AC1, ADR-0007). An anchor's time is the unit's
    /// server clock, kept beside the control plane's own receipt time (ADR-0050).
    /// </summary>
    private static readonly Dictionary<string, string[]> ChosenTimes = new(StringComparer.Ordinal)
    {
        ["POST /api/v1/delegations"] = ["starts_at", "ends_at"],
        ["POST /api/v1/access-reviews"] = ["due_on"],
        ["POST /api/v1/tenants/{slug}/plan"] = ["starts_on", "ends_on"],
        ["POST /api/v1/audit-anchors"] = ["at"],

        // The period of the audit trail a reviewer chooses to review (TF-219 AC3); the review and its signature carry the server's time.
        ["POST /api/v1/audit-reviews"] = ["period_from", "period_to"],

        // The period an evidence pack covers, as its requester chooses it (TF-067 AC1); the pack's own times are the server's.
        ["POST /api/v1/evidence-packs"] = ["from", "to"],
    };

    [Fact]
    [Proves("TF-065 AC1", "ADR-0050")]
    public async Task NoEndpointTakesAClientsTimeForWhatItRecords()
    {
        await using var api = new WebApplicationFactory<Iron.Host.Api.ApiHost>().WithWebHostBuilder(host => host.UseEnvironment(Environments.Development));
        await using var controlPlane = new WebApplicationFactory<Iron.Host.ControlPlane.ControlPlaneHost>().WithWebHostBuilder(host => host.UseEnvironment(Environments.Development));

        var found = new List<string>();
        foreach (var factory in new object[] { api, controlPlane })
        {
            using var client = factory is WebApplicationFactory<Iron.Host.Api.ApiHost> apiFactory ? apiFactory.CreateClient() : ((WebApplicationFactory<Iron.Host.ControlPlane.ControlPlaneHost>)factory).CreateClient();
            using var answer = await Send(client, "/openapi/v1.json", "acme.localhost");
            using var document = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            found.AddRange(TimesInBodies(document));
        }

        var unexpected = found.Where(entry =>
        {
            var (operation, property) = (entry[..entry.LastIndexOf(' ')], entry[(entry.LastIndexOf(' ') + 1)..]);
            return !ChosenTimes.TryGetValue(operation, out var allowed) || !allowed.Contains(property);
        }).Distinct().ToList();
        Assert.True(unexpected.Count == 0, $"A request body carries a time the server should stamp itself:{Environment.NewLine}{string.Join(Environment.NewLine, unexpected)}");
        Assert.NotEmpty(found); // the walk found the chosen periods, so it reads the bodies at all
    }

    /// <summary>Every property of a request body that is a time or a day, as "METHOD /path property", following references.</summary>
    private static IEnumerable<string> TimesInBodies(JsonDocument document)
    {
        var schemas = document.RootElement.TryGetProperty("components", out var components) && components.TryGetProperty("schemas", out var all) ? all : default;
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject().Where(o => o.Name is "post" or "put" or "patch"))
            {
                if (!operation.Value.TryGetProperty("requestBody", out var body) || !body.TryGetProperty("content", out var content))
                {
                    continue;
                }

                foreach (var media in content.EnumerateObject())
                {
                    if (!media.Value.TryGetProperty("schema", out var schema))
                    {
                        continue;
                    }

                    foreach (var property in TimeProperties(schema, schemas, 0))
                    {
                        yield return $"{operation.Name.ToUpperInvariant()} {path.Name} {property}";
                    }
                }
            }
        }
    }

    private static IEnumerable<string> TimeProperties(JsonElement schema, JsonElement schemas, int depth)
    {
        if (depth > 6)
        {
            yield break;
        }

        if (schema.TryGetProperty("$ref", out var reference) && schemas.ValueKind == JsonValueKind.Object)
        {
            var name = reference.GetString()![(reference.GetString()!.LastIndexOf('/') + 1)..];
            if (schemas.TryGetProperty(name, out var target))
            {
                foreach (var nested in TimeProperties(target, schemas, depth + 1))
                {
                    yield return nested;
                }
            }

            yield break;
        }

        foreach (var key in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (schema.TryGetProperty(key, out var options))
            {
                foreach (var option in options.EnumerateArray())
                {
                    foreach (var nested in TimeProperties(option, schemas, depth + 1))
                    {
                        yield return nested;
                    }
                }
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            foreach (var nested in TimeProperties(items, schemas, depth + 1))
            {
                yield return nested;
            }
        }

        if (!schema.TryGetProperty("properties", out var properties))
        {
            yield break;
        }

        foreach (var property in properties.EnumerateObject())
        {
            if (property.Value.TryGetProperty("format", out var format) && format.GetString() is "date-time" or "date")
            {
                yield return property.Name;
            }
            else
            {
                foreach (var nested in TimeProperties(property.Value, schemas, depth + 1))
                {
                    yield return $"{property.Name}.{nested}";
                }
            }
        }
    }

    private static async Task<IReadOnlyList<string>> DocumentProblemsAsync<TEntryPoint>(WebApplicationFactory<TEntryPoint> factory)
        where TEntryPoint : class
    {
        using var client = factory.CreateClient();
        using var answer = await Send(client, "/openapi/v1.json", "acme.localhost");
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        using var document = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return OpenApiProblems(document).ToList();
    }

    /// <summary>
    /// What an operation of the document lacks: a note of what it needs (its permission, a signed-in principal or the
    /// anonymous marker), a schema for its request body, a success response, and a schema for a success that has a body.
    /// </summary>
    private static IEnumerable<string> OpenApiProblems(JsonDocument document)
    {
        string[] methods = ["get", "put", "post", "delete", "patch"];
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject().Where(o => methods.Contains(o.Name)))
            {
                var name = $"{operation.Name.ToUpperInvariant()} {path.Name}";
                var description = operation.Value.TryGetProperty("description", out var text) ? text.GetString() ?? string.Empty : string.Empty;
                if (!description.StartsWith("Requires permission `", StringComparison.Ordinal)
                    && !description.StartsWith(Iron.Kernel.Api.OpenApi.OpenApiExtensions.AccessSignedIn, StringComparison.Ordinal)
                    && !description.StartsWith(Iron.Kernel.Api.OpenApi.OpenApiExtensions.AccessAnonymous, StringComparison.Ordinal))
                {
                    yield return $"{name} does not say what it needs";
                }

                if (operation.Value.TryGetProperty("requestBody", out var body) && !HasSchema(body))
                {
                    yield return $"{name} has a request body without a schema";
                }

                var successes = operation.Value.GetProperty("responses").EnumerateObject().Where(r => r.Name.StartsWith('2')).ToList();
                if (successes.Count == 0)
                {
                    yield return $"{name} documents no success response";
                }

                foreach (var success in successes.Where(r => r.Name is not ("202" or "204") && !HasSchema(r.Value)))
                {
                    yield return $"{name} answers {success.Name} without a schema";
                }
            }
        }
    }

    private static bool HasSchema(JsonElement element) =>
        element.TryGetProperty("content", out var content) && content.EnumerateObject().Any(media => media.Value.TryGetProperty("schema", out _));

    private static WebApplicationFactory<TEntryPoint> Production<TEntryPoint>()
        where TEntryPoint : class =>
        new WebApplicationFactory<TEntryPoint>().WithWebHostBuilder(host => host.UseEnvironment(Environments.Production));

    private static IReadOnlyList<string> Anonymous(IServiceProvider services) =>
        services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(EndpointPermissionCheck.Describe)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, string host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Host = host;
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
