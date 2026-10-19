using System.Net;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Api.Tests.Permissions;

/// <summary>ADR-0030: an endpoint that declares its scope gets its check at that unit or record; without one, tenant-wide.</summary>
public class ScopedPermissionFilterTests : IAsyncLifetime
{
    private static readonly Permission Read = new("sample.things.read", "Read things", Risk.Low);
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UnitA = Guid.Parse("aaaaaaaa-0000-7000-8000-000000000001");
    private static readonly Guid Thing = Guid.Parse("bbbbbbbb-0000-7000-8000-000000000002");

    private readonly List<AccessRequest> _requests = [];
    private readonly Dictionary<Guid, Guid> _owners = new() { [Thing] = UnitA };
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddIronApi();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(new PermissionCatalogue([Read]));
        builder.Services.AddScoped<ICurrentPrincipal, HeaderPrincipal>();
        builder.Services.AddScoped<IPermissionEvaluator>(_ => new Recording(_requests));

        _app = builder.Build();
        _app.UseIronTelemetry();
        _app.UseIronApi();
        _app.MapGet("/api/v1/things", () => "things").RequirePermission(Read);
        _app.MapGet("/api/v1/rows", () => "rows").RequirePermission(Read).ScopedByRow();
        _app.MapGet("/api/v1/units/{id}", (string id) => id).RequirePermission(Read).ScopedToUnit();
        _app.MapGet("/api/v1/things/{id}", (string id) => id)
            .RequirePermission(Read)
            .ScopedToRecord("thing", (_, id, _) => Task.FromResult(_owners.TryGetValue(id, out var unit) ? unit : (Guid?)null));
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
    public async Task WithoutAMarkerTheCheckIsTenantWide()
    {
        Assert.Equal(AccessScope.Tenant, await ScopeOf("/api/v1/things"));
    }

    [Fact]
    public async Task ScopedByRowChecksThePermissionAnywhere()
    {
        Assert.Equal(AccessScope.Anywhere, await ScopeOf("/api/v1/rows"));
    }

    [Fact]
    public async Task ScopedToUnitChecksAtTheUnitOfTheRoute()
    {
        Assert.Equal(AccessScope.Unit(UnitA), await ScopeOf($"/api/v1/units/{UnitA}"));
    }

    [Fact]
    public async Task ScopedToRecordChecksAtTheRecordAndTheUnitThatOwnsIt()
    {
        var scope = await ScopeOf($"/api/v1/things/{Thing}");

        Assert.Equal(AccessScope.Record("thing", Thing, UnitA), scope);
        Assert.Equal(UnitA, scope.UnitId);
    }

    [Fact]
    public async Task ARecordTheLookupDoesNotFindIsCheckedTenantWide()
    {
        Assert.Equal(AccessScope.Tenant, await ScopeOf($"/api/v1/things/{Ids.New()}"));
    }

    [Fact]
    public async Task ARouteValueThatIsNotAnIdIsCheckedTenantWide()
    {
        Assert.Equal(AccessScope.Tenant, await ScopeOf("/api/v1/units/not-an-id"));
    }

    private async Task<AccessScope> ScopeOf(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add(HeaderPrincipal.HeaderName, Alice.ToString());
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single(_requests).Scope;
    }

    private sealed class Recording(List<AccessRequest> requests) : IPermissionEvaluator
    {
        public ValueTask<AccessDecision> EvaluateAsync(AccessRequest request, CancellationToken cancellationToken)
        {
            requests.Add(request);
            return ValueTask.FromResult(AccessDecision.Allow);
        }
    }
}
