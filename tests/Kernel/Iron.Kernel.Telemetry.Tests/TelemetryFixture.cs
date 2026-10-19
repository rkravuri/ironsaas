using System.Diagnostics;
using Iron.Kernel.Telemetry.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;

namespace Iron.Kernel.Telemetry.Tests;

/// <summary>A host with the telemetry pipeline, spans collected in memory instead of exported to a collector.</summary>
public sealed class TelemetryFixture : IAsyncLifetime
{
    private WebApplication? _app;

    public List<Activity> ExportedSpans { get; } = [];

    public HttpClient Client { get; private set; } = null!;

    public TracerProvider TracerProvider { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.AddIronTelemetry("iron-tests", telemetry =>
        {
            telemetry.ExportToCollector = false;
            telemetry.TenantId = "acme";
        });
        builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddInMemoryExporter(ExportedSpans));
        builder.Services.AddIronHealth();
        builder.Services.AddHealthChecks()
            .AddCheck("always-up", () => HealthCheckResult.Healthy("up"), tags: ["ready"])
            .AddCheck("not-ready", () => HealthCheckResult.Unhealthy("still warming up"), tags: ["ready"])
            .AddCheck("untagged", () => HealthCheckResult.Unhealthy("not part of readiness"));

        var app = builder.Build();
        app.UseIronTelemetry();
        app.MapGet("/ping", () => "pong");
        app.MapIronHealth();

        await app.StartAsync(TestContext.Current.CancellationToken);
        _app = app;
        Client = app.GetTestClient();
        TracerProvider = app.Services.GetRequiredService<TracerProvider>();
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
public sealed class TelemetryCollection : ICollectionFixture<TelemetryFixture>
{
    public const string Name = "telemetry";
}
