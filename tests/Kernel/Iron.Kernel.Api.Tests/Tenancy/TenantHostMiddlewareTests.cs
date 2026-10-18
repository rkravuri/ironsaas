using System.Net;
using System.Text.Json;
using Iron.Kernel.Api.Tenancy;
using Iron.Kernel.Telemetry;
using Iron.Kernel.Telemetry.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Api.Tests.Tenancy;

/// <summary>A unit bound to acme, asked with different Host headers (TF-003).</summary>
public class TenantHostMiddlewareTests : IAsyncLifetime
{
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddIronTenant(new TenantContext(TenantId.Parse("acme"), ["acme.localhost"]));
        builder.Services.AddIronApi();
        builder.Services.AddIronHealth();

        _app = builder.Build();
        _app.UseIronTelemetry();
        _app.UseIronApi();
        _app.UseIronTenant();
        _app.MapIronHealth();
        _app.MapGet("/api/v1/ping", () => "pong").AllowAnonymous();
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
    public async Task ServesItsOwnHostname()
    {
        using var response = await Send("/api/v1/ping", "acme.localhost");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RefusesAnotherHostnameWithAProblemAndNeverReachesTheEndpoint()
    {
        using var response = await Send("/api/v1/ping", "bravo.localhost");

        Assert.Equal(HttpStatusCode.MisdirectedRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(TenantHostMiddleware.ErrorCode, problem.RootElement.GetProperty("error_code").GetString());
        Assert.Equal(32, problem.RootElement.GetProperty("correlation_id").GetString()?.Length);
        Assert.DoesNotContain("acme", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthProbesAreExemptFromTheHostnameCheck()
    {
        using var response = await Send("/health/live", "10.0.0.7:8080");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<HttpResponseMessage> Send(string path, string host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Host = host;
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
