using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Authorization;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Events;
using Iron.Kernel.Fleet;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Telemetry;
using Iron.Kernel.Tenancy.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Tenancy.Tests;

/// <summary>The control plane's tenant API as the operator and as a reader.</summary>
[Collection(TenancyCollection.Name)]
public class TenantsEndpointTests(TenancyFixture fixture) : IAsyncLifetime
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
        builder.Services.AddIronDatabase(fixture.ConnectionString);
        builder.Services.AddIronSecretStore(builder.Configuration);
        builder.Services.AddModules(builder.Configuration, [new AuditModule(), new EventsModule(), new AuthorizationModule(), new FleetModule(), new TenancyModule()]);
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
    [Proves("TF-001 AC2", "ADR-0017")]
    public async Task TheOperatorRegistersATenantAndRequestsProvisioning()
    {
        var operatorId = await Grant(SystemRoles.TenantAdministrator);
        var slug = $"echo-{Ids.New():N}"[..20];

        using var created = await Send(HttpMethod.Post, "/api/v1/tenants", operatorId, new { slug, display_name = "Echo GmbH", administrator_login_id = "admin@echo.test", size_class = "medium" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("provisioning", body.RootElement.GetProperty("status").GetString());
        Assert.Equal($"{slug}.localhost", Assert.Single(body.RootElement.GetProperty("hostnames").EnumerateArray()).GetString());
        Assert.Equal("medium", body.RootElement.GetProperty("size_class").GetString());

        using var duplicate = await Send(HttpMethod.Post, "/api/v1/tenants", operatorId, new { slug = $"{slug}-2", display_name = "Echo 2", hostnames = new[] { $"{slug}.localhost" }, administrator_login_id = "admin@echo.test" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("tenancy.hostname.taken", await ErrorCode(duplicate));

        using var invalid = await Send(HttpMethod.Post, "/api/v1/tenants", operatorId, new { slug = "Not A Slug", display_name = "x", administrator_login_id = "not-an-email" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var provision = await Send(HttpMethod.Post, $"/api/v1/tenants/{slug}/provision", operatorId);
        Assert.Equal(HttpStatusCode.Accepted, provision.StatusCode);
        using var job = JsonDocument.Parse(await provision.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("requested", job.RootElement.GetProperty("status").GetString());
        var jobId = job.RootElement.GetProperty("job_id").GetGuid();

        using var latest = await Send(HttpMethod.Get, $"/api/v1/tenants/{slug}/provisioning", operatorId);
        Assert.Equal(HttpStatusCode.OK, latest.StatusCode);
        using var latestBody = JsonDocument.Parse(await latest.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(jobId, latestBody.RootElement.GetProperty("job_id").GetGuid());

        // hostnames are joined in, not a column, so they are not in the field whitelist; every row carries them.
        using var list = await Send(HttpMethod.Get, $"/api/v1/tenants?filter=slug:eq:{slug}", operatorId);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listBody = JsonDocument.Parse(await list.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var row = Assert.Single(listBody.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal(slug, row.GetProperty("slug").GetString());
        Assert.Equal("provisioning", row.GetProperty("status").GetString());
        Assert.Equal($"{slug}.localhost", Assert.Single(row.GetProperty("hostnames").EnumerateArray()).GetString());

        using var unknownField = await Send(HttpMethod.Get, "/api/v1/tenants?fields=hostnames", operatorId);
        Assert.Equal(HttpStatusCode.BadRequest, unknownField.StatusCode);
    }

    [Fact]
    [Proves("ADR-0017")]
    public async Task AReaderSeesTenantsButCannotCreateOrProvision()
    {
        var reader = await Grant(SystemRoles.ReadOnly);

        using var list = await Send(HttpMethod.Get, "/api/v1/tenants", reader);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        using var create = await Send(HttpMethod.Post, "/api/v1/tenants", reader, new { slug = "foxtrot", display_name = "Foxtrot", administrator_login_id = "a@foxtrot.test" });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(PermissionErrors.PermissionDenied, await ErrorCode(create));

        using var provision = await Send(HttpMethod.Post, "/api/v1/tenants/foxtrot/provision", reader);
        Assert.Equal(HttpStatusCode.Forbidden, provision.StatusCode);
    }

    [Fact]
    [Proves("ADR-0046")]
    public async Task TheOperatorAsksForAnAdministratorsRecoveryWithAReasonAndTheControlPlaneAuditsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        const string Reason = "The administrator lost their password (ticket 4711)";
        var operatorId = await Grant(SystemRoles.TenantAdministrator);
        var readerId = await Grant(SystemRoles.ReadOnly);
        var slug = $"rec-{Ids.New():N}"[..20];
        using (var created = await Send(HttpMethod.Post, "/api/v1/tenants", operatorId, new { slug, display_name = "Recovering", administrator_login_id = "admin@recovering.test" }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using var whileProvisioning = await Send(HttpMethod.Post, $"/api/v1/tenants/{slug}/administrator-recovery", operatorId, new { reason = Reason });
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<ITenantRegistry>().SetStatusAsync(slug, TenantStatus.Active, ct)).IsSuccess);
        }

        using var byReader = await Send(HttpMethod.Post, $"/api/v1/tenants/{slug}/administrator-recovery", readerId, new { reason = Reason });
        using var withoutReason = await Send(HttpMethod.Post, $"/api/v1/tenants/{slug}/administrator-recovery", operatorId, new { reason = "" });
        using var requested = await Send(HttpMethod.Post, $"/api/v1/tenants/{slug}/administrator-recovery", operatorId, new { reason = Reason });
        using var job = JsonDocument.Parse(await requested.Content.ReadAsStringAsync(ct));
        await using var check = _app!.Services.CreateAsyncScope();
        var audit = await check.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .SingleAsync(e => e.EventType == TenancyAuditEvents.AdministratorRecoveryRequested && e.Target == slug, ct);

        Assert.Equal(TenancyErrors.NotProvisionable, await ErrorCode(whileProvisioning));
        Assert.Equal(HttpStatusCode.Forbidden, byReader.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, withoutReason.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        Assert.Equal("recover", job.RootElement.GetProperty("kind").GetString());
        var request = job.RootElement.GetProperty("request");
        Assert.Equal(("admin@recovering.test", Reason), (request.GetProperty("subject").GetString(), request.GetProperty("reason").GetString()));
        Assert.Equal((operatorId, Reason), (audit.ActorId, audit.Reason));
        using var details = JsonDocument.Parse(audit.Details!);
        Assert.Equal("admin@recovering.test", details.RootElement.GetProperty("administrator").GetString());
        Assert.Equal(operatorId.ToString("D"), details.RootElement.GetProperty("operator").GetString()); // no directory here: the operator by id
    }

    private async Task<Guid> Grant(string role)
    {
        var principal = Ids.New();
        await using var scope = _app!.Services.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(principal, role, TestContext.Current.CancellationToken)).IsSuccess);
        return principal;
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, Guid principal, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(HeaderPrincipal.HeaderName, principal.ToString());
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return problem.RootElement.GetProperty("error_code").GetString();
    }
}
