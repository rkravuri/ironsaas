using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Api;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Authorization.Services;
using Iron.Kernel.Data;
using Iron.Kernel.Events;
using Iron.Kernel.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Authorization.Tests;

/// <summary>
/// Segregation of duties against PostgreSQL (TF-036, ADR-0039): a block rule refuses a grant or a delegation and names
/// itself, a warn rule allows it and lists it at once, unrelated units are not a conflict, the report shows a rule's
/// conflicts within a day on a fake clock, and a review item that takes part in a conflict says so. Each test's rules
/// name its own roles, and are removed at its end, so the other tests' grants never meet them.
/// </summary>
[Collection(AuthorizationCollection.Name)]
public class ConflictTests(AuthorizationFixture fixture) : IAsyncLifetime
{
    private readonly RecordingAuditWriter _audit = new();
    private readonly List<Guid> _rules = [];
    private WebApplication? _app;
    private HttpClient _client = null!;
    private Guid _administrator;
    private (OrganisationUnit Root, OrganisationUnit Sales, OrganisationUnit North, OrganisationUnit Finance) _tree;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentPrincipal, HeaderPrincipal>();
        builder.Services.AddSingleton<IAuditWriter>(_audit);
        builder.Services.AddSingleton<IPrincipals>(fixture.Principals);
        builder.Services.AddIronApi();
        builder.Services.AddIronDatabase(fixture.ConnectionString);
        builder.Services.AddModules(builder.Configuration, [new EventsModule(), new AuthorizationModule(), new DutyProbeModule()]);
        builder.Services.AddScoped<IEntitlementGate, AllEntitled>();
        builder.Services.AddScoped<IFlagGate, AllEntitled>();

        _app = builder.Build();
        _app.UseIronTelemetry();
        _app.UseIronApi();
        _app.MapModules();
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = _app.GetTestClient();

