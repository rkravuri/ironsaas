using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Data;
using Iron.Kernel.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Authorization.Tests;

/// <summary>
/// The access review API over HTTP (TF-038): started by a manager, decided by the reviewer of a unit and nobody else, a
/// revoke ending the assignment, and anyone who neither manages nor reviews refused at every endpoint.
/// </summary>
[Collection(AuthorizationCollection.Name)]
public class AccessReviewsEndpointTests(AuthorizationFixture fixture) : IAsyncLifetime
{
    private readonly RecordingAuditWriter _audit = new();
    private Guid _administrator;
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var connectionString = await fixture.CreateTenantDatabaseAsync($"review_api_{Ids.New():N}");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentPrincipal, HeaderPrincipal>();
        builder.Services.AddSingleton<IAuditWriter>(_audit);
        builder.Services.AddSingleton<IPrincipals>(fixture.Principals);
        builder.Services.AddIronApi();
        builder.Services.AddIronDatabase(connectionString);
        builder.Services.AddModules(builder.Configuration, [new AuthorizationModule()]);
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
    public async Task TheReviewerOfAUnitDecidesItsItemsAndNobodyElseDoes()
    {
        var (north, store) = await TreeAsync();
        var (alice, bob, stranger) = (fixture.Principals.Add(), fixture.Principals.Add(), fixture.Principals.Add());
        var reader = await RoleAsync(AuthorizationPermissions.UnitsRead.Name);
        await AssignAsync(alice, reader, RoleScope.Unit(north));
        await AssignAsync(bob, reader, RoleScope.Unit(store));
        var due = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        using var started = await SendAsync(HttpMethod.Post, "/api/v1/access-reviews", _administrator, $$"""{"name":"Quarterly review","due_on":"{{due}}","units":[{"unit_id":"{{north}}","include_children":true}]}""");
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var review = (await JsonAsync(started)).RootElement.GetProperty("id").GetGuid();
        var reviewer = fixture.Principals.Add();
        await AssignAsync(reviewer, await RoleAsync(AuthorizationPermissions.ReviewsDecide.Name), RoleScope.Unit(store));
        var items = await ItemsAsync(review, _administrator);
        var alicesItem = items.Single(i => i.Principal == alice).Id;
        var bobsItem = items.Single(i => i.Principal == bob).Id;

        // Someone who neither manages nor reviews is refused everywhere.
        foreach (var (method, path, body) in new (HttpMethod, string, string?)[]
        {
            (HttpMethod.Get, "/api/v1/access-reviews", null),
            (HttpMethod.Get, $"/api/v1/access-reviews/{review}", null),
            (HttpMethod.Get, $"/api/v1/access-reviews/{review}/items", null),
            (HttpMethod.Post, $"/api/v1/access-reviews/{review}/items/{bobsItem}/decision", """{"decision":"confirm"}"""),
            (HttpMethod.Post, $"/api/v1/access-reviews/{review}/confirm", $$"""{"item_ids":["{{bobsItem}}"]}"""),
        })
        {
            using var refused = await SendAsync(method, path, stranger, body);
            Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} answered {refused.StatusCode}");
        }

        // The store's reviewer sees the store's item only, cannot decide North's, and cannot start or close a campaign.
        var mine = await ItemsAsync(review, reviewer);
        using var outside = await SendAsync(HttpMethod.Post, $"/api/v1/access-reviews/{review}/items/{alicesItem}/decision", reviewer, """{"decision":"revoke"}""");
        using var start = await SendAsync(HttpMethod.Post, "/api/v1/access-reviews", reviewer, $$"""{"name":"Mine","due_on":"{{due}}","units":[{"unit_id":"{{store}}","include_children":false}]}""");
        using var close = await SendAsync(HttpMethod.Post, $"/api/v1/access-reviews/{review}/close", reviewer, null);
        using var list = await SendAsync(HttpMethod.Get, "/api/v1/access-reviews", reviewer, null);

        Assert.Equal([bob], mine.Select(i => i.Principal));
        Assert.Equal(HttpStatusCode.Forbidden, outside.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, start.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, close.StatusCode);
        var listed = (await JsonAsync(list)).RootElement.GetProperty("data").EnumerateArray().Single();
        Assert.Equal((review, 1, 1), (listed.GetProperty("id").GetGuid(), listed.GetProperty("progress").GetProperty("total").GetInt32(), listed.GetProperty("progress").GetProperty("open").GetInt32()));

        // A revoke by the reviewer ends Bob's assignment at once.
        using var revoked = await SendAsync(HttpMethod.Post, $"/api/v1/access-reviews/{review}/items/{bobsItem}/decision", reviewer, """{"decision":"revoke","comment":"Moved to another store."}""");
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        var decision = (await JsonAsync(revoked)).RootElement.GetProperty("decision");
        Assert.Equal(("revoke", true), (decision.GetProperty("decision").GetString(), decision.GetProperty("assignment_removed").GetBoolean()));
        await using var scope = _app!.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>().RoleAssignments.AnyAsync(a => a.PrincipalId == bob, TestContext.Current.CancellationToken));
        Assert.True(await scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>().RoleAssignments.AnyAsync(a => a.PrincipalId == alice, TestContext.Current.CancellationToken));
    }

    private async Task<IReadOnlyList<(Guid Id, Guid Principal)>> ItemsAsync(Guid review, Guid principal)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/v1/access-reviews/{review}/items?page_size=100", principal, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await JsonAsync(response)).RootElement.GetProperty("data").EnumerateArray().Select(i => (i.GetProperty("id").GetGuid(), i.GetProperty("principal_id").GetGuid()))];
    }

    private async Task<(Guid North, Guid Store)> TreeAsync()
    {
        await using var scope = _app!.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        var root = await context.OrganisationUnits.SingleAsync(u => u.ParentId == null, TestContext.Current.CancellationToken);
        var north = OrganisationUnit.ChildOf(root, "North", OrganisationUnitServiceTests.Code(), KernelOrganisationKinds.Unit);
        var store = OrganisationUnit.ChildOf(north, "Store 12", OrganisationUnitServiceTests.Code(), KernelOrganisationKinds.Unit);
        context.OrganisationUnits.AddRange(north, store);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (north.Id, store.Id);
    }

    private async Task<Guid> RoleAsync(string permission)
    {
        await using var scope = _app!.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        var role = new Role(RoleServiceTests.Code(), "Review endpoint role", string.Empty, isSystem: false, PrincipalKind.User, [permission]);
        context.Roles.Add(role);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return role.Id;
    }

    private async Task AssignAsync(Guid principal, Guid role, RoleScope scope)
    {
        await using var services = _app!.Services.CreateAsyncScope();
        var context = services.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        context.RoleAssignments.Add(new RoleAssignment(role, principal, scope));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Guid? principal, string? json)
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
}
