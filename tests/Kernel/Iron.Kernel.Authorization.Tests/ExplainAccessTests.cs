using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Routing;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Visibility;
using Iron.Kernel.Events;
using Iron.Kernel.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Authorization.Tests;

/// <summary>
/// Explaining access (TF-039, ADR-0039) over HTTP: the explanation agrees with the real call over a matrix of cases;
/// a denial names its first failing check and an allowance the granting role and scope; an unknown person is not
/// found; explaining is permissioned at the person's home unit and audited; a record outside the person's visibility
/// tags is explained by its tag check (TF-241 AC2, ADR-0057).
/// </summary>
[Collection(AuthorizationCollection.Name)]
public class ExplainAccessTests(AuthorizationFixture fixture) : IAsyncLifetime
{
    private readonly RecordingAuditWriter _audit = new();
    private readonly ControlledGates _gates = new();
    private readonly HeldTags _tags = new();
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentPrincipal, ActingHeaderPrincipal>();
        builder.Services.AddSingleton<IAuditWriter>(_audit);
        builder.Services.AddSingleton<IPrincipals>(fixture.Principals);
        builder.Services.AddSingleton<IEntitlementGate>(_gates);
        builder.Services.AddSingleton<IFlagGate>(_gates);
        builder.Services.AddSingleton<IVisibilityTags>(_tags);
        builder.Services.AddIronApi();
        builder.Services.AddIronDatabase(fixture.ConnectionString);
        builder.Services.AddModules(builder.Configuration, [new EventsModule(), new AuthorizationModule(), new DutyProbeModule(), new LicensedProbeModule()]);

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
    [Proves("ADR-0039")]
    public async Task TheExplanationAgreesWithTheRealCallOverAMatrixOfCases()
    {
        var world = await WorldAsync();
        var cases = new List<Case>
        {
            // A list that keeps its own rows: the permission held anywhere, as oneself or by delegation.
            new("tenant administrator lists roles", world.Administrator, null, HttpMethod.Get, "/api/v1/roles", "ListRoles", NoValues),
            new("a unit manager lists roles", world.SalesManager, null, HttpMethod.Get, "/api/v1/roles", "ListRoles", NoValues),
            new("a delegate acting with the receiving duty lists roles", world.Delegate, world.Administrator, HttpMethod.Get, "/api/v1/roles", "ListRoles", NoValues),
            new("the delegate as themselves lists roles", world.Delegate, null, HttpMethod.Get, "/api/v1/roles", "ListRoles", NoValues),

            // A unit route: the permission at the unit, with the units below it or not, and not while acting without it.
            new("a manager renames a unit below theirs", world.SalesManager, null, HttpMethod.Post, $"/api/v1/organisation-units/{world.North.Id}/rename", "RenameOrganisationUnit", Values(world.North.Id), new { name = world.North.Name }),
            new("a manager renames a unit outside theirs", world.SalesManager, null, HttpMethod.Post, $"/api/v1/organisation-units/{world.Finance.Id}/rename", "RenameOrganisationUnit", Values(world.Finance.Id), new { name = world.Finance.Name }),
            new("a reader renames their own unit", world.FinanceReader, null, HttpMethod.Post, $"/api/v1/organisation-units/{world.Finance.Id}/rename", "RenameOrganisationUnit", Values(world.Finance.Id), new { name = world.Finance.Name }),
            new("a delegate acting without the duty renames a unit", world.Delegate, world.Administrator, HttpMethod.Post, $"/api/v1/organisation-units/{world.North.Id}/rename", "RenameOrganisationUnit", Values(world.North.Id), new { name = world.North.Name }),

            // A feature, a kill switch and the permission, in that order.
            new("the administrator imports roles", world.Administrator, null, HttpMethod.Post, "/api/v1/roles/import", "ImportRoles", NoValues, EmptyImport),
            new("the administrator imports roles without the feature", world.Administrator, null, HttpMethod.Post, "/api/v1/roles/import", "ImportRoles", NoValues, EmptyImport, Off: AuthorizationFeatures.RoleTransfer.Code),
            new("the administrator imports roles with the kill switch on", world.Administrator, null, HttpMethod.Post, "/api/v1/roles/import", "ImportRoles", NoValues, EmptyImport, Killed: KillSwitches.Imports.Key),
            new("a person without roles imports roles", world.Nobody, null, HttpMethod.Post, "/api/v1/roles/import", "ImportRoles", NoValues, EmptyImport),
            new("a person without roles imports roles without the feature", world.Nobody, null, HttpMethod.Post, "/api/v1/roles/import", "ImportRoles", NoValues, EmptyImport, Off: AuthorizationFeatures.RoleTransfer.Code),

            // A licensed module, on and off.
            new("the administrator reads a licensed module", world.Administrator, null, HttpMethod.Get, "/api/v1/licensed/things", "ListLicensedThings", NoValues),
            new("the administrator reads a licensed module that is off", world.Administrator, null, HttpMethod.Get, "/api/v1/licensed/things", "ListLicensedThings", NoValues, Disabled: LicensedProbeModule.Licence.Code),

            // A record route: the permission at the record's unit, then its policy.
            new("a manager reads a crate below their unit", world.SalesManager, null, HttpMethod.Get, $"/api/v1/probe/crates/{world.NorthCrate}", "GetProbeCrate", Values(world.NorthCrate)),
            new("a manager reads a crate outside their unit", world.SalesManager, null, HttpMethod.Get, $"/api/v1/probe/crates/{world.FinanceCrate}", "GetProbeCrate", Values(world.FinanceCrate)),
            new("a reader reads a crate of their unit", world.FinanceReader, null, HttpMethod.Get, $"/api/v1/probe/crates/{world.FinanceCrate}", "GetProbeCrate", Values(world.FinanceCrate)),
            new("a reader changes a crate their policy keeps them from changing", world.FinanceReader, null, HttpMethod.Post, $"/api/v1/probe/crates/{world.FinanceCrate}/update", "UpdateProbeCrate", Values(world.FinanceCrate)),
            new("a manager changes a crate below their unit", world.SalesManager, null, HttpMethod.Post, $"/api/v1/probe/crates/{world.NorthCrate}/update", "UpdateProbeCrate", Values(world.NorthCrate)),
            new("the administrator reads a crate that does not exist", world.Administrator, null, HttpMethod.Get, $"/api/v1/probe/crates/{world.Missing}", "GetProbeCrate", Values(world.Missing)),

            // Then the reader's visibility tags: a crate of another tag is not found, one sharing a tag is seen.
            new("a reader of one tag reads a crate of another", world.FinanceReader, null, HttpMethod.Get, $"/api/v1/probe/crates/{world.LatamCrate}", "GetProbeCrate", Values(world.LatamCrate)),
            new("a reader of one tag reads a crate sharing it", world.FinanceReader, null, HttpMethod.Get, $"/api/v1/probe/crates/{world.MixedCrate}", "GetProbeCrate", Values(world.MixedCrate)),
            new("a reader of one tag changes a crate of another", world.FinanceReader, null, HttpMethod.Post, $"/api/v1/probe/crates/{world.LatamCrate}/update", "UpdateProbeCrate", Values(world.LatamCrate)),

            // Signed in is enough.
            new("a person without roles reads their own assignments", world.Nobody, null, HttpMethod.Get, "/api/v1/role-assignments/mine", "MyRoleAssignments", NoValues),
        };

        var disagreements = new List<string>();
        var answered = new HashSet<int>();
        foreach (var item in cases)
        {
            _gates.Set(item.Disabled, item.Off, item.Killed);
            var real = await RealCallAsync(item);
            answered.Add((int)real);
            using var explained = await ExplainAsync(world.Administrator, item.Principal, item.Action, item.Values, item.ActingFor);
            Assert.Equal(HttpStatusCode.OK, explained.StatusCode);
            using var body = await JsonAsync(explained);
            var allowed = body.RootElement.GetProperty("allowed").GetBoolean();
            var status = body.RootElement.GetProperty("status").GetInt32();
            var agrees = allowed ? (int)real is >= 200 and < 300 : (int)real == status;
            if (!agrees)
            {
                disagreements.Add($"{item.Name}: the API answered {(int)real}, the explanation {(allowed ? "allowed" : status.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
            }
        }

        _gates.Set(null, null, null);
        Assert.Empty(disagreements);

        // The matrix reaches every kind of answer: allowed, refused by a check, not found by a policy, stopped by a switch.
        Assert.Equal([200, 403, 404, 503], answered.Order());
    }

    [Fact]
    [Proves("TF-039 AC1", "ADR-0039")]
    public async Task ADenialNamesTheFirstFailingCheckAndTheChecksAfterItAreNotReached()
    {
        var world = await WorldAsync();
        _gates.Set(null, AuthorizationFeatures.RoleTransfer.Code, null);

        using var explained = await ExplainAsync(world.Administrator, world.Nobody, "ImportRoles", NoValues);
        _gates.Set(null, null, null);

        using var body = await JsonAsync(explained);
        Assert.False(body.RootElement.GetProperty("allowed").GetBoolean());
        Assert.Equal(403, body.RootElement.GetProperty("status").GetInt32());
        var checks = body.RootElement.GetProperty("checks").EnumerateArray().Select(c => (c.GetProperty("check").GetString(), c.GetProperty("result").GetString())).ToList();
        Assert.Equal(
            [("principal", "passed"), ("entitlement", "failed"), ("flags", "not_reached"), ("permission", "not_reached"), ("record_policy", "not_reached"), ("visibility_tags", "not_reached")],
            checks);
        var entitlement = body.RootElement.GetProperty("checks")[1];
        Assert.Equal((EntitlementErrors.FeatureDisabled, AuthorizationFeatures.RoleTransfer.Code), (entitlement.GetProperty("error_code").GetString(), entitlement.GetProperty("facts").GetProperty("subject").GetString()));
    }

    [Fact]
    [Proves("TF-039 AC2")]
    public async Task AnAllowanceNamesTheGrantingRoleAndScope()
    {
        var world = await WorldAsync();

        using var explained = await ExplainAsync(world.Administrator, world.SalesManager, "RenameOrganisationUnit", Values(world.North.Id));

        using var body = await JsonAsync(explained);
        Assert.True(body.RootElement.GetProperty("allowed").GetBoolean());
        var permission = body.RootElement.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("check").GetString() == "permission");
        Assert.Equal(("passed", "unit", "North"), (permission.GetProperty("result").GetString(), permission.GetProperty("facts").GetProperty("scope").GetString(), permission.GetProperty("facts").GetProperty("unit").GetString()));
        var grant = Assert.Single(permission.GetProperty("granted_by").EnumerateArray());
        Assert.Equal(("Units and crates manager", "unit_with_children", "Sales"), (grant.GetProperty("role_name").GetString(), grant.GetProperty("scope_kind").GetString(), grant.GetProperty("unit").GetProperty("name").GetString()));
    }

