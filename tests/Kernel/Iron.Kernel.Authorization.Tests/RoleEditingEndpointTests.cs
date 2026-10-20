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
using Iron.Kernel.Data;
using Iron.Kernel.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Authorization.Tests;

/// <summary>The role API over HTTP (TF-032, ADR-0031): created, changed and deleted tenant-wide, read at any scope, the catalogue, export and import.</summary>
[Collection(AuthorizationCollection.Name)]
public class RoleEditingEndpointTests(AuthorizationFixture fixture) : IAsyncLifetime
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
    [Untraced("Creating, reading, changing and deleting a tenant role over HTTP is TF-032's requirement text; no criterion or ADR rule states it.")]
    public async Task TheAdministratorCreatesReadsChangesAndDeletesARole()
    {
        var code = RoleServiceTests.Code();

        using var created = await SendAsync(HttpMethod.Post, "/api/v1/roles", _administrator, $$"""{"code":"{{code}}","name":"Unit reader","description":"Reads units.","principal_type":"user","permissions":["authz.units.read"]}""");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonAsync(created)).RootElement.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/roles/{id}", created.Headers.Location?.ToString());

        using var read = await SendAsync(HttpMethod.Get, $"/api/v1/roles/{id}", _administrator);
        using var readBody = await JsonAsync(read);
        Assert.Equal(code, readBody.RootElement.GetProperty("code").GetString());
        Assert.Equal("user", readBody.RootElement.GetProperty("principal_type").GetString());
        Assert.False(readBody.RootElement.GetProperty("is_system").GetBoolean());

        using var changed = await SendAsync(HttpMethod.Put, $"/api/v1/roles/{id}", _administrator, """{"name":"Reader","description":"","principal_type":"user","permissions":["authz.units.read","authz.roles.read"]}""");
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var changedBody = await JsonAsync(changed);
        Assert.Equal(["authz.roles.read", "authz.units.read"], changedBody.RootElement.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));

        using var deleted = await SendAsync(HttpMethod.Delete, $"/api/v1/roles/{id}", _administrator);
        using var gone = await SendAsync(HttpMethod.Get, $"/api/v1/roles/{id}", _administrator);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await AssertProblemAsync(gone, HttpStatusCode.NotFound, AuthorizationErrors.RoleNotFound);
        Assert.Equal(
            [AuthorizationAuditEvents.RoleCreated, AuthorizationAuditEvents.RoleChanged, AuthorizationAuditEvents.RoleDeleted],
            _audit.Events.Where(e => e.RecordId == id).Select(e => e.Action));
    }

    [Fact]
    [Proves("TF-032 AC2", "ADR-0031")]
    public async Task ASystemRoleAnswers409AndABadCodeAnswers400WithTheField()
    {
        var readOnly = await RoleIdAsync(SystemRoles.ReadOnly);

        using var change = await SendAsync(HttpMethod.Put, $"/api/v1/roles/{readOnly}", _administrator, """{"name":"Mine","description":"","principal_type":"user","permissions":[]}""");
        using var badCode = await SendAsync(HttpMethod.Post, "/api/v1/roles", _administrator, """{"code":"Not A Code","name":"Bad","principal_type":"user","permissions":[]}""");

        await AssertProblemAsync(change, HttpStatusCode.Conflict, AuthorizationErrors.RoleSystem);
        Assert.Equal("code", await AssertProblemAsync(badCode, HttpStatusCode.BadRequest, ApiErrors.Validation));
    }

    [Fact]
    [Proves("ADR-0031")]
    public async Task AUnitAdministratorReadsRolesAndTheCatalogueButChangesNoRole()
    {
        var unitAdministrator = Ids.New();
        await AssignAsync(unitAdministrator, RoleScope.Unit(await RootIdAsync(), includeChildren: true));

        using var list = await SendAsync(HttpMethod.Get, "/api/v1/roles", unitAdministrator);
        using var catalogue = await SendAsync(HttpMethod.Get, "/api/v1/permissions", unitAdministrator);
        using var create = await SendAsync(HttpMethod.Post, "/api/v1/roles", unitAdministrator, $$"""{"code":"{{RoleServiceTests.Code()}}","name":"Mine","principal_type":"user","permissions":[]}""");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, catalogue.StatusCode);
        using var body = await JsonAsync(catalogue);
        var manage = body.RootElement.GetProperty("data").EnumerateArray().Single(p => p.GetProperty("name").GetString() == AuthorizationPermissions.RolesManage.Name);
        Assert.Equal("authz", manage.GetProperty("module").GetString());
        Assert.Equal("high", manage.GetProperty("risk").GetString());
        Assert.False(string.IsNullOrEmpty(manage.GetProperty("description").GetString()));
        await AssertProblemAsync(create, HttpStatusCode.Forbidden, PermissionErrors.PermissionDenied);
    }

    [Fact]
    [Untraced("The export format and a dry run that changes nothing are in the module README; TF-032 AC3 and ADR-0031 cover only unknown permissions and system roles.")]
    public async Task TheExportImportsAsADryRunThatChangesNothing()
    {
        var code = RoleServiceTests.Code();
        using (var created = await SendAsync(HttpMethod.Post, "/api/v1/roles", _administrator, $$"""{"code":"{{code}}","name":"Exported","principal_type":"service","permissions":["authz.units.read"]}"""))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using var export = await SendAsync(HttpMethod.Get, "/api/v1/roles/export", _administrator);
        using var document = await JsonAsync(export);
        Assert.Equal(1, document.RootElement.GetProperty("format_version").GetInt32());
        var exported = document.RootElement.GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("code").GetString() == code);
        Assert.Equal("service", exported.GetProperty("principal_type").GetString());

        var renamed = $$"""{"format_version":1,"roles":[{"code":"{{code}}","name":"Renamed","description":"","principal_type":"service","permissions":["authz.units.read","authz.roles.read"]}]}""";
        using var dryRun = await SendAsync(HttpMethod.Post, "/api/v1/roles/import", _administrator, $$"""{"dry_run":true,"document":{{renamed}}}""");
        using var unknownVersion = await SendAsync(HttpMethod.Post, "/api/v1/roles/import", _administrator, """{"dry_run":true,"document":{"format_version":9,"roles":[]}}""");

        Assert.Equal(HttpStatusCode.OK, dryRun.StatusCode);
        using var diff = await JsonAsync(dryRun);
        Assert.False(diff.RootElement.GetProperty("applied").GetBoolean());
        var change = Assert.Single(diff.RootElement.GetProperty("changed").EnumerateArray());
        Assert.Equal(["authz.roles.read"], change.GetProperty("added").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(["name"], change.GetProperty("fields").EnumerateArray().Select(p => p.GetString()));
        using var stillNamed = await SendAsync(HttpMethod.Get, $"/api/v1/roles?filter=code:eq:{code}&fields=name", _administrator);
        Assert.Equal("Exported", (await JsonAsync(stillNamed)).RootElement.GetProperty("data")[0].GetProperty("name").GetString());
        await AssertProblemAsync(unknownVersion, HttpStatusCode.BadRequest, AuthorizationErrors.ImportFormat);
    }

    private async Task<Guid> RoleIdAsync(string code)
    {
        using var list = await SendAsync(HttpMethod.Get, $"/api/v1/roles?filter=code:eq:{code}&fields=id", _administrator);
        return (await JsonAsync(list)).RootElement.GetProperty("data")[0].GetProperty("id").GetGuid();
    }

    private async Task<Guid> RootIdAsync()
    {
        using var tree = await SendAsync(HttpMethod.Get, "/api/v1/organisation-units", _administrator);
        return (await JsonAsync(tree)).RootElement.GetProperty("data")[0].GetProperty("id").GetGuid();
    }

    private async Task AssignAsync(Guid principal, RoleScope scope)
    {
        await using var services = _app!.Services.CreateAsyncScope();
        var result = await services.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(principal, SystemRoles.TenantAdministrator, scope, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
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
