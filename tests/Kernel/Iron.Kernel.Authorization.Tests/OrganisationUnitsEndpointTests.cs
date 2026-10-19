using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Authorization.Tests;

/// <summary>The unit API over HTTP (TF-034, ADR-0030): permissioned, audited, refusing cycles and kinds, scoped to the unit it touches.</summary>
[Collection(AuthorizationCollection.Name)]
public class OrganisationUnitsEndpointTests(AuthorizationFixture fixture) : IAsyncLifetime
{
    private readonly RecordingAuditWriter _audit = new();
    private readonly Guid _administrator = Ids.New();
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
        builder.Services.AddModules(builder.Configuration, [new AuthorizationModule()]);
        builder.Services.AddScoped<IEntitlementGate, AllEntitled>();
        builder.Services.AddScoped<IFlagGate, AllEntitled>();

        _app = builder.Build();
        _app.UseIronTelemetry();
        _app.UseIronApi();
        _app.MapModules();
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = _app.GetTestClient();
        await AssignAsync(_administrator, RoleScope.Tenant);
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
    public async Task NobodySignedInGets401AndSomebodyWithoutTheRoleGets403()
    {
        using var anonymous = await SendAsync(HttpMethod.Get, "/api/v1/organisation-units", null);
        using var stranger = await SendAsync(HttpMethod.Get, "/api/v1/organisation-units", Ids.New());

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode);
    }

    [Fact]
    public async Task TheAdministratorReadsTheTreeAndTheKinds()
    {
        using var tree = await SendAsync(HttpMethod.Get, "/api/v1/organisation-units", _administrator);
        using var kinds = await SendAsync(HttpMethod.Get, "/api/v1/organisation-kinds", _administrator);

        Assert.Equal(HttpStatusCode.OK, tree.StatusCode);
        using var treeBody = await JsonAsync(tree);
        var first = treeBody.RootElement.GetProperty("data")[0];
        Assert.Equal(JsonValueKind.Null, first.GetProperty("parent_id").ValueKind);
        Assert.Equal(KernelOrganisationKinds.Root, first.GetProperty("kind").GetString());
        Assert.Equal("active", first.GetProperty("status").GetString());
        using var kindsBody = await JsonAsync(kinds);
        Assert.Equal(KernelOrganisationKinds.Root, kindsBody.RootElement.GetProperty("root_kind").GetString());
        Assert.Equal([KernelOrganisationKinds.Root, KernelOrganisationKinds.Unit], kindsBody.RootElement.GetProperty("data").EnumerateArray().Select(k => k.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task AUnitIsCreatedUnderItsParentAndAudited()
    {
        var root = await RootIdAsync();

        using var created = await SendAsync(HttpMethod.Post, $"/api/v1/organisation-units/{root}/children", _administrator, new { name = "Sales", code = OrganisationUnitServiceTests.Code(), kind = KernelOrganisationKinds.Unit });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var body = await JsonAsync(created);
        var id = body.RootElement.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/organisation-units/{id}", created.Headers.Location?.ToString());
        Assert.Equal(root, body.RootElement.GetProperty("parent_id").GetGuid());
        Assert.Contains(_audit.Events, e => e.Action == AuthorizationAuditEvents.UnitCreated && e.RecordId == id && e.PrincipalId == _administrator);
    }

    [Fact]
    public async Task AKindTheParentDoesNotAllowAndACycleAreRefusedOnTheirFields()
    {
        var root = await RootIdAsync();
        var sales = await CreateAsync(root, "Sales");
        var north = await CreateAsync(sales, "North");

        using var secondRoot = await SendAsync(HttpMethod.Post, $"/api/v1/organisation-units/{root}/children", _administrator, new { name = "Root again", code = OrganisationUnitServiceTests.Code(), kind = KernelOrganisationKinds.Root });
        using var cycle = await SendAsync(HttpMethod.Post, $"/api/v1/organisation-units/{sales}/move", _administrator, new { parent_id = north });

        await AssertProblemAsync(secondRoot, HttpStatusCode.BadRequest, AuthorizationErrors.UnitKindNotAllowed, "kind");
        await AssertProblemAsync(cycle, HttpStatusCode.BadRequest, AuthorizationErrors.UnitCycle, "parent_id");
    }

    [Fact]
    public async Task AManagerOfAUnitShapesThatSubtreeAndNothingElse()
    {
        var root = await RootIdAsync();
        var sales = await CreateAsync(root, "Sales");
        var north = await CreateAsync(sales, "North");
        var finance = await CreateAsync(root, "Finance");
        var manager = Ids.New();
        await AssignAsync(manager, RoleScope.Unit(sales, includeChildren: true));

        using var inside = await SendAsync(HttpMethod.Post, $"/api/v1/organisation-units/{north}/children", manager, new { name = "City", code = OrganisationUnitServiceTests.Code(), kind = KernelOrganisationKinds.Unit });
        using var outside = await SendAsync(HttpMethod.Post, $"/api/v1/organisation-units/{finance}/children", manager, new { name = "Audit", code = OrganisationUnitServiceTests.Code(), kind = KernelOrganisationKinds.Unit });
        using var moveOut = await SendAsync(HttpMethod.Post, $"/api/v1/organisation-units/{north}/move", manager, new { parent_id = finance });
        using var rename = await SendAsync(HttpMethod.Post, $"/api/v1/organisation-units/{north}/rename", manager, new { name = "North and West" });

        Assert.Equal(HttpStatusCode.Created, inside.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, outside.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, moveOut.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
    }

    [Fact]
    public async Task AReaderOfASubtreeGetsThatPartOfTheTreeWithItsTopFirstAndTheKinds()
    {
        var root = await RootIdAsync();
        var sales = await CreateAsync(root, "Sales");
        var north = await CreateAsync(sales, "North");
        var finance = await CreateAsync(root, "Finance");
        var reader = Ids.New();
        await AssignAsync(reader, RoleScope.Unit(sales, includeChildren: true));

        using var tree = await SendAsync(HttpMethod.Get, "/api/v1/organisation-units", reader);
        using var kinds = await SendAsync(HttpMethod.Get, "/api/v1/organisation-kinds", reader);
        using var outside = await SendAsync(HttpMethod.Get, $"/api/v1/organisation-units/{finance}", reader);

        Assert.Equal(HttpStatusCode.OK, tree.StatusCode);
        using var body = await JsonAsync(tree);
        Assert.Equal([sales, north], body.RootElement.GetProperty("data").EnumerateArray().Select(u => u.GetProperty("id").GetGuid()));
        Assert.Equal(root, body.RootElement.GetProperty("data")[0].GetProperty("parent_id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, kinds.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, outside.StatusCode);
    }

    [Fact]
    public async Task AUnitWithActiveChildrenAnswers409()
    {
        var root = await RootIdAsync();
        var sales = await CreateAsync(root, "Sales");
        await CreateAsync(sales, "North");

        using var refused = await SendAsync(HttpMethod.Post, $"/api/v1/organisation-units/{sales}/deactivate", _administrator);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        using var problem = await JsonAsync(refused);
        Assert.Equal(AuthorizationErrors.UnitHasActiveChildren, problem.RootElement.GetProperty("error_code").GetString());
    }

    private async Task<Guid> RootIdAsync()
    {
        using var tree = await SendAsync(HttpMethod.Get, "/api/v1/organisation-units", _administrator);
        using var body = await JsonAsync(tree);
        return body.RootElement.GetProperty("data")[0].GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateAsync(Guid parent, string name)
    {
        using var created = await SendAsync(HttpMethod.Post, $"/api/v1/organisation-units/{parent}/children", _administrator, new { name, code = OrganisationUnitServiceTests.Code(), kind = KernelOrganisationKinds.Unit });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var body = await JsonAsync(created);
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private async Task AssignAsync(Guid principal, RoleScope scope)
    {
        await using var services = _app!.Services.CreateAsyncScope();
        var result = await services.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(principal, SystemRoles.TenantAdministrator, scope, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Guid? principal, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (principal is not null)
        {
            request.Headers.Add(HeaderPrincipal.HeaderName, principal.Value.ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code, string field)
    {
        Assert.Equal(status, response.StatusCode);
        using var problem = await JsonAsync(response);
        Assert.Equal(code, problem.RootElement.GetProperty("error_code").GetString());
        Assert.Equal(field, problem.RootElement.GetProperty("errors")[0].GetProperty("field").GetString());
    }
}
