using Iron.Kernel.Api.Problems;
using Iron.Kernel.Telemetry.Correlation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Iron.Kernel.Api.Tenancy;

/// <summary>
/// Isolation by construction (TF-003): a request whose Host header is not one of this unit's hostnames gets a
/// 421 Misdirected Request and never reaches business code. Health probes come from the platform with the
/// container's own address, so they are exempt.
/// </summary>
public sealed class TenantHostMiddleware(RequestDelegate next, TenantContext tenant)
{
    public const string ErrorCode = "tenant.host.not_served";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.Path.StartsWithSegments("/health") || tenant.ServesHost(context.Request.Host.Value))
        {
            await next(context);
            return;
        }

        var problem = new ProblemDetails
        {
            Type = ProblemFactory.TypePrefix + ErrorCode,
            Title = "Misdirected request.",
            Status = StatusCodes.Status421MisdirectedRequest,
            Detail = "This address is not served by this unit.",
            Instance = context.Request.Path.Value,
        };
        problem.Extensions[ProblemFactory.ErrorCodeKey] = ErrorCode;
        problem.Extensions[ProblemFactory.CorrelationIdKey] = RequestCorrelation.Get(context).Value;

        await TypedResults.Problem(problem).ExecuteAsync(context);
    }
}
