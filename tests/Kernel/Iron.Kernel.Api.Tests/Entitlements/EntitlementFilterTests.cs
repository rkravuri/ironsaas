using System.Net;
using System.Text.Json;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Modules;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Tests.Permissions;
using Iron.Kernel.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Api.Tests.Entitlements;

/// <summary>
/// ADR-0034: a licensed module's endpoints and a feature's endpoints ask the gate after authentication and before the
/// permission; the gate's refusal is the answer, and the permission evaluator is never asked for it.
/// </summary>
public class EntitlementFilterTests : IAsyncLifetime
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly RecordingGate _gate = new();
    private readonly List<AccessRequest> _permissionChecks = [];
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddIronApi();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentPrincipal, HeaderPrincipal>();
        builder.Services.AddScoped<IPermissionEvaluator>(_ => new Everyone(_permissionChecks));
        builder.Services.AddSingleton<IEntitlementGate>(_gate);
        builder.Services.AddModules(builder.Configuration, [new LicensedModule(), new PlatformModule()]);

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
    [Proves("ADR-0034")]
    public async Task ALicensedModulesEndpointAsksTheGateWithItsLicenceAndWhetherItWrites()
    {
        using var read = await Send(HttpMethod.Get, "/api/v1/widgets", Alice);
        using var write = await Send(HttpMethod.Post, "/api/v1/widgets", Alice);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        Assert.Collection(
            _gate.Requests,
            request =>
            {
                Assert.Equal("widgets", request.Licence);
                Assert.False(request.IsWrite);
                Assert.Equal(Alice, request.PrincipalId);
                Assert.Equal("GET /api/v1/widgets", request.Operation);
            },
            request => Assert.True(request.IsWrite));
    }

    [Fact]
    [Proves("ADR-0034")]
    public async Task ARefusalAnswers403WithTheGatesCodeAndThePermissionIsNeverAsked()
    {
        _gate.Answer = EntitlementDecision.Refuse(EntitlementErrors.ReadOnly, "Widgets are read only for your organisation.", EntitlementReasons.Licensed, "widgets");

        using var response = await Send(HttpMethod.Post, "/api/v1/widgets", Alice);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(EntitlementErrors.ReadOnly, problem.RootElement.GetProperty("error_code").GetString());
        Assert.Equal("widgets", problem.RootElement.GetProperty("entitlement").GetString());
        Assert.Equal(EntitlementReasons.Licensed, problem.RootElement.GetProperty("reason").GetString());
        Assert.Equal("Widgets are read only for your organisation.", problem.RootElement.GetProperty("detail").GetString());
        Assert.Empty(_permissionChecks);
    }

    [Fact]
    [Proves("ADR-0034")]
    public async Task NobodySignedInHears401BeforeAnyEntitlementIsChecked()
    {
        _gate.Answer = EntitlementDecision.Refuse(EntitlementErrors.ModuleDisabled, "Off.", EntitlementReasons.NotLicensed, "widgets");

        using var response = await Send(HttpMethod.Get, "/api/v1/widgets", principal: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_gate.Requests);
    }

    [Fact]
    [Proves("ADR-0034")]
    public async Task AFeatureOnAPlatformEndpointIsCheckedAndOtherPlatformEndpointsAreNot()
    {
        using var plain = await Send(HttpMethod.Get, "/api/v1/gadgets", Alice);
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Empty(_gate.Requests);

        _gate.Answer = EntitlementDecision.Refuse(EntitlementErrors.FeatureDisabled, "Not in your plan.", EntitlementReasons.NotLicensed, "gadgets.export");
        using var export = await Send(HttpMethod.Get, "/api/v1/gadgets/export", Alice);

        Assert.Equal(HttpStatusCode.Forbidden, export.StatusCode);
        var request = Assert.Single(_gate.Requests);
        Assert.Null(request.Licence);
        Assert.Equal(["gadgets.export"], request.Features);
        Assert.DoesNotContain(_permissionChecks, check => check.Path == "/api/v1/gadgets/export");
    }

    [Fact]
    [Proves("TF-043 AC2", "ADR-0034")]
    public async Task AnAnonymousEndpointOfALicensedModuleIsCheckedOnceWithoutAPrincipal()
    {
        _gate.Answer = EntitlementDecision.Refuse(EntitlementErrors.ModuleDisabled, "Off.", EntitlementReasons.NotLicensed, "widgets");

        using var response = await Send(HttpMethod.Get, "/api/v1/widgets/public", principal: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var request = Assert.Single(_gate.Requests);
        Assert.Null(request.PrincipalId);
        Assert.Equal("widgets", request.Licence);
    }

    [Fact]
    [Proves("ADR-0034")]
    public void TheHostsCatalogueListsTheLicensedModuleAndBothFeatures()
    {
        var catalogue = _app!.Services.GetRequiredService<LicensingCatalogue>();

        var module = Assert.Single(catalogue.Modules);
        Assert.Equal(("widgets", "widgets", SubscriptionType.PerPerson), (module.Code, module.Module, module.SubscriptionType));
        Assert.Equal(["widgets.bulk"], module.Features);
        Assert.Equal(["gadgets.export", "widgets.bulk"], catalogue.Features.Select(f => f.Code));
        Assert.Null(catalogue.FindFeature("gadgets.export")!.Licence);
        Assert.Equal("widgets", catalogue.FindFeature("widgets.bulk")!.Licence);
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, Guid? principal)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (principal is not null)
        {
            request.Headers.Add(HeaderPrincipal.HeaderName, principal.Value.ToString());
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed class RecordingGate : IEntitlementGate
    {
        public List<EntitlementRequest> Requests { get; } = [];

        public EntitlementDecision Answer { get; set; } = EntitlementDecision.Allow;

        public ValueTask<EntitlementDecision> CheckAsync(EntitlementRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(Answer);
        }
    }

    private sealed class Everyone(List<AccessRequest> requests) : IPermissionEvaluator
    {
        public ValueTask<AccessDecision> EvaluateAsync(AccessRequest request, CancellationToken cancellationToken)
        {
            requests.Add(request);
            return ValueTask.FromResult(AccessDecision.Allow);
        }
    }

    /// <summary>A module like a suite's: licensed per person, with a feature of its own.</summary>
    private sealed class LicensedModule : IModule
    {
        public static readonly Permission Read = new("widgets.items.read", "Read widgets", Risk.Low);

        public string Name => "widgets";

        public IReadOnlyList<Permission> Permissions => [Read];

        public ModuleLicence Licence => new("widgets", SubscriptionType.PerPerson);

        public IReadOnlyList<Feature> Features => [new("widgets.bulk", "Change many widgets at once")];

        public void AddServices(IServiceCollection services, IConfiguration configuration)
        {
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/v1/widgets", () => "widgets").RequirePermission(Read);
            endpoints.MapPost("/api/v1/widgets", () => "made").RequirePermission(Read);
            endpoints.MapGet("/api/v1/widgets/public", () => "public").AllowAnonymous();
        }
    }

    /// <summary>A kernel-like module: always on, with one feature on one endpoint.</summary>
    private sealed class PlatformModule : IModule
    {
        public static readonly Permission Read = new("gadgets.items.read", "Read gadgets", Risk.Low);

        public string Name => "gadgets";

        public IReadOnlyList<Permission> Permissions => [Read];

        public IReadOnlyList<Feature> Features => [new("gadgets.export", "Export gadgets")];

        public void AddServices(IServiceCollection services, IConfiguration configuration)
        {
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/v1/gadgets", () => "gadgets").RequirePermission(Read);
            endpoints.MapGet("/api/v1/gadgets/export", () => "exported").RequirePermission(Read).RequireFeature("gadgets.export");
        }
    }
}
