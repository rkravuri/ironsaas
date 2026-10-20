using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Problems;
using Iron.Kernel.Audit;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Authorization;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Iron.Kernel.Data;
using Iron.Kernel.Identity.Contracts;
using Iron.Kernel.Identity.Data;
using Iron.Kernel.Identity.Services;
using Iron.Kernel.Settings;
using Iron.Kernel.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Identity.Tests;

/// <summary>
/// Users by scope over HTTP (A2, ADR-0031): a reader at a unit with its children sees that subtree's users and nobody
/// else, a user outside is not found, <c>/me</c> and <c>/me/access</c> say what the principal may do, and creating a
/// user needs the manage permission at the new user's home unit.
/// </summary>
[Collection(IdentityCollection.Name)]
public class ScopedUsersTests(IdentityFixture fixture) : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";

    private readonly FakeTimeProvider _time = new();
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(IdentityFixture.Configuration);
        builder.Services.AddSingleton<TimeProvider>(_time);
        builder.Services.AddSingleton(IdentityFixture.Tenant);
        builder.Services.AddIronApi();
        builder.Services.AddIronDatabase(fixture.ConnectionString);
        builder.Services.AddModules(builder.Configuration, [new AuditModule(), new AuthorizationModule(), new IdentityModule(), new SettingsModule()]);
        builder.Services.AddScoped<IEntitlementGate, AllEntitled>();
        builder.Services.AddScoped<IFlagGate, AllEntitled>();

        _app = builder.Build();
        _app.UseIronTelemetry();
        _app.UseIronApi();
        _app.UseIronIdentity();
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
    public async Task AReaderOfAUnitWithItsChildrenSeesThoseUsersOnlyAndAUserOutsideIsNotFound()
    {
        var (sales, east, marketing) = await TreeAsync();
        var inSales = await UserAsync(sales);
        var inEast = await UserAsync(east);
        var inMarketing = await UserAsync(marketing);
        var reader = await UserAsync(sales);
        await GrantAsync(reader.Id, [IdentityPermissions.UsersRead.Name], RoleScope.Unit(sales, includeChildren: true));
        var cookie = await LoginAsync(reader.LoginId);

        using var list = await GetAsync("/api/v1/users?fields=id,home_unit_id&page_size=100", cookie);
        using var inside = await GetAsync($"/api/v1/users/{inEast.Id}", cookie);
        using var outside = await GetAsync($"/api/v1/users/{inMarketing.Id}", cookie);
        using var unknown = await GetAsync($"/api/v1/users/{Ids.New()}", cookie);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listBody = await JsonAsync(list);
        var seen = listBody.RootElement.GetProperty("data").EnumerateArray().Select(u => u.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(new[] { inSales.Id, inEast.Id, reader.Id }.Order(), seen.Order());
        Assert.Equal(HttpStatusCode.OK, inside.StatusCode);
        using var insideBody = await JsonAsync(inside);
        Assert.Equal("East", insideBody.RootElement.GetProperty("home_unit").GetProperty("name").GetString());
        Assert.Equal(IdentityErrors.UserNotFound, await ProblemAsync(outside, HttpStatusCode.NotFound));
        Assert.Equal(IdentityErrors.UserNotFound, await ProblemAsync(unknown, HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task APersonWithRolesInSeveralUnitsChoosesTheUnitTheyWorkForAndTheUsersListFollows()
    {
        var (sales, east, marketing) = await TreeAsync();
        var inSales = await UserAsync(sales);
        var inEast = await UserAsync(east);
        var inMarketing = await UserAsync(marketing);
        var person = await UserAsync(east);
        await GrantAsync(person.Id, [IdentityPermissions.UsersRead.Name], RoleScope.Unit(sales, includeChildren: true));
        await GrantAsync(person.Id, [IdentityPermissions.UsersRead.Name], RoleScope.Unit(marketing));
        var cookie = await LoginAsync(person.LoginId);

        // Signed in, the person works for their home unit, and may choose Sales, Marketing or all of them.
        using (var me = await JsonAsync(await GetAsync("/api/v1/me", cookie)))
        {
            Assert.Equal(east, me.RootElement.GetProperty("working_unit").GetProperty("id").GetGuid());
            Assert.False(me.RootElement.GetProperty("all_units").GetBoolean());
            var choices = me.RootElement.GetProperty("working_units");
            Assert.Equal(new[] { sales, east, marketing }.Order(), choices.GetProperty("units").EnumerateArray().Select(u => u.GetProperty("id").GetGuid()).Order());
            Assert.Equal(["Sales", "East"], choices.GetProperty("units").EnumerateArray().Single(u => u.GetProperty("id").GetGuid() == east).GetProperty("path").EnumerateArray().Skip(1).Select(p => p.GetString()));
            Assert.True(choices.GetProperty("all_units_available").GetBoolean(), "a role at Sales with the units below it");
        }

        Assert.Equal(new[] { inEast.Id, person.Id }.Order(), await UserIdsAsync(cookie));

        using (var chosen = await JsonAsync(await PostAsync("/api/v1/me/working-unit", cookie, new Dictionary<string, object> { ["unit_id"] = marketing })))
        {
            Assert.Equal(marketing, chosen.RootElement.GetProperty("working_unit").GetProperty("id").GetGuid());
            Assert.Equal("Marketing", chosen.RootElement.GetProperty("working_unit").GetProperty("name").GetString());
        }

        Assert.Equal([inMarketing.Id], await UserIdsAsync(cookie));

        // A read by id is not narrowed: a link to a colleague in another of the person's units still opens.
        using (var other = await GetAsync($"/api/v1/users/{inSales.Id}", cookie))
        {
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        }

        // All my units: everything the roles reach, and new records and settings follow the home unit.
        using (var all = await JsonAsync(await PostAsync("/api/v1/me/working-unit", cookie, new Dictionary<string, object> { ["all_units"] = true })))
        {
            Assert.True(all.RootElement.GetProperty("all_units").GetBoolean());
            Assert.Equal(east, all.RootElement.GetProperty("working_unit").GetProperty("id").GetGuid());
        }

        Assert.Equal(new[] { inSales.Id, inEast.Id, inMarketing.Id, person.Id }.Order(), await UserIdsAsync(cookie));
        await using var scope = _app!.Services.CreateAsyncScope();
        var events = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .Where(e => e.ActorId == person.Id && e.EventType == IdentityAuditEvents.WorkingUnitChosen)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task AUnitThatIsNotYoursOrAllUnitsWithoutARoleOverSeveralIsRefusedAndAStaleChoiceFallsBackHome()
    {
        var (sales, east, marketing) = await TreeAsync();
        var person = await UserAsync(sales);
        await GrantAsync(person.Id, [IdentityPermissions.UsersRead.Name], RoleScope.Unit(sales));
        await GrantAsync(person.Id, [IdentityPermissions.UsersRead.Name], RoleScope.Unit(marketing));
        var cookie = await LoginAsync(person.LoginId);

        using var notYours = await PostAsync("/api/v1/me/working-unit", cookie, new Dictionary<string, object> { ["unit_id"] = east });
        using var notSeveral = await PostAsync("/api/v1/me/working-unit", cookie, new Dictionary<string, object> { ["all_units"] = true });
        using var marketingChosen = await PostAsync("/api/v1/me/working-unit", cookie, new Dictionary<string, object> { ["unit_id"] = marketing });

        Assert.Equal(IdentityErrors.WorkingUnitNotYours, await ProblemAsync(notYours, HttpStatusCode.Forbidden));
        Assert.Equal(IdentityErrors.WorkingUnitNotYours, await ProblemAsync(notSeveral, HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.OK, marketingChosen.StatusCode);

        // The role at Marketing is removed: the session works for the home unit again.
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
            await context.RoleAssignments.Where(a => a.PrincipalId == person.Id && a.ScopeId == marketing).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        using var me = await JsonAsync(await GetAsync("/api/v1/me", cookie));
        Assert.Equal(sales, me.RootElement.GetProperty("working_unit").GetProperty("id").GetGuid());
        Assert.Equal([sales], me.RootElement.GetProperty("working_units").GetProperty("units").EnumerateArray().Select(u => u.GetProperty("id").GetGuid()));
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            var session = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>().Sessions.AsNoTracking()
                .SingleAsync(s => s.PrincipalId == person.Id, TestContext.Current.CancellationToken);
            Assert.Null(session.WorkingUnitId);
        }
    }

    [Fact]
    public async Task MeListsThePermissionsHeldAnywhereAndMeAccessThoseAtAUnit()
    {
        var (sales, east, marketing) = await TreeAsync();
        var reader = await UserAsync(east);
        await GrantAsync(reader.Id, [IdentityPermissions.UsersRead.Name], RoleScope.Unit(sales, includeChildren: true));
        var cookie = await LoginAsync(reader.LoginId);

        using var me = await GetAsync("/api/v1/me", cookie);
        using var atEast = await GetAsync($"/api/v1/me/access?unit={east}", cookie);
        using var atMarketing = await GetAsync($"/api/v1/me/access?unit={marketing}", cookie);
        using var atRecord = await GetAsync($"/api/v1/me/access?record_type=principal&record_id={reader.Id}&unit={east}", cookie);
        using var nowhere = await GetAsync("/api/v1/me/access", cookie);

        using var meBody = await JsonAsync(me);
        Assert.Empty(meBody.RootElement.GetProperty("permissions").EnumerateArray());
        Assert.Equal([IdentityPermissions.UsersRead.Name], meBody.RootElement.GetProperty("permissions_anywhere").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal([IdentityPermissions.UsersRead.Name], await PermissionsAsync(atEast));
        Assert.Empty(await PermissionsAsync(atMarketing));
        Assert.Equal([IdentityPermissions.UsersRead.Name], await PermissionsAsync(atRecord));
        Assert.Equal(ApiErrors.Validation, await ProblemAsync(nowhere, HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task MeAccessWithTheUnitsBelowAnswersOnlyWhatIsHeldOverTheWholeSubtree()
    {
        var (sales, east, _) = await TreeAsync();
        var oneUnit = await UserAsync(sales);
        var withChildren = await UserAsync(sales);
        await GrantAsync(oneUnit.Id, [IdentityPermissions.UsersRead.Name], RoleScope.Unit(sales));
        await GrantAsync(withChildren.Id, [IdentityPermissions.UsersRead.Name], RoleScope.Unit(sales, includeChildren: true));
        var oneUnitCookie = await LoginAsync(oneUnit.LoginId);
        var withChildrenCookie = await LoginAsync(withChildren.LoginId);

        using var atTheUnit = await GetAsync($"/api/v1/me/access?unit={sales}", oneUnitCookie);
        using var overOneUnit = await GetAsync($"/api/v1/me/access?unit={sales}&include_children=true", oneUnitCookie);
        using var overTheSubtree = await GetAsync($"/api/v1/me/access?unit={sales}&include_children=true", withChildrenCookie);
        using var overALowerUnit = await GetAsync($"/api/v1/me/access?unit={east}&include_children=true", withChildrenCookie);
        using var onARecord = await GetAsync($"/api/v1/me/access?record_type=principal&record_id={withChildren.Id}&include_children=true", withChildrenCookie);

        // A grant to the unit alone holds at the unit, not over the units below it (ADR-0031).
        Assert.Equal([IdentityPermissions.UsersRead.Name], await PermissionsAsync(atTheUnit));
        Assert.Empty(await PermissionsAsync(overOneUnit));
        using (var body = await JsonAsync(overTheSubtree))
        {
            Assert.Equal($"unit_with_children:{sales:N}", body.RootElement.GetProperty("scope").GetString());
            Assert.Equal([IdentityPermissions.UsersRead.Name], body.RootElement.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        }

        Assert.Equal([IdentityPermissions.UsersRead.Name], await PermissionsAsync(overALowerUnit));
        Assert.Equal(ApiErrors.Validation, await ProblemAsync(onARecord, HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task ARoleWithoutTheUsersReadPermissionAnswers403()
    {
        var (sales, _, _) = await TreeAsync();
        var person = await UserAsync(sales);
        await GrantAsync(person.Id, [AuthorizationPermissions.UnitsRead.Name], RoleScope.Tenant);
        var cookie = await LoginAsync(person.LoginId);

        using var list = await GetAsync("/api/v1/users", cookie);
        using var one = await GetAsync($"/api/v1/users/{person.Id}", cookie);

        Assert.Equal(PermissionErrors.PermissionDenied, await ProblemAsync(list, HttpStatusCode.Forbidden));
        Assert.Equal(PermissionErrors.PermissionDenied, await ProblemAsync(one, HttpStatusCode.Forbidden));
    }

    [Fact]
    public async Task AUserIsCreatedOnlyWhereTheCreatorManagesUsersAndTheInvitationLinkWorks()
    {
        var (sales, east, marketing) = await TreeAsync();
        var manager = await UserAsync(sales);
        await GrantAsync(manager.Id, [IdentityPermissions.UsersRead.Name, IdentityPermissions.UsersManage.Name], RoleScope.Unit(sales, includeChildren: true));
        var cookie = await LoginAsync(manager.LoginId);
        var loginId = $"new-{Ids.New():N}@acme.test";

        using var created = await PostAsync("/api/v1/users", cookie, new Dictionary<string, object> { ["login_id"] = loginId, ["display_name"] = "New Colleague", ["home_unit_id"] = east });
        using var elsewhere = await PostAsync("/api/v1/users", cookie, new Dictionary<string, object> { ["login_id"] = $"x-{Ids.New():N}@acme.test", ["display_name"] = "Elsewhere", ["home_unit_id"] = marketing });
        using var taken = await PostAsync("/api/v1/users", cookie, new Dictionary<string, object> { ["login_id"] = loginId, ["display_name"] = "Again", ["home_unit_id"] = east });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var body = await JsonAsync(created);
        var user = body.RootElement.GetProperty("user");
        var id = user.GetProperty("id").GetGuid();
        Assert.Equal($"/api/v1/users/{id}", created.Headers.Location?.ToString());
        Assert.Equal("invited", user.GetProperty("status").GetString());
        Assert.Equal(east, user.GetProperty("home_unit_id").GetGuid());
        var link = body.RootElement.GetProperty("invitation_link").GetString()!;
        Assert.StartsWith("/invitations/", link, StringComparison.Ordinal);
        Assert.Equal(PermissionErrors.PermissionDenied, await ProblemAsync(elsewhere, HttpStatusCode.Forbidden));
        Assert.Equal(IdentityErrors.LoginIdTaken, await ProblemAsync(taken, HttpStatusCode.Conflict));
        await using (var scope = _app!.Services.CreateAsyncScope())
        {
            var audit = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
                .SingleAsync(e => e.EventType == IdentityAuditEvents.UserCreated && e.RecordId == id, TestContext.Current.CancellationToken);
            Assert.Equal(manager.Id, audit.ActorId);
        }

        using var accepted = await _client.PostAsJsonAsync($"/auth{link}/accept", new { password = Password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.False(string.IsNullOrEmpty(await LoginAsync(loginId)));
    }

    private async Task<(Guid Sales, Guid East, Guid Marketing)> TreeAsync()
    {
        await using var scope = _app!.Services.CreateAsyncScope();
        var units = scope.ServiceProvider.GetRequiredService<OrganisationUnitService>();
        var root = (await units.RootAsync(TestContext.Current.CancellationToken))!.Id;
        var sales = await CreateUnitAsync(units, root, "Sales");
        return (sales, await CreateUnitAsync(units, sales, "East"), await CreateUnitAsync(units, root, "Marketing"));
    }

    private static async Task<Guid> CreateUnitAsync(OrganisationUnitService units, Guid parent, string name)
    {
        var result = await units.CreateAsync(parent, name, $"u-{Ids.New():N}", KernelOrganisationKinds.Unit, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        return result.Value.Id;
    }

    private async Task<Principal> UserAsync(Guid homeUnit)
    {
        await using var scope = _app!.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var user = new Principal($"user-{Ids.New():N}@acme.test", PrincipalType.User, "Scoped User", null);
        user.AcceptInvitation(scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(Password), null);
        user.SetHomeUnit(homeUnit);
        context.Principals.Add(user);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user;
    }

    private async Task GrantAsync(Guid principal, IReadOnlyList<string> permissions, RoleScope scope)
    {
        await using var services = _app!.Services.CreateAsyncScope();
        var context = services.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        var role = new Role($"r-{Ids.New():N}", "Scoped role", string.Empty, isSystem: false, PrincipalKind.User, permissions);
        context.Roles.Add(role);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var result = await services.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(principal, role.Code, scope, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
    }

    private async Task<string> LoginAsync(string loginId)
    {
        using var login = await _client.PostAsJsonAsync("/auth/login", new { login_id = loginId, password = Password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0];
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add("Cookie", cookie);
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<IReadOnlyList<Guid>> UserIdsAsync(string cookie)
    {
        using var list = await GetAsync("/api/v1/users?fields=id&page_size=100", cookie);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var body = await JsonAsync(list);
        return [.. body.RootElement.GetProperty("data").EnumerateArray().Select(u => u.GetProperty("id").GetGuid()).Order()];
    }

    private async Task<HttpResponseMessage> PostAsync(string path, string cookie, IReadOnlyDictionary<string, object> body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.Add("Cookie", cookie);
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    private static async Task<IReadOnlyList<string?>> PermissionsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonAsync(response);
        return body.RootElement.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
    }

    private static async Task<string?> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        using var problem = await JsonAsync(response);
        return problem.RootElement.GetProperty("error_code").GetString();
    }
}
