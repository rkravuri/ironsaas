using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit.Data;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data;
using Iron.Kernel.Entitlements.Contracts;
using Iron.Kernel.Entitlements.Services;
using Iron.Kernel.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Entitlements.Tests;

/// <summary>
/// The checks in the API (TF-040, TF-041, TF-043 AC2, TF-046), through a host with the real modules and a fixture module
/// licensed per person: each refusal with its code, read-only writes refused while reads work, expiry and grace on a
/// fake clock, a feature governed by its module, role export behind its feature, and seats.
/// </summary>
[Collection(EntitlementsCollection.Name)]
public class EntitlementChecksTests(EntitlementsFixture fixture) : IAsyncLifetime
{
    private static readonly string RoleTransfer = AuthorizationFeatures.RoleTransfer.Code;

    private readonly ControlledSource _source = new();
    private readonly TestPrincipals _principals = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero));
    private WebApplication? _app;
    private HttpClient _client = null!;
    private Guid _administrator;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(_time);
        builder.Services.AddSingleton<IEntitlementSource>(_source);
        builder.Services.AddSingleton<IPrincipals>(_principals);
        builder.Services.AddSingleton(new TenantContext(TenantId.Parse("echo"), ["echo.localhost"]));
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentPrincipal, HeaderPrincipal>();
        builder.Services.AddIronApi();
        builder.Services.AddIronDatabase(fixture.ConnectionString);
        builder.Services.AddModules(builder.Configuration, EntitlementsFixture.Modules());

        _app = builder.Build();
        _app.UseIronTelemetry();
        _app.UseIronApi();
        _app.MapModules();
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = _app.GetTestClient();
        _administrator = await GrantAsync(_principals.Person("Ada"), SystemRoles.TenantAdministrator);
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
    public async Task TheGatesDecisionForAnExplanationIsTheChecksAnswerAndWritesNothing()
    {
        await PublishAsync(Documents.With(10, [Documents.Fixture(EntitlementStatus.Disabled)]));
        var request = new EntitlementRequest(FixtureModule.Licence.Code, [], IsWrite: false, _administrator, "GET /api/v1/fixture-things", "/api/v1/fixture-things");
        await using var scope = _app!.Services.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<IEntitlementGate>();
        var events = scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking().Where(e => e.EventType == EntitlementsAuditEvents.AccessDenied && e.ActorId == _administrator);
        var before = await events.CountAsync(TestContext.Current.CancellationToken);

        var decided = await gate.DecideAsync(request, TestContext.Current.CancellationToken);
        var afterDeciding = await events.CountAsync(TestContext.Current.CancellationToken);
        var checkedAnswer = await gate.CheckAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(checkedAnswer, decided);
        Assert.Equal((false, EntitlementErrors.ModuleDisabled), (decided.Allowed, decided.ErrorCode));
        Assert.Equal(before, afterDeciding);
        Assert.Equal(before + 1, await events.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [Proves("TF-040 AC1", "TF-043 AC2", "ADR-0034")]
    public async Task ADisabledModuleIsRefusedWhateverThePermissionAndTheRefusalIsAudited()
    {
        await PublishAsync(Documents.With(10, [Documents.Fixture(EntitlementStatus.Disabled)]));

        using var read = await SendAsync(HttpMethod.Get, "/api/v1/fixture-things", _administrator);

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(EntitlementErrors.ModuleDisabled, await ErrorCodeAsync(read));
        await using var scope = _app!.Services.CreateAsyncScope();
        var denial = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .Where(e => e.EventType == EntitlementsAuditEvents.AccessDenied && e.ActorId == _administrator)
            .OrderByDescending(e => e.CreatedAt).FirstAsync(TestContext.Current.CancellationToken);
        Assert.Equal("denied", denial.Outcome);
        Assert.Contains("entitlements.module_disabled", denial.Details, StringComparison.Ordinal);
        Assert.Contains("switched_off", denial.Details, StringComparison.Ordinal);
    }

    [Fact]
    [Proves("TF-040 AC2")]
    public async Task AReadOnlyModuleAnswersReadsAndRefusesWrites()
    {
        await PublishAsync(Documents.With(11, [Documents.Fixture(EntitlementStatus.ReadOnly)]));
        await SeatAsync(_administrator);

        using var read = await SendAsync(HttpMethod.Get, "/api/v1/fixture-things", _administrator);
        using var write = await SendAsync(HttpMethod.Post, "/api/v1/fixture-things", _administrator);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal(EntitlementErrors.ReadOnly, await ErrorCodeAsync(write));
    }

    [Fact]
    [Proves("TF-040 AC3")]
    public async Task AnExpiredModuleWorksThroughItsGraceAndIsRefusedAfterIt()
    {
        var ends = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime).AddDays(-3);
        await PublishAsync(Documents.With(12, [Documents.Fixture(endsOn: ends)], graceDays: 5));
        await SeatAsync(_administrator);

        using var inGrace = await SendAsync(HttpMethod.Get, "/api/v1/fixture-things", _administrator);
        Assert.Equal(HttpStatusCode.OK, inGrace.StatusCode);

        _time.Advance(TimeSpan.FromDays(3));
        await RefreshAsync();
        using var expired = await SendAsync(HttpMethod.Get, "/api/v1/fixture-things", _administrator);
        Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
        using var problem = await JsonAsync(expired);
        Assert.Equal(EntitlementErrors.ModuleDisabled, problem.RootElement.GetProperty("error_code").GetString());
        Assert.Equal(EntitlementReasons.Expired, problem.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    [Proves("TF-046 AC3")]
    public async Task AFeatureOfADisabledModuleIsUnavailableEvenWhenItIsEnabledAlone()
    {
        await PublishAsync(Documents.With(13, [Documents.Fixture()], [Documents.Feature(FixtureModule.Bulk.Code)]));
        await SeatAsync(_administrator);
        using var enabled = await SendAsync(HttpMethod.Post, "/api/v1/fixture-things/bulk", _administrator);
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);

        // The module is off and its feature still says enabled: the module governs (TF-046 AC3).
        await PublishAsync(Documents.With(14, [Documents.Fixture(EntitlementStatus.Disabled)], [Documents.Feature(FixtureModule.Bulk.Code)]));
        await using var scope = _app!.Services.CreateAsyncScope();
        Assert.Equal(new Entitlement(EntitlementStatus.Disabled, EntitlementReasons.ModuleUnavailable), scope.ServiceProvider.GetRequiredService<IEntitlements>().Feature(FixtureModule.Bulk.Code));
        using var refused = await SendAsync(HttpMethod.Post, "/api/v1/fixture-things/bulk", _administrator);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(EntitlementErrors.ModuleDisabled, await ErrorCodeAsync(refused));
    }

    [Fact]
    [Proves("TF-046 AC1", "ADR-0034")]
    public async Task RoleExportAndImportNeedTheRoleTransferFeatureAndTheOtherRoleEndpointsDoNot()
    {
        await PublishAsync(Documents.With(15));

        using var export = await SendAsync(HttpMethod.Get, "/api/v1/roles/export", _administrator);
        using var import = await SendAsync(HttpMethod.Post, "/api/v1/roles/import", _administrator, new { dry_run = true, document = new { format_version = 1, roles = Array.Empty<object>() } });
        using var roles = await SendAsync(HttpMethod.Get, "/api/v1/roles", _administrator);

        Assert.Equal(HttpStatusCode.Forbidden, export.StatusCode);
        using (var problem = await JsonAsync(export))
        {
            Assert.Equal(EntitlementErrors.FeatureDisabled, problem.RootElement.GetProperty("error_code").GetString());
            Assert.Equal(RoleTransfer, problem.RootElement.GetProperty("entitlement").GetString());
            Assert.Equal(EntitlementReasons.NotLicensed, problem.RootElement.GetProperty("reason").GetString());
        }

        Assert.Equal(EntitlementErrors.FeatureDisabled, await ErrorCodeAsync(import));
        Assert.Equal(HttpStatusCode.OK, roles.StatusCode);

        await PublishAsync(Documents.With(16, features: [Documents.Feature(RoleTransfer)]));
        using var allowed = await SendAsync(HttpMethod.Get, "/api/v1/roles/export", _administrator);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    [Proves("TF-041 AC1", "TF-041 AC2", "TF-041 AC3", "ADR-0034")]
    public async Task SeatsAreRefusedAboveTheCountUsageIsReportedAndARemovedSeatLosesTheModuleOnTheNextRequest()
    {
        await ClearSeatsAsync();
        await PublishAsync(Documents.With(17, [Documents.Fixture(seats: 2)]));
        var grace = _principals.Person("Grace");
        var linus = _principals.Person("Linus");
        var robot = _principals.Service("Robot");
        await GrantAsync(grace, SystemRoles.ReadOnly);

        using var noSeat = await SendAsync(HttpMethod.Get, "/api/v1/fixture-things", grace);
        Assert.Equal(HttpStatusCode.Forbidden, noSeat.StatusCode);
        Assert.Equal(EntitlementErrors.SeatRequired, await ErrorCodeAsync(noSeat));

        using var first = await SendAsync(HttpMethod.Post, "/api/v1/seats", _administrator, new { module = "fixture", principal_id = grace });
        using var second = await SendAsync(HttpMethod.Post, "/api/v1/seats", _administrator, new { module = "fixture", principal_id = linus });
        using var third = await SendAsync(HttpMethod.Post, "/api/v1/seats", _administrator, new { module = "fixture", principal_id = _administrator });
        using var service = await SendAsync(HttpMethod.Post, "/api/v1/seats", _administrator, new { module = "fixture", principal_id = robot });
        using var notPerPerson = await SendAsync(HttpMethod.Post, "/api/v1/seats", _administrator, new { module = "authz", principal_id = linus });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
        using (var problem = await JsonAsync(third))
        {
            Assert.Equal(EntitlementsErrors.SeatLimit, problem.RootElement.GetProperty("error_code").GetString());
            Assert.Contains("All 2 seats of 'fixture' are assigned (2 of 2)", problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }

        Assert.Equal(HttpStatusCode.BadRequest, service.StatusCode);
        Assert.Equal(EntitlementsErrors.SeatNotPerPerson, await ErrorCodeAsync(notPerPerson));

        using var usage = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/seats/usage", _administrator));
        var fixtureUsage = usage.RootElement.GetProperty("data").EnumerateArray().Single(u => u.GetProperty("module").GetString() == "fixture");
        Assert.Equal((2, 2, 0), (fixtureUsage.GetProperty("limit").GetInt32(), fixtureUsage.GetProperty("assigned").GetInt32(), fixtureUsage.GetProperty("remaining").GetInt32()));

        using var withSeat = await SendAsync(HttpMethod.Get, "/api/v1/fixture-things", grace);
        Assert.Equal(HttpStatusCode.OK, withSeat.StatusCode);

        using var seat = await JsonAsync(first);
        using var removed = await SendAsync(HttpMethod.Delete, $"/api/v1/seats/{seat.RootElement.GetProperty("id").GetGuid()}", _administrator);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var lost = await SendAsync(HttpMethod.Get, "/api/v1/fixture-things", grace);
        Assert.Equal(HttpStatusCode.Forbidden, lost.StatusCode);
        Assert.Equal(EntitlementErrors.SeatRequired, await ErrorCodeAsync(lost));
    }

    [Fact]
    [Proves("ADR-0034")]
    public async Task TheContextAndTheSubscriptionSayWhatAppliesAndWhy()
    {
        await ClearSeatsAsync();
        var ends = new DateOnly(2026, 12, 31);
        await PublishAsync(Documents.With(18, [Documents.Fixture(seats: 5, endsOn: ends)], [Documents.Feature(RoleTransfer)], graceDays: 10));

        using var context = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/me/context", _administrator));
        var entitlements = context.RootElement.GetProperty("entitlements");
        Assert.Equal(18, entitlements.GetProperty("revision").GetInt32());
        Assert.False(entitlements.GetProperty("stale").GetBoolean());
        var module = Assert.Single(entitlements.GetProperty("modules").EnumerateArray());
        Assert.Equal(("fixture", "disabled", "no_seat"), (module.GetProperty("code").GetString(), module.GetProperty("status").GetString(), module.GetProperty("reason").GetString()));
        var transfer = entitlements.GetProperty("features").EnumerateArray().Single(f => f.GetProperty("code").GetString() == RoleTransfer);
        Assert.Equal("enabled", transfer.GetProperty("status").GetString());
        var bulk = entitlements.GetProperty("features").EnumerateArray().Single(f => f.GetProperty("code").GetString() == FixtureModule.Bulk.Code);
        Assert.Equal(("disabled", "not_licensed"), (bulk.GetProperty("status").GetString(), bulk.GetProperty("reason").GetString()));

        using var subscription = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/subscription", _administrator));
        var root = subscription.RootElement;
        Assert.Equal(("test", 10), (root.GetProperty("plan").GetString(), root.GetProperty("grace_days").GetInt32()));
        var fixtureModule = Assert.Single(root.GetProperty("modules").EnumerateArray());
        Assert.Equal("per_person", fixtureModule.GetProperty("subscription_type").GetString());
        Assert.Equal("enabled", fixtureModule.GetProperty("status").GetString());
        Assert.Equal("2026-12-31", fixtureModule.GetProperty("ends_on").GetString());
        Assert.Equal("2027-01-10", fixtureModule.GetProperty("grace_ends_on").GetString());
        Assert.Equal(5, fixtureModule.GetProperty("seats").GetProperty("remaining").GetInt32());
        var feature = root.GetProperty("features").EnumerateArray().Single(f => f.GetProperty("code").GetString() == RoleTransfer);
        Assert.Equal("authz", feature.GetProperty("module").GetString());
        Assert.Equal(JsonValueKind.Null, feature.GetProperty("licence").ValueKind);
        Assert.Contains("Export the tenant's roles", feature.GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    private async Task PublishAsync(EntitlementsDocument document)
    {
        _source.Document = document;
        await RefreshAsync();
    }

    private async Task RefreshAsync() => Assert.True(await _app!.Services.GetRequiredService<EntitlementRefresher>().RefreshOnceAsync(TestContext.Current.CancellationToken));

    private async Task SeatAsync(Guid principal)
    {
        // Each test seats its own administrator, and the seats outlive the test: without this, the third test in a row
        // to seat someone would find the fixture's two seats taken by the tests before it.
        await ClearSeatsAsync();
        await using var scope = _app!.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<SeatService>().AssignAsync(FixtureModule.Licence.Code, principal, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
    }

    private async Task ClearSeatsAsync()
    {
        await using var scope = _app!.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<Data.EntitlementsDbContext>().Seats.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> GrantAsync(Guid principal, string role)
    {
        await using var scope = _app!.Services.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<IRoleAssignments>().AssignAsync(principal, role, TestContext.Current.CancellationToken)).IsSuccess);
        return principal;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Guid principal, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(HeaderPrincipal.HeaderName, principal.ToString());
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var problem = await JsonAsync(response);
        return problem.RootElement.GetProperty("error_code").GetString();
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
}
