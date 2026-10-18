using System.Net;
using System.Text.Json;
using Iron.Api;
using Iron.Api.Modules;
using Iron.Api.Permissions;
using Iron.Audit.Contracts;
using Iron.Authorization;
using Iron.Authorization.Contracts;
using Iron.Data;
using Iron.Kernel;
using Iron.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Audit.Tests;

/// <summary>A denial over HTTP becomes a row, and the administrator finds it through the search endpoint with the response's own correlation id.</summary>
[Collection(AuditCollection.Name)]
public class AuditEventsEndpointTests(AuditFixture fixture) : IAsyncLifetime
{
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentPrincipal, HeaderPrincipal>();
        builder.Services.AddIronApi();
        builder.Services.AddIronDatabase(fixture.AppConnectionString, AuditFixture.AppRole);
        builder.Services.AddModules(builder.Configuration, [new AuditModule(), new AuthorizationModule()]);

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
    public async Task ADenialBecomesARowThatTheAdministratorFindsByCorrelationId()
    {
        var alice = Ids.New();
        var bob = Ids.New();
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            var assignments = scope.ServiceProvider.GetRequiredService<IRoleAssignments>();
            Assert.True((await assignments.AssignAsync(alice, SystemRoles.TenantAdministrator, TestContext.Current.CancellationToken)).IsSuccess);
        }

        using var denied = await Send(bob, "/api/v1/audit-events");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var correlation = Assert.Single(denied.Headers.GetValues(CorrelationId.HeaderName));

        using var found = await Send(alice, $"/api/v1/audit-events?filter=correlation_id:eq:{correlation}");
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        using var body = JsonDocument.Parse(await found.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var row = Assert.Single(body.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal("authz.permission_denied", row.GetProperty("event_type").GetString());
        Assert.Equal(AuditOutcome.Denied, row.GetProperty("outcome").GetString());
        Assert.Equal(bob, row.GetProperty("actor_id").GetGuid());
        Assert.Equal(bob, row.GetProperty("created_by").GetGuid());
        Assert.Equal(AuditSource.Api, row.GetProperty("source").GetString());
        Assert.Equal("GET /api/v1/audit-events", row.GetProperty("target").GetString());
        Assert.Equal(AuditPermissions.EventsRead.Name, row.GetProperty("details").GetProperty("permission").GetString());
    }

    [Fact]
    public async Task NobodySignedInGets401()
    {
        using var response = await Send(null, "/api/v1/audit-events");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PermissionErrors.NotAuthenticated, problem.RootElement.GetProperty("error_code").GetString());
    }

    private async Task<HttpResponseMessage> Send(Guid? principal, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (principal is not null)
        {
            request.Headers.Add(HeaderPrincipal.HeaderName, principal.Value.ToString());
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
