using System.Net;
using System.Text;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Problems;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Data;
using Iron.Kernel.Events;
using Iron.Kernel.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Authorization.Tests;

/// <summary>
/// The assignment API over HTTP (TF-033, ADR-0031), on a tenant database of its own so that its administrator is the
/// only one: permissioned, validated, refusing escalation and the removal of the last administrator.
/// </summary>
[Collection(AuthorizationCollection.Name)]
public class RoleAssignmentsEndpointTests(AuthorizationFixture fixture) : IAsyncLifetime
{
    private readonly RecordingAuditWriter _audit = new();
    private Guid _administrator;
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var connectionString = await fixture.CreateTenantDatabaseAsync($"assign_{Ids.New():N}");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentPrincipal, HeaderPrincipal>();
        builder.Services.AddSingleton<IAuditWriter>(_audit);
        builder.Services.AddSingleton<IPrincipals>(fixture.Principals);
        builder.Services.AddIronApi();
        builder.Services.AddIronDatabase(connectionString);
        builder.Services.AddModules(builder.Configuration, [new EventsModule(), new AuthorizationModule()]);
        builder.Services.AddScoped<IEntitlementGate, AllEntitled>();
        builder.Services.AddScoped<IFlagGate, AllEntitled>();

        _app = builder.Build();
        _app.UseIronTelemetry();
        _app.UseIronApi();
        _app.MapModules();
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = _app.GetTestClient();
        _administrator = fixture.Principals.Add();
        await using var scope = _app.Services.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(_administrator, SystemRoles.TenantAdministrator, TestContext.Current.CancellationToken)).IsSuccess);
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
    [Proves("ADR-0012", "ADR-0031")]
    public async Task NobodySignedInGets401AndSomebodyWithoutTheReadPermissionGets403()
    {
        using var anonymous = await SendAsync(HttpMethod.Get, "/api/v1/role-assignments", null);
        using var stranger = await SendAsync(HttpMethod.Get, "/api/v1/role-assignments", fixture.Principals.Add());

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await AssertProblemAsync(stranger, HttpStatusCode.Forbidden, PermissionErrors.PermissionDenied);
    }

    [Fact]
    [Proves("TF-033 AC5")]
    public async Task TheAdministratorGrantsAtAUnitWithChildrenListsTheGrantForThePrincipalAndRemovesIt()
    {
        var root = await RootIdAsync();
        var person = fixture.Principals.Add();
        var readOnly = await RoleIdAsync(SystemRoles.ReadOnly);

        using var granted = await SendAsync(HttpMethod.Post, "/api/v1/role-assignments", _administrator, $$"""{"principal_id":"{{person}}","role_id":"{{readOnly}}","scope_kind":"unit_with_children","unit_id":"{{root}}"}""");
        Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
        using var grantedBody = await JsonAsync(granted);
        var id = grantedBody.RootElement.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/role-assignments/{id}", granted.Headers.Location?.ToString());
        Assert.Equal("unit_with_children", grantedBody.RootElement.GetProperty("scope_kind").GetString());

        using var list = await SendAsync(HttpMethod.Get, $"/api/v1/role-assignments?filter=principal_id:eq:{person}", _administrator);
        using var listBody = await JsonAsync(list);
        var row = Assert.Single(listBody.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal(SystemRoles.ReadOnly, row.GetProperty("role_code").GetString());
        Assert.Equal("Read only", row.GetProperty("role_name").GetString());
        Assert.Equal(root, row.GetProperty("unit_path")[0].GetProperty("id").GetGuid());
        Assert.StartsWith("Principal", row.GetProperty("principal_name").GetString(), StringComparison.Ordinal);

        using var forUnit = await SendAsync(HttpMethod.Get, $"/api/v1/role-assignments?filter=unit_id:eq:{root}", _administrator);
        using var forUnitBody = await JsonAsync(forUnit);
        Assert.Contains(forUnitBody.RootElement.GetProperty("data").EnumerateArray(), a => a.GetProperty("id").GetGuid() == id);

        using var removed = await SendAsync(HttpMethod.Delete, $"/api/v1/role-assignments/{id}", _administrator);
        using var gone = await SendAsync(HttpMethod.Get, $"/api/v1/role-assignments/{id}", _administrator);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        await AssertProblemAsync(gone, HttpStatusCode.NotFound, AuthorizationErrors.AssignmentNotFound);
        Assert.Equal(
            [AuthorizationAuditEvents.RoleAssigned, AuthorizationAuditEvents.RoleUnassigned],
            _audit.Events.Where(e => e.RecordId == id).Select(e => e.Action));
    }

    [Fact]
    [Proves("ADR-0031", "ADR-0033")]
    public async Task EveryoneReadsTheirOwnAssignmentsAndNobodyElses()
    {
        var root = await RootIdAsync();
        var person = fixture.Principals.Add();
        var colleague = fixture.Principals.Add();
        var readOnly = await RoleIdAsync(SystemRoles.ReadOnly);
        // A role without authz.assignments.read, so the person may not list anybody's assignments.
        using var unitReader = await SendAsync(HttpMethod.Post, "/api/v1/roles", _administrator, $$"""{"code":"unit-reader-{{Ids.New():N}}","name":"Unit reader","description":"Sees the tree.","principal_type":"user","permissions":["authz.units.read"]}""");
        Assert.Equal(HttpStatusCode.Created, unitReader.StatusCode);
        using var unitReaderBody = await JsonAsync(unitReader);
        var unitReaderId = unitReaderBody.RootElement.GetProperty("id").GetGuid();
        using var granted = await SendAsync(HttpMethod.Post, "/api/v1/role-assignments", _administrator, $$"""{"principal_id":"{{person}}","role_id":"{{unitReaderId}}","scope_kind":"unit","unit_id":"{{root}}"}""");
        using var other = await SendAsync(HttpMethod.Post, "/api/v1/role-assignments", _administrator, $$"""{"principal_id":"{{colleague}}","role_id":"{{readOnly}}","scope_kind":"tenant"}""");
        Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);

        // The person holds no authz.assignments.read: the list refuses, their own assignments do not.
        using var list = await SendAsync(HttpMethod.Get, $"/api/v1/role-assignments?filter=principal_id:eq:{person}", person);
        using var mine = await SendAsync(HttpMethod.Get, "/api/v1/role-assignments/mine", person);
        using var anonymous = await SendAsync(HttpMethod.Get, "/api/v1/role-assignments/mine", null);

        await AssertProblemAsync(list, HttpStatusCode.Forbidden, PermissionErrors.PermissionDenied);
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        using var body = await JsonAsync(mine);
        var row = Assert.Single(body.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal(person, row.GetProperty("principal_id").GetGuid());
        Assert.Equal("Unit reader", row.GetProperty("role_name").GetString());
        Assert.Equal("unit", row.GetProperty("scope_kind").GetString());
        Assert.Equal(root, row.GetProperty("unit_path")[0].GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    [Proves("ADR-0009")]
    public async Task AUnitScopeWithoutAUnitAnswers400WithTheField()
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/role-assignments", _administrator, $$"""{"principal_id":"{{fixture.Principals.Add()}}","role_id":"{{await RoleIdAsync(SystemRoles.ReadOnly)}}","scope_kind":"unit"}""");

        Assert.Equal("unit_id", await AssertProblemAsync(response, HttpStatusCode.BadRequest, ApiErrors.Validation));
    }

    [Fact]
    [Proves("ADR-0031")]
    public async Task AGranterWithoutTheRolesPermissionsAnswers403AndTheLastAdministratorAnswers409()
    {
        var root = await RootIdAsync();
        var granter = fixture.Principals.Add();
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
            var role = new Role(RoleServiceTests.Code(), "Granter", string.Empty, isSystem: false, PrincipalKind.User, [AuthorizationPermissions.AssignmentsManage.Name, AuthorizationPermissions.AssignmentsRead.Name]);
            context.Roles.Add(role);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await RoleServiceTests.AssignAsync(scope, granter, role.Code, RoleScope.Unit(root, includeChildren: true));
        }

        using var escalation = await SendAsync(HttpMethod.Post, "/api/v1/role-assignments", granter, $$"""{"principal_id":"{{fixture.Principals.Add()}}","role_id":"{{await RoleIdAsync(SystemRoles.ReadOnly)}}","scope_kind":"unit","unit_id":"{{root}}"}""");
        using var last = await SendAsync(HttpMethod.Delete, $"/api/v1/role-assignments/{await AdministratorAssignmentAsync()}", _administrator);

        await AssertProblemAsync(escalation, HttpStatusCode.Forbidden, AuthorizationErrors.AssignmentEscalation);
        await AssertProblemAsync(last, HttpStatusCode.Conflict, AuthorizationErrors.AssignmentLastAdministrator);
    }

    private async Task<Guid> AdministratorAssignmentAsync()
    {
        await using var scope = _app!.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>().RoleAssignments
            .Where(a => a.PrincipalId == _administrator && a.ScopeKind == Scopes.Tenant)
            .Select(a => a.Id)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> RoleIdAsync(string code)
    {
        using var list = await SendAsync(HttpMethod.Get, $"/api/v1/roles?filter=code:eq:{code}&fields=id", _administrator);
        using var body = await JsonAsync(list);
        return body.RootElement.GetProperty("data")[0].GetProperty("id").GetGuid();
    }

    private async Task<Guid> RootIdAsync()
    {
        using var tree = await SendAsync(HttpMethod.Get, "/api/v1/organisation-units", _administrator);
        using var body = await JsonAsync(tree);
        return body.RootElement.GetProperty("data")[0].GetProperty("id").GetGuid();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Guid? principal, string? json = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (principal is not null)
        {
            request.Headers.Add(HeaderPrincipal.HeaderName, principal.Value.ToString());
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    /// <summary>Asserts the status and the error code, and answers the field of the first field error, if there is one.</summary>
    private static async Task<string?> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var problem = await JsonAsync(response);
        Assert.Equal(code, problem.RootElement.GetProperty("error_code").GetString());
        return problem.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0 ? errors[0].GetProperty("field").GetString() : null;
    }
}