    [Fact]
    [Proves("TF-039 AC1", "ADR-0039")]
    public async Task ARecordPolicyAndActingForSomeoneAreExplained()
    {
        var world = await WorldAsync();

        using var forbidden = await ExplainAsync(world.Administrator, world.FinanceReader, "UpdateProbeCrate", Values(world.FinanceCrate));
        using var acting = await ExplainAsync(world.Administrator, world.Delegate, "RenameOrganisationUnit", Values(world.North.Id), world.Administrator);
        using var missing = await ExplainAsync(world.Administrator, world.Administrator, "GetProbeCrate", Values(world.Missing));

        using (var body = await JsonAsync(forbidden))
        {
            var policy = body.RootElement.GetProperty("checks")[4];
            Assert.Equal(("failed", "forbidden", 403), (policy.GetProperty("result").GetString(), policy.GetProperty("reason").GetString(), policy.GetProperty("status").GetInt32()));
            Assert.Equal("update", policy.GetProperty("facts").GetProperty("action").GetString());
        }

        using (var body = await JsonAsync(acting))
        {
            Assert.Equal(world.Administrator, body.RootElement.GetProperty("acting_for").GetProperty("id").GetGuid());
            var permission = body.RootElement.GetProperty("checks")[3];
            Assert.Equal(("failed", "not_granted"), (permission.GetProperty("result").GetString(), permission.GetProperty("reason").GetString()));
            Assert.Equal("1", permission.GetProperty("facts").GetProperty("acting_permissions").GetString());
        }

        using (var body = await JsonAsync(missing))
        {
            var policy = body.RootElement.GetProperty("checks")[4];
            Assert.Equal(("failed", "no_such_record", 404), (policy.GetProperty("result").GetString(), policy.GetProperty("reason").GetString(), policy.GetProperty("status").GetInt32()));
        }
    }