        await using var scope = _app.Services.CreateAsyncScope();
        var units = scope.ServiceProvider.GetRequiredService<OrganisationUnitService>();
        var root = await OrganisationUnitServiceTests.RootAsync(scope);
        var sales = await OrganisationUnitServiceTests.CreateAsync(units, root.Id, "Sales");
        var north = await OrganisationUnitServiceTests.CreateAsync(units, sales.Id, "North");
        var finance = await OrganisationUnitServiceTests.CreateAsync(units, root.Id, "Finance");
        _tree = (root, sales, north, finance);
        _administrator = fixture.Principals.Add(homeUnit: root.Id, name: "Ada Lovelace");
        await RoleServiceTests.AssignAsync(scope, _administrator, SystemRoles.TenantAdministrator);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var rule in _rules)
        {
            using var _ = await SendAsync(HttpMethod.Delete, $"/api/v1/conflict-rules/{rule}", null);
        }

        _client.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    [Fact]
    [Proves("TF-036 AC1", "ADR-0039")]
    public async Task ABlockRuleRefusesAGrantThatCreatesItsConflictNamesItselfAndUnrelatedUnitsAreNoConflict()
    {
        var creator = await RoleAsync("Supplier creator", [AuthorizationPermissions.UnitsRead.Name]);
        var approver = await RoleAsync("Supplier approver", [AuthorizationPermissions.UnitsRead.Name]);
        await RuleAsync("Create and approve suppliers", Role(creator), Role(approver), ConflictModes.Block, "One person may not create a supplier and approve it.");
        var grace = fixture.Principals.Add(homeUnit: _tree.North.Id, name: "Grace Hopper");
        Assert.Equal(HttpStatusCode.Created, (await GrantAsync(grace, creator, "unit_with_children", _tree.Sales.Id)).StatusCode);

        // Before the confirm, the grant dialog asks what it would create, in words.
        using (var preview = await JsonAsync(await SendAsync(HttpMethod.Post, "/api/v1/role-assignments/conflicts", Grant(grace, approver, "unit", _tree.North.Id))))
        {
            var conflict = Assert.Single(preview.RootElement.GetProperty("data").EnumerateArray());
            Assert.Equal(("Create and approve suppliers", "block", "North"), (conflict.GetProperty("rule_name").GetString(), conflict.GetProperty("mode").GetString(), conflict.GetProperty("unit_name").GetString()));
            Assert.Equal(("Supplier creator", "Sales"), (conflict.GetProperty("with").GetProperty("role").GetString(), conflict.GetProperty("with").GetProperty("unit_name").GetString()));
        }

        using var refused = await GrantAsync(grace, approver, "unit", _tree.North.Id);
        using var elsewhere = await GrantAsync(grace, approver, "unit", _tree.Finance.Id);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        using (var problem = await JsonAsync(refused))
        {
            Assert.Equal(AuthorizationErrors.SodConflict, problem.RootElement.GetProperty("error_code").GetString());
            Assert.Equal("The conflict rule 'Create and approve suppliers' blocks this: Grace Hopper would hold both sides of it at North. One person may not create a supplier and approve it.", problem.RootElement.GetProperty("detail").GetString());
        }

        Assert.Equal(HttpStatusCode.Created, elsewhere.StatusCode);
    }

    [Fact]
    [Proves("TF-036 AC2", "ADR-0039")]
    public async Task AWarnRuleAllowsTheGrantAndListsTheConflictAtOnceAndARemovalClearsIt()
    {
        var creator = await RoleAsync("Order creator", [AuthorizationPermissions.UnitsRead.Name]);
        var approver = await RoleAsync("Order approver", [AuthorizationPermissions.UnitsRead.Name]);
        await RuleAsync("Create and approve orders", Role(creator), Role(approver), ConflictModes.Warn, "Watched by internal audit.");
        var linus = fixture.Principals.Add(homeUnit: _tree.Sales.Id, name: "Linus Pauling");
        Assert.Equal(HttpStatusCode.Created, (await GrantAsync(linus, creator, "unit", _tree.Sales.Id)).StatusCode);

        using var allowed = await GrantAsync(linus, approver, "unit_with_children", _tree.Sales.Id);

        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        var assignment = (await JsonAsync(allowed)).RootElement.GetProperty("id").GetGuid();
        using (var report = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/conflict-report?filter=principal_id:eq:{linus}", null)))
        {
            var entry = Assert.Single(report.RootElement.GetProperty("data").EnumerateArray());
            Assert.Equal(("Create and approve orders", "warn", "Sales"), (entry.GetProperty("rule_name").GetString(), entry.GetProperty("mode").GetString(), entry.GetProperty("unit_name").GetString()));
            Assert.Equal(new[] { "Order approver", "Order creator" }.Order(), new[] { entry.GetProperty("first").GetProperty("role").GetString(), entry.GetProperty("second").GetProperty("role").GetString() }.Order());
            Assert.Equal("assignment", entry.GetProperty("first").GetProperty("source").GetString());
        }

        using (var removed = await SendAsync(HttpMethod.Delete, $"/api/v1/role-assignments/{assignment}", null))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        using var after = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/conflict-report?filter=principal_id:eq:{linus}", null));
        Assert.Empty(after.RootElement.GetProperty("data").EnumerateArray());
    }

    [Fact]
    [Proves("ADR-0039")]
    public async Task ADelegationIsCheckedByTheDelegatesOwnRightsAndTheDelegatedDutiesAsIfHeldTogether()
    {
        // The administrator holds authz.roles.read tenant-wide; the receiving duty hands it over.
        var keeper = await RoleAsync("Crate keeper", [ProbeRecords.CratesManage.Name]);
        var blocking = await RuleAsync("Read roles and keep crates", Permission(AuthorizationPermissions.RolesRead.Name), Role(keeper), ConflictModes.Block, "Keepers do not read the role book.");
        var marie = fixture.Principals.Add(homeUnit: _tree.North.Id, name: "Marie Curie");
        Assert.Equal(HttpStatusCode.Created, (await GrantAsync(marie, keeper, "unit", _tree.North.Id)).StatusCode);

        using var refused = await DelegateAsync(marie, DutyProbeModule.Receiving.Code);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        using (var problem = await JsonAsync(refused))
        {
            Assert.Equal(AuthorizationErrors.SodConflict, problem.RootElement.GetProperty("error_code").GetString());
            Assert.Contains("'Read roles and keep crates'", problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }

        // As a warning, the delegation goes ahead and is listed, as a delegation.
        await ChangeModeAsync(blocking, "Read roles and keep crates", Permission(AuthorizationPermissions.RolesRead.Name), Role(keeper), ConflictModes.Warn);
        using var allowed = await DelegateAsync(marie, DutyProbeModule.Receiving.Code);
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        using var report = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/conflict-report?filter=principal_id:eq:{marie}", null));
        var entry = Assert.Single(report.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal(new[] { "assignment", "delegation" }, new[] { entry.GetProperty("first").GetProperty("source").GetString(), entry.GetProperty("second").GetProperty("source").GetString() }.Order());
        Assert.Equal("North", entry.GetProperty("unit_name").GetString());

        // A duty that does not hand the permission over makes no conflict.
        var curie = fixture.Principals.Add(homeUnit: _tree.North.Id, name: "Pierre Curie");
        Assert.Equal(HttpStatusCode.Created, (await GrantAsync(curie, keeper, "unit", _tree.North.Id)).StatusCode);
        using var other = await DelegateAsync(curie, DutyProbeModule.Approval.Code);
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        using var none = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/conflict-report?filter=principal_id:eq:{curie}", null));
        Assert.Empty(none.RootElement.GetProperty("data").EnumerateArray());
    }

    [Fact]
    [Proves("ADR-0039")]
    public async Task ARoleChangeThatWouldCreateAConflictUnderABlockRuleIsRefusedWithTheRuleAndTheNumberOfPeople()
    {
        var counter = await RoleAsync("Stock counter", [AuthorizationPermissions.UnitsRead.Name]);
        await RuleAsync("Count stock and read roles", Role(counter), Permission(AuthorizationPermissions.RolesRead.Name), ConflictModes.Block, "Counters do not read the role book.");
        var emmy = fixture.Principals.Add(homeUnit: _tree.North.Id, name: "Emmy Noether");
        var lise = fixture.Principals.Add(homeUnit: _tree.Finance.Id, name: "Lise Meitner");
        Assert.Equal(HttpStatusCode.Created, (await GrantAsync(emmy, counter, "unit", _tree.North.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await GrantAsync(lise, counter, "unit", _tree.Finance.Id)).StatusCode);

        // Each holder's one grant would hold both sides once the role reads roles.
        using var refused = await ChangeRoleAsync(counter, "Stock counter", [AuthorizationPermissions.UnitsRead.Name, AuthorizationPermissions.RolesRead.Name]);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        using (var problem = await JsonAsync(refused))
        {
            Assert.Equal(AuthorizationErrors.SodConflict, problem.RootElement.GetProperty("error_code").GetString());
            Assert.Equal(
                "The conflict rule 'Count stock and read roles' blocks this change to 'Stock counter': 2 people would hold both sides of it. Counters do not read the role book.",
                problem.RootElement.GetProperty("detail").GetString());
        }

        using (var role = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/roles/{counter}", null)))
        {
            Assert.Equal(new[] { AuthorizationPermissions.UnitsRead.Name }, role.RootElement.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        }

        // A change the rule does not concern is saved.
        using var saved = await ChangeRoleAsync(counter, "Stock counter", [AuthorizationPermissions.UnitsRead.Name, AuthorizationPermissions.AssignmentsRead.Name]);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    }

    [Fact]
    [Proves("ADR-0039")]
    public async Task ARoleChangeUnderAWarnRuleIsSavedAndTheReportListsItsNewConflictsAtOnce()
    {
        var keeper = await RoleAsync("Shelf keeper", [AuthorizationPermissions.UnitsRead.Name]);
        await RuleAsync("Keep shelves and read roles", Role(keeper), Permission(AuthorizationPermissions.RolesRead.Name), ConflictModes.Warn, "Watched by internal audit.");
        var rosalind = fixture.Principals.Add(homeUnit: _tree.Sales.Id, name: "Rosalind Franklin");
        var barbara = fixture.Principals.Add(homeUnit: _tree.North.Id, name: "Barbara McClintock");
        Assert.Equal(HttpStatusCode.Created, (await GrantAsync(rosalind, keeper, "unit_with_children", _tree.Sales.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await GrantAsync(barbara, keeper, "unit", _tree.North.Id)).StatusCode);

        using var saved = await ChangeRoleAsync(keeper, "Shelf keeper", [AuthorizationPermissions.UnitsRead.Name, AuthorizationPermissions.RolesRead.Name]);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        foreach (var (person, unit) in new[] { (rosalind, "Sales"), (barbara, "North") })
        {
            using var report = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/conflict-report?filter=principal_id:eq:{person}", null));
            var entry = Assert.Single(report.RootElement.GetProperty("data").EnumerateArray());
            Assert.Equal(("Keep shelves and read roles", "warn", unit), (entry.GetProperty("rule_name").GetString(), entry.GetProperty("mode").GetString(), entry.GetProperty("unit_name").GetString()));
        }

        // Taking the permission away again ends both conflicts, and the report says so at once.
        using var back = await ChangeRoleAsync(keeper, "Shelf keeper", [AuthorizationPermissions.UnitsRead.Name]);
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Empty(await EntriesAsync(_app!.Services, rosalind));
        Assert.Empty(await EntriesAsync(_app!.Services, barbara));
    }

    [Fact]
    [Proves("TF-036 AC3", "ADR-0039")]
    public async Task TheReportShowsTheConflictsOfARuleWithinADayOfItsChangeOnAFakeClock()
    {
        var creator = await RoleAsync("Invoice creator", [AuthorizationPermissions.UnitsRead.Name]);
        var payer = await RoleAsync("Invoice payer", [AuthorizationPermissions.UnitsRead.Name]);
        var alan = fixture.Principals.Add(homeUnit: _tree.North.Id, name: "Alan Turing");
        await DirectAsync(alan, creator, RoleScope.Unit(_tree.Sales.Id, includeChildren: true));
        await DirectAsync(alan, payer, RoleScope.Unit(_tree.North.Id));

        // The job runs once a day (ADR-0061): a rule written since its last run shows its conflicts at its next.
        Assert.Equal(TimeSpan.FromDays(1), ConflictReportJob.Definition.Schedule.Every);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var provider = fixture.BuildServices(time, new FixedPrincipal(SystemPrincipal.SystemId), new RecordingAuditWriter());
        var started = time.GetUtcNow();
        await RunReportJobAsync(provider);
        Assert.Empty(await EntriesAsync(provider, alan));

        await RuleAsync("Create and pay invoices", Role(creator), Role(payer), ConflictModes.Warn, "Two people for every payment.");
        Assert.Empty(await EntriesAsync(provider, alan));
        time.Advance(ConflictReportJob.Definition.Schedule.Every!.Value);
        await RunReportJobAsync(provider);

        var entry = Assert.Single(await EntriesAsync(provider, alan));
        Assert.Equal(("Create and pay invoices", _tree.North.Id), (entry.RuleName, entry.UnitId));
        await using var scope = provider.CreateAsyncScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>().ConflictReportRuns.AsNoTracking()
            .CountAsync(r => r.Trigger == ConflictReportTriggers.Daily && r.CreatedAt >= started, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Proves("ADR-0039")]
    public async Task AReviewItemThatTakesPartInAConflictSaysSoByTheRulesName()
    {
        var creator = await RoleAsync("Payment creator", [AuthorizationPermissions.UnitsRead.Name]);
        var releaser = await RoleAsync("Payment releaser", [AuthorizationPermissions.UnitsRead.Name]);
        await RuleAsync("Create and release payments", Role(creator), Role(releaser), ConflictModes.Warn, "Four eyes on every payment.");
        var joan = fixture.Principals.Add(homeUnit: _tree.Finance.Id, name: "Joan Clarke");
        var plain = fixture.Principals.Add(homeUnit: _tree.Finance.Id, name: "Tommy Flowers");
        await DirectAsync(joan, creator, RoleScope.Unit(_tree.Finance.Id));
        await DirectAsync(joan, releaser, RoleScope.Unit(_tree.Finance.Id));
        await DirectAsync(plain, creator, RoleScope.Unit(_tree.Finance.Id));
        using (var run = await SendAsync(HttpMethod.Post, "/api/v1/conflict-report/runs", null))
        {
            Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        }

        await using var scope = _app!.Services.CreateAsyncScope();
        var reviews = scope.ServiceProvider.GetRequiredService<ReviewService>();
        var context = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        var review = new AccessReview("Finance quarter", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7), _administrator);
        context.AccessReviews.Add(review);
        var assignments = await context.RoleAssignments.AsNoTracking().Where(a => (a.PrincipalId == joan || a.PrincipalId == plain) && a.ScopeId == _tree.Finance.Id).ToListAsync(TestContext.Current.CancellationToken);
        var roles = await context.Roles.AsNoTracking().Where(r => r.Id == creator || r.Id == releaser).ToDictionaryAsync(r => r.Id, TestContext.Current.CancellationToken);
        var path = OrganisationUnit.PathIds(_tree.Finance.Path);
        var items = assignments.Select(a => new AccessReviewItem(review.Id, a, a.PrincipalId == joan ? "Joan Clarke" : "Tommy Flowers", roles[a.RoleId], path, [.. path.Select(_ => "Finance")])).ToList();
        context.AccessReviewItems.AddRange(items);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var described = await reviews.DescribeItemsAsync(items, TestContext.Current.CancellationToken);

        Assert.All(described.Where(i => i.PrincipalId == joan), i => Assert.Equal(["Create and release payments"], i.Conflicts));
        Assert.Empty(Assert.Single(described, i => i.PrincipalId == plain).Conflicts);
    }

    [Fact]
    [Proves("ADR-0039")]
    public async Task RulesAreValidatedNamedInWordsAndEveryChangeIsAudited()
    {
        var creator = await RoleAsync("Ledger keeper", [AuthorizationPermissions.UnitsRead.Name]);
        using var unknown = await SendAsync(HttpMethod.Post, "/api/v1/conflict-rules", Body("Unknown", Permission("nobody.nothing.do"), Role(creator), ConflictModes.Block));
        using var same = await SendAsync(HttpMethod.Post, "/api/v1/conflict-rules", Body("Same", Role(creator), Role(creator), ConflictModes.Block));
        var id = await RuleAsync("Keep the ledger and manage units", Role(creator), Permission(AuthorizationPermissions.UnitsManage.Name), ConflictModes.Warn, "Separate hands.");

        Assert.Equal((HttpStatusCode.BadRequest, AuthorizationErrors.SodSideUnknown), (unknown.StatusCode, await ErrorCodeAsync(unknown)));
        Assert.Equal((HttpStatusCode.BadRequest, AuthorizationErrors.SodSidesSame), (same.StatusCode, await ErrorCodeAsync(same)));
        using (var list = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/conflict-rules?filter=id:eq:{id}", null)))
        {
            var rule = Assert.Single(list.RootElement.GetProperty("data").EnumerateArray());
            Assert.Equal(("Ledger keeper", AuthorizationPermissions.UnitsManage.Description, "Ada Lovelace"), (rule.GetProperty("first").GetProperty("name").GetString(), rule.GetProperty("second").GetProperty("name").GetString(), rule.GetProperty("owner_name").GetString()));
        }

        await ChangeModeAsync(id, "Keep the ledger and manage units", Role(creator), Permission(AuthorizationPermissions.UnitsManage.Name), ConflictModes.Block);
        using (var deleted = await SendAsync(HttpMethod.Delete, $"/api/v1/conflict-rules/{id}", null))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        _rules.Remove(id);
        Assert.Equal(
            [AuthorizationAuditEvents.SodRuleCreated, AuthorizationAuditEvents.SodRuleChanged, AuthorizationAuditEvents.SodRuleDeleted],
            _audit.Events.Where(e => e.RecordId == id).Select(e => e.Action));
    }

    private static ConflictSideRequest Role(Guid roleId) => new(ConflictSides.Role, roleId.ToString("D"));

    private static ConflictSideRequest Permission(string name) => new(ConflictSides.Permission, name);

    private static object Body(string name, ConflictSideRequest first, ConflictSideRequest second, string mode, string reason = "") =>
        new { name, first = new { kind = first.Kind, value = first.Value }, second = new { kind = second.Kind, value = second.Value }, mode, reason };

    private static object Grant(Guid principal, Guid role, string scopeKind, Guid unit) => new { principal_id = principal, role_id = role, scope_kind = scopeKind, unit_id = unit };

    private async Task<Guid> RuleAsync(string name, ConflictSideRequest first, ConflictSideRequest second, string mode, string reason)
    {
        using var created = await SendAsync(HttpMethod.Post, "/api/v1/conflict-rules", Body(name, first, second, mode, reason));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonAsync(created)).RootElement.GetProperty("id").GetGuid();
        _rules.Add(id);
        return id;
    }

    private async Task ChangeModeAsync(Guid id, string name, ConflictSideRequest first, ConflictSideRequest second, string mode)
    {
        using var changed = await SendAsync(HttpMethod.Put, $"/api/v1/conflict-rules/{id}", Body(name, first, second, mode));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
    }

    private Task<HttpResponseMessage> GrantAsync(Guid principal, Guid role, string scopeKind, Guid unit) =>
        SendAsync(HttpMethod.Post, "/api/v1/role-assignments", Grant(principal, role, scopeKind, unit));

    private Task<HttpResponseMessage> ChangeRoleAsync(Guid role, string name, IReadOnlyList<string> permissions) =>
        SendAsync(HttpMethod.Put, $"/api/v1/roles/{role}", new { name, description = string.Empty, principal_type = "user", permissions });

    private Task<HttpResponseMessage> DelegateAsync(Guid delegateTo, string duty) =>
        SendAsync(HttpMethod.Post, "/api/v1/delegations", new { delegate_id = delegateTo, duties = new[] { duty }, starts_at = DateTimeOffset.UtcNow.AddMinutes(-1), ends_at = DateTimeOffset.UtcNow.AddHours(2), reason = "Covering for a week." });

    private async Task<Guid> RoleAsync(string name, IReadOnlyList<string> permissions)
    {
        await using var scope = _app!.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        var role = new Role(RoleServiceTests.Code(), name, string.Empty, isSystem: false, PrincipalKind.User, permissions);
        context.Roles.Add(role);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return role.Id;
    }

    /// <summary>An assignment written straight to the table, as rights that existed before a rule.</summary>
    private async Task DirectAsync(Guid principal, Guid role, RoleScope scope)
    {
        await using var services = _app!.Services.CreateAsyncScope();
        var context = services.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        context.RoleAssignments.Add(new RoleAssignment(role, principal, scope));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<ConflictReportEntry>> EntriesAsync(IServiceProvider provider, Guid principal)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>().ConflictReportEntries.AsNoTracking().Where(e => e.PrincipalId == principal).ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Waits, on the real clock, until the worker's runs since the start number this many, nudging the fake clock between looks.</summary>
    /// <summary>One run of the conflicts report's job, as the worker's runner makes it.</summary>
    private static async Task RunReportJobAsync(IServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var job = ActivatorUtilities.CreateInstance<ConflictReportJob>(scope.ServiceProvider, NullLogger<ConflictReportJob>.Instance);
        var result = await job.RunAsync(new JobContext(Ids.New(), ConflictReportJob.Definition.Name, "test", 1, DateTimeOffset.UtcNow, null, null), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess, result.ToString());
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(HeaderPrincipal.HeaderName, _administrator.ToString());
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
}
