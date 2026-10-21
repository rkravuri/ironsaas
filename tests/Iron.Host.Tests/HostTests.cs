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
    /// (T2: both are for a person without a session), the token endpoint and the public keys, and nothing else. Adding to it
    /// is a reviewed change.
    /// </summary>
    [Fact]
    [Proves("TF-030 AC3", "ADR-0014")]
    public async Task TheAnonymousAllowListIsFixedOutsideDevelopment()
    {
        await using var api = Production<Iron.Host.Api.ApiHost>();
        await using var controlPlane = Production<Iron.Host.ControlPlane.ControlPlaneHost>();
        await using var worker = Production<Iron.Host.Worker.WorkerHost>();
        await using var fleet = Production<Iron.Host.Fleet.FleetHost>();

        string[] health = ["ANY /health/live", "ANY /health/ready"];
        string[] serving = ["ANY /health/live", "ANY /health/ready", "GET /.well-known/jwks.json", "GET /auth/password-policy", "POST /auth/invitations/{token}/accept", "POST /auth/login", "POST /auth/recovery/{token}", "POST /connect/token"];
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