    [Fact]
    [Proves("TF-241 AC2", "TF-039 AC1", "ADR-0057")]
    public async Task ACrateOfAnotherTagIsNotFoundAndTheExplanationNamesTheTagCheckWithTheTagsCompared()
    {
        var world = await WorldAsync();

        using var real = await SendAsync(HttpMethod.Get, $"/api/v1/probe/crates/{world.LatamCrate}", world.FinanceReader, null, null);
        using var hidden = await ExplainAsync(world.Administrator, world.FinanceReader, "GetProbeCrate", Values(world.LatamCrate));
        using var shared = await ExplainAsync(world.Administrator, world.FinanceReader, "GetProbeCrate", Values(world.MixedCrate));

        Assert.Equal(HttpStatusCode.NotFound, real.StatusCode);
        using (var body = await JsonAsync(hidden))
        {
            Assert.Equal((false, 404), (body.RootElement.GetProperty("allowed").GetBoolean(), body.RootElement.GetProperty("status").GetInt32()));
            var checks = body.RootElement.GetProperty("checks");
            Assert.Equal(("record_policy", "passed", "visible"), (checks[4].GetProperty("check").GetString(), checks[4].GetProperty("result").GetString(), checks[4].GetProperty("reason").GetString()));
            var tags = checks[5];
            Assert.Equal(("visibility_tags", "failed", "no_shared_tag", 404), (tags.GetProperty("check").GetString(), tags.GetProperty("result").GetString(), tags.GetProperty("reason").GetString(), tags.GetProperty("status").GetInt32()));
            Assert.Equal(("LATAM", "APAC"), (tags.GetProperty("facts").GetProperty("record_tags").GetString(), tags.GetProperty("facts").GetProperty("held_tags").GetString()));
            Assert.Equal("The record carries LATAM; they hold APAC. They share no tag, so the record answers not found.", tags.GetProperty("message").GetString());
        }

        using (var body = await JsonAsync(shared))
        {
            Assert.True(body.RootElement.GetProperty("allowed").GetBoolean());
            var tags = body.RootElement.GetProperty("checks")[5];
            Assert.Equal(("passed", "shared_tag", "APAC"), (tags.GetProperty("result").GetString(), tags.GetProperty("reason").GetString(), tags.GetProperty("facts").GetProperty("shared_tags").GetString()));
        }
    }

