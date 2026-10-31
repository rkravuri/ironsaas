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
using Iron.Kernel.Tenancy.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Tenancy.Tests;

/// <summary>
/// A tenant's keys through the control plane's API (TF-075 AC3, AC4, ADR-0054): a rotation needs the key officer's
/// permission, a reason and a fresh token, and is audited with the officer and the reason; a unit reads and reports only
/// its own rotations and sets its policy within the operator's bounds.
/// </summary>
[Collection(TenancyCollection.Name)]
public class KeyEndpointTests(TenancyFixture fixture) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private WebApplication? _app;
    private HttpClient _client = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

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
        builder.Services.AddScoped<IRecentAuthentication, HeaderRecentAuthentication>();
        _app = builder.Build();
        _app.UseIronTelemetry();
        _app.UseIronApi();
        _app.MapModules();
        await _app.StartAsync(Cancel);
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
    [Proves("TF-075 AC4", "TF-075 AC3", "ADR-0054")]
    public async Task ARotationNeedsTheKeyOfficersPermissionAReasonAndAFreshTokenAndIsAuditedWithTheOfficerAndTheReason()
    {
        var slug = await TenantAsync("keys");
        var reader = await GrantAsync(SystemRoles.ReadOnly);
        var officer = await GrantAsync(SystemRoles.TenantAdministrator);
        var path = $"/api/v1/tenants/{slug}/keys/data/rotations";
        const string Reason = "A contractor with access to the data left";

        using (var refused = await Send(HttpMethod.Post, path, reader, new { reason = Reason }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(PermissionErrors.PermissionDenied, await ErrorCode(refused));
        }

        using (var stale = await Send(HttpMethod.Post, path, officer, new { reason = Reason }, staleToken: true))
        {
            Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
            Assert.Equal(RecentAuthentication.ErrorCode, await ErrorCode(stale));
        }

        using (var noReason = await Send(HttpMethod.Post, path, officer, new { reason = " " }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
            Assert.Equal(TenancyErrors.KeyReasonRequired, await ErrorCode(noReason));
        }

        using (var unknown = await Send(HttpMethod.Post, $"/api/v1/tenants/{slug}/keys/master/rotations", officer, new { reason = Reason }))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Equal(TenancyErrors.KeyUnknown, await ErrorCode(unknown));
        }

        Guid id;
        using (var accepted = await Send(HttpMethod.Post, path, officer, new { reason = Reason }))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            using var body = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync(Cancel));
            Assert.Equal(KeyRotationStates.Requested, body.RootElement.GetProperty("state").GetString());
            Assert.Equal(Reason, body.RootElement.GetProperty("reason").GetString());
            id = body.RootElement.GetProperty("id").GetGuid();
        }

        using (var twice = await Send(HttpMethod.Post, path, officer, new { reason = Reason }))
        {
            Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
            Assert.Equal(TenancyErrors.KeyRotationInProgress, await ErrorCode(twice));
        }

        using (var status = await Send(HttpMethod.Get, $"/api/v1/tenants/{slug}/keys/rotations/{id}", reader))
        {
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        }

        await using var scope = _app!.Services.CreateAsyncScope();
        var requested = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .SingleAsync(e => e.EventType == TenancyAuditEvents.KeyRotationRequested && e.Target == slug, Cancel);
        Assert.Equal((officer, Reason), (requested.ActorId, requested.Reason));
        Assert.Equal("data", JsonDocument.Parse(requested.Details!).RootElement.GetProperty("key").GetString());
    }

    [Fact]
    [Proves("ADR-0054")]
    public async Task AUnitReadsAndReportsOnlyItsOwnRotationsAndSetsItsPolicyWithinTheOperatorsBounds()
    {
        var mine = await TenantAsync("mine");
        var theirs = await TenantAsync("theirs");
        var unit = await UnitClientAsync(mine);
        Guid ownRotation;
        Guid otherRotation;
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            var keys = scope.ServiceProvider.GetRequiredService<ITenantKeys>();
            ownRotation = await RotatedAsync(scope.ServiceProvider, mine);
            otherRotation = await RotatedAsync(scope.ServiceProvider, theirs);
            await keys.RecordKeyAsync(mine, TenantKeyNames.Data, 2, 1, Cancel);
        }

        using (var listed = await Send(HttpMethod.Get, "/api/v1/key-rotations", unit))
        {
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            using var body = JsonDocument.Parse(await listed.Content.ReadAsStringAsync(Cancel));
            Assert.Equal([ownRotation], body.RootElement.GetProperty("rotations").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));
        }

        using (var foreign = await Send(HttpMethod.Post, $"/api/v1/key-rotations/{otherRotation}/progress", unit, new { done = 1, total = 1, finished = true }))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        }

        using (var reported = await Send(HttpMethod.Post, $"/api/v1/key-rotations/{ownRotation}/progress", unit, new { done = 4, total = 9, finished = false }))
        {
            using var body = JsonDocument.Parse(await reported.Content.ReadAsStringAsync(Cancel));
            Assert.Equal((4L, 9L, KeyRotationStates.Rotated), (body.RootElement.GetProperty("done").GetInt64(), body.RootElement.GetProperty("total").GetInt64(), body.RootElement.GetProperty("state").GetString()));
        }

        using var tooOften = await Send(HttpMethod.Put, "/api/v1/key-rotations/policy", unit, new { data_days = 5 });
        using var tooRarely = await Send(HttpMethod.Put, "/api/v1/key-rotations/policy", unit, new { data_days = 5000 });
        Assert.Equal(30, JsonDocument.Parse(await tooOften.Content.ReadAsStringAsync(Cancel)).RootElement.GetProperty("data_days").GetInt32());
        Assert.Equal(730, JsonDocument.Parse(await tooRarely.Content.ReadAsStringAsync(Cancel)).RootElement.GetProperty("data_days").GetInt32());
    }

    private async Task<string> TenantAsync(string prefix)
    {
        var slug = $"{prefix}-{Ids.New():N}"[..20];
        await using var scope = _app!.Services.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<TenantRegistry>().CreateAsync(new NewTenant(slug, prefix, [], $"admin@{prefix}.test", null, SizeClass.Small, null, null), Cancel)).IsSuccess);
        return slug;
    }

    /// <summary>A rotation of the tenant's index key the fleet has carried out, waiting for the unit's re-encryption.</summary>
    private static async Task<Guid> RotatedAsync(IServiceProvider services, string slug)
    {
        var rotation = await services.GetRequiredService<KeyRotationService>().RequestAsync(slug, TenantKeyNames.Index, "A test of the unit's report", Cancel);
        Assert.True(rotation.IsSuccess, rotation.IsFailure ? rotation.Error.Message : null);
        await services.GetRequiredService<ITenantKeys>().RotatedAsync(rotation.Value.Id, 1, 2, Cancel);
        return rotation.Value.Id;
    }

    private async Task<Guid> UnitClientAsync(string slug)
    {
        var client = await GrantAsync(SystemRoles.UnitClient);
        await using var scope = _app!.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ITenantAuditChains>().BindUnitClientAsync(slug, client, Cancel);
        return client;
    }

    private async Task<Guid> GrantAsync(string role)
    {
        var principal = Ids.New();
        await using var scope = _app!.Services.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(principal, role, Cancel)).IsSuccess);
        return principal;
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, Guid principal, object? body = null, bool staleToken = false)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(HeaderPrincipal.HeaderName, principal.ToString());
        if (staleToken)
        {
            request.Headers.Add(HeaderRecentAuthentication.HeaderName, "true");
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        return await _client.SendAsync(request, Cancel);
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel));
        return problem.RootElement.GetProperty("error_code").GetString();
    }
}
