using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Iron.Kernel.Api;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Data;
using Iron.Kernel.Data.Sharing;
using Iron.Kernel.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Authorization.Tests;

/// <summary>
/// A record's sharing over HTTP (TF-222, ADR-0038): read by whoever may read the record, changed by whoever the
/// record's policy lets share it, with the units named; the listed units then see it, and a tenant-wide record is
/// seen by anyone who holds the read permission somewhere.
/// </summary>
[Collection(AuthorizationCollection.Name)]
public class RecordSharingEndpointTests(AuthorizationFixture fixture) : IAsyncLifetime
{
    private readonly RecordingAuditWriter _audit = new();
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
        builder.Services.AddSingleton<IPrincipals>(fixture.Principals);
        builder.Services.AddIronApi();
        builder.Services.AddIronDatabase(fixture.ConnectionString);
        builder.Services.AddModules(builder.Configuration, [new AuthorizationModule(), new DutyProbeModule()]);
        builder.Services.AddScoped<IEntitlementGate, AllEntitled>();
        builder.Services.AddScoped<IFlagGate, AllEntitled>();

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
    public async Task TheOwnersManagerSharesARecordWithAnotherUnitWhichThenReadsItButMayNotChangeIt()
    {
        var (north, south, west) = await TreeAsync();
        var crate = await CrateAsync(north);
        var manager = await PersonAsync(north, [ProbeRecords.CratesRead.Name, ProbeRecords.CratesManage.Name]);
        var southReader = await PersonAsync(south, [ProbeRecords.CratesRead.Name, ProbeRecords.CratesManage.Name]);
        var westReader = await PersonAsync(west, [ProbeRecords.CratesRead.Name]);
        var path = $"/api/v1/record-sharing/{ProbeRecords.CrateType}/{crate}";

        using (var before = await JsonAsync(await SendAsync(HttpMethod.Get, path, manager)))
        {
            Assert.Equal(("private", true), (before.RootElement.GetProperty("mode").GetString(), before.RootElement.GetProperty("may_change").GetBoolean()));
            Assert.Equal("North", before.RootElement.GetProperty("owning_unit").GetProperty("name").GetString());
            Assert.Empty(before.RootElement.GetProperty("units").EnumerateArray());
            var choices = before.RootElement.GetProperty("choices").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToList();
            Assert.Contains(south, choices);
            Assert.Contains(west, choices);
        }

        using (var hidden = await SendAsync(HttpMethod.Get, path, southReader))
        {
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        }

        using (var shared = await JsonAsync(await SendAsync(HttpMethod.Put, path, manager, new { mode = "shared", units = new[] { south, north } })))
        {
            Assert.Equal("shared", shared.RootElement.GetProperty("mode").GetString());
            Assert.Equal(["South"], shared.RootElement.GetProperty("units").EnumerateArray().Select(u => u.GetProperty("name").GetString()));
        }

        // South reads it now, and may not change it: the share is for reading, its policy needs the permission at North.
        using (var seen = await JsonAsync(await SendAsync(HttpMethod.Get, path, southReader)))
        {
            Assert.Equal(("shared", false), (seen.RootElement.GetProperty("mode").GetString(), seen.RootElement.GetProperty("may_change").GetBoolean()));
            Assert.Empty(seen.RootElement.GetProperty("choices").EnumerateArray());
        }

        using (var refused = await SendAsync(HttpMethod.Put, path, southReader, new { mode = "tenant" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        using (var stillHidden = await SendAsync(HttpMethod.Get, path, westReader))
        {
            Assert.Equal(HttpStatusCode.NotFound, stillHidden.StatusCode);
        }

        using (var tenant = await SendAsync(HttpMethod.Put, path, manager, new { mode = "tenant" }))
        {
            Assert.Equal(HttpStatusCode.OK, tenant.StatusCode);
        }

        using var everyone = await JsonAsync(await SendAsync(HttpMethod.Get, path, westReader));
        Assert.Equal("tenant", everyone.RootElement.GetProperty("mode").GetString());
        Assert.Empty(everyone.RootElement.GetProperty("units").EnumerateArray());
    }

    [Fact]
    public async Task SharedWithNoOtherUnitAnUnknownTypeAndNobodySignedInAreRefused()
    {
        var (north, _, _) = await TreeAsync();
        var crate = await CrateAsync(north);
        var manager = await PersonAsync(north, [ProbeRecords.CratesRead.Name, ProbeRecords.CratesManage.Name]);

        using var noUnits = await SendAsync(HttpMethod.Put, $"/api/v1/record-sharing/{ProbeRecords.CrateType}/{crate}", manager, new { mode = "shared", units = new[] { north } });
        using var unknownType = await SendAsync(HttpMethod.Get, $"/api/v1/record-sharing/probe_barrel/{crate}", manager);
        using var anonymous = await SendAsync(HttpMethod.Get, $"/api/v1/record-sharing/{ProbeRecords.CrateType}/{crate}", null);

        Assert.Equal((HttpStatusCode.BadRequest, SharingErrors.UnitsRequired), (noUnits.StatusCode, await ErrorCodeAsync(noUnits)));
        Assert.Equal((HttpStatusCode.NotFound, SharingErrors.RecordTypeUnknown), (unknownType.StatusCode, await ErrorCodeAsync(unknownType)));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    private async Task<(Guid North, Guid South, Guid West)> TreeAsync()
    {
        await using var scope = _app!.Services.CreateAsyncScope();
        var units = scope.ServiceProvider.GetRequiredService<Services.OrganisationUnitService>();
        var root = await OrganisationUnitServiceTests.RootAsync(scope);
        return ((await OrganisationUnitServiceTests.CreateAsync(units, root.Id, "North")).Id, (await OrganisationUnitServiceTests.CreateAsync(units, root.Id, "South")).Id, (await OrganisationUnitServiceTests.CreateAsync(units, root.Id, "West")).Id);
    }

    private async Task<Guid> CrateAsync(Guid unit)
    {
        await using var scope = _app!.Services.CreateAsyncScope();
        var probe = scope.ServiceProvider.GetRequiredService<ProbeDbContext>();
        var crate = new Crate(unit, "a crate");
        probe.Crates.Add(crate);
        await probe.SaveChangesAsync(TestContext.Current.CancellationToken);
        return crate.Id;
    }

    /// <summary>A person homed at a unit with a role of these permissions at that unit alone.</summary>
    private async Task<Guid> PersonAsync(Guid unit, IReadOnlyList<string> permissions)
    {
        var person = fixture.Principals.Add(homeUnit: unit);
        await using var scope = _app!.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
        var role = new Role(RoleServiceTests.Code(), "Crate role", string.Empty, isSystem: false, PrincipalKind.User, permissions);
        context.Roles.Add(role);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await RoleServiceTests.AssignAsync(scope, person, role.Code, RoleScope.Unit(unit));
        return person;
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

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var problem = await JsonAsync(response);
        return problem.RootElement.GetProperty("error_code").GetString();
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
}
