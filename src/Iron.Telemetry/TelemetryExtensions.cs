using Iron.Telemetry.Correlation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Iron.Telemetry;

/// <summary>
/// Observable by default (P6, TF-130): one call gives a host traces, metrics and logs over OpenTelemetry, with the
/// service name, the environment and the tenant on every signal, and the correlation id on every response.
/// </summary>
public static class TelemetryExtensions
{
    public const string ServiceNamespace = "foundation";

    public static WebApplicationBuilder AddIronTelemetry(this WebApplicationBuilder builder, string serviceName, Action<TelemetryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var options = new TelemetryOptions { ServiceName = serviceName };
        builder.Configuration.GetSection(TelemetryOptions.SectionName).Bind(options);
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        var version = (System.Reflection.Assembly.GetEntryAssembly() ?? typeof(TelemetryExtensions).Assembly).GetName().Version?.ToString() ?? "0.0.0";
        var attributes = new List<KeyValuePair<string, object>>
        {
            new("deployment.environment.name", builder.Environment.EnvironmentName),
            new("tenant.id", options.TenantId ?? "unbound"),
        };

        var openTelemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(options.ServiceName, serviceNamespace: ServiceNamespace, serviceVersion: version, serviceInstanceId: Environment.MachineName)
                .AddAttributes(attributes))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(instrumentation =>
                {
                    instrumentation.RecordException = true;
                    if (!options.TraceHealthEndpoints)
                    {
                        instrumentation.Filter = context => !context.Request.Path.StartsWithSegments("/health");
                    }
                })
                .AddHttpClientInstrumentation()
                .AddSource("Npgsql")
                .AddSource("Iron.*"))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter("Npgsql", "System.Runtime", "Iron.*"))
            .WithLogging();

        if (options.ExportToCollector)
        {
            openTelemetry.UseOtlpExporter();
        }

        builder.Services.Configure<OpenTelemetryLoggerOptions>(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        return builder;
    }

    /// <summary>Puts the correlation id on every response. Call first in the pipeline, before the exception handler.</summary>
    public static IApplicationBuilder UseIronTelemetry(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<CorrelationMiddleware>();
    }
}
