using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Api.Tests.Permissions;

/// <summary>TF-030 AC1: a host with an unguarded endpoint does not start, and the message names the operation.</summary>
public class EndpointPermissionCheckTests
{
    private static readonly Permission Read = new("sample.things.read", "Read things", Risk.Low);
    private static readonly Permission Unknown = new("sample.things.write", "Write things", Risk.High);

    [Fact]
    public async Task StartUpFailsNamingAnEndpointWithoutAPermissionOrAnonymousMarker()
    {
        await using var app = Build(catalogue: [Read], evaluator: true);
        app.MapGet("/api/v1/bare", () => "no permission here");
        app.MapPost("/api/v1/things", () => "ok").RequirePermission(Read);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("GET /api/v1/bare", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("POST /api/v1/things", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartUpFailsForAPermissionNoModuleDeclared()
    {
        await using var app = Build(catalogue: [Read], evaluator: true);
        app.MapGet("/api/v1/things", () => "ok").RequirePermission(Unknown);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("sample.things.write", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not declared", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartUpFailsWhenProtectedEndpointsExistWithoutAnEvaluator()
    {
        await using var app = Build(catalogue: [Read], evaluator: false);
        app.MapGet("/api/v1/things", () => "ok").RequirePermission(Read);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("IPermissionEvaluator", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartUpPassesWhenEveryEndpointIsDeclared()
    {
        await using var app = Build(catalogue: [Read], evaluator: true);
        app.MapGet("/api/v1/things", () => "ok").RequirePermission(Read);
        app.MapGet("/public", () => "ok").AllowAnonymous();

        await app.StartAsync(TestContext.Current.CancellationToken);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void TheRulesRefuseAFeatureNoModuleDeclaredAndALicensedEndpointWithoutAGate()
    {
        var licensing = new Iron.Kernel.Api.Modules.LicensingCatalogue([], [new Iron.Kernel.Api.Modules.FeatureEntry("sample.export", "sample", null, "Export things")]);
        var declared = Endpoint("/api/v1/things/export", new PermissionMetadata(Read), new Iron.Kernel.Api.Entitlements.FeatureMetadata("sample.export"));
        var undeclared = Endpoint("/api/v1/things/import", new PermissionMetadata(Read), new Iron.Kernel.Api.Entitlements.FeatureMetadata("sample.import"));
        var licensed = Endpoint("/api/v1/widgets", new PermissionMetadata(Read), new Iron.Kernel.Api.Entitlements.LicensedModuleMetadata("widgets"));

        Assert.Empty(EndpointPermissionCheck.Check([declared], new PermissionCatalogue([Read]), evaluatorRegistered: true, licensing, gateRegistered: true));
        var unknown = Assert.Single(EndpointPermissionCheck.Check([undeclared], new PermissionCatalogue([Read]), evaluatorRegistered: true, licensing, gateRegistered: true));
        Assert.Contains("feature 'sample.import' is not declared", unknown, StringComparison.Ordinal);
        var noGate = Assert.Single(EndpointPermissionCheck.Check([licensed], new PermissionCatalogue([Read]), evaluatorRegistered: true, licensing, gateRegistered: false));
        Assert.Contains("IEntitlementGate", noGate, StringComparison.Ordinal);
    }

    private static Microsoft.AspNetCore.Routing.RouteEndpoint Endpoint(string pattern, params object[] metadata) => new(
        _ => Task.CompletedTask,
        Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse(pattern),
        0,
        new Microsoft.AspNetCore.Http.EndpointMetadataCollection(metadata),
        pattern);

    [Fact]
    public void TheRulesNameBothMarkersOnOneEndpointAsAMistake()
    {
        var endpoint = new Microsoft.AspNetCore.Routing.RouteEndpoint(
            _ => Task.CompletedTask,
            Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse("/api/v1/mixed"),
            0,
            new Microsoft.AspNetCore.Http.EndpointMetadataCollection(new PermissionMetadata(Read), new Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute()),
            "mixed");

        var violations = EndpointPermissionCheck.Check([endpoint], new PermissionCatalogue([Read]), evaluatorRegistered: true);

        var violation = Assert.Single(violations);
        Assert.Contains("ANY /api/v1/mixed", violation, StringComparison.Ordinal);
        Assert.Contains("both", violation, StringComparison.Ordinal);
    }

    private static WebApplication Build(Permission[] catalogue, bool evaluator)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddIronApi();
        builder.Services.AddSingleton(new PermissionCatalogue(catalogue));
        if (evaluator)
        {
            builder.Services.AddScoped<IPermissionEvaluator, AllowEverything>();
        }

        var app = builder.Build();
        app.UseIronTelemetry();
        app.UseIronApi();
        return app;
    }

    private sealed class AllowEverything : IPermissionEvaluator
    {
        public ValueTask<AccessDecision> EvaluateAsync(AccessRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(AccessDecision.Allow);
    }
}
