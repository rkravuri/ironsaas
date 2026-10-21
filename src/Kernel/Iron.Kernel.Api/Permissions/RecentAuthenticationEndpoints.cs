using Iron.Kernel.Api.Responses;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Api.Permissions;

/// <summary>Endpoint metadata: the operation needs an authentication at most this old (TF-248). OpenAPI and the tests read it.</summary>
public sealed class RecentAuthenticationMetadata(TimeSpan maxAge)
{
    public TimeSpan MaxAge { get; } = maxAge;
}

public static class RecentAuthenticationEndpointExtensions
{
    /// <summary>
    /// Step-up (TF-248, ADR-0047): the operation runs only when the caller authenticated within <paramref name="minutes"/>
    /// minutes; otherwise it answers 403 <c>auth.reauthentication_required</c> and the shell asks the person to confirm it is
    /// them, then sends the request again. Declare it after the permission, so a caller without the permission hears that
    /// first and is never asked for a password to be refused.
    /// </summary>
    public static TBuilder RequireRecentAuthentication<TBuilder>(this TBuilder builder, int minutes = 5)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThan(minutes, 1);
        var maxAge = TimeSpan.FromMinutes(minutes);
        builder.WithMetadata(new RecentAuthenticationMetadata(maxAge));
        builder.AddEndpointFilter(new RecentAuthenticationFilter(maxAge));
        return builder;
    }
}

/// <summary>Asks <see cref="IRecentAuthentication"/>, the one mechanism every step-up goes through, before the handler runs.</summary>
public sealed class RecentAuthenticationFilter(TimeSpan maxAge) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var principal = http.RequestServices.GetService<ICurrentPrincipal>();
        if (principal is null || principal.IsAnonymous())
        {
            return AuthenticationFailure.Problem(http);
        }

        var recent = http.RequestServices.GetService<IRecentAuthentication>() ?? NoSignInRecentAuthentication.Instance;
        var result = recent.Require(maxAge);
        return result.IsFailure ? ApiResults.Problem(result.Error, http) : await next(context).ConfigureAwait(false);
    }
}