    [Fact]
    [Proves("TF-039 AC3", "ADR-0039")]
    public async Task AnUnknownPersonIsNotFoundAndAnInvitedOneIsRefusedAtSignIn()
    {
        var world = await WorldAsync();
        var invited = fixture.Principals.Add(homeUnit: world.Sales.Id, status: PrincipalStatuses.Invited);

        using var unknown = await ExplainAsync(world.Administrator, Ids.New(), "ListRoles", NoValues);
        using var notYet = await ExplainAsync(world.Administrator, invited, "ListRoles", NoValues);
        using var noSuchAction = await ExplainAsync(world.Administrator, world.Nobody, "NoSuchAction", NoValues);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(AuthorizationErrors.PrincipalNotFound, await ErrorCodeAsync(unknown));
        using (var body = await JsonAsync(notYet))
        {
            Assert.Equal(401, body.RootElement.GetProperty("status").GetInt32());
            Assert.Equal(("failed", "invited"), (body.RootElement.GetProperty("checks")[0].GetProperty("result").GetString(), body.RootElement.GetProperty("checks")[0].GetProperty("reason").GetString()));
        }

        Assert.Equal(HttpStatusCode.NotFound, noSuchAction.StatusCode);
        Assert.Equal(AuthorizationErrors.ExplainActionUnknown, await ErrorCodeAsync(noSuchAction));
    }

