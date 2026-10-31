using System.Net;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Events;
using Iron.Kernel.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Authorization.Tests;

/// <summary>The done-when of T8 over HTTP: 401 for nobody, 403 and an audit event for the wrong somebody, the list for the administrator.</summary>
[Collection(AuthorizationCollection.Name)]
public class RolesEndpointTests(AuthorizationFixture fixture) : IAsyncLifetime
{
    private readonly RecordingAuditWriter _audit = new();
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentPrincipal, HeaderPrincipal>();
        builder.Services.AddSingleton<IAuditWriter>(_audit);
        builder.Services.AddIronApi();
        builder.Services.AddIronDatabase(fixture.ConnectionString);
        builder.Services.AddModules(builder.Configuration, [new EventsModule(), new AuthorizationModule()]);
        builder.Services.AddScoped<IEntitlementGate, AllEntitled>();
        builder.Services.AddScoped<IFlagGate, AllEntitled>();

        _app = builder.Build();
        _app.UseIronTelemetry();
        _app.UseIronApi();
        _app.MapModules();
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    [Fact]
    [Proves("ADR-0012")]
    public async Task NobodySignedInGets401()
    {
        using var response = await Send(null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(PermissionErrors.NotAuthenticated, (await Problem(response)).RootElement.GetProperty("error_code").GetString());
    }

    [Fact]
    [Proves("TF-031 AC1", "ADR-0012")]
    public async Task APrincipalWithoutThePermissionGets403AndAnAuditEvent()
    {
        var nobody = Ids.New();

        using var response = await Send(nobody);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var problem = await Problem(response);
        Assert.Equal(PermissionErrors.PermissionDenied, problem.RootElement.GetProperty("error_code").GetString());
        Assert.Equal(AuthorizationPermissions.RolesRead.Name, problem.RootElement.GetProperty("permission").GetString());
        var auditEvent = Assert.Single(_audit.Events, e => e.PrincipalId == nobody);
        Assert.Equal("GET /api/v1/roles", auditEvent.Target);
    }

    [Fact]
    [Proves("ADR-0009", "ADR-0012")]
    public async Task TheTenantAdministratorListsTheSystemRolesInTheEnvelope()
    {
        var alice = Ids.New();
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            var assignments = scope.ServiceProvider.GetRequiredService<IRoleAssignments>();
            Assert.True((await assignments.AssignAsync(alice, SystemRoles.TenantAdministrator, TestContext.Current.CancellationToken)).IsSuccess);
        }

        using var response = await Send(alice, "?sort=code&filter=is_system:eq:true&fields=code,name,is_system,permissions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var data = body.RootElement.GetProperty("data");
        Assert.Equal(SystemRoles.All.Order(StringComparer.Ordinal), data.EnumerateArray().Select(r => r.GetProperty("code").GetString()));
        Assert.All(data.EnumerateArray(), role => Assert.True(role.GetProperty("is_system").GetBoolean()));
        Assert.Equal(SystemRoles.All.Count, body.RootElement.GetProperty("meta").GetProperty("total_count").GetInt32());
    }

    private async Task<HttpResponseMessage> Send(Guid? principal, string query = "")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/roles" + query, UriKind.Relative));
        if (principal is not null)
        {
            request.Headers.Add(HeaderPrincipal.HeaderName, principal.Value.ToString());
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonDocument> Problem(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
