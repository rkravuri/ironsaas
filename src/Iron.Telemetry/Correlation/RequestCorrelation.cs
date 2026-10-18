using System.Diagnostics;
using Iron.Core.Correlation;
using Microsoft.AspNetCore.Http;

namespace Iron.Telemetry.Correlation;

/// <summary>Where a request's correlation id is kept for the rest of the pipeline (problem details, logs).</summary>
public static class RequestCorrelation
{
    private const string ItemKey = "Iron.CorrelationId";

    /// <summary>The id the middleware fixed for this request, or the current trace id when the middleware did not run.</summary>
    public static CorrelationId Get(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ItemKey, out var value) && value is CorrelationId id ? id : CorrelationId.Current();
    }

    internal static void Set(HttpContext context, CorrelationId id) => context.Items[ItemKey] = id;
}

/// <summary>
/// Fixes the correlation id of the request (the trace id) and returns it in the X-Correlation-Id header (TF-130).
/// When no trace is active, for example in a test without OpenTelemetry, it starts one so that the id is stable.
/// </summary>
public sealed class CorrelationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Activity? started = null;
        if (Activity.Current is null)
        {
            started = new Activity("http.request");
            started.Start();
        }

        try
        {
            var id = CorrelationId.Current();
            RequestCorrelation.Set(context, id);
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[CorrelationId.HeaderName] = id.Value;
                return Task.CompletedTask;
            });

            await next(context);
        }
        finally
        {
            started?.Dispose();
        }
    }
}