    [Fact]
    [Proves("ADR-0039")]
    public async Task ExplainingNeedsThePermissionAtThePersonsHomeUnitAndIsAudited()
    {
        var world = await WorldAsync();
        var financeExplainer = fixture.Principals.Add(homeUnit: world.Finance.Id);
        await GrantAsync(financeExplainer, "Finance explainer", [AuthorizationPermissions.AccessExplain.Name], RoleScope.Unit(world.Finance.Id));

        using var outside = await ExplainAsync(financeExplainer, world.SalesManager, "ListRoles", NoValues);
        using var inside = await ExplainAsync(financeExplainer, world.FinanceReader, "ListRoles", NoValues);

        Assert.Equal(HttpStatusCode.Forbidden, outside.StatusCode);
        Assert.Equal(HttpStatusCode.OK, inside.StatusCode);
        var audited = Assert.Single(_audit.Events, e => e.Action == AuthorizationAuditEvents.AccessExplained && e.PrincipalId == financeExplainer);
        Assert.Equal(world.FinanceReader, audited.RecordId);
        Assert.Equal(("ListRoles", "denied", "permission"), (audited.Details!["action"], audited.Details["outcome"], audited.Details["failed_check"]));
    }

    [Fact]
    [Proves("ADR-0039")]
    public async Task TheActionsAreListedByModuleWithTheRouteValuesTheyNeed()
    {
        var world = await WorldAsync();

        using var list = await SendAsync(HttpMethod.Get, "/api/v1/access/actions", world.Administrator, null, null);

        using var body = await JsonAsync(list);
        var actions = body.RootElement.GetProperty("data").EnumerateArray().ToList();
        var rename = actions.Single(a => a.GetProperty("name").GetString() == "RenameOrganisationUnit");
        Assert.Equal(("authz", "authz.units.manage"), (rename.GetProperty("module").GetString(), rename.GetProperty("permission").GetString()));
        Assert.Equal(("id", "unit"), (rename.GetProperty("route_values")[0].GetProperty("name").GetString(), rename.GetProperty("route_values")[0].GetProperty("kind").GetString()));
        var crate = actions.Single(a => a.GetProperty("name").GetString() == "GetProbeCrate");
        Assert.Equal(("record", ProbeRecords.CrateType), (crate.GetProperty("route_values")[0].GetProperty("kind").GetString(), crate.GetProperty("route_values")[0].GetProperty("record_type").GetString()));
        Assert.DoesNotContain(actions, a => a.GetProperty("name").GetString() == "Login");
    }

    private static readonly object EmptyImport = new { dry_run = true, document = new { format_version = 1, roles = Array.Empty<object>() } };

    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>(StringComparer.Ordinal);

    private static Dictionary<string, string> Values(Guid id) => new(StringComparer.Ordinal) { ["id"] = id.ToString("D") };

    private async Task<HttpStatusCode> RealCallAsync(Case item)
    {
        using var response = await SendAsync(item.Method, item.Path, item.Principal, item.ActingFor, item.Body);
        return response.StatusCode;
    }

