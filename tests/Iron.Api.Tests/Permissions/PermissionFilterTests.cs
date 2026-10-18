using System.Net;
using System.Text.Json;
using Iron.Api.Permissions;
using Iron.Kernel;
using Iron.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Api.Tests.Permissions;

/// <summary>TF-031: the filter runs before the handler; 401 for nobody, 403 with a stable code for the wrong somebody.</summary>
public class PermissionFilterTests : IAsyncLifetime
{
    private static readonly Permission Read = new("sample.things.read", "Read things", Risk.Low);
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly List<AccessRequest> _requests = [];
    private WebApplication? _app;
    private HttpClient _client = null!;
    private int _handlerCalls;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddIronApi();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(new PermissionCatalogue([Read]));
        builder.Services.AddScoped<ICurrentPrincipal, HeaderPrincipal>();
        builder.Services.AddScoped<IPermissionEvaluator>(_ => new OnlyAlice(_requests));

        _app = builder.Build();
        _app.UseIronTelemetry();
        _app.UseIronApi();
        _app.MapGet("/api/v1/things", () =>
        {
            _handlerCalls++;
            return "things";
        }).RequirePermission(Read);
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
    public async Task NobodySignedInGets401AndTheHandlerNeverRuns()
    {
        using var response = await Send(principal: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(PermissionErrors.NotAuthenticated, await ErrorCode(response));
        Assert.Equal(0, _handlerCalls);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task ThePermittedPrincipalReachesTheHandler()
    {
        using var response = await Send(Alice);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, _handlerCalls);
        var request = Assert.Single(_requests);
        Assert.Equal(Alice, request.PrincipalId);
        Assert.Equal(Read, request.Permission);
        Assert.Equal("GET /api/v1/things", request.Operation);
    }

    [Fact]
    public async Task AnotherPrincipalGets403WithTheStableCodeAndThePermissionNamed()
    {
        using var response = await Send(Bob);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PermissionErrors.PermissionDenied, problem.RootElement.GetProperty("error_code").GetString());
        Assert.Equal("sample.things.read", problem.RootElement.GetProperty("permission").GetString());
        Assert.Equal(32, problem.RootElement.GetProperty("correlation_id").GetString()?.Length);
        Assert.False(problem.RootElement.TryGetProperty("trace_id", out _));
        Assert.Equal(0, _handlerCalls);
    }

    private async Task<HttpResponseMessage> Send(Guid? principal)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/things", UriKind.Relative));
        if (principal is not null)
        {
            request.Headers.Add(HeaderPrincipal.HeaderName, principal.Value.ToString());
        }

        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return problem.RootElement.GetProperty("error_code").GetString();
    }

    private sealed class OnlyAlice(List<AccessRequest> requests) : IPermissionEvaluator
    {
        public ValueTask<AccessDecision> EvaluateAsync(AccessRequest request, CancellationToken cancellationToken)
        {
            requests.Add(request);
            return ValueTask.FromResult(request.PrincipalId == Alice ? AccessDecision.Allow : AccessDecision.Deny(AccessDecision.NotGranted));
        }
    }
}

/// <summary>A test principal taken from a request header; Identity replaces this with the session principal in T10.</summary>
public sealed class HeaderPrincipal(IHttpContextAccessor accessor) : ICurrentPrincipal
{
    public const string HeaderName = "X-Test-Principal";

    public Guid Id => Guid.TryParse(accessor.HttpContext?.Request.Headers[HeaderName].ToString(), out var id) ? id : Guid.Empty;
}
