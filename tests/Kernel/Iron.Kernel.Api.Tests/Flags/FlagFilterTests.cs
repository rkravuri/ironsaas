using System.Net;
using System.Text.Json;
using Iron.Kernel.Api.Entitlements;
using Iron.Kernel.Api.Flags;
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

namespace Iron.Kernel.Api.Tests.Flags;

/// <summary>
/// ADR-0035: an endpoint's flags are asked after authentication and the entitlements and before the permission; a kill
/// switch answers 503 <c>flags.killed</c> with a retry time, a flag that is off 404 <c>flags.disabled</c>.
/// </summary>
public class FlagFilterTests : IAsyncLifetime
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Permission Manage = new("gadgets.items.manage", "Change gadgets", Risk.Medium);

    private readonly RecordingGate _gate = new();
    private readonly List<string> _order = [];
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
        builder.Services.AddScoped<IPermissionEvaluator>(_ => new Recording(_order));
        builder.Services.AddSingleton<IEntitlementGate>(new RecordingEntitlements(_order));
        builder.Services.AddSingleton<IFlagGate>(_gate);
        builder.Services.AddSingleton(_order);
        builder.Services.AddModules(builder.Configuration, [new GadgetsModule()]);

        _app = builder.Build();
        _app.UseIronTelemetry();
        _app.UseIronApi();
        _app.MapModules();
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = _app.GetTestClient();
        _gate.Order = _order;
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
    [Proves("ADR-0035")]
    public async Task TheFlagsAreAskedAfterTheEntitlementsAndBeforeThePermission()
    {
        using var response = await SendAsync("/api/v1/gadgets/import", Alice);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["entitlements", "flags", "permission"], _order);
        var request = Assert.Single(_gate.Requests);
        Assert.Equal([new FlagRequirement("kill.imports", MustBeOn: false)], request.Flags);
        Assert.Equal(Alice, request.PrincipalId);
    }

    [Fact]
    [Proves("ADR-0035")]
    public async Task AKillSwitchAnswers503WithItsCodeTheReasonAndARetryTimeAndThePermissionIsNeverAsked()
    {
        _gate.Answer = FlagDecision.Refuse(FlagErrors.Killed, "Imports are stopped by the platform operator for now: a fix is coming. Try again later.", "kill.imports", "a fix is coming.");

        using var response = await SendAsync("/api/v1/gadgets/import", Alice);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("30", response.Headers.RetryAfter?.ToString());
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(FlagErrors.Killed, problem.RootElement.GetProperty("error_code").GetString());
        Assert.Equal("kill.imports", problem.RootElement.GetProperty("flag").GetString());
        Assert.Equal("a fix is coming.", problem.RootElement.GetProperty("reason").GetString());
        Assert.DoesNotContain("permission", _order);
    }

    [Fact]
    [Proves("TF-054 AC2", "ADR-0035")]
    public async Task AFlagThatIsOffAnswers404WithItsCodeAndNobodySignedInHears401First()
    {
        _gate.Answer = FlagDecision.Refuse(FlagErrors.Disabled, "This is not available to you yet.", "flags.example_release", null);

        using var anonymous = await SendAsync("/api/v1/gadgets/beta", null);
        using var refused = await SendAsync("/api/v1/gadgets/beta", Alice);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        using var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(FlagErrors.Disabled, problem.RootElement.GetProperty("error_code").GetString());
        Assert.Equal([new FlagRequirement("flags.example_release", MustBeOn: true)], Assert.Single(_gate.Requests).Flags);
    }

    [Fact]
    [Proves("ADR-0035")]
    public void TheStartUpCheckRefusesAnUndeclaredFlagAndAFlaggedEndpointWithoutAGate()
    {
        var catalogue = LicensingCatalogue.From([new GadgetsModule()]);
        var declared = Endpoint("/api/v1/gadgets/import", new SignedInMetadata(), new FlagMetadata("kill.imports", false));
        var undeclared = Endpoint("/api/v1/gadgets/other", new SignedInMetadata(), new FlagMetadata("gadgets.not_declared", true));

        Assert.Empty(EndpointPermissionCheck.Check([declared], new PermissionCatalogue([Manage]), true, catalogue, true, flagGateRegistered: true));
        Assert.Contains("flag 'gadgets.not_declared' is not declared", Assert.Single(EndpointPermissionCheck.Check([undeclared], new PermissionCatalogue([Manage]), true, catalogue, true, flagGateRegistered: true)), StringComparison.Ordinal);
        Assert.Contains("IFlagGate", Assert.Single(EndpointPermissionCheck.Check([declared], new PermissionCatalogue([Manage]), true, catalogue, true, flagGateRegistered: false)), StringComparison.Ordinal);
    }

    private static RouteEndpoint Endpoint(string pattern, params object[] metadata) =>
        new(_ => Task.CompletedTask, Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse(pattern), 0, new EndpointMetadataCollection(metadata), pattern);

    private async Task<HttpResponseMessage> SendAsync(string path, Guid? principal)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        if (principal is not null)
        {
            request.Headers.Add(HeaderPrincipal.HeaderName, principal.Value.ToString());
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private sealed class RecordingGate : IFlagGate
    {
        public List<FlagRequest> Requests { get; } = [];

        public FlagDecision Answer { get; set; } = FlagDecision.Allow;

        public List<string> Order { get; set; } = [];

        public ValueTask<FlagDecision> CheckAsync(FlagRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Order.Add("flags");
            return ValueTask.FromResult(Answer);
        }
    }

    private sealed class RecordingEntitlements(List<string> order) : IEntitlementGate
    {
        public ValueTask<EntitlementDecision> CheckAsync(EntitlementRequest request, CancellationToken cancellationToken)
        {
            order.Add("entitlements");
            return ValueTask.FromResult(EntitlementDecision.Allow);
        }
    }

    private sealed class Recording(List<string> order) : IPermissionEvaluator
    {
        public ValueTask<AccessDecision> EvaluateAsync(AccessRequest request, CancellationToken cancellationToken)
        {
            order.Add("permission");
            return ValueTask.FromResult(AccessDecision.Allow);
        }
    }

    /// <summary>A licensed module with a kill switch and a release flag on two endpoints.</summary>
    private sealed class GadgetsModule : IModule
    {
        public string Name => "gadgets";

        public IReadOnlyList<Permission> Permissions => [Manage];

        public ModuleLicence Licence => new("gadgets", SubscriptionType.Platform);

        public IReadOnlyList<Flag> Flags => [Flag.Boolean("flags.example_release", FlagKind.Release, "Releases the beta", "Gadgets team", new DateOnly(2027, 1, 1), false)];

        public void AddServices(IServiceCollection services, IConfiguration configuration)
        {
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
            endpoints.MapPost("/api/v1/gadgets/import", () => "imported").RequirePermission(Manage).RequireFlagOff("kill.imports");
            endpoints.MapPost("/api/v1/gadgets/beta", () => "beta").RequirePermission(Manage).RequireFlag("flags.example_release");
        }
    }
}