    private Task<HttpResponseMessage> ExplainAsync(Guid explainer, Guid principal, string action, IReadOnlyDictionary<string, string> values, Guid? actingFor = null)
    {
        var query = $"/api/v1/access/explain?principal={principal}&action={action}" + string.Concat(values.Select(v => $"&{v.Key}={v.Value}")) + (actingFor is { } delegator ? $"&acting_for={delegator}" : string.Empty);
        return SendAsync(HttpMethod.Get, query, explainer, null, null);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Guid principal, Guid? actingFor, object? body)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(HeaderPrincipal.HeaderName, principal.ToString());
        if (actingFor is { } delegator)
        {
            request.Headers.Add(ActingHeaderPrincipal.ActingHeader, delegator.ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var problem = await JsonAsync(response);
        return problem.RootElement.GetProperty("error_code").GetString();
    }

    /// <summary>
    /// A small tree of its own, Sales with North under it and Finance, crates in North and Finance, and the people of the
    /// matrix: a tenant administrator, a manager of Sales and below, a reader of Finance, a person without roles, and a
    /// delegate of the administrator for the receiving duty.
    /// </summary>
    private async Task<World> WorldAsync()
    {
        await using var scope = _app!.Services.CreateAsyncScope();
        var units = scope.ServiceProvider.GetRequiredService<OrganisationUnitService>();
        var root = await OrganisationUnitServiceTests.RootAsync(scope);
        var sales = await OrganisationUnitServiceTests.CreateAsync(units, root.Id, "Sales");
        var north = await OrganisationUnitServiceTests.CreateAsync(units, sales.Id, "North");
        var finance = await OrganisationUnitServiceTests.CreateAsync(units, root.Id, "Finance");

        var administrator = fixture.Principals.Add(homeUnit: root.Id, name: "Ada Lovelace");
        await RoleServiceTests.AssignAsync(scope, administrator, SystemRoles.TenantAdministrator);
        var manager = fixture.Principals.Add(homeUnit: north.Id, name: "Grace Hopper");
        await GrantAsync(manager, "Units and crates manager", [AuthorizationPermissions.UnitsRead.Name, AuthorizationPermissions.UnitsManage.Name, ProbeRecords.CratesRead.Name, ProbeRecords.CratesManage.Name], RoleScope.Unit(sales.Id, includeChildren: true));
        var reader = fixture.Principals.Add(homeUnit: finance.Id, name: "Linus Pauling");
        await GrantAsync(reader, "Crate reader", [AuthorizationPermissions.UnitsRead.Name, ProbeRecords.CratesRead.Name], RoleScope.Unit(finance.Id));
        var nobody = fixture.Principals.Add(homeUnit: sales.Id, name: "Marie Curie");
        var delegateId = fixture.Principals.Add(homeUnit: sales.Id, name: "Alan Turing");
        var context = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        var now = DateTimeOffset.UtcNow;
        context.Delegations.Add(new Delegation(administrator, delegateId, [DutyProbeModule.Receiving.Code], now.AddMinutes(-5), now.AddHours(1), "Covering the role desk."));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var probe = scope.ServiceProvider.GetRequiredService<ProbeDbContext>();
        var northCrate = new Crate(north.Id, "north");
        var financeCrate = new Crate(finance.Id, "finance");
        var latamCrate = new Crate(finance.Id, "finance, latam");
        var mixedCrate = new Crate(finance.Id, "finance, apac and latam");
        probe.Crates.AddRange(northCrate, financeCrate, latamCrate, mixedCrate);
        await probe.SaveChangesAsync(TestContext.Current.CancellationToken);

        // The reader holds APAC; the other people hold no tags, so they see every crate their roles allow.
        await probe.Crates.Where(c => c.Id == latamCrate.Id).ExecuteUpdateAsync(c => c.SetProperty(x => x.VisibilityTags, new[] { "LATAM" }), TestContext.Current.CancellationToken);
        await probe.Crates.Where(c => c.Id == mixedCrate.Id).ExecuteUpdateAsync(c => c.SetProperty(x => x.VisibilityTags, new[] { "APAC", "LATAM" }), TestContext.Current.CancellationToken);
        _tags.Hold(reader, "APAC");
        return new World(sales, north, finance, administrator, manager, reader, nobody, delegateId, northCrate.Id, financeCrate.Id, Ids.New(), latamCrate.Id, mixedCrate.Id);
    }

    private async Task GrantAsync(Guid principal, string roleName, IReadOnlyList<string> permissions, RoleScope scope)
    {
        await using var services = _app!.Services.CreateAsyncScope();
        var context = services.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        var role = new Role(RoleServiceTests.Code(), roleName, string.Empty, isSystem: false, PrincipalKind.User, permissions);
        context.Roles.Add(role);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await RoleServiceTests.AssignAsync(services, principal, role.Code, scope);
    }

    private sealed record World(OrganisationUnit Sales, OrganisationUnit North, OrganisationUnit Finance, Guid Administrator, Guid SalesManager, Guid FinanceReader, Guid Nobody, Guid Delegate, Guid NorthCrate, Guid FinanceCrate, Guid Missing, Guid LatamCrate, Guid MixedCrate);

    private sealed record Case(string Name, Guid Principal, Guid? ActingFor, HttpMethod Method, string Path, string Action, IReadOnlyDictionary<string, string> Values, object? Body = null, string? Disabled = null, string? Off = null, string? Killed = null);
}

/// <summary>The visibility tags each person holds, as the Visibility module would answer them (ADR-0057).</summary>
public sealed class HeldTags : IVisibilityTags
{
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<string>> _held = new();

