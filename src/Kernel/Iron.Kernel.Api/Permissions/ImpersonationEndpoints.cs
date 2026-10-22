using Iron.Kernel.Api.Responses;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Api.Permissions;

/// <summary>Endpoint metadata: the operation is the person's own and never runs while someone is signed in as them (ADR-0049).</summary>
public sealed class ForbiddenDuringImpersonationMetadata;

public static class ImpersonationEndpointExtensions
{
    /// <summary>
    /// The person's own sign-in, factors, password, sessions and acceptances (TF-025, ADR-0049): refused with
    /// <c>auth.impersonation_forbidden</c> while someone is signed in as them. Declare it after the permission.
    /// </summary>
    public static TBuilder ForbiddenDuringImpersonation<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(new ForbiddenDuringImpersonationMetadata());
        builder.AddEndpointFilter(new ImpersonationFilter());
        return builder;
    }
}

/// <summary>Refuses the operation when someone is signed in as the request's principal.</summary>
public sealed class ImpersonationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var principal = context.HttpContext.RequestServices.GetService<ICurrentPrincipal>();
        var rule = principal is null ? Result.Success() : ImpersonationRules.Require(principal);
        return rule.IsFailure ? ApiResults.Problem(rule.Error, context.HttpContext) : await next(context).ConfigureAwait(false);
    }
}
