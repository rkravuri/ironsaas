using Iron.Kernel.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Security.Tests;

public sealed class SecurityFixture : IAsyncLifetime
{
    private WebApplication? _app;

    public HttpClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddIronSecurity(builder.Configuration, security =>
        {
            security.Cors.AllowedOrigins = ["https://allowed.example"];
            security.Headers.CspExemptPathPrefixes = ["/docs"];
        });

        var app = builder.Build();
        app.UseIronSecurity();
        app.MapGet("/ok", () => "ok");
        app.MapGet("/api/v1/things", () => "things");
        app.MapGet("/docs/page", () => "docs");
        app.MapGet("/cookie", (HttpContext http) =>
        {
            http.Response.Cookies.Append("session", "abc");
            return "cookie set";
        });

        await app.StartAsync(TestContext.Current.CancellationToken);
        _app = app;
        Client = app.GetTestClient();
        Client.BaseAddress = new Uri("https://localhost");
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync(TestContext.Current.CancellationToken);
            await _app.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SecurityCollection : ICollectionFixture<SecurityFixture>
{
    public const string Name = "security";
}