    public void Hold(Guid principal, params string[] codes) => _held[principal] = codes;

    public Task<IReadOnlyList<string>> HeldAsync(Guid principalId, CancellationToken cancellationToken) => Task.FromResult(_held.GetValueOrDefault(principalId) ?? []);

    public Task<IReadOnlyDictionary<string, VisibilityTagInfo>> FindAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, VisibilityTagInfo>>(codes.Distinct().ToDictionary(c => c, c => new VisibilityTagInfo(c, c, true), StringComparer.Ordinal));

    public Task<IReadOnlyList<VisibilityTagInfo>> ActiveAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<VisibilityTagInfo>>([]);
}

/// <summary>The test principal of a header, acting for the person another header names, as a session that chose to act (ADR-0037).</summary>
public sealed class ActingHeaderPrincipal(IHttpContextAccessor accessor) : ICurrentPrincipal
{
    public const string ActingHeader = "X-Test-Acting-For";

    public Guid Id => Guid.TryParse(accessor.HttpContext?.Request.Headers[HeaderPrincipal.HeaderName].ToString(), out var id) ? id : Guid.Empty;

    public Guid? ActingForId => Guid.TryParse(accessor.HttpContext?.Request.Headers[ActingHeader].ToString(), out var id) ? id : null;
}

/// <summary>
/// The entitlement and flag gates with a switch for each case of the matrix: a licensed module off, a feature off, a
/// kill switch on. The check is the decision, as the real gates' is (their side effects are tested where they live).
/// </summary>
public sealed class ControlledGates : IEntitlementGate, IFlagGate
{
    private string? _disabled;
    private string? _off;
    private string? _killed;

    public void Set(string? disabledLicence, string? offFeature, string? killSwitch)
    {
        _disabled = disabledLicence;
        _off = offFeature;
        _killed = killSwitch;
    }

    public ValueTask<EntitlementDecision> CheckAsync(EntitlementRequest request, CancellationToken cancellationToken) => DecideAsync(request, cancellationToken);

    public ValueTask<EntitlementDecision> DecideAsync(EntitlementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Licence is { } licence && licence == _disabled)
        {
            return ValueTask.FromResult(EntitlementDecision.Refuse(EntitlementErrors.ModuleDisabled, "This module is not part of your organisation's subscription.", EntitlementReasons.SwitchedOff, licence));
        }

        return ValueTask.FromResult(request.Features.FirstOrDefault(f => f == _off) is { } feature
            ? EntitlementDecision.Refuse(EntitlementErrors.FeatureDisabled, "This feature is not part of your organisation's subscription.", EntitlementReasons.NotLicensed, feature)
            : EntitlementDecision.Allow);
    }

    public ValueTask<FlagDecision> CheckAsync(FlagRequest request, CancellationToken cancellationToken) => DecideAsync(request, cancellationToken);

    public ValueTask<FlagDecision> DecideAsync(FlagRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ValueTask.FromResult(request.Flags.FirstOrDefault(f => !f.MustBeOn && f.Key == _killed) is { } killed
            ? FlagDecision.Refuse(FlagErrors.Killed, "Stopped for now.", killed.Key, "A test stops it.")
            : FlagDecision.Allow);
    }
}

/// <summary>A licensed module with one list, for the matrix's module that is off.</summary>
public sealed class LicensedProbeModule : IModule
{
    public static readonly ModuleLicence Licence = new("licensed", SubscriptionType.Platform);

    public string Name => "licensed";

    public IReadOnlyList<Permission> Permissions => [];

    ModuleLicence? IModule.Licence => Licence;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapApiGroup("licensed").MapGet("/things", () => TypedResults.Ok(Array.Empty<string>()))
            .RequirePermission(AuthorizationPermissions.UnitsRead).ScopedByRow().WithName("ListLicensedThings").WithSummary("The licensed module's things.");
}
