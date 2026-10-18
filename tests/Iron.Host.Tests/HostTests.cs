using System.Net;
using System.Text.Json;
using Iron.Api.Permissions;
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

    /// <summary>TF-030 AC3: the anonymous list is health, login, invitation acceptance, the token endpoint and the public keys, and nothing else. Adding to it is a reviewed change.</summary>
    [Fact]
    public async Task TheAnonymousAllowListIsFixedOutsideDevelopment()
    {
        await using var api = Production<Iron.Host.Api.ApiHost>();
        await using var controlPlane = Production<Iron.Host.ControlPlane.ControlPlaneHost>();
        await using var worker = Production<Iron.Host.Worker.WorkerHost>();
        await using var fleet = Production<Iron.Host.Fleet.FleetHost>();

        string[] health = ["ANY /health/live", "ANY /health/ready"];
        string[] serving = ["ANY /health/live", "ANY /health/ready", "GET /.well-known/jwks.json", "POST /auth/invitations/{token}/accept", "POST /auth/login", "POST /connect/token"];
        Assert.Equal(serving, Anonymous(api.Services));
        Assert.Equal(serving, Anonymous(controlPlane.Services));
        Assert.Equal(health, Anonymous(worker.Services));
        Assert.Equal(health, Anonymous(fleet.Services));
    }

    [Fact]
    public async Task InDevelopmentOnlyTheApiBrowserJoinsTheAnonymousList()
    {
        await using var api = new WebApplicationFactory<Iron.Host.Api.ApiHost>().WithWebHostBuilder(host => host.UseEnvironment(Environments.Development));
        using var client = api.CreateClient();

        var anonymous = Anonymous(api.Services);

        Assert.Contains("ANY /health/live", anonymous);
        Assert.Contains("ANY /health/ready", anonymous);
        Assert.All(
            anonymous.Where(e => !e.Contains("/health/", StringComparison.Ordinal) && !e.StartsWith("POST /auth/", StringComparison.Ordinal) && !e.Contains("/connect/", StringComparison.Ordinal) && !e.Contains("/.well-known/", StringComparison.Ordinal)),
            e => Assert.True(e.Contains("/openapi", StringComparison.Ordinal) || e.Contains("/scalar", StringComparison.Ordinal), e));
    }

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
